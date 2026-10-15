using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using WinShot.App.Overlays;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Interop;

namespace WinShot.App.Scrolling;

/// <summary>
/// Live preview shown beside the scrolling-capture region: just the stitched image, floating on
/// its own (no surrounding card). It scales to fit and grows taller/wider as the user scrolls.
/// The Cancel / Done controls live separately, under the capture area (see ScrollControlBar).
/// </summary>
public sealed class ScrollPreviewWindow : Window
{
    private const double ShadowMargin = 18;
    private const double PreviewW = 240;
    private const double PreviewMaxH = 460;

    private readonly PixelRect _region;
    private readonly Image _image;
    private readonly Border _frame;

    public ScrollPreviewWindow(PixelRect region)
    {
        _region = region;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        OverlayChrome.MakeCrisp(this);

        _image = new Image
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        // The preview IS the image on its own rounded surface — no outer container.
        _frame = new Border
        {
            Width = PreviewW,
            Height = 150,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x14, 0x14, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Direction = 270, Opacity = 0.55 },
            Child = _image,
        };

        Content = new Grid { Margin = new Thickness(ShadowMargin), Children = { _frame } };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_TOOLWINDOW);
            WindowCaptureExclusion.Apply(hwnd);
        };
        Loaded += (_, _) => Reposition();
    }

    public void UpdatePreview(BitmapSource stitched)
    {
        _image.Source = stitched;
        double aspectH = PreviewW * stitched.PixelHeight / stitched.PixelWidth;
        _frame.Height = Math.Min(PreviewMaxH, Math.Max(120, aspectH));
        Reposition();
    }

    private void Reposition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var monitor = MonitorService.GetMonitorAt(_region.X + _region.Width / 2, _region.Y + _region.Height / 2);
        double scale = monitor.ScaleFactor;
        UpdateLayout();
        int w = (int)Math.Round(ActualWidth * scale);
        int h = (int)Math.Round(ActualHeight * scale);
        if (w == 0)
        {
            w = (int)Math.Round((PreviewW + ShadowMargin * 2) * scale);
            h = (int)Math.Round(300 * scale);
        }

        // Prefer to the right of the region, else left, clamped on screen.
        int gap = (int)(10 * scale);
        int x = _region.Right + gap;
        if (x + w > monitor.WorkArea.Right)
            x = _region.X - gap - w;
        x = Math.Clamp(x, monitor.WorkArea.X, Math.Max(monitor.WorkArea.X, monitor.WorkArea.Right - w));
        int y = _region.Y + (_region.Height - h) / 2;
        y = Math.Clamp(y, monitor.WorkArea.Y, Math.Max(monitor.WorkArea.Y, monitor.WorkArea.Bottom - h));

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h,
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
    }
}
