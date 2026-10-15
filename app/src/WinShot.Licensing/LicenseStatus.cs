namespace WinShot.Licensing;

public enum LicenseState
{
    /// <summary>Within the free trial window; full features.</summary>
    Trial,

    /// <summary>Trial elapsed and no valid license; app is gated.</summary>
    Expired,

    /// <summary>A valid license key is active.</summary>
    Licensed,
}

public enum LicenseAccessIssue
{
    None,
    ReleaseNotCovered,
    ReleaseDateMissing,
    DifferentComputer,
    ActivationCheckRequired,
}

/// <summary>
/// Provider entitlement derived only from the application's compile-time variant mapping. It does
/// not contain provider-supplied names, customer data, or an invented offline license payload.
/// </summary>
public sealed record ProviderLicenseEntitlement(long VariantId, int Major, string Edition);

/// <summary>Snapshot of the current entitlement. Immutable; produced by <see cref="LicenseService"/>.</summary>
public sealed record LicenseStatus(
    LicenseState State,
    int TrialDaysLeft,
    LicensePayload? License,
    ProviderLicenseEntitlement? ProviderEntitlement = null,
    LicenseAccessIssue AccessIssue = LicenseAccessIssue.None,
    TimeSpan TrialRemaining = default)
{
    /// <summary>True when the app may be used (trial or licensed).</summary>
    public bool IsActive => State is LicenseState.Trial or LicenseState.Licensed;

    /// <summary>Legacy entitlement major, or the major purchased for a dated license. Dated
    /// update eligibility must use the release timestamp instead of this number.</summary>
    public int EntitledMajor => License?.Major ?? ProviderEntitlement?.Major ?? 0;

    public string? Edition => License?.Edition ?? ProviderEntitlement?.Edition;
}
