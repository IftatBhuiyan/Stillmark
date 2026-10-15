namespace RelicCommerce;

public sealed class FulfillmentService
{
    private readonly CommerceOptions _options;
    private readonly OrderStore _store;

    public FulfillmentService(CommerceOptions options, OrderStore store)
    {
        _options = options;
        _store = store;
    }

    public bool TryValidatePaidSession(
        StripeSessionSnapshot session,
        out string reason)
    {
        bool expectedLiveMode = _options.StripeSecretKey.StartsWith("sk_live_", StringComparison.Ordinal);
        if (!session.LiveMode.Equals(expectedLiveMode)) { reason = "livemode-mismatch"; return false; }
        if (!session.Id.StartsWith("cs_", StringComparison.Ordinal)) { reason = "invalid-session"; return false; }
        if (!string.Equals(session.Mode, "payment", StringComparison.Ordinal)) { reason = "wrong-mode"; return false; }
        if (!string.Equals(session.PaymentStatus, "paid", StringComparison.Ordinal)) { reason = "unpaid"; return false; }
        if (!string.Equals(session.Status, "complete", StringComparison.Ordinal)) { reason = "incomplete"; return false; }
        if (string.IsNullOrWhiteSpace(session.PaymentIntentId)) { reason = "missing-payment-intent"; return false; }
        if (!string.Equals(session.PriceId, _options.StripePriceId, StringComparison.Ordinal)) { reason = "wrong-price"; return false; }
        if (session.PriceUnitAmountCents != 3000) { reason = "wrong-price-amount"; return false; }
        if (session.Quantity != 1) { reason = "wrong-quantity"; return false; }
        if (!string.Equals(session.Currency, "usd", StringComparison.Ordinal)
            || !string.Equals(session.Currency, _options.Currency, StringComparison.Ordinal)) { reason = "wrong-currency"; return false; }
        if (!session.Metadata.TryGetValue("product", out string? product)
            || !string.Equals(product, "relic-screenshot", StringComparison.Ordinal)) { reason = "wrong-product"; return false; }
        if (!session.Metadata.TryGetValue("offer", out string? offer)
            || !string.Equals(offer, $"v{_options.ApplicationMajor}", StringComparison.Ordinal)) { reason = "wrong-offer"; return false; }
        if (!session.Metadata.TryGetValue("edition", out string? edition)
            || !string.Equals(edition, _options.Edition, StringComparison.Ordinal)) { reason = "wrong-edition"; return false; }
        if (session.CreatedUnixSeconds <= 0) { reason = "missing-created-time"; return false; }
        reason = "";
        return true;
    }

    public LicenseOrder Fulfill(
        StripeSessionSnapshot session,
        DateTimeOffset paidAtUtc)
    {
        _options.RequireReady();
        if (!TryValidatePaidSession(session, out string reason))
            throw new InvalidOperationException($"Checkout Session is not fulfillable: {reason}.");
        if (paidAtUtc.ToUnixTimeSeconds() <= 0)
            throw new ArgumentOutOfRangeException(nameof(paidAtUtc));
        return _store.UpsertPaid(session, paidAtUtc, _options.ApplicationMajor, _options.Edition);
    }
}
