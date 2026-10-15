using WinShot.Core.Startup;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class InstalledRecoveryRedirectPolicyTests
{
    private const string LocalRoot = @"C:\Users\Test\AppData\Local";
    private const string StableLauncher =
        LocalRoot + @"\PROVISIONAL.WinShot\WinShot.exe";
    private const string VersionedPayload =
        LocalRoot + @"\PROVISIONAL.WinShot\versions\1.0.0\WinShot.exe";
    private const string VelopackPayload =
        LocalRoot + @"\RelicScreenshot\current\WinShot.exe";

    [Fact]
    public void PublicRecovery_FromVersionedPayloadRedirectsToStableRoot()
    {
        InstalledRecoveryLaunchDecision decision =
            InstalledRecoveryRedirectPolicy.Evaluate(
                ["--tray", "--recovered"],
                VersionedPayload,
                LocalRoot,
                "PROVISIONAL.WinShot");

        Assert.Equal(
            InstalledRecoveryLaunchRoute.RedirectToStableLauncher,
            decision.Route);
        Assert.Equal(StableLauncher, decision.StableLauncherPath);
        Assert.False(decision.IsRecoveredStart);
    }

    [Fact]
    public void LauncherRecovery_FromVersionedPayloadContinuesWithoutLoop()
    {
        InstalledRecoveryLaunchDecision decision =
            InstalledRecoveryRedirectPolicy.Evaluate(
                ["--tray", "--launcher-recovered"],
                VersionedPayload,
                LocalRoot,
                "PROVISIONAL.WinShot");

        Assert.Equal(
            InstalledRecoveryLaunchRoute.ContinueRecovered,
            decision.Route);
        Assert.Equal(StableLauncher, decision.StableLauncherPath);
        Assert.True(decision.IsRecoveredStart);
    }

    [Fact]
    public void PublicRecovery_FromVelopackCurrentPayloadContinuesDirectly()
    {
        InstalledRecoveryLaunchDecision decision =
            InstalledRecoveryRedirectPolicy.Evaluate(
                ["--tray", "--recovered"],
                VelopackPayload,
                LocalRoot,
                "RelicScreenshot");

        Assert.Equal(
            InstalledRecoveryLaunchRoute.ContinueRecovered,
            decision.Route);
        Assert.Null(decision.StableLauncherPath);
        Assert.True(decision.IsRecoveredStart);
    }

    [Fact]
    public void PrivateLauncherRecovery_IsRejectedForVelopackCurrentPayload()
    {
        InstalledRecoveryLaunchDecision decision =
            InstalledRecoveryRedirectPolicy.Evaluate(
                ["--tray", "--launcher-recovered"],
                VelopackPayload,
                LocalRoot,
                "RelicScreenshot");

        Assert.Equal(InstalledRecoveryLaunchRoute.Reject, decision.Route);
        Assert.Null(decision.StableLauncherPath);
        Assert.False(decision.IsRecoveredStart);
    }

    [Fact]
    public void PublicRecovery_FromPortableBuildContinuesDirectly()
    {
        InstalledRecoveryLaunchDecision decision =
            InstalledRecoveryRedirectPolicy.Evaluate(
                ["--tray", "--recovered"],
                @"C:\Portable\WinShot.exe",
                LocalRoot,
                "PROVISIONAL.WinShot");

        Assert.Equal(
            InstalledRecoveryLaunchRoute.ContinueRecovered,
            decision.Route);
        Assert.Null(decision.StableLauncherPath);
        Assert.True(decision.IsRecoveredStart);
    }

    [Theory]
    [InlineData("--TRAY", "--recovered")]
    [InlineData("--tray", "--RECOVERED")]
    [InlineData("--tray", "--recovered", "--extra")]
    [InlineData("--launcher-recovered")]
    public void MalformedOrUnroutedRecoveryIntentIsRejected(
        params string[] arguments)
    {
        InstalledRecoveryLaunchDecision decision =
            InstalledRecoveryRedirectPolicy.Evaluate(
                arguments,
                VersionedPayload,
                LocalRoot,
                "PROVISIONAL.WinShot");

        Assert.Equal(InstalledRecoveryLaunchRoute.Reject, decision.Route);
        Assert.False(decision.IsRecoveredStart);
    }

    [Fact]
    public void PrivateMarker_IsRejectedOutsideInstalledPayload()
    {
        InstalledRecoveryLaunchDecision decision =
            InstalledRecoveryRedirectPolicy.Evaluate(
                ["--tray", "--launcher-recovered"],
                @"C:\Portable\WinShot.exe",
                LocalRoot,
                "PROVISIONAL.WinShot");

        Assert.Equal(InstalledRecoveryLaunchRoute.Reject, decision.Route);
    }

    [Fact]
    public void OrdinaryTrayStartIsNotRecovery()
    {
        InstalledRecoveryLaunchDecision decision =
            InstalledRecoveryRedirectPolicy.Evaluate(
                ["--tray"],
                VersionedPayload,
                LocalRoot,
                "PROVISIONAL.WinShot");

        Assert.Equal(InstalledRecoveryLaunchRoute.Normal, decision.Route);
        Assert.False(
            InstalledRecoveryRedirectPolicy.ContainsRecoveryIntent(["--tray"]));
    }

    [Fact]
    public void RecoveryShapeUnderAnotherPackageRootIsRejected()
    {
        InstalledRecoveryLaunchDecision decision =
            InstalledRecoveryRedirectPolicy.Evaluate(
                ["--tray", "--recovered"],
                LocalRoot + @"\Other.Package\versions\1.0.0\WinShot.exe",
                LocalRoot,
                "PROVISIONAL.WinShot");

        Assert.Equal(InstalledRecoveryLaunchRoute.Reject, decision.Route);
    }
}
