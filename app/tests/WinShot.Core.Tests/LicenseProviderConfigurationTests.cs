using WinShot.App;
using WinShot.Licensing;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class LicenseProviderConfigurationTests
{
    [Fact]
    public void Pre_release_configuration_remains_offline_until_owner_ids_are_approved()
    {
        LicenseEnforcementOptions options = LicenseProviderConfiguration.Create();

        Assert.Equal(LicenseEnforcementMode.OfflineSigned, options.Mode);
    }
}
