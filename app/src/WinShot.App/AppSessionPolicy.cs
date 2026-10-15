using Microsoft.Win32;

namespace WinShot.App;

/// <summary>
/// Classifies Windows power and session transitions without performing UI or native work.
/// Keeping the policy deterministic makes the privacy and data-preservation behavior testable.
/// </summary>
internal static class AppSessionPolicy
{
    internal static bool ShouldInterruptActiveWork(PowerModes mode) =>
        mode == PowerModes.Suspend;

    internal static bool ShouldInterruptActiveWork(SessionSwitchReason reason) =>
        reason is SessionSwitchReason.SessionLock
            or SessionSwitchReason.SessionLogoff
            or SessionSwitchReason.ConsoleDisconnect
            or SessionSwitchReason.RemoteDisconnect;

    internal static bool ShouldRecoverHotkeys(SessionSwitchReason reason) =>
        reason is SessionSwitchReason.SessionUnlock
            or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.RemoteConnect;
}
