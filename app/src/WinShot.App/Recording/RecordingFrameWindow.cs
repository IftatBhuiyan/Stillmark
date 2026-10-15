using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Interop;

namespace WinShot.App.Recording;

/// <summary>
/// Click-through overlay covering the region's monitor while recording:
/// dims everything outside the recorded region (optional) and draws a subtle
/// light border just outside it — nothing of this appears in the video.
/// </summary>
public sealed class RecordingFrameWindow : Window
{
    private readonly PixelRect _region;
    private readonly MonitorInfo _monitor;

    public RecordingFrameWindow(PixelRect region, bool dimOutside)
    {
        _region = region;
        _monitor = MonitorService.GetMonitorAt(region.X + region.Width / 2, region.Y + region.Height / 2);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;

        Content = new FrameElement(_region, _monitor, dimOutside);

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                exStyle | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
            // WDA_MONITOR would black out this full-monitor transparent frame on older Windows.
            // Keep the geometric border/dim behavior as the fallback when full exclusion is absent.
            WindowCaptureExclusion.Apply(hwnd, allowMonitorFallback: false);
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                _monitor.Bounds.X, _monitor.Bounds.Y, _monitor.Bounds.Width, _monitor.Bounds.Height,
                NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
        };
    }

    private sealed class FrameElement : FrameworkElement
    {
        private static readonly Brush DimBrush = Make(new SolidColorBrush(Color.FromArgb(0x59, 0x00, 0x00, 0x00)));
        private static readonly Pen BorderPen = Make(new Pen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), 1.5));

        private readonly PixelRect _region;
        private readonly MonitorInfo _monitor;
        private readonly bool _dim;

        public FrameElement(PixelRect region, MonitorInfo monitor, bool dim)
        {
            _region = region;
            _monitor = monitor;
            _dim = dim;
            IsHitTestVisible = false;
        }

        private static T Make<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth, height = ActualHeight;
            if (width <= 0)
                return;
            var dpi = VisualTreeHelper.GetDpi(this);
            var hole = new Rect(
                (_region.X - _monitor.Bounds.X) / dpi.DpiScaleX,
                (_region.Y - _monitor.Bounds.Y) / dpi.DpiScaleY,
                _region.Width / dpi.DpiScaleX,
                _region.Height / dpi.DpiScaleY);

            if (_dim)
            {
                var mask = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new RectangleGeometry(new Rect(0, 0, width, height)),
                    new RectangleGeometry(hole));
                dc.DrawGeometry(DimBrush, null, mask);
            }

            var borderRect = hole;
            borderRect.Inflate(2.5 / dpi.DpiScaleX, 2.5 / dpi.DpiScaleY);
            dc.DrawRoundedRectangle(null, BorderPen, borderRect, 5, 5);
        }
    }
}
