namespace RelicCommerce;

public sealed record StripeSessionSnapshot(
    string Id,
    bool LiveMode,
    string Mode,
    string PaymentStatus,
    string Status,
    string PriceId,
    long Quantity,
    string Currency,
    string? PaymentIntentId,
    string? Email,
    string? Name,
    long CreatedUnixSeconds,
    IReadOnlyDictionary<string, string> Metadata,
    long? PriceUnitAmountCents);

public sealed record LicenseOrder(
    string SessionId,
    string LicenseId,
    DateTimeOffset PurchasedAtUtc,
    int ApplicationMajor,
    string Edition,
    string Email,
    string Name,
    string Status,
    string? PaymentIntentId,
    string? DeliveryTokenHash,
    DateTimeOffset? DeliveryTokenExpiresUtc)
{
    public DateTimeOffset UpdatesUntilUtc { get; init; } = PurchasedAtUtc.AddYears(1);
    public int SeatLimit { get; init; } = 1;
    public string NormalizedEmail => Email.Trim().ToLowerInvariant();
}
