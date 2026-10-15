using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WinShot.App.Overlays;
using WinShot.Capture;
using WinShot.Interop;

namespace WinShot.App;

/// <summary>Floating countdown pill for timed captures. Click anywhere on it to cancel.</summary>
public sealed class CountdownWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _number;
    private readonly ScaleTransform _pulse;
    private readonly MonitorInfo _monitor;
    private readonly Action _onFinished;
    private readonly Action _onCancelled;
    private int _remaining;
    private bool _done;

    public CountdownWindow(int seconds, MonitorInfo monitor, Action onFinished, Action onCancelled)
    {
        _remaining = seconds;
        _monitor = monitor;
        _onFinished = onFinished;
        _onCancelled = onCancelled;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Cursor = Cursors.Hand;
        OverlayChrome.MakeCrisp(this);

        _pulse = new ScaleTransform(1, 1);
        _number = new TextBlock
        {
            Text = seconds.ToString(),
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 36,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF1, 0xF2)),
            HorizontalAlignment = HorizontalAlignment.Center,
            RenderTransform = _pulse,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        var caption = new TextBlock
        {
            Text = "Full screen capture · click to cancel",
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xA6, 0xB2)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var pill = OverlayChrome.ShadowedCard(
            new StackPanel { Children = { _number, caption } }, cornerRadius: 16,
            background: new SolidColorBrush(Color.FromArgb(0xF2, 0x20, 0x22, 0x24)),
            border: new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            padding: new Thickness(26, 14, 26, 12),
            blur: 18, depth: 4, shadowOpacity: 0.5);

        Content = new Grid { Margin = new Thickness(16), Children = { pill } };
        MouseLeftButtonUp += (_, _) => CancelCountdown();

        Loaded += (_, _) => PositionOnMonitor();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Pulse();
    }

    private void PositionOnMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        double scale = _monitor.ScaleFactor;
        int width = (int)Math.Round(ActualWidth * scale);
        int height = (int)Math.Round(ActualHeight * scale);
        int x = _monitor.Bounds.X + (_monitor.Bounds.Width - width) / 2;
        int y = _monitor.Bounds.Bottom - height - (int)Math.Round(48 * scale);
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, width, height,
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
    }

    private void Tick()
    {
        _remaining--;
        if (_remaining <= 0)
        {
            _timer.Stop();
            _done = true;
            Close();
            _onFinished();
            return;
        }
        _number.Text = _remaining.ToString();
        Pulse();
    }

    private void Pulse()
    {
        if (!OverlayChrome.AnimationsEnabled)
            return;
        var anim = new DoubleAnimation(1.28, 1.0, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        _pulse.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _pulse.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    public void CancelCountdown()
    {
        if (_done)
            return;
        _done = true;
        _timer.Stop();
        Close();
        _onCancelled();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _timer.Stop();
    }
}
