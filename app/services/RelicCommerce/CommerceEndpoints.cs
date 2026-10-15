using System.Text;
using System.Text.Json;
using Stripe;

namespace RelicCommerce;

public static class CommerceEndpoints
{
    private static readonly TimeSpan DeliveryTokenLifetime = TimeSpan.FromDays(3);

    public static void MapCommerceEndpoints(this WebApplication app)
    {
        app.MapPost("/api/checkout", async (
            HttpResponse response,
            CommerceOptions options,
            StripeGateway stripe,
            CancellationToken cancellationToken) =>
        {
            if (!options.IsReady(out _)) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            string url = await stripe.CreateCheckoutAsync(cancellationToken);
            response.StatusCode = StatusCodes.Status303SeeOther;
            response.Headers.Location = url;
            return Results.StatusCode(StatusCodes.Status303SeeOther);
        });

        app.MapPost("/api/stripe/webhook", async (
            HttpRequest request,
            CommerceOptions options,
            StripeGateway stripe,
            OrderStore store,
            FulfillmentService fulfillment,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (!options.IsReady(out _)) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (request.ContentLength is > 1_048_576) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            string raw;
            try
            {
                raw = await ReadLimitedBodyAsync(request, 1_048_576, cancellationToken);
                string signature = request.Headers["Stripe-Signature"].ToString();
                VerifiedStripeEvent webhook = ParseVerifiedEvent(raw, signature, options.StripeWebhookSecret);
                switch (webhook.Type)
                {
                    case "checkout.session.completed":
                        // A completed Checkout Session can still be unpaid while an asynchronous
                        // method is processing. A later retry must not use this older timestamp.
                        if (!string.Equals(webhook.PaymentStatus, "paid", StringComparison.Ordinal))
                            break;
                        goto case "checkout.session.async_payment_succeeded";
                    case "checkout.session.async_payment_succeeded":
                        if (webhook.ObjectId is null) return Results.BadRequest();
                        StripeSessionSnapshot session = await stripe.RetrieveSessionAsync(webhook.ObjectId, cancellationToken);
                        try
                        {
                            fulfillment.Fulfill(session, DateTimeOffset.FromUnixTimeSeconds(webhook.CreatedUnixSeconds));
                        }
                        catch (PaymentBlockedException)
                        {
                            // A refund or dispute can arrive before this event. The signed event is
                            // valid, but its payment must remain unfulfillable and be acknowledged.
                        }
                        break;
                    case "charge.refunded":
                    case "charge.dispute.created":
                    case "charge.dispute.funds_withdrawn":
                        if (webhook.PaymentIntentId is not null)
                            store.MarkRefundedByPaymentIntent(webhook.PaymentIntentId);
                        break;
                    case "refund.created":
                        if (string.Equals(webhook.ObjectStatus, "succeeded", StringComparison.Ordinal)
                            && webhook.PaymentIntentId is not null)
                            store.MarkRefundedByPaymentIntent(webhook.PaymentIntentId);
                        break;
                }
                store.RecordEvent(webhook.Id, webhook.Type, DateTimeOffset.UtcNow);
                return Results.Ok();
            }
            catch (WebhookSignatureException)
            {
                return Results.BadRequest();
            }
            catch (StripeException ex)
            {
                loggerFactory.CreateLogger("RelicCommerce").LogError(ex, "Stripe retrieval failed; webhook will be retried.");
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }
            catch (ArgumentException)
            {
                return Results.BadRequest();
            }
            catch (InvalidOperationException ex)
            {
                loggerFactory.CreateLogger("RelicCommerce").LogWarning("Webhook rejected: {Reason}", ex.Message);
                return Results.BadRequest();
            }
        });

        app.MapGet("/api/purchase-status/{sessionId}", (
            string sessionId,
            CommerceOptions options,
            OrderStore store) =>
        {
            if (!options.IsReady(out _)) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            LicenseOrder? order = store.Get(sessionId);
            if (order is null) return Results.NotFound();
            if (order.Status == "refunded") return Results.StatusCode(StatusCodes.Status410Gone);
            if (order.Status != "paid") return Results.StatusCode(StatusCodes.Status409Conflict);
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            DateTimeOffset deliveryDeadline = order.PurchasedAtUtc.Add(DeliveryTokenLifetime);
            if (nowUtc >= deliveryDeadline) return Results.StatusCode(StatusCodes.Status410Gone);
            string token = store.IssueDeliveryToken(sessionId, deliveryDeadline);
            return Results.Ok(new { status = "ready", deliveryUrl = $"/api/license/redeem/{token}" });
        }).AddEndpointFilter(NoStoreFilter);

        app.MapGet("/api/license/redeem/{token}", (
            string token,
            CommerceOptions options,
            OrderStore store,
            LicenseIssuer issuer) =>
        {
            if (!options.IsReady(out _)) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            LicenseOrder? order = store.GetByDeliveryToken(token, DateTimeOffset.UtcNow);
            if (order is null) return Results.NotFound();
            string key = issuer.Issue(order);
            byte[] contents = Encoding.UTF8.GetBytes(key + "\n");
            return Results.File(contents, "text/plain; charset=utf-8", "RelicScreenshot-License.txt");
        }).AddEndpointFilter(NoStoreFilter);
    }

    private static async ValueTask<object?> NoStoreFilter(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        context.HttpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.HttpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return await next(context);
    }

    internal static VerifiedStripeEvent ParseVerifiedEvent(
        string raw,
        string signature,
        string secret)
    {
        try
        {
            EventUtility.ConstructEvent(raw, signature, secret, 300, throwOnApiVersionMismatch: false);
        }
        catch (StripeException ex)
        {
            throw new WebhookSignatureException(ex);
        }
        using JsonDocument document = JsonDocument.Parse(raw);
        JsonElement root = document.RootElement;
        string id = RequiredString(root, "id");
        string type = RequiredString(root, "type");
        long created = root.GetProperty("created").GetInt64();
        JsonElement objectElement = root.GetProperty("data").GetProperty("object");
        string? objectId = OptionalString(objectElement, "id");
        string? paymentIntentId = OptionalString(objectElement, "payment_intent");
        string? paymentStatus = OptionalString(objectElement, "payment_status");
        string? objectStatus = OptionalString(objectElement, "status");
        return new VerifiedStripeEvent(id, type, created, objectId, paymentIntentId, paymentStatus, objectStatus);
    }

    private static string RequiredString(JsonElement objectElement, string name)
    {
        string? value = OptionalString(objectElement, name);
        return string.IsNullOrWhiteSpace(value) ? throw new JsonException($"Missing {name}.") : value;
    }

    private static string? OptionalString(JsonElement objectElement, string name)
    {
        if (!objectElement.TryGetProperty(name, out JsonElement value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static async Task<string> ReadLimitedBodyAsync(
        HttpRequest request,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        byte[] buffer = new byte[8192];
        int total = 0;
        while (true)
        {
            int read = await request.Body.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > maximumBytes) throw new InvalidOperationException("Webhook payload is too large.");
            stream.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal sealed record VerifiedStripeEvent(
        string Id,
        string Type,
        long CreatedUnixSeconds,
        string? ObjectId,
        string? PaymentIntentId,
        string? PaymentStatus,
        string? ObjectStatus);

    internal sealed class WebhookSignatureException : Exception
    {
        public WebhookSignatureException(Exception innerException)
            : base("The Stripe webhook signature is invalid.", innerException) { }
    }
}
