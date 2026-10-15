using System.Security.Cryptography;
using RelicCommerce;
using Stripe;
using WinShot.Licensing;
using Xunit;

namespace RelicCommerce.Tests;

public sealed class CommerceSecurityTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), "relic-commerce-tests", Guid.NewGuid().ToString("N"), "orders.db");

    [Fact]
    public void Unpaid_session_is_rejected()
    {
        CommerceOptions options = Options();
        using var store = new OrderStore(options);
        var service = new FulfillmentService(options, store);
        Assert.False(service.TryValidatePaidSession(Session(paymentStatus: "unpaid"), out string reason));
        Assert.Equal("unpaid", reason);
    }

    [Fact]
    public void Malformed_signing_key_cannot_enable_sales()
    {
        CommerceOptions options = Options();
        Assert.Throws<InvalidOperationException>(() => options.RequireSigningKeyMatchesDesktopVerifier());
    }

    [Fact]
    public void Mismatched_signing_key_cannot_enable_sales()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = key.ExportEncryptedPkcs8PrivateKeyPem(
            "wrong-passphrase", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));
        CommerceOptions options = Options(privateKeyPem: pem, passphrase: "wrong-passphrase");
        Assert.Throws<InvalidOperationException>(() => options.RequireSigningKeyMatchesDesktopVerifier());
    }

    [Fact]
    public void Exact_price_and_metadata_are_required()
    {
        CommerceOptions options = Options();
        using var store = new OrderStore(options);
        var service = new FulfillmentService(options, store);
        Assert.False(service.TryValidatePaidSession(Session(priceId: "price_other"), out string priceReason));
        Assert.Equal("wrong-price", priceReason);
        Assert.False(service.TryValidatePaidSession(Session(priceAmount: 2999), out string amountReason));
        Assert.Equal("wrong-price-amount", amountReason);
        Assert.False(service.TryValidatePaidSession(Session(metadata: new Dictionary<string, string>
        {
            ["product"] = "relic-screenshot", ["offer"] = "v1", ["edition"] = "wrong"
        }), out string editionReason));
        Assert.Equal("wrong-edition", editionReason);
    }

    [Fact]
    public void Duplicate_webhook_cannot_create_a_second_license_order()
    {
        CommerceOptions options = Options();
        using var store = new OrderStore(options);
        var service = new FulfillmentService(options, store);
        StripeSessionSnapshot session = Session();
        LicenseOrder first = service.Fulfill(session, DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        LicenseOrder second = service.Fulfill(session, DateTimeOffset.FromUnixTimeSeconds(1_800_000_100));
        Assert.Equal(first.LicenseId, second.LicenseId);
        Assert.Equal(first.PurchasedAtUtc, second.PurchasedAtUtc);
        Assert.Equal(first.PurchasedAtUtc.AddYears(1), first.UpdatesUntilUtc);
        Assert.Equal(1, first.SeatLimit);
    }

    [Fact]
    public void Refund_blocks_existing_delivery_token()
    {
        CommerceOptions options = Options();
        using var store = new OrderStore(options);
        var service = new FulfillmentService(options, store);
        LicenseOrder order = service.Fulfill(Session(), DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        string token = store.IssueDeliveryToken(order.SessionId, DateTimeOffset.UtcNow.AddDays(3));
        Assert.NotNull(store.GetByDeliveryToken(token, DateTimeOffset.UtcNow));
        store.MarkRefundedByPaymentIntent(order.PaymentIntentId!);
        Assert.Null(store.GetByDeliveryToken(token, DateTimeOffset.UtcNow));
        Assert.Equal("refunded", store.Get(order.SessionId)!.Status);
    }

    [Fact]
    public void Refund_before_completion_blocks_later_webhook_replay()
    {
        CommerceOptions options = Options();
        using var store = new OrderStore(options);
        var service = new FulfillmentService(options, store);
        store.MarkRefundedByPaymentIntent("pi_test");
        Assert.ThrowsAny<InvalidOperationException>(() =>
            service.Fulfill(Session(), DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)));
        Assert.Null(store.Get("cs_test_123"));
    }

    [Fact]
    public void Expired_delivery_token_is_rejected()
    {
        CommerceOptions options = Options();
        using var store = new OrderStore(options);
        var service = new FulfillmentService(options, store);
        LicenseOrder order = service.Fulfill(Session(), DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        string token = store.IssueDeliveryToken(order.SessionId, DateTimeOffset.UtcNow.AddDays(-1));
        Assert.Null(store.GetByDeliveryToken(token, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Delivery_token_expires_at_purchase_deadline_exclusively()
    {
        CommerceOptions options = Options();
        using var store = new OrderStore(options);
        var service = new FulfillmentService(options, store);
        DateTimeOffset purchased = DateTimeOffset.UtcNow.AddDays(-3);
        LicenseOrder order = service.Fulfill(Session(), purchased);
        DateTimeOffset deadline = purchased.AddDays(3);
        string token = store.IssueDeliveryToken(order.SessionId, deadline);
        Assert.Null(store.GetByDeliveryToken(token, deadline));
    }

    [Fact]
    public void Signed_completed_event_preserves_unpaid_status()
    {
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string raw = $"{{\"id\":\"evt_test\",\"type\":\"checkout.session.completed\",\"created\":{created},\"data\":{{\"object\":{{\"id\":\"cs_test_123\",\"payment_status\":\"unpaid\"}}}}}}";
        string signature = EventUtility.GenerateSignatureHeader(raw, "whsec_x", created);
        CommerceEndpoints.VerifiedStripeEvent parsed = CommerceEndpoints.ParseVerifiedEvent(raw, signature, "whsec_x");
        Assert.Equal("unpaid", parsed.PaymentStatus);
    }

    [Fact]
    public void Issuer_preserves_purchase_timestamp_and_calendar_year_cutoff()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        DateTimeOffset purchased = new(2028, 2, 29, 12, 0, 0, TimeSpan.Zero);
        string pem = key.ExportEncryptedPkcs8PrivateKeyPem(
            "test-passphrase", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));
        CommerceOptions options = Options(privateKeyPem: pem, passphrase: "test-passphrase");
        var issuer = new LicenseIssuer(options);
        var order = new LicenseOrder(
            "cs_test", "RS-ABC", purchased, 1, "pro", "buyer@example.test", "Buyer", "paid",
            "pi_test", null, null);
        string encoded = issuer.Issue(order);
        Assert.True(LicenseKey.TryVerify(encoded, key, out LicensePayload? payload));
        Assert.NotNull(payload);
        Assert.Equal(purchased.ToUnixTimeSeconds(), payload!.IssuedAt);
        Assert.Equal(new DateTimeOffset(2029, 2, 28, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), payload.UpdatesUntil);
    }

    private CommerceOptions Options(string? privateKeyPem = null, string passphrase = "secret") => new(
        true, _databasePath, "sk_x", "whsec_x", "price_test", "usd", 1, "pro", false,
        "https://example.test/success?session_id={CHECKOUT_SESSION_ID}", "https://example.test/cancel",
        privateKeyPem ?? "invalid-test-key-material", passphrase);

    private static StripeSessionSnapshot Session(
        string paymentStatus = "paid",
        string priceId = "price_test",
        IReadOnlyDictionary<string, string>? metadata = null,
        long? priceAmount = 3000) => new(
            "cs_test_123", false, "payment", paymentStatus, "complete", priceId, 1, "usd", "pi_test", "buyer@example.test",
        "Buyer", 1_800_000_000, metadata ?? new Dictionary<string, string>
        {
            ["product"] = "relic-screenshot", ["offer"] = "v1", ["edition"] = "pro"
        }, priceAmount);

    public void Dispose()
    {
        string? directory = Path.GetDirectoryName(_databasePath);
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
