using WinShot.App.Lifecycle;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class ApplicationServicingPolicyTests
{
    [Fact]
    public void UnpackagedDeployment_UsesVerifiedHostServicing()
    {
        ApplicationServicingDecision decision =
            ApplicationServicingPolicy.Evaluate(
                ApplicationDeploymentKind.Unpackaged);

        Assert.Equal(
            ApplicationServicingOwner.UnpackagedHost,
            decision.Owner);
        Assert.True(decision.AllowCustomUpdateHandoff);
        Assert.True(decision.AllowStableLauncherRouting);
    }

    [Fact]
    public void PackagedDeployment_UsesWindowsAndDeniesCustomMutation()
    {
        ApplicationServicingDecision decision =
            ApplicationServicingPolicy.Evaluate(
                ApplicationDeploymentKind.Packaged);

        Assert.Equal(
            ApplicationServicingOwner.WindowsPackagePlatform,
            decision.Owner);
        Assert.False(decision.AllowCustomUpdateHandoff);
        Assert.False(decision.AllowStableLauncherRouting);
    }

    [Theory]
    [InlineData((int)ApplicationDeploymentKind.Unknown)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void UnknownOrInvalidDeployment_DeniesEveryServicingPath(
        int deploymentKindValue)
    {
        ApplicationServicingDecision decision =
            ApplicationServicingPolicy.Evaluate(
                (ApplicationDeploymentKind)deploymentKindValue);

        Assert.Equal(ApplicationServicingOwner.None, decision.Owner);
        Assert.False(decision.AllowCustomUpdateHandoff);
        Assert.False(decision.AllowStableLauncherRouting);
    }
}
