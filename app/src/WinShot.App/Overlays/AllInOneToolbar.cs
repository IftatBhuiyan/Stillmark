using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Interop;

namespace WinShot.App.Overlays;

public enum RegionMode
{
    Area,
    Fullscreen,
    Window,
}

/// <summary>
/// The primary all-in-one toolbar. Region-mode cells (Area / Fullscreen / Window) only change the
/// selection — they never capture. Timer modifies the eventual capture; OCR requests a fresh area
/// and immediately recognizes it. Record opens the recording panel. The Capture button, or Enter
/// while Capture has focus, captures.
/// </summary>
public sealed class AllInOneToolbar : Window
{
    private const double ShadowMargin = 14;
    private const double CardHeight = 54;

    private static readonly FontFamily FluentFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Brush CardBg = Freeze(new SolidColorBrush(Color.FromRgb(0x25, 0x28, 0x2A)));
    private static readonly Brush CardBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush DividerBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ChipBg = Freeze(new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0xA8, 0xA9, 0xB3)));

    private readonly Action<RegionMode> _onRegionMode;
    private readonly TextBlock _sizeChip;
    private readonly Dictionary<RegionMode, ToggleButton> _regionToggles = new();
    private readonly List<ButtonBase> _actionControls = new();
    private readonly double _cellWidth;
    private readonly bool _compact;
    internal Button OcrButton { get; }
    private PixelRect _region;
    private double _scale = 1.0;
    private bool _suppressRegionCallback;

    private static Brush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    public AllInOneToolbar(PixelRect region, RegionMode initialMode,
        Action<RegionMode> onRegionMode, Action<bool> onTimer, Action onOcr,
        Action onRecord, Action onScroll, Action onCapture)
    {
        _region = region;
        _onRegionMode = onRegionMode;
        var initialMonitor = MonitorService.GetMonitorAt(
            region.X + region.Width / 2,
            region.Y + region.Height / 2);
        _scale = initialMonitor.ScaleFactor;
        double availableWidthDip = Math.Max(480, (initialMonitor.WorkArea.Width - 24) / _scale);
        _compact = availableWidthDip < 720;
        _cellWidth = _compact ? 48 : 62;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "Stillmark Capture Toolbar";
        Width = Math.Min(_compact ? 566 : 730, availableWidthDip);
        Height = CardHeight + ShadowMargin * 2 + 8;
        OverlayChrome.MakeCrisp(this);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Height = CardHeight };
        KeyboardNavigation.SetTabNavigation(row, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(row, KeyboardNavigationMode.Cycle);
        AutomationProperties.SetName(this, "Capture toolbar");
        AutomationProperties.SetHelpText(this,
            "Choose the selection mode and optional capture settings, then capture, record, or scroll.");
        PreviewKeyDown += OnToolbarPreviewKeyDown;

        AddRegionCell(row, RegionMode.Area, "", "Area", "Select a custom area", first: true);
        AddRegionCell(row, RegionMode.Fullscreen, "", "Screen", "Select the whole screen");
        AddRegionCell(row, RegionMode.Window, "", "Window", "Pick a window");
        _regionToggles[initialMode].IsChecked = true;

        row.Children.Add(VDivider());
        AddToggleCell(row, "", "Timer", "Capture after a countdown", onTimer);
        OcrButton = AddButtonCell(
            row,
            "",
            "Text",
            "Select a fresh area and copy its text",
            Muted,
            onOcr);

        row.Children.Add(VDivider());
        AddButtonCell(row, "", "Record", "Record the selected area",
            (Brush)FindResource("RecordBrush"), onRecord);
        row.Children.Add(VDivider());
        AddButtonCell(row, "", "Scroll", "Scrolling capture", Muted, onScroll);

        row.Children.Add(VDivider());
        _sizeChip = ChipText($"{region.Width} × {region.Height}");
        row.Children.Add(new Border
        {
            Margin = new Thickness(_compact ? 8 : 12, 0, _compact ? 8 : 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Chip(_sizeChip),
        });

        row.Children.Add(VDivider());
        row.Children.Add(MakeCaptureCell(onCapture));

        var inner = new Border { Child = row };
        inner.SizeChanged += (_, e) =>
            inner.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 8, 8);
        var card = OverlayChrome.ShadowedCard(inner, 8, CardBg, CardBorder, new Thickness(0));

        Content = new Grid { Margin = new Thickness(ShadowMargin), Children = { card } };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (!ShowInTaskbar)
            {
                int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
                NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_TOOLWINDOW);
            }
            Reposition();
        };
    }

    private Border VDivider() => new()
    {
        Width = 1,
        Background = DividerBrush,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private void AddRegionCell(StackPanel row, RegionMode mode, string glyph, string label, string tooltip, bool first = false)
    {
        if (!first)
            row.Children.Add(VDivider());
        var toggle = new ToggleButton
        {
            Style = (Style)FindResource("ToolCellToggle"),
            Width = _cellWidth,
            Height = CardHeight,
            ToolTip = tooltip,
            Content = CellContent(glyph, label),
        };
        RegisterAction(toggle, $"{label} selection mode",
            $"{tooltip}. Press Enter or Space to select this mode.");
        toggle.Checked += (_, _) => UpdateRegionAutomationState();
        toggle.Unchecked += (_, _) => UpdateRegionAutomationState();
        toggle.Click += (_, _) =>
        {
            if (_suppressRegionCallback)
                return;
            SetActiveRegion(mode);
            _onRegionMode(mode);
        };
        _regionToggles[mode] = toggle;
        row.Children.Add(toggle);
    }

    private void AddToggleCell(StackPanel row, string glyph, string label, string tooltip, Action<bool> onToggle)
    {
        row.Children.Add(VDivider());
        var toggle = new ToggleButton
        {
            Style = (Style)FindResource("ToolCellToggle"),
            Width = _cellWidth,
            Height = CardHeight,
            ToolTip = tooltip,
            Content = CellContent(glyph, label),
        };
        RegisterAction(toggle, label, tooltip);
        void UpdateAutomationState()
        {
            bool enabled = toggle.IsChecked == true;
            AutomationProperties.SetHelpText(toggle,
                $"{tooltip}. {label} is {(enabled ? "on" : "off")}. Press Enter or Space to toggle.");
            AutomationProperties.SetItemStatus(toggle, enabled ? "On" : "Off");
        }
        toggle.Checked += (_, _) => UpdateAutomationState();
        toggle.Unchecked += (_, _) => UpdateAutomationState();
        UpdateAutomationState();
        toggle.Click += (_, _) => onToggle(toggle.IsChecked == true);
        row.Children.Add(toggle);
    }

    private Button AddButtonCell(StackPanel row, string glyph, string label, string tooltip, Brush iconColor, Action onClick)
    {
        var button = new Button
        {
            Style = (Style)FindResource("ToolCellButton"),
            Width = _cellWidth,
            Height = CardHeight,
            ToolTip = tooltip,
            Content = CellContent(glyph, label, iconColor),
        };
        RegisterAction(button, label, $"{tooltip}. Press Enter or Space to activate.");
        button.Click += (_, _) => onClick();
        row.Children.Add(button);
        return button;
    }

    private Grid MakeCaptureCell(Action onCapture)
    {
        var accent = (Brush)FindResource("QuietAccentBrush");
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new TextBlock { Text = "", FontFamily = FluentFont, FontSize = 16, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = "Capture", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });

        var button = new Button
        {
            Style = (Style)FindResource("ToolCellButton"),
            Width = _compact ? 74 : 84,
            Height = CardHeight,
            ToolTip = "Capture the selection (Enter)",
            Content = content,
        };
        RegisterAction(button, "Capture selection",
            "Capture the current selection. Press Enter or Space to activate.");
        button.Click += (_, _) => onCapture();
        return new Grid { Children = { new Border { Background = accent }, button } };
    }

    private void RegisterAction(ButtonBase control, string name, string helpText)
    {
        AutomationProperties.SetName(control, name);
        AutomationProperties.SetHelpText(control, helpText);
        KeyboardNavigation.SetTabIndex(control, _actionControls.Count);
        control.IsTabStop = true;
        _actionControls.Add(control);
    }

    private void UpdateRegionAutomationState()
    {
        foreach (var (mode, toggle) in _regionToggles)
        {
            bool selected = toggle.IsChecked == true;
            AutomationProperties.SetItemStatus(toggle, selected ? "Selected" : "Not selected");
            AutomationProperties.SetHelpText(toggle,
                $"{RegionModeHelp(mode)}. This mode is {(selected ? "selected" : "not selected")}. " +
                "Press Enter or Space to select it.");
        }
    }

    private static string RegionModeHelp(RegionMode mode) => mode switch
    {
        RegionMode.Area => "Select a custom area",
        RegionMode.Fullscreen => "Select the whole screen",
        RegionMode.Window => "Pick a window",
        _ => "Select a capture region",
    };

    private void OnToolbarPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is not ButtonBase focused ||
            !_actionControls.Contains(focused))
        {
            return;
        }

        int currentIndex = _actionControls.IndexOf(focused);
        int targetIndex;
        switch (e.Key)
        {
            case Key.Left:
                targetIndex = (currentIndex - 1 + _actionControls.Count) % _actionControls.Count;
                break;
            case Key.Right:
                targetIndex = (currentIndex + 1) % _actionControls.Count;
                break;
            case Key.Home:
                targetIndex = 0;
                break;
            case Key.End:
                targetIndex = _actionControls.Count - 1;
                break;
            case Key.Enter:
                if (focused is ToggleButton toggle)
                    toggle.IsChecked = toggle.IsChecked != true;
                focused.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, focused));
                e.Handled = true;
                return;
            default:
                return;
        }

        _actionControls[targetIndex].Focus();
        e.Handled = true;
    }

    private StackPanel CellContent(string glyph, string label, Brush? iconColor = null)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(CreateToolbarGlyph(glyph, iconColor));
        if (!_compact)
        {
            sp.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 10,
                Foreground = Muted,
                Margin = new Thickness(0, 3, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        return sp;
    }

    internal static TextBlock CreateToolbarGlyph(string glyph, Brush? iconColor = null)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontFamily = FluentFont,
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        if (iconColor is not null)
            text.Foreground = iconColor;
        return text;
    }

    private void SetActiveRegion(RegionMode mode)
    {
        foreach (var (m, t) in _regionToggles)
            t.IsChecked = m == mode;
        UpdateRegionAutomationState();
    }

    /// <summary>Sync the highlighted region mode without firing the callback (called when the region changes externally).</summary>
    public void SetRegionMode(RegionMode mode)
    {
        _suppressRegionCallback = true;
        SetActiveRegion(mode);
        _suppressRegionCallback = false;
    }

    private static TextBlock ChipText(string text) => new()
    {
        Text = text,
        FontSize = 13.5,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White,
    };

    private static Border Chip(TextBlock text) => new()
    {
        CornerRadius = new CornerRadius(7),
        Background = ChipBg,
        Padding = new Thickness(11, 4, 11, 4),
        Child = text,
    };

    public void UpdateRegion(PixelRect region)
    {
        _region = region;
        _sizeChip.Text = $"{region.Width} × {region.Height}";
        if (IsLoaded)
            Reposition();
    }

    private void Reposition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var monitor = MonitorService.GetMonitorAt(_region.X + _region.Width / 2, _region.Y + _region.Height / 2);
        _scale = monitor.ScaleFactor;
        int width = (int)Math.Round(Width * _scale);
        int height = (int)Math.Round(Height * _scale);
        int edge = (int)Math.Round(12 * _scale);

        int x = _region.X + (_region.Width - width) / 2;
        int minX = monitor.WorkArea.X + edge;
        int maxX = Math.Max(minX, monitor.WorkArea.Right - width - edge);
        x = Math.Clamp(x, minX, maxX);

        int below = _region.Bottom + (int)(8 * _scale);
        int above = _region.Y - height - (int)(8 * _scale);
        int y = below + height <= monitor.WorkArea.Bottom ? below
            : above >= monitor.WorkArea.Y ? above
            : _region.Bottom - height - (int)(10 * _scale);
        int minY = monitor.WorkArea.Y + edge;
        int maxY = Math.Max(minY, monitor.WorkArea.Bottom - height - edge);
        y = Math.Clamp(y, minY, maxY);

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, width, height,
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
    }
}
