using WinShot.App.Lifecycle;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class AppLaunchIntentPolicyTests
{
    [Fact]
    public void EmptyArguments_AreInteractiveAndShowExistingInstance()
    {
        AppLaunchIntent intent = AppLaunchIntentPolicy.Classify([]);

        Assert.Equal(AppLaunchIntent.Interactive, intent);
        Assert.True(AppLaunchIntentPolicy.ShouldShowExistingInstance(intent));
    }

    [Theory]
    [InlineData("--tray")]
    [InlineData("--TRAY")]
    public void ExactTrayArgument_IsQuietBackground(string argument)
    {
        AppLaunchIntent intent = AppLaunchIntentPolicy.Classify([argument]);

        Assert.Equal(AppLaunchIntent.Background, intent);
        Assert.False(AppLaunchIntentPolicy.ShouldShowExistingInstance(intent));
    }

    [Theory]
    [InlineData("--tray", "--recovered")]
    [InlineData("--TRAY", "--RECOVERED")]
    [InlineData("--tray", "--launcher-recovered")]
    public void ExactRecoveryShape_IsQuiet(
        string trayArgument,
        string recoveryArgument)
    {
        AppLaunchIntent intent = AppLaunchIntentPolicy.Classify(
            [trayArgument, recoveryArgument]);

        Assert.Equal(AppLaunchIntent.Recovery, intent);
        Assert.False(AppLaunchIntentPolicy.ShouldShowExistingInstance(intent));
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--tray", "--unknown")]
    [InlineData("--recovered", "--tray")]
    [InlineData("--tray", "--recovered", "--extra")]
    [InlineData(" --tray")]
    public void MalformedOrUnknownArguments_RemainInteractive(
        params string[] arguments)
    {
        AppLaunchIntent intent = AppLaunchIntentPolicy.Classify(arguments);

        Assert.Equal(AppLaunchIntent.Interactive, intent);
        Assert.True(AppLaunchIntentPolicy.ShouldShowExistingInstance(intent));
    }

    [Fact]
    public void NullArguments_AreRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => AppLaunchIntentPolicy.Classify(null!));
    }
}
