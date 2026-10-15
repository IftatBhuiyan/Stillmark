using WinShot.Interop;

namespace WinShot.Capture;

/// <summary>Toggles desktop icon visibility for clean shots (same trick Explorer's own context menu uses).</summary>
public static class DesktopIconsService
{
    private static IntPtr FindShellView()
    {
        IntPtr progman = NativeMethods.FindWindow("Progman", null);
        IntPtr view = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (view != IntPtr.Zero)
            return view;

        // With wallpaper slideshow/active desktop, the shell view lives under a WorkerW.
        IntPtr worker = IntPtr.Zero;
        while ((worker = NativeMethods.FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
        {
            view = NativeMethods.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view != IntPtr.Zero)
                return view;
        }
        return IntPtr.Zero;
    }

    public static bool AreIconsVisible()
    {
        IntPtr view = FindShellView();
        if (view == IntPtr.Zero)
            return true;
        IntPtr listView = NativeMethods.FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
        return listView == IntPtr.Zero || NativeMethods.IsWindowVisible(listView);
    }

    public static void Toggle()
    {
        IntPtr view = FindShellView();
        if (view != IntPtr.Zero)
            NativeMethods.SendMessage(view, NativeMethods.WM_COMMAND,
                new IntPtr(NativeMethods.SHELL_TOGGLE_DESKTOP_ICONS), IntPtr.Zero);
    }
}
