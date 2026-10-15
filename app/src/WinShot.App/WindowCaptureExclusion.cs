using System.Runtime.InteropServices;
using System.Threading;
using WinShot.Interop;

namespace WinShot.App;

internal enum WindowCaptureExclusionResult
{
    Unavailable,
    MonitorFallback,
    Excluded,
}

/// <summary>
/// Applies Windows' best available capture protection to a WinShot-owned top-level window.
/// This is defense in depth: callers must retain their geometric outside-capture placement.
/// </summary>
internal static class WindowCaptureExclusion
{
    private static int _degradationLogged;

    internal static WindowCaptureExclusionResult Apply(IntPtr hwnd, bool allowMonitorFallback = true)
    {
        if (hwnd == IntPtr.Zero)
        {
            LogDegradation("Capture exclusion could not be applied because the window handle was unavailable.");
            return WindowCaptureExclusionResult.Unavailable;
        }

        int excludeError = 0;
        int fallbackError = 0;

        bool SetAffinity(uint affinity)
        {
            bool applied = NativeMethods.SetWindowDisplayAffinity(hwnd, affinity);
            if (!applied)
            {
                int error = Marshal.GetLastPInvokeError();
                if (affinity == NativeMethods.WDA_EXCLUDEFROMCAPTURE)
                    excludeError = error;
                else
                    fallbackError = error;
            }
            return applied;
        }

        bool supportsFullExclusion = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
        var result = TryApply(supportsFullExclusion, allowMonitorFallback, SetAffinity);

        if (result == WindowCaptureExclusionResult.MonitorFallback && supportsFullExclusion)
        {
            LogDegradation(
                $"Full capture exclusion was unavailable; legacy monitor affinity is active (Win32 error {excludeError}).");
        }
        else if (result == WindowCaptureExclusionResult.Unavailable)
        {
            string details = supportsFullExclusion
                ? $"Win32 errors {excludeError}/{fallbackError}"
                : "this Windows build does not support full exclusion";
            LogDegradation($"Capture exclusion was unavailable ({details}).");
        }

        return result;
    }

    internal static WindowCaptureExclusionResult TryApply(
        bool supportsFullExclusion,
        bool allowMonitorFallback,
        Func<uint, bool> setAffinity)
    {
        ArgumentNullException.ThrowIfNull(setAffinity);

        if (supportsFullExclusion && setAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE))
            return WindowCaptureExclusionResult.Excluded;

        if (allowMonitorFallback && setAffinity(NativeMethods.WDA_MONITOR))
            return WindowCaptureExclusionResult.MonitorFallback;

        return WindowCaptureExclusionResult.Unavailable;
    }

    private static void LogDegradation(string message)
    {
        if (Interlocked.Exchange(ref _degradationLogged, 1) == 0)
            App.LogError(new InvalidOperationException(message));
    }
}
