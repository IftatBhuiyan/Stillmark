namespace WinShot.Licensing;

/// <summary>
/// Selects one coherent entitlement contract. Offline-signed mode never phones home. Provider
/// mode requires a confirmed provider instance and permits only the explicitly configured grace
/// period between successful instance validations.
/// </summary>
public enum LicenseEnforcementMode
{
    ConfigurationInvalid,
    OfflineSigned,
    ProviderValidated,
}

public sealed class LicenseEnforcementOptions
{
    private static readonly Func<DateTimeOffset> UtcClock = () => DateTimeOffset.UtcNow;
    private const string PrivacySafeProviderInstanceName = "WinShot Windows device";

    private LicenseEnforcementOptions(
        LicenseEnforcementMode mode,
        ILicenseValidator? validator,
        LicenseProductIdentity? productIdentity,
        string? instanceName,
        TimeSpan offlineGracePeriod,
        Func<DateTimeOffset> now)
    {
        Mode = mode;
        Validator = validator;
        ProductIdentity = productIdentity;
        InstanceName = instanceName;
        OfflineGracePeriod = offlineGracePeriod;
        Now = now;
    }

    public LicenseEnforcementMode Mode { get; }
    public TimeSpan OfflineGracePeriod { get; }
    public string? InstanceName { get; }
    internal ILicenseValidator? Validator { get; }
    internal LicenseProductIdentity? ProductIdentity { get; }
    internal Func<DateTimeOffset> Now { get; }

    public static LicenseEnforcementOptions OfflineSigned { get; } =
        new(LicenseEnforcementMode.OfflineSigned, null, null, null, TimeSpan.Zero, UtcClock);

    public static LicenseEnforcementOptions InvalidConfiguration { get; } =
        new(LicenseEnforcementMode.ConfigurationInvalid, null, null, null, TimeSpan.Zero, UtcClock);

    public static LicenseEnforcementOptions RequireProvider(
        ILicenseValidator validator,
        LicenseProductIdentity productIdentity,
        TimeSpan offlineGracePeriod) =>
        RequireProvider(
            validator,
            productIdentity,
            PrivacySafeProviderInstanceName,
            offlineGracePeriod,
            UtcClock);

    internal static LicenseEnforcementOptions RequireProvider(
        ILicenseValidator validator,
        LicenseProductIdentity productIdentity,
        string instanceName,
        TimeSpan offlineGracePeriod,
        Func<DateTimeOffset> now)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(productIdentity);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        string normalizedName = instanceName.Trim();
        if (normalizedName.Length > 128)
            throw new ArgumentException("The provider device label cannot exceed 128 characters.", nameof(instanceName));
        if (offlineGracePeriod < TimeSpan.Zero || offlineGracePeriod > TimeSpan.FromDays(30))
            throw new ArgumentOutOfRangeException(nameof(offlineGracePeriod));

        return new LicenseEnforcementOptions(
            LicenseEnforcementMode.ProviderValidated,
            validator,
            productIdentity,
            normalizedName,
            offlineGracePeriod,
            now);
    }
}

public enum ProviderRefreshOutcome
{
    Confirmed,
    Rejected,
    Unreachable,
    ActivationRequired,
    NotConfigured,
    NoStoredLicense,
}

public sealed record ProviderRefreshResult(ProviderRefreshOutcome Outcome, string Message)
{
    public bool Confirmed => Outcome == ProviderRefreshOutcome.Confirmed;
}

public enum DeactivationOutcome
{
    Success,
    Rejected,
    Unreachable,
}

public sealed record DeactivationResult(DeactivationOutcome Outcome, string Message)
{
    public bool Succeeded => Outcome == DeactivationOutcome.Success;
}
