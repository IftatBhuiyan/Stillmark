using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Core.Settings;
using WinShot.Interop;

namespace WinShot.App.Overlays;

/// <summary>
/// Recording options centered in the selected area, modeled on CleanShot's layout.
/// One card with a 5-column grid: row 1 = [settings | dimensions + fullscreen toggle (3 cells) |
/// aspect selector], a full-width divider, row 2 = five toggle cells — all cells aligned and split
/// by dividers, hover fills the whole cell. A second seamless card holds Record GIF / Record Video.
/// </summary>
public sealed class RecordingOptionsPanel : Window
{
    private const double ShadowMargin = 16;
    private const double CardWidth = 330;
    private const double RowHeight = 44;

    private static readonly (string Label, double Ratio)[] AspectRatios =
    {
        ("1:1", 1.0), ("5:4", 1.25), ("4:3", 4.0 / 3), ("7:5", 1.4), ("3:2", 1.5),
        ("16:10", 1.6), ("16:9", 16.0 / 9), ("1.85:1", 1.85), ("2.35:1", 2.35),
        ("4:5", 0.8), ("2:3", 2.0 / 3), ("9:16", 9.0 / 16),
    };

    private static readonly FontFamily FluentFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Brush CardBg = Frozen(new SolidColorBrush(Color.FromArgb(0xFF, 0x22, 0x22, 0x28)));
    private static readonly Brush CardBorder = Frozen(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Divider = Frozen(new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ChipBg = Frozen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Muted = Frozen(new SolidColorBrush(Color.FromArgb(0xFF, 0x8C, 0x8C, 0x98)));

    private readonly SettingsManager _settingsRef;
    private readonly Action<double> _onAspect;
    private readonly TextBlock _wChip;
    private readonly TextBlock _hChip;
    private readonly List<ButtonBase> _actionControls = new();
    private PixelRect _region;
    private RecordingSettingsPopover? _popover;
    private Popup? _aspectPopup;

    private static Brush Frozen(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    public RecordingOptionsPanel(PixelRect region, SettingsManager settings, Action onRecordVideo, Action onRecordGif,
        Action onBack, Action<bool> onFullscreen, Action<double> onAspect)
    {
        _region = region;
        _settingsRef = settings;
        _onAspect = onAspect;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = CardWidth + ShadowMargin * 2;
        Height = 252;
        OverlayChrome.MakeCrisp(this);
        AutomationProperties.SetName(this, "Recording options");
        AutomationProperties.SetHelpText(this,
            "Choose recording settings and capture options, then start a GIF or video recording.");
        PreviewKeyDown += OnPanelPreviewKeyDown;

        // ===== Card 1 =====
        // Row 1: 5 columns aligned with row 2 — settings | dims+fullscreen (span 3) | aspect
        var row1 = new Grid { Height = RowHeight };
        for (int i = 0; i < 5; i++)
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var settingsBtn = MakeSettingsButton();
        Grid.SetColumn(settingsBtn, 0);
        row1.Children.Add(settingsBtn);

        _wChip = ChipLabel(region.Width.ToString());
        _hChip = ChipLabel(region.Height.ToString());
        UpdateDimensionAutomation();
        var mid = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(Chip(_wChip));
        mid.Children.Add(new TextBlock { Text = "×", Foreground = Muted, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 7, 0) });
        mid.Children.Add(Chip(_hChip));
        mid.Children.Add(MakeFullscreenToggle(onFullscreen));
        Grid.SetColumn(mid, 1);
        Grid.SetColumnSpan(mid, 3);
        row1.Children.Add(mid);

        var aspectBtn = MakeAspectButton();
        Grid.SetColumn(aspectBtn, 4);
        row1.Children.Add(aspectBtn);

        row1.Children.Add(VLine(1));
        row1.Children.Add(VLine(4));

        // Row 2: five toggle cells with vertical dividers
        var row2 = new Grid { Height = RowHeight };
        for (int i = 0; i < 5; i++)
            row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddCell(row2, 0, MakeToggle("", "Record microphone", "Include microphone audio in the recording",
            settings.Current.RecordMicrophone, v => { settings.Current.RecordMicrophone = v; settings.SaveCurrent(); }), divider: false);
        AddCell(row2, 1, MakeToggle("", "Record system audio", "Include system audio in the recording",
            settings.Current.RecordSystemAudio, v => { settings.Current.RecordSystemAudio = v; settings.SaveCurrent(); }), divider: true);
        AddCell(row2, 2, MakeDisabledCell("", "Camera overlay", "Camera overlay is not available yet"), divider: true);
        AddCell(row2, 3, MakeToggle("", "Highlight clicks", "Show a visual highlight around mouse clicks",
            settings.Current.HighlightClicks, v => { settings.Current.HighlightClicks = v; settings.SaveCurrent(); }), divider: true);
        AddCell(row2, 4, MakeToggle("", "Show keystrokes",
            "Show keystrokes from all apps; sensitive input may appear in the recording",
            settings.Current.ShowKeystrokes, v => { settings.Current.ShowKeystrokes = v; settings.SaveCurrent(); }), divider: true);

        var grid1 = new Grid();
        grid1.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid1.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid1.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(row1, 0);
        grid1.Children.Add(row1);
        var hDiv1 = new Border { Height = 1, Background = Divider };
        Grid.SetRow(hDiv1, 1);
        grid1.Children.Add(hDiv1);
        Grid.SetRow(row2, 2);
        grid1.Children.Add(row2);

        _ = onBack; // reserved: Esc cancels; CleanShot has no explicit back button

        var card1 = ClippedCard(grid1);

        // ===== Card 2: record actions (seamless flat rows) =====
        var rows = new StackPanel();
        rows.Children.Add(MakeFlatRow(
            leading: new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = ChipBg,
                Padding = new Thickness(5, 1, 5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock { Text = "GIF", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Muted },
            },
            label: "Record GIF", hint: "GIF", enabled: true, onClick: onRecordGif,
            helpText: "Start recording the selected area for animated GIF conversion."));
        rows.Children.Add(new Border { Height = 1, Background = Divider });
        rows.Children.Add(MakeFlatRow(
            leading: new TextBlock { Text = "", FontFamily = FluentFont, FontSize = 16, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            label: "Record Video", hint: "↩", enabled: true, onClick: onRecordVideo,
            helpText: "Start an MP4 video recording of the selected area."));

        var card2 = ClippedCard(rows);
        card2.Margin = new Thickness(0, 9, 0, 0);

        var root = new Grid
        {
            Margin = new Thickness(ShadowMargin),
            Children = { new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { card1, card2 } } },
        };
        KeyboardNavigation.SetTabNavigation(root, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(root, KeyboardNavigationMode.Cycle);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_TOOLWINDOW);
            Reposition();
        };
    }

    /// <summary>Re-center over the (possibly moved/resized) selection and refresh the size chips.</summary>
    public void UpdateRegion(PixelRect region)
    {
        _region = region;
        _wChip.Text = region.Width.ToString();
        _hChip.Text = region.Height.ToString();
        UpdateDimensionAutomation();
        if (IsLoaded)
            Reposition();
    }

    private void Reposition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var monitor = MonitorService.GetMonitorAt(_region.X + _region.Width / 2, _region.Y + _region.Height / 2);
        double scale = monitor.ScaleFactor;
        int width = (int)Math.Round(Width * scale);
        int height = (int)Math.Round(Height * scale);
        int x = _region.X + (_region.Width - width) / 2;
        int y = _region.Y + (_region.Height - height) / 2;
        x = Math.Clamp(x, monitor.Bounds.X, Math.Max(monitor.Bounds.X, monitor.Bounds.Right - width));
        y = Math.Clamp(y, monitor.Bounds.Y, Math.Max(monitor.Bounds.Y, monitor.Bounds.Bottom - height));
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, width, height,
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
    }

    // ===== Card / cell scaffolding =====

    private static FrameworkElement ClippedCard(UIElement content)
    {
        // Zero padding so cells + dividers reach the edges; clip to the card's rounded
        // shape so full-cell (square) hover highlights round off at the corners.
        var inner = new Border { Width = CardWidth, Child = content };
        inner.SizeChanged += (_, e) =>
            inner.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 12, 12);
        return OverlayChrome.ShadowedCard(inner, 13, CardBg, CardBorder, new Thickness(0));
    }

    private Border VLine(int col)
    {
        var line = new Border { Width = 1, Background = Divider, HorizontalAlignment = HorizontalAlignment.Left };
        Grid.SetColumn(line, col);
        return line;
    }

    private void AddCell(Grid grid, int col, UIElement cell, bool divider)
    {
        Grid.SetColumn((FrameworkElement)cell, col);
        grid.Children.Add(cell);
        if (divider)
            grid.Children.Add(VLine(col)); // added after the cell so it stays visible over the hover fill
    }

    private static TextBlock ChipLabel(string text) => new()
    {
        Text = text,
        FontSize = 13.5,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White,
    };

    private static Border Chip(TextBlock label) => new()
    {
        CornerRadius = new CornerRadius(7),
        Background = ChipBg,
        Padding = new Thickness(11, 4, 11, 4),
        Child = label,
    };

    // ===== Row 1 controls =====

    private ToggleButton MakeSettingsButton()
    {
        var button = CellToggle("", "Recording settings", Muted);
        RegisterAction(button, "Recording settings",
            "Open or close detailed recording settings. Press Enter or Space to toggle.");
        void UpdateAutomationState()
        {
            bool open = button.IsChecked == true;
            AutomationProperties.SetItemStatus(button, open ? "Open" : "Closed");
            AutomationProperties.SetHelpText(button,
                $"Detailed recording settings are {(open ? "open" : "closed")}. Press Enter or Space to toggle.");
        }
        button.Checked += (_, _) => UpdateAutomationState();
        button.Unchecked += (_, _) => UpdateAutomationState();
        UpdateAutomationState();
        button.Click += (_, _) =>
        {
            if (button.IsChecked == true)
            {
                _popover = new RecordingSettingsPopover(_region, _settingsRef);
                _popover.Closed += (_, _) => { _popover = null; button.IsChecked = false; };
                _popover.Show();
            }
            else
            {
                _popover?.Close();
            }
        };
        return button;
    }

    private ToggleButton MakeFullscreenToggle(Action<bool> onFullscreen)
    {
        var toggle = new ToggleButton
        {
            Style = (Style)FindResource("SegmentToggle"),
            Width = 30,
            Height = 30,
            Margin = new Thickness(9, 0, 0, 0),
            ToolTip = "Toggle fullscreen",
            Content = new TextBlock { Text = "", FontFamily = FluentFont, FontSize = 14, Foreground = Muted },
        };
        RegisterAction(toggle, "Fullscreen recording area",
            "Use the full display as the recording area. Press Enter or Space to toggle.");
        void UpdateAutomationState()
        {
            bool enabled = toggle.IsChecked == true;
            AutomationProperties.SetItemStatus(toggle, enabled ? "On" : "Off");
            AutomationProperties.SetHelpText(toggle,
                $"Fullscreen recording is {(enabled ? "on" : "off")}. Press Enter or Space to toggle.");
        }
        toggle.Checked += (_, _) => UpdateAutomationState();
        toggle.Unchecked += (_, _) => UpdateAutomationState();
        UpdateAutomationState();
        toggle.Checked += (_, _) => { ((TextBlock)toggle.Content).Foreground = Brushes.White; onFullscreen(true); };
        toggle.Unchecked += (_, _) => { ((TextBlock)toggle.Content).Foreground = Muted; onFullscreen(false); };
        return toggle;
    }

    private ToggleButton MakeAspectButton()
    {
        var button = CellToggle("", "Aspect ratio", Muted);
        RegisterAction(button, "Aspect ratio",
            "Choose a fixed aspect ratio for the recording area. Press Enter or Space to open.");
        void UpdateAutomationState()
        {
            bool open = button.IsChecked == true;
            AutomationProperties.SetItemStatus(button, open ? "Open" : "Closed");
            AutomationProperties.SetHelpText(button,
                $"The aspect ratio menu is {(open ? "open" : "closed")}. Press Enter or Space to toggle.");
        }
        button.Checked += (_, _) => UpdateAutomationState();
        button.Unchecked += (_, _) => UpdateAutomationState();
        UpdateAutomationState();
        button.Click += (_, _) =>
        {
            if (button.IsChecked == true)
                OpenAspectPopup(button);
            else
                _aspectPopup?.SetCurrentValue(Popup.IsOpenProperty, false);
        };
        return button;
    }

    private ToggleButton CellToggle(string glyph, string tooltip, Brush iconColor) => new()
    {
        Style = (Style)FindResource("SegmentToggle"),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        ToolTip = tooltip,
        Content = new TextBlock { Text = glyph, FontFamily = FluentFont, FontSize = 15, Foreground = iconColor, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    private void OpenAspectPopup(ToggleButton anchor)
    {
        var list = new StackPanel();
        KeyboardNavigation.SetTabNavigation(list, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(list, KeyboardNavigationMode.Cycle);
        int tabIndex = 0;
        foreach (var (label, ratio) in AspectRatios)
        {
            double r = ratio;
            var item = new Button
            {
                Style = (Style)FindResource("FlatRowButton"),
                Padding = new Thickness(14, 7, 14, 7),
                Content = new TextBlock { Text = label, FontSize = 13, Foreground = Brushes.White },
            };
            AutomationProperties.SetName(item, $"{label} aspect ratio");
            AutomationProperties.SetHelpText(item,
                $"Resize the recording area to the {label} aspect ratio.");
            KeyboardNavigation.SetTabIndex(item, tabIndex++);
            item.Click += (_, _) =>
            {
                _onAspect(r);
                _aspectPopup!.IsOpen = false;
            };
            list.Children.Add(item);
        }

        var card = new Border
        {
            CornerRadius = new CornerRadius(11),
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            Width = 150,
            Child = list,
        };
        OverlayChrome.EnableClearType(card);

        _aspectPopup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 8,
            StaysOpen = true,
            AllowsTransparency = true,
            Child = new Grid { Margin = new Thickness(10), Children = { card } },
        };
        _aspectPopup.Closed += (_, _) => anchor.IsChecked = false;
        _aspectPopup.IsOpen = true;
    }

    // ===== Row 2 toggle cells =====

    private ToggleButton MakeToggle(
        string glyph,
        string name,
        string helpText,
        bool initial,
        Action<bool> onChanged)
    {
        var check = new TextBlock
        {
            Text = "",
            FontFamily = FluentFont,
            FontSize = 8,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0),
            Visibility = initial ? Visibility.Visible : Visibility.Hidden,
        };
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new TextBlock { Text = glyph, FontFamily = FluentFont, FontSize = 16, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(check);

        var toggle = new ToggleButton
        {
            Style = (Style)FindResource("SegmentToggle"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsChecked = initial,
            ToolTip = helpText,
            Content = content,
        };
        RegisterAction(toggle, name, helpText);
        void UpdateAutomationState()
        {
            bool on = toggle.IsChecked == true;
            AutomationProperties.SetItemStatus(toggle, on ? "On" : "Off");
            AutomationProperties.SetHelpText(toggle,
                $"{helpText}. {name} is {(on ? "on" : "off")}. Press Enter or Space to toggle.");
        }
        toggle.Checked += (_, _) => UpdateAutomationState();
        toggle.Unchecked += (_, _) => UpdateAutomationState();
        UpdateAutomationState();
        toggle.Click += (_, _) =>
        {
            bool on = toggle.IsChecked == true;
            check.Visibility = on ? Visibility.Visible : Visibility.Hidden;
            onChanged(on);
        };
        return toggle;
    }

    private ToggleButton MakeDisabledCell(string glyph, string name, string helpText)
    {
        var toggle = new ToggleButton
        {
            Style = (Style)FindResource("SegmentToggle"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsEnabled = false,
            Opacity = 0.4,
            ToolTip = helpText,
            Content = new TextBlock { Text = glyph, FontFamily = FluentFont, FontSize = 16, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        AutomationProperties.SetName(toggle, name);
        AutomationProperties.SetHelpText(toggle, helpText);
        AutomationProperties.SetItemStatus(toggle, "Unavailable");
        return toggle;
    }

    // ===== Card 2 rows =====

    private Button MakeFlatRow(
        UIElement leading,
        string label,
        string hint,
        bool enabled,
        Action onClick,
        string helpText)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn((FrameworkElement)leading, 0);
        grid.Children.Add(leading);

        var labelText = new TextBlock { Text = label, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = enabled ? Brushes.White : Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(labelText, 1);
        grid.Children.Add(labelText);

        var hintText = new TextBlock { Text = hint, FontSize = 12.5, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(hintText, 2);
        grid.Children.Add(hintText);

        var button = new Button
        {
            Style = (Style)FindResource("FlatRowButton"),
            Padding = new Thickness(13, 11, 15, 11),
            IsEnabled = enabled,
            Content = grid,
        };
        RegisterAction(button, label, $"{helpText} Press Enter or Space to activate.");
        button.Click += (_, _) => onClick();
        return button;
    }

    private void RegisterAction(ButtonBase control, string name, string helpText)
    {
        AutomationProperties.SetName(control, name);
        AutomationProperties.SetHelpText(control, helpText);
        KeyboardNavigation.SetTabIndex(control, _actionControls.Count);
        control.IsTabStop = true;
        _actionControls.Add(control);
    }

    private void UpdateDimensionAutomation()
    {
        AutomationProperties.SetName(_wChip, $"Recording area width, {_region.Width} pixels");
        AutomationProperties.SetName(_hChip, $"Recording area height, {_region.Height} pixels");
    }

    private void OnPanelPreviewKeyDown(object sender, KeyEventArgs e)
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
            case Key.Up:
                targetIndex = (currentIndex - 1 + _actionControls.Count) % _actionControls.Count;
                break;
            case Key.Right:
            case Key.Down:
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

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_aspectPopup is not null)
            _aspectPopup.IsOpen = false;
        _popover?.Close();
    }
}
