using Microsoft.Win32;
using WinShot.App;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class AppSessionPolicyTests
{
    [Theory]
    [InlineData(PowerModes.Suspend, true)]
    [InlineData(PowerModes.Resume, false)]
    [InlineData(PowerModes.StatusChange, false)]
    public void PowerTransition_InterruptsOnlyBeforeSuspend(PowerModes mode, bool expected) =>
        Assert.Equal(expected, AppSessionPolicy.ShouldInterruptActiveWork(mode));

    [Theory]
    [InlineData(SessionSwitchReason.SessionLock, true)]
    [InlineData(SessionSwitchReason.SessionLogoff, true)]
    [InlineData(SessionSwitchReason.ConsoleDisconnect, true)]
    [InlineData(SessionSwitchReason.RemoteDisconnect, true)]
    [InlineData(SessionSwitchReason.SessionUnlock, false)]
    [InlineData(SessionSwitchReason.ConsoleConnect, false)]
    [InlineData(SessionSwitchReason.RemoteConnect, false)]
    [InlineData(SessionSwitchReason.SessionRemoteControl, false)]
    public void SessionTransition_InterruptsOnlyWhenCaptureShouldNotContinue(
        SessionSwitchReason reason,
        bool expected) =>
        Assert.Equal(expected, AppSessionPolicy.ShouldInterruptActiveWork(reason));

    [Theory]
    [InlineData(SessionSwitchReason.SessionUnlock, true)]
    [InlineData(SessionSwitchReason.ConsoleConnect, true)]
    [InlineData(SessionSwitchReason.RemoteConnect, true)]
    [InlineData(SessionSwitchReason.SessionLock, false)]
    [InlineData(SessionSwitchReason.ConsoleDisconnect, false)]
    [InlineData(SessionSwitchReason.RemoteDisconnect, false)]
    public void SessionTransition_RecoversHotkeysOnlyAfterUsableSessionReturns(
        SessionSwitchReason reason,
        bool expected) =>
        Assert.Equal(expected, AppSessionPolicy.ShouldRecoverHotkeys(reason));
}
