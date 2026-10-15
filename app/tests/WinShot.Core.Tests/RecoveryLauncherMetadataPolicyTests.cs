using WinShot.App.Lifecycle;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class RecoveryLauncherMetadataPolicyTests
{
    [Fact]
    public void ExactProductReleaseAndMinimumEpochAreAllowed()
    {
        Assert.True(
            RecoveryLauncherMetadataPolicy.IsAllowed(
                new RecoveryLauncherMetadataEvidence(
                    "WinShot",
                    "Iftat Bhuiyan",
                    1,
                    0,
                    0,
                    1),
                "WinShot",
                "Iftat Bhuiyan",
                new Version(1, 0, 0),
                1));
    }

    [Theory]
    [InlineData("Other", "Iftat Bhuiyan", 1, 0, 0, 1)]
    [InlineData("WinShot", "Other", 1, 0, 0, 1)]
    [InlineData("WinShot", "Iftat Bhuiyan", 0, 9, 9, 99)]
    [InlineData("WinShot", "Iftat Bhuiyan", 1, 0, 0, 0)]
    public void WrongIdentityReleaseOrOlderSecurityEpochIsRejected(
        string product,
        string company,
        int major,
        int minor,
        int build,
        int securityEpoch)
    {
        Assert.False(
            RecoveryLauncherMetadataPolicy.IsAllowed(
                new RecoveryLauncherMetadataEvidence(
                    product,
                    company,
                    major,
                    minor,
                    build,
                    securityEpoch),
                "WinShot",
                "Iftat Bhuiyan",
                new Version(1, 0, 0),
                1));
    }
}
