using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinShot.App.Overlays;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Core.Hotkeys;
using WinShot.Interop;

namespace WinShot.App.Recording;

/// <summary>
/// Shows pressed keys in a pill at the bottom-center of the recorded region —
/// intentionally INSIDE the region so keystrokes appear in the video.
/// Uses a low-level keyboard hook for the recording's duration only.
/// </summary>
public sealed class KeystrokeOverlayWindow : Window
{
    private readonly PixelRect _region;
    private readonly TextBlock _text;
    private readonly DispatcherTimer _fade;
    private NativeMethods.LowLevelKeyboardProc? _hookProc; // kept alive for the hook's lifetime
    private IntPtr _hook;

    public KeystrokeOverlayWindow(PixelRect region)
    {
        _region = region;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;
        Width = 340;
        Height = 52;
        Opacity = 0;
        OverlayChrome.MakeCrisp(this);

        _text = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Content = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x1A, 0x1A, 0x1F)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 8, 18, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _text,
        };

        _fade = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        _fade.Tick += (_, _) =>
        {
            _fade.Stop();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                exStyle | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);

            var monitor = MonitorService.GetMonitorAt(_region.X + _region.Width / 2, _region.Y + _region.Height / 2);
            double scale = monitor.ScaleFactor;
            int width = (int)Math.Round(Width * scale);
            int height = (int)Math.Round(Height * scale);
            int x = _region.X + (_region.Width - width) / 2;
            int y = _region.Bottom - height - (int)(14 * scale);
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, width, height,
                NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);

            InstallHook();
        };
    }

    private void InstallHook()
    {
        _hookProc = HookProc;
        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL, _hookProc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            App.LogError(new InvalidOperationException(
                $"SetWindowsHookEx (keystrokes) failed, error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}"));
            _hookProc = null;
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && ((int)wParam == NativeMethods.WM_KEYDOWN || (int)wParam == NativeMethods.WM_SYSKEYDOWN))
        {
            uint vk = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(lParam);
            Dispatcher.BeginInvoke(() => ShowKey(vk));
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void ShowKey(uint vk)
    {
        // Pure modifier presses wait for the real key.
        if (vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C)
            return;

        var parts = new List<string>(4);
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0) parts.Add("Ctrl");
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0) parts.Add("Shift");
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0) parts.Add("Alt");
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0 ||
            (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0) parts.Add("Win");

        parts.Add(HotkeyGesture.TryGetKeyName(vk, out string name) ? name : KeyNameFallback(vk));
        _text.Text = string.Join(" + ", parts);

        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(90)));
        _fade.Stop();
        _fade.Start();
    }

    private static string KeyNameFallback(uint vk) => vk switch
    {
        0x0D => "Enter",
        0x1B => "Esc",
        0x08 => "Backspace",
        0x09 => "Tab",
        0xBC => ",",
        0xBE => ".",
        0xBD => "-",
        0xBB => "=",
        _ => ((System.Windows.Input.Key)System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)vk)).ToString(),
    };

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _fade.Stop();
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _hookProc = null;
    }
}
