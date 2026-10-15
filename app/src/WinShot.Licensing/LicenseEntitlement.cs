namespace WinShot.Licensing;

/// <summary>
/// A dated license grants permanent use of releases published before its update cutoff.
/// Neither the installation date nor the customer's current clock changes that right.
/// Format 1 keys without a cutoff retain their original major-version entitlement.
/// </summary>
public static class LicenseEntitlement
{
    internal const long MaximumUnixSeconds = 253_402_300_799;

    public static bool IsValid(LicensePayload payload) =>
        payload.Version is 1 or 2 or 3
        && !string.IsNullOrWhiteSpace(payload.Id)
        && payload.Major > 0
        && (payload.Version == 1 && payload.UpdatesUntil == 0
            || payload.IssuedAt is > 0 and <= MaximumUnixSeconds
                && payload.UpdatesUntil > payload.IssuedAt
                && payload.UpdatesUntil <= MaximumUnixSeconds)
        && (payload.Version != 3 || IsValidDeviceId(payload.DeviceId)
            && Guid.TryParseExact(payload.ActivationId, "D", out _)
            && payload.LeaseIssuedAt >= payload.IssuedAt
            && payload.ValidUntil > payload.LeaseIssuedAt
            && payload.ValidUntil <= MaximumUnixSeconds
            && payload.ValidUntil - payload.LeaseIssuedAt <= 30 * 24 * 60 * 60);

    public static bool IsValidDeviceId(string? deviceId) => deviceId is { Length: 64 }
        && deviceId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool MatchesDevice(LicensePayload payload, string? deviceId) =>
        payload.Version != 3 || IsValidDeviceId(deviceId)
            && string.Equals(payload.DeviceId, deviceId, StringComparison.Ordinal);

    public static bool HasCurrentActivation(LicensePayload payload, DateTimeOffset now) =>
        payload.Version != 3 || IsValid(payload)
            // Allow normal clock skew; a clock far behind the signed server time must reconnect.
            && now.ToUnixTimeSeconds() >= payload.LeaseIssuedAt - 300
            && now.ToUnixTimeSeconds() < payload.ValidUntil;

    public static bool CoversRelease(
        LicensePayload payload,
        int releaseMajor,
        DateTimeOffset? releasedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!IsValid(payload) || releaseMajor <= 0)
            return false;

        if (payload.UpdatesUntil == 0)
            return releaseMajor <= payload.Major;

        // The cutoff is exclusive: the purchase includes one complete calendar year.
        // Missing release metadata must never silently grant all future updates.
        return releasedAtUtc is { } released
            && released.ToUnixTimeSeconds() > 0
            && released < DateTimeOffset.FromUnixTimeSeconds(payload.UpdatesUntil);
    }

    public static LicensePayload CreatePurchase(
        string id,
        DateTimeOffset purchasedAtUtc,
        int purchasedMajor = 1,
        string edition = "pro")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(edition);
        if (purchasedMajor <= 0)
            throw new ArgumentOutOfRangeException(nameof(purchasedMajor));
        var purchased = purchasedAtUtc.ToUniversalTime();
        if (purchased.ToUnixTimeSeconds() <= 0 || purchased.Year >= 9999)
            throw new ArgumentOutOfRangeException(nameof(purchasedAtUtc));

        return new LicensePayload
        {
            Version = 2,
            Id = id,
            Major = purchasedMajor,
            Edition = edition,
            IssuedAt = purchased.ToUnixTimeSeconds(),
            UpdatesUntil = purchased.AddYears(1).ToUnixTimeSeconds(),
        };
    }
}
