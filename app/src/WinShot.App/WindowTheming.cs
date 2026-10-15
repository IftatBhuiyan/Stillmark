using System.Windows;
using System.Windows.Interop;
using WinShot.Interop;

namespace WinShot.App;

public static class WindowTheming
{
    /// <summary>Updates the native title bar to match the current app appearance.</summary>
    public static void ApplyTitleBar(Window window)
    {
        void Apply()
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;
            bool dark = ApplicationTheme.Current == Core.Settings.AppearanceKind.Dark;
            int enabled = dark ? 1 : 0;
            _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref enabled, sizeof(int));
            int captionColor = dark ? ToColorRef(0x20, 0x22, 0x24) : ToColorRef(0xF7, 0xF7, 0xF9);
            int textColor = dark ? ToColorRef(0xF2, 0xF2, 0xF6) : ToColorRef(0x18, 0x18, 0x1D);
            _ = NativeMethods.DwmSetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_CAPTION_COLOR,
                ref captionColor,
                sizeof(int));
            _ = NativeMethods.DwmSetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_TEXT_COLOR,
                ref textColor,
                sizeof(int));
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Apply();
        else
            window.SourceInitialized += (_, _) => Apply();
    }

    /// <summary>Compatibility entry point for existing windows; now follows the active theme.</summary>
    public static void ApplyDarkTitleBar(Window window) => ApplyTitleBar(window);

    private static int ToColorRef(byte red, byte green, byte blue) =>
        red | (green << 8) | (blue << 16);
}
