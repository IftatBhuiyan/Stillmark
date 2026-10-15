using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinShot.App.Overlays;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Interop;

namespace WinShot.App.Scrolling;

/// <summary>
/// Control bar for a scrolling capture, centered just under the capture area: a scroll hint,
/// the running dimensions, and Cancel / Done. Kept separate from the preview so the actions
/// sit beneath the region the user is capturing rather than beside the preview image.
/// </summary>
public sealed class ScrollControlBar : Window
{
    private const double ShadowMargin = 14;

    private static readonly FontFamily FluentFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly PixelRect _region;
    private readonly TextBlock _dims;
    private readonly Button _cancelButton;
    private readonly Button _doneButton;
    private readonly List<ButtonBase> _actions = new();
    private int _lastWidth;
    private int _lastHeight;
    private int _lastAnnouncedHeight;
    private bool _actionInvoked;

    public ScrollControlBar(PixelRect region, Action onDone, Action onCancel)
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
        Title = "Stillmark Scrolling Capture Controls";
        AutomationProperties.SetName(this, "Scrolling capture controls");
        AutomationProperties.SetHelpText(
            this,
            "Scroll the selected window to extend the capture, then finish or cancel.");
        OverlayChrome.MakeCrisp(this);

        var hint = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(hint, "Scrolling capture instructions");
        AutomationProperties.SetHelpText(hint, "Scroll the selected window to add content to the capture.");
        var hintGlyph = new TextBlock { Text = "", FontFamily = FluentFont, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF1, 0xF2)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(hintGlyph, string.Empty);
        hint.Children.Add(hintGlyph);
        hint.Children.Add(new TextBlock { Text = "Scroll the window to capture", FontSize = 13, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });

        _dims = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x8C, 0x8C, 0x98)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 4, 0),
        };
        AutomationProperties.SetAutomationId(_dims, "ScrollCaptureSizeStatus");
        AutomationProperties.SetName(_dims, "Captured size unavailable");
        AutomationProperties.SetHelpText(_dims, "Current width and stitched height of the scrolling capture.");
        AutomationProperties.SetLiveSetting(_dims, AutomationLiveSetting.Polite);

        _cancelButton = MakeButton(
            "Cancel",
            "ScrollCaptureCancel",
            "Cancel scrolling capture",
            "Cancel without keeping the scrolling capture.",
            accent: false,
            tabIndex: 0,
            onClick: onCancel);
        _doneButton = MakeButton(
            "Done",
            "ScrollCaptureDone",
            "Finish scrolling capture",
            "Stop scrolling and keep the stitched capture.",
            accent: true,
            tabIndex: 1,
            onClick: onDone);

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        KeyboardNavigation.SetTabNavigation(row, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(row, KeyboardNavigationMode.Cycle);
        row.Children.Add(hint);
        row.Children.Add(_dims);
        row.Children.Add(new Border { Width = 6 });
        row.Children.Add(_cancelButton);
        row.Children.Add(_doneButton);

        var card = OverlayChrome.ShadowedCard(
            row, cornerRadius: 14,
            background: new SolidColorBrush(Color.FromArgb(0xFF, 0x20, 0x22, 0x24)),
            border: new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
            padding: new Thickness(14, 8, 10, 8),
            blur: 20, depth: 5, shadowOpacity: 0.55);
        Content = new Grid { Margin = new Thickness(ShadowMargin), Children = { card } };
        PreviewKeyDown += OnPreviewKeyDown;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_TOOLWINDOW);
            WindowCaptureExclusion.Apply(hwnd);
        };
        Loaded += (_, _) => Reposition();
    }

    private Button MakeButton(
        string text,
        string automationId,
        string automationName,
        string helpText,
        bool accent,
        int tabIndex,
        Action onClick)
    {
        var button = new Button
        {
            Style = accent ? (Style)FindResource("AccentButton") : (Style)FindResource(typeof(Button)),
            Content = text,
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(8, 0, 0, 0),
            IsTabStop = true,
            TabIndex = tabIndex,
        };
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, automationName);
        AutomationProperties.SetHelpText(button, $"{helpText} Press Enter or Space to activate.");
        button.Click += (_, _) => InvokeActionOnce(onClick);
        _actions.Add(button);
        return button;
    }

    public void UpdateDims(int width, int height)
    {
        if (width == _lastWidth && height == _lastHeight)
            return;

        _lastWidth = width;
        _lastHeight = height;
        _dims.Text = $"{width} × {height}";
        AutomationProperties.SetName(_dims, $"Captured size: {width} by {height} pixels");
        AutomationProperties.SetItemStatus(_dims, $"{width} by {height} pixels");

        if (!IsLoaded ||
            height <= _lastAnnouncedHeight ||
            height - _lastAnnouncedHeight < Math.Max(1, _region.Height) ||
            !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            return;
        }

        _lastAnnouncedHeight = height;
        AutomationPeer? peer =
            UIElementAutomationPeer.FromElement(_dims)
            ?? UIElementAutomationPeer.CreatePeerForElement(_dims);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsActive)
        {
            _cancelButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, _cancelButton));
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is not ButtonBase focused ||
            !_actions.Contains(focused))
        {
            return;
        }

        int currentIndex = _actions.IndexOf(focused);
        int targetIndex;
        switch (e.Key)
        {
            case Key.Left:
                targetIndex = (currentIndex - 1 + _actions.Count) % _actions.Count;
                break;
            case Key.Right:
                targetIndex = (currentIndex + 1) % _actions.Count;
                break;
            case Key.Home:
                targetIndex = 0;
                break;
            case Key.End:
                targetIndex = _actions.Count - 1;
                break;
            case Key.Enter:
                focused.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, focused));
                e.Handled = true;
                return;
            default:
                return;
        }

        _actions[targetIndex].Focus();
        e.Handled = true;
    }

    private void InvokeActionOnce(Action action)
    {
        if (_actionInvoked)
            return;

        _actionInvoked = true;
        _cancelButton.IsEnabled = false;
        _doneButton.IsEnabled = false;
        action();
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
            w = (int)Math.Round(360 * scale);
            h = (int)Math.Round(70 * scale);
        }

        // Centered horizontally on the region, just below it (flip above / inside when cramped).
        int x = _region.X + (_region.Width - w) / 2;
        x = Math.Clamp(x, monitor.WorkArea.X, Math.Max(monitor.WorkArea.X, monitor.WorkArea.Right - w));

        int gap = (int)(10 * scale);
        int below = _region.Bottom + gap;
        int above = _region.Y - h - gap;
        int y = below + h <= monitor.WorkArea.Bottom ? below
            : above >= monitor.WorkArea.Y ? above
            : _region.Bottom - h - gap; // fullscreen: sit inside, near the bottom
        y = Math.Clamp(y, monitor.WorkArea.Y, Math.Max(monitor.WorkArea.Y, monitor.WorkArea.Bottom - h));

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h,
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
    }
}
