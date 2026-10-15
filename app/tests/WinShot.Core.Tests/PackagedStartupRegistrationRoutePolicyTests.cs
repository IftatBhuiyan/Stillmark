using WinShot.App.Lifecycle;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class PackagedStartupRegistrationRoutePolicyTests
{
    [Theory]
    [InlineData(
        (int)ApplicationDeploymentKind.Unpackaged,
        (int)StartupRegistrationRoute.UnpackagedRunValue)]
    [InlineData(
        (int)ApplicationDeploymentKind.Packaged,
        (int)StartupRegistrationRoute.PackagedStartupTask)]
    [InlineData(
        (int)ApplicationDeploymentKind.Unknown,
        (int)StartupRegistrationRoute.Unavailable)]
    [InlineData(int.MinValue, (int)StartupRegistrationRoute.Unavailable)]
    [InlineData(int.MaxValue, (int)StartupRegistrationRoute.Unavailable)]
    public void DeploymentSelectsExactlyOneStartupAuthority(
        int deploymentKindValue,
        int expectedRouteValue)
    {
        StartupRegistrationRoute route =
            PackagedStartupRegistrationRoutePolicy.Evaluate(
                (ApplicationDeploymentKind)deploymentKindValue);

        Assert.Equal((StartupRegistrationRoute)expectedRouteValue, route);
    }
}
