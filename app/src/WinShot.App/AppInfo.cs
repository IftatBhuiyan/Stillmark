namespace WinShot.App;

/// <summary>
/// Central product identity used by licensing, the (future) updater, and About UI.
/// Bump <see cref="Version"/> every release. Dated licenses use the immutable publication date;
/// major-version entitlement is retained only for legacy licenses.
/// </summary>
public static class AppInfo
{
    public const string Name = ProductIdentity.DisplayName;
    public const string PackageId = "RelicScreenshot";
    public const string CompanyName = "Iftat Bhuiyan";
    public const string ExpectedPublisherSubject = "CN=Iftat Bhuiyan, O=Iftat Bhuiyan, L=Buffalo, S=ny, C=US";
    public const string ExpectedArtifactSigningIdentityEku = "1.3.6.1.4.1.311.97.875364832.309588348.267714163.555564550";
    public const int MinimumStableLauncherSecurityEpoch = 1;

    /// <summary>Displayed build version. Keep in sync with the installer/release tag.</summary>
    public const string Version = "1.1.0";

    // Set once when this version is published, and never change it for that version.
    // An unscheduled engineering build must not pretend to have a customer release date.
    public const string ReleaseDateUtc = "";

    internal static DateTimeOffset? PublishedAtUtc =>
        DateTimeOffset.TryParseExact(
            ReleaseDateUtc,
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var published) ? published : null;

    public static string BuildDescription => PublishedAtUtc is null ? "Development build" : "Release";

    /// <summary>
    /// Entitlement major version used by license validation. Customer-facing update terms are
    /// configured separately and must not be inferred from this value.
    /// </summary>
    public const int Major = 1;
}
