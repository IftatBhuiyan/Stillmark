using WinShot.Core;
using WinShot.Interop;

namespace WinShot.Capture;

/// <summary>A display monitor with physical-pixel bounds in virtual-screen coordinates.</summary>
public sealed record MonitorInfo(
    string DeviceName,
    PixelRect Bounds,
    PixelRect WorkArea,
    bool IsPrimary,
    uint DpiX,
    uint DpiY)
{
    public double ScaleFactor => DpiX / 96.0;
}

public static class MonitorService
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        NativeMethods.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                uint dpiX = 96, dpiY = 96;
                _ = NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
                monitors.Add(new MonitorInfo(
                    info.szDevice,
                    ToPixelRect(info.rcMonitor),
                    ToPixelRect(info.rcWork),
                    (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                    dpiX,
                    dpiY));
            }
            return true;
        };
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return MonitorSelectionPolicy.EnsureAvailable(monitors, GetVirtualScreenBounds());
    }

    public static MonitorInfo GetMonitorAt(int px, int py)
    {
        var monitors = GetMonitors();
        return MonitorSelectionPolicy.Select(monitors, px, py);
    }

    public static PixelRect GetVirtualScreenBounds() => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

    public static (int X, int Y) GetCursorPosition()
    {
        NativeMethods.GetCursorPos(out POINT pt);
        return (pt.X, pt.Y);
    }

    private static PixelRect ToPixelRect(RECT r) => PixelRect.FromLtrb(r.Left, r.Top, r.Right, r.Bottom);
}

internal static class MonitorSelectionPolicy
{
    internal static IReadOnlyList<MonitorInfo> EnsureAvailable(
        IReadOnlyList<MonitorInfo> monitors,
        PixelRect virtualBounds)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count > 0)
            return monitors;

        var safeBounds = new PixelRect(
            virtualBounds.X,
            virtualBounds.Y,
            Math.Max(1, virtualBounds.Width),
            Math.Max(1, virtualBounds.Height));
        return
        [
            new MonitorInfo(
                "DISPLAY",
                safeBounds,
                safeBounds,
                IsPrimary: true,
                DpiX: 96,
                DpiY: 96),
        ];
    }

    internal static MonitorInfo Select(
        IReadOnlyList<MonitorInfo> monitors,
        int px,
        int py)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
            throw new ArgumentException("A normalized monitor inventory cannot be empty.", nameof(monitors));

        return monitors.FirstOrDefault(m => m.Bounds.Contains(px, py))
            ?? monitors.FirstOrDefault(m => m.IsPrimary)
            ?? monitors[0];
    }
}
