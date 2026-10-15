using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Interop;

namespace WinShot.App.Overlays;

/// <summary>Private, non-activating progress feedback for the current local OCR operation.</summary>
public sealed class OcrProgressWindow : Window
{
    private const double WindowWidth = 250;
    private const double WindowHeight = 82;
    private const double ShadowMargin = 12;

    private static readonly FontFamily FluentFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private readonly PixelRect _sourceRect;

    internal TextBlock StatusText { get; }
    internal ProgressBar ProgressIndicator { get; }

    public OcrProgressWindow(PixelRect sourceRect)
    {
        _sourceRect = sourceRect;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = WindowWidth;
        Height = WindowHeight;
        Title = "Stillmark Text Recognition";
        AutomationProperties.SetName(this, "Text recognition in progress");
        AutomationProperties.SetHelpText(
            this,
            "Stillmark is recognizing text locally from the selected area.");
        OverlayChrome.MakeCrisp(this);

        var glyph = new TextBlock
        {
            Text = "",
            FontFamily = FluentFont,
            FontSize = 18,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x7C, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        AutomationProperties.SetName(glyph, string.Empty);

        StatusText = new TextBlock
        {
            Text = "Recognizing text…",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
        };
        AutomationProperties.SetAutomationId(StatusText, "OcrProgressStatus");
        AutomationProperties.SetName(StatusText, "Recognizing text");
        AutomationProperties.SetLiveSetting(StatusText, AutomationLiveSetting.Polite);

        ProgressIndicator = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 3,
            Margin = new Thickness(0, 9, 0, 0),
        };
        AutomationProperties.SetName(ProgressIndicator, "Text recognition progress");
        AutomationProperties.SetHelpText(
            ProgressIndicator,
            "Text recognition is in progress. Windows does not provide a percentage.");

        var status = new StackPanel();
        status.Children.Add(StatusText);
        status.Children.Add(ProgressIndicator);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(glyph);
        row.Children.Add(new Border { Width = 172, Child = status });

        var card = OverlayChrome.ShadowedCard(
            row,
            cornerRadius: 12,
            background: new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x28)),
            border: new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
            padding: new Thickness(16, 12, 16, 12),
            blur: 18,
            depth: 4,
            shadowOpacity: 0.5);
        Content = new Grid
        {
            Margin = new Thickness(ShadowMargin),
            Children = { card },
        };

        SourceInitialized += (_, _) => ConfigureNativeWindow();
        Loaded += (_, _) =>
        {
            Reposition();
            RaiseProgressAnnouncement();
        };
    }

    private void ConfigureNativeWindow()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(
            hwnd,
            NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
        WindowCaptureExclusion.Apply(hwnd);
    }

    private void Reposition()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        MonitorInfo monitor = MonitorService.GetMonitorAt(
            _sourceRect.X + _sourceRect.Width / 2,
            _sourceRect.Y + _sourceRect.Height / 2);
        int width = (int)Math.Round(WindowWidth * monitor.ScaleFactor);
        int height = (int)Math.Round(WindowHeight * monitor.ScaleFactor);
        int gap = (int)Math.Round(10 * monitor.ScaleFactor);
        PixelRect placement = CalculatePlacement(
            _sourceRect,
            monitor.WorkArea,
            width,
            height,
            gap);

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            placement.X,
            placement.Y,
            placement.Width,
            placement.Height,
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
    }

    internal static PixelRect CalculatePlacement(
        PixelRect sourceRect,
        PixelRect workArea,
        int width,
        int height,
        int gap)
    {
        int x = sourceRect.X + (sourceRect.Width - width) / 2;
        x = Math.Clamp(x, workArea.X, Math.Max(workArea.X, workArea.Right - width));

        int below = sourceRect.Bottom + gap;
        int above = sourceRect.Y - height - gap;
        int y = below + height <= workArea.Bottom
            ? below
            : above >= workArea.Y
                ? above
                : Math.Clamp(
                    sourceRect.Bottom - height - gap,
                    workArea.Y,
                    Math.Max(workArea.Y, workArea.Bottom - height));

        return new PixelRect(x, y, width, height);
    }

    private void RaiseProgressAnnouncement()
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            return;

        AutomationPeer? peer =
            UIElementAutomationPeer.FromElement(StatusText)
            ?? UIElementAutomationPeer.CreatePeerForElement(StatusText);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
