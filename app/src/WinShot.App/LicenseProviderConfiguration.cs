using WinShot.Licensing;
using WinShot.App.Commercial;

namespace WinShot.App;

/// <summary>
/// Credential-free licensing composition from the immutable commercial contract embedded in the
/// application. Provider IDs are public identifiers; private provider credentials never belong in
/// this configuration.
/// </summary>
internal static class LicenseProviderConfiguration
{
    internal static LicenseEnforcementOptions Create()
    {
        CommercialConfigurationLoadResult loaded =
            CommercialReleaseConfigurationLoader.Current;
        if (!loaded.IsValid)
            return LicenseEnforcementOptions.InvalidConfiguration;

        CommercialReleaseConfiguration configuration = loaded.Configuration!;
        return configuration.LicenseMode == CommercialLicenseMode.OfflineSigned
            && configuration.Provider.ValueKind == System.Text.Json.JsonValueKind.Null
                ? LicenseEnforcementOptions.OfflineSigned
                : LicenseEnforcementOptions.InvalidConfiguration;
    }
}
