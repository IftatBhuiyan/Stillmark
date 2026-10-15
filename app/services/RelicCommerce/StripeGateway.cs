using Stripe;
using Stripe.Checkout;

namespace RelicCommerce;

public sealed class StripeGateway
{
    private readonly CommerceOptions _options;
    private readonly SessionService _sessions;

    public StripeGateway(CommerceOptions options)
    {
        _options = options;
        if (options.IsReady(out _))
        {
            _sessions = new SessionService(new StripeClient(options.StripeSecretKey));
        }
        else
        {
            _sessions = null!;
        }
    }

    public async Task<string> CreateCheckoutAsync(CancellationToken cancellationToken)
    {
        _options.RequireReady();
        var create = new SessionCreateOptions
        {
            Mode = "payment",
            LineItems =
            [new SessionLineItemOptions { Price = _options.StripePriceId, Quantity = 1 }],
            CustomerCreation = "if_required",
            SuccessUrl = _options.SuccessUrl,
            CancelUrl = _options.CancelUrl,
            ClientReferenceId = $"relic-{Guid.NewGuid():N}",
            Metadata = new Dictionary<string, string>
            {
                ["product"] = "relic-screenshot",
                ["offer"] = $"v{_options.ApplicationMajor}",
                ["edition"] = _options.Edition,
            },
        };
        if (_options.ManagedPayments)
            create.ManagedPayments = new SessionManagedPaymentsOptions { Enabled = true };

        Session session = await _sessions.CreateAsync(
            create,
            new RequestOptions { IdempotencyKey = $"checkout-{Guid.NewGuid():N}" },
            cancellationToken);
        return session.Url ?? throw new InvalidOperationException("Stripe returned no checkout URL.");
    }

    public async Task<StripeSessionSnapshot> RetrieveSessionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        _options.RequireReady();
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                sessionId, "^cs_[A-Za-z0-9_]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid Checkout Session identifier.", nameof(sessionId));

        Session session = await _sessions.GetAsync(
            sessionId,
            new SessionGetOptions { Expand = ["line_items"] },
            null,
            cancellationToken);
        LineItem? item = session.LineItems?.Data.SingleOrDefault();
        if (item is null)
            throw new InvalidOperationException("Checkout Session has no single line item.");

        return new StripeSessionSnapshot(
            session.Id,
            session.Livemode,
            session.Mode ?? "",
            session.PaymentStatus ?? "",
            session.Status ?? "",
            item.Price?.Id ?? "",
            item.Quantity ?? 0,
            session.Currency ?? "",
            session.PaymentIntentId,
            session.CustomerDetails?.Email,
            session.CustomerDetails?.Name,
            new DateTimeOffset(session.Created).ToUnixTimeSeconds(),
            session.Metadata ?? new Dictionary<string, string>(),
            item.Price?.UnitAmount);
    }
}
