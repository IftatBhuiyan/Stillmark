using WinShot.App;
using WinShot.Licensing;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class LocalDeveloperUsePolicyTests
{
    private static readonly LicenseStatus Expired =
        new(LicenseState.Expired, 0, null);

    private static readonly LicenseStatus Licensed =
        new(
            LicenseState.Licensed,
            0,
            new LicensePayload
            {
                Version = 1,
                Product = "winshot",
                Id = "synthetic-local-developer-policy",
                Major = 1,
                Edition = "Test",
                IssuedAt = DateTimeOffset.UnixEpoch.ToUnixTimeSeconds(),
            });

    [Fact]
    public void ExactBuildConfigurationControlsLocalDeveloperEntitlement()
    {
#if WINSHOT_LOCAL_DEVELOPER_USE_EXPECTED
        Assert.True(LocalDeveloperUsePolicy.IsEnabled);
#else
        Assert.False(LocalDeveloperUsePolicy.IsEnabled);
#endif
    }

    [Fact]
    public void LicensedCustomerUsageIsAlwaysAllowed()
    {
        Assert.True(LocalDeveloperUsePolicy.AllowsUsage(Licensed));
    }

    [Fact]
    public void ExpiredUsageMatchesCompileTimeDeveloperPolicy()
    {
        Assert.Equal(
            LocalDeveloperUsePolicy.IsEnabled,
            LocalDeveloperUsePolicy.AllowsUsage(Expired));
    }
}
