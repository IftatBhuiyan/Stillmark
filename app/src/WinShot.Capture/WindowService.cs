using System.Runtime.InteropServices;
using System.Text;
using WinShot.Core;
using WinShot.Interop;

namespace WinShot.Capture;

/// <summary>A visible top-level window, with DWM extended-frame bounds in physical pixels.</summary>
public sealed record WindowInfo(IntPtr Hwnd, string Title, PixelRect Bounds);

/// <summary>What the user selected in the overlay: a free area, or a specific window.</summary>
public sealed record SelectionResult(PixelRect Rect, WindowInfo? Window);

public static class WindowService
{
    /// <summary>
    /// Visible, non-cloaked top-level windows in z-order (topmost first),
    /// excluding this process, tool windows, and the desktop shell.
    /// </summary>
    public static IReadOnlyList<WindowInfo> GetTopLevelWindows(PixelRect virtualBounds)
    {
        uint ownPid = (uint)Environment.ProcessId;
        var windows = new List<WindowInfo>();
        var buffer = new StringBuilder(512);

        NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
                return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == ownPid)
                return true;

            if ((NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOOLWINDOW) != 0)
                return true;

            // Cloaked = invisible UWP/backgrounded windows that still report as "visible".
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                && cloaked != 0)
                return true;

            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                    out RECT frame, Marshal.SizeOf<RECT>()) != 0)
                return true;

            var bounds = PixelRect.FromLtrb(frame.Left, frame.Top, frame.Right, frame.Bottom).Intersect(virtualBounds);
            if (bounds.IsEmpty || bounds.Width < 12 || bounds.Height < 12)
                return true;

            buffer.Clear();
            NativeMethods.GetClassName(hwnd, buffer, buffer.Capacity);
            string className = buffer.ToString();
            if (className is "Progman" or "WorkerW") // desktop wallpaper hosts
                return true;

            buffer.Clear();
            NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity);
            windows.Add(new WindowInfo(hwnd, buffer.ToString(), bounds));
            return true;
        };

        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return windows;
    }

    /// <summary>Topmost window under the point, honoring z-order.</summary>
    public static WindowInfo? HitTest(IReadOnlyList<WindowInfo> windows, int px, int py) =>
        windows.FirstOrDefault(w => w.Bounds.Contains(px, py));
}
