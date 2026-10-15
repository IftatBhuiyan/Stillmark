using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WinShot.App.Recording;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Core.Settings;
using WinShot.Interop;

namespace WinShot.App.Overlays;

/// <summary>
/// In-overlay recording preferences flyout with General / Video / GIF tabs.
/// Every change persists immediately so the recording that follows uses it.
/// </summary>
public sealed class RecordingSettingsPopover : Window
{
    private const double ShadowMargin = 16;

    private readonly SettingsManager _settings;
    private readonly PixelRect _anchorRegion;
    private readonly StackPanel _generalPane = new();
    private readonly StackPanel _videoPane = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _gifPane = new() { Visibility = Visibility.Collapsed };
    private readonly List<ToggleButton> _tabs = new();
    private readonly HashSet<ButtonBase> _buttonControls = new();
    private readonly AudioDeviceRequestGate _audioDeviceRequestGate = new();
    private AudioDeviceRow? _outputDeviceRow;
    private AudioDeviceRow? _inputDeviceRow;
    private TextBlock? _audioDeviceStatus;
    private CancellationTokenSource? _audioDeviceLoadCancellation;
    private bool _audioDeviceLoadStarted;
    private bool _populatingAudioDevices;
    private int _nextTabIndex;

    public RecordingSettingsPopover(PixelRect anchorRegion, SettingsManager settings)
    {
        _settings = settings;
        _anchorRegion = anchorRegion;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 356;
        Height = 392;
        OverlayChrome.MakeCrisp(this);
        AutomationProperties.SetName(this, "Recording settings");
        AutomationProperties.SetHelpText(this,
            "Configure general, video, audio, and GIF recording preferences.");
        PreviewKeyDown += OnPopoverPreviewKeyDown;

        var tabBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        tabBar.Children.Add(MakeTab("General", _generalPane, isChecked: true));
        tabBar.Children.Add(MakeTab("Video", _videoPane, isChecked: false));
        tabBar.Children.Add(MakeTab("GIF", _gifPane, isChecked: false));

        BuildGeneralPane();
        BuildVideoPane();
        BuildGifPane();

        var body = new Grid { Margin = new Thickness(4, 10, 4, 2) };
        body.Children.Add(_generalPane);
        body.Children.Add(_videoPane);
        body.Children.Add(_gifPane);

        var stack = new StackPanel();
        KeyboardNavigation.SetTabNavigation(stack, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(tabBar, KeyboardNavigationMode.Cycle);
        stack.Children.Add(tabBar);
        stack.Children.Add(body);

        var card = OverlayChrome.ShadowedCard(
            stack, cornerRadius: 13,
            background: new SolidColorBrush(Color.FromArgb(0xF5, 0x1E, 0x1E, 0x23)),
            border: new SolidColorBrush(Color.FromArgb(0x2B, 0xFF, 0xFF, 0xFF)),
            padding: new Thickness(12, 10, 12, 12),
            blur: 16, depth: 4, shadowOpacity: 0.5);
        Content = new Grid { Margin = new Thickness(ShadowMargin), Children = { card } };

        Loaded += OnLoaded;
        Closed += OnClosed;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_TOOLWINDOW);

            // Sit above the recording panel, inside the selection when possible.
            var monitor = MonitorService.GetMonitorAt(
                _anchorRegion.X + _anchorRegion.Width / 2, _anchorRegion.Y + _anchorRegion.Height / 2);
            double scale = monitor.ScaleFactor;
            int width = (int)Math.Round(Width * scale);
            int height = (int)Math.Round(Height * scale);
            int x = _anchorRegion.X + (_anchorRegion.Width - width) / 2;
            int y = _anchorRegion.Y + (_anchorRegion.Height / 2) - height - (int)(90 * scale);
            x = Math.Clamp(x, monitor.Bounds.X, Math.Max(monitor.Bounds.X, monitor.Bounds.Right - width));
            y = Math.Clamp(y, monitor.Bounds.Y, Math.Max(monitor.Bounds.Y, monitor.Bounds.Bottom - height));
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, width, height,
                NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
        };
    }

    private ToggleButton MakeTab(string label, StackPanel pane, bool isChecked)
    {
        var tab = new ToggleButton
        {
            Style = (Style)FindResource("ToolButton"),
            Width = 76,
            Height = 28,
            Margin = new Thickness(2, 0, 2, 0),
            IsChecked = isChecked,
            Content = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold },
        };
        RegisterControl(tab, $"{label} recording settings tab",
            $"Show {label.ToLowerInvariant()} recording settings. Press Enter or Space to select.");
        void UpdateAutomationState()
        {
            bool selected = tab.IsChecked == true;
            AutomationProperties.SetItemStatus(tab, selected ? "Selected" : "Not selected");
            AutomationProperties.SetHelpText(tab,
                $"{label} recording settings are {(selected ? "shown" : "hidden")}. " +
                "Press Enter or Space to select this tab.");
        }
        tab.Checked += (_, _) => UpdateAutomationState();
        tab.Unchecked += (_, _) => UpdateAutomationState();
        UpdateAutomationState();
        tab.Click += (_, _) =>
        {
            foreach (var t in _tabs)
                t.IsChecked = t == tab;
            _generalPane.Visibility = pane == _generalPane ? Visibility.Visible : Visibility.Collapsed;
            _videoPane.Visibility = pane == _videoPane ? Visibility.Visible : Visibility.Collapsed;
            _gifPane.Visibility = pane == _gifPane ? Visibility.Visible : Visibility.Collapsed;
        };
        _tabs.Add(tab);
        return tab;
    }

    private void BuildGeneralPane()
    {
        AddCheck(_generalPane, "Show controls while recording",
            _settings.Current.ShowRecordingControls, v => _settings.Current.ShowRecordingControls = v);
        AddCheck(_generalPane, "Show cursor",
            _settings.Current.RecordCursor, v => _settings.Current.RecordCursor = v);
        AddCheck(_generalPane, "Highlight clicks",
            _settings.Current.HighlightClicks, v => _settings.Current.HighlightClicks = v);
        AddCheck(_generalPane, "Play sound when saved",
            _settings.Current.PlaySound, v => _settings.Current.PlaySound = v);
        AddHint(_generalPane, "The selection is always remembered, and a 3-second countdown always runs before recording starts.");
    }

    private void BuildVideoPane()
    {
        var fpsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 0, 10) };
        fpsRow.Children.Add(new TextBlock
        {
            Text = "Frame rate",
            FontSize = 13,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        });
        var fps = new ComboBox { Width = 110 };
        RegisterControl(fps, "Recording frame rate",
            "Choose 30 or 60 frames per second for video recordings.");
        fps.Items.Add("30 fps");
        fps.Items.Add("60 fps");
        fps.SelectedIndex = _settings.Current.RecordingFps >= 60 ? 1 : 0;
        AutomationProperties.SetItemStatus(fps, fps.SelectedItem?.ToString() ?? "Not selected");
        fps.SelectionChanged += (_, _) =>
        {
            _settings.Current.RecordingFps = fps.SelectedIndex == 1 ? 60 : 30;
            _settings.SaveCurrent();
            AutomationProperties.SetItemStatus(fps, fps.SelectedItem?.ToString() ?? "Not selected");
        };
        fpsRow.Children.Add(fps);
        _videoPane.Children.Add(fpsRow);

        AddCheck(_videoPane, "Record system audio",
            _settings.Current.RecordSystemAudio, v => _settings.Current.RecordSystemAudio = v);
        _outputDeviceRow = AddDeviceRow(_videoPane, "Speakers",
            _settings.Current.SystemAudioDeviceId, id => _settings.Current.SystemAudioDeviceId = id);

        AddCheck(_videoPane, "Record microphone",
            _settings.Current.RecordMicrophone, v => _settings.Current.RecordMicrophone = v);
        _inputDeviceRow = AddDeviceRow(_videoPane, "Microphone",
            _settings.Current.MicrophoneDeviceId, id => _settings.Current.MicrophoneDeviceId = id);

        _audioDeviceStatus = new TextBlock
        {
            Text = "Checking audio devices\u2026",
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(22, -2, 0, 4),
        };
        AutomationProperties.SetName(_audioDeviceStatus, "Audio device status");
        AutomationProperties.SetHelpText(_audioDeviceStatus,
            "Reports whether recording audio devices are available.");
        AutomationProperties.SetItemStatus(_audioDeviceStatus, _audioDeviceStatus.Text);
        AutomationProperties.SetLiveSetting(_audioDeviceStatus, AutomationLiveSetting.Polite);
        _videoPane.Children.Add(_audioDeviceStatus);

        AddHint(_videoPane, "Recordings are saved as MP4 (H.264). Trim, resize and re-encode them afterwards from the player's edit button.");
    }

    private void BuildGifPane()
    {
        AddHint(_gifPane, "Record GIF captures the region as a video, then converts it to an animated GIF (15 fps, up to 720px wide, looping). Use it for short, shareable clips; MP4 stays best for longer or higher-fidelity recordings.");
    }

    private AudioDeviceRow AddDeviceRow(Panel parent, string label,
        string? currentId, Action<string?> apply)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 0, 0, 10) };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 78,
        });

        var checkingDevices = new[] { new AudioDeviceEntry(null, "Checking\u2026") };
        var combo = new ComboBox
        {
            Width = 190,
            ItemsSource = checkingDevices,
            DisplayMemberPath = nameof(AudioDeviceEntry.Name),
            SelectedIndex = 0,
            IsEnabled = false,
        };
        RegisterControl(combo, $"{label} audio device",
            $"Choose the {label.ToLowerInvariant()} device used for recording.");
        AutomationProperties.SetItemStatus(combo, "Checking audio devices");
        combo.SelectionChanged += (_, _) =>
        {
            if (_populatingAudioDevices
                || !combo.IsEnabled
                || combo.SelectedItem is not AudioDeviceEntry entry)
                return;

            apply(entry.Id);
            _settings.SaveCurrent();
            AutomationProperties.SetItemStatus(combo, $"Selected: {entry.Name}");
        };
        row.Children.Add(combo);
        parent.Children.Add(row);
        return new AudioDeviceRow(combo, currentId);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_audioDeviceLoadStarted)
            return;

        _audioDeviceLoadStarted = true;
        BeginAudioDeviceLoad();
    }

    private async void BeginAudioDeviceLoad()
    {
        CancellationTokenSource? previous = _audioDeviceLoadCancellation;
        var current = new CancellationTokenSource();
        _audioDeviceLoadCancellation = current;
        previous?.Cancel();
        previous?.Dispose();

        long generation = _audioDeviceRequestGate.Begin();
        CancellationToken cancellationToken = current.Token;
        try
        {
            AudioDeviceLoadResult result =
                await AudioDeviceService.GetDevicesAsync(cancellationToken);
            if (!ReferenceEquals(_audioDeviceLoadCancellation, current)
                || !_audioDeviceRequestGate.CanApply(
                    generation,
                    cancellationToken.IsCancellationRequested))
                return;

            ApplyAudioDeviceResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A superseded or closed popover must never receive an older result.
        }
        finally
        {
            if (ReferenceEquals(_audioDeviceLoadCancellation, current))
                _audioDeviceLoadCancellation = null;
            current.Dispose();
        }
    }

    private void ApplyAudioDeviceResult(AudioDeviceLoadResult result)
    {
        if (_outputDeviceRow is null || _inputDeviceRow is null)
            return;

        _populatingAudioDevices = true;
        try
        {
            PopulateDeviceRow(_outputDeviceRow, result.Catalog.OutputDevices);
            PopulateDeviceRow(_inputDeviceRow, result.Catalog.InputDevices);
        }
        finally
        {
            _populatingAudioDevices = false;
        }

        if (_audioDeviceStatus is not null)
        {
            _audioDeviceStatus.Text = result.Status switch
            {
                AudioDeviceLoadStatus.Ready => "Audio devices ready.",
                AudioDeviceLoadStatus.TimedOut =>
                    "Audio device check timed out. System default is available.",
                _ => "Audio devices could not be checked. System default is available.",
            };
            AutomationProperties.SetItemStatus(_audioDeviceStatus, _audioDeviceStatus.Text);
        }
    }

    private static void PopulateDeviceRow(
        AudioDeviceRow row,
        IReadOnlyList<AudioDeviceEntry> devices)
    {
        row.Combo.ItemsSource = devices;
        int selectedIndex = 0;
        for (int i = 0; i < devices.Count; i++)
        {
            if (string.Equals(
                devices[i].Id,
                row.CurrentId,
                StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = i;
                break;
            }
        }

        row.Combo.SelectedIndex = selectedIndex;
        row.Combo.IsEnabled = true;
        AutomationProperties.SetItemStatus(
            row.Combo,
            $"Selected: {devices[selectedIndex].Name}");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _audioDeviceRequestGate.Close();
        CancellationTokenSource? cancellation = _audioDeviceLoadCancellation;
        _audioDeviceLoadCancellation = null;
        cancellation?.Cancel();
    }

    private void AddCheck(Panel parent, string label, bool initial, Action<bool> apply)
    {
        var check = new CheckBox
        {
            Content = label,
            IsChecked = initial,
            Margin = new Thickness(4, 2, 0, 9),
        };
        RegisterControl(check, label, $"{label}. Press Enter or Space to toggle.");
        void UpdateAutomationState()
        {
            bool enabled = check.IsChecked == true;
            AutomationProperties.SetItemStatus(check, enabled ? "On" : "Off");
            AutomationProperties.SetHelpText(check,
                $"{label} is {(enabled ? "on" : "off")}. Press Enter or Space to toggle.");
        }
        check.Checked += (_, _) => UpdateAutomationState();
        check.Unchecked += (_, _) => UpdateAutomationState();
        UpdateAutomationState();
        check.Click += (_, _) =>
        {
            apply(check.IsChecked == true);
            _settings.SaveCurrent();
        };
        parent.Children.Add(check);
    }

    private void RegisterControl(Control control, string name, string helpText)
    {
        AutomationProperties.SetName(control, name);
        AutomationProperties.SetHelpText(control, helpText);
        KeyboardNavigation.SetTabIndex(control, _nextTabIndex++);
        control.IsTabStop = true;
        if (control is ButtonBase button)
            _buttonControls.Add(button);
    }

    private void OnPopoverPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is not ButtonBase focused ||
            !_buttonControls.Contains(focused))
        {
            return;
        }

        if (focused is ToggleButton tab && _tabs.Contains(tab))
        {
            int currentIndex = _tabs.IndexOf(tab);
            int targetIndex;
            switch (e.Key)
            {
                case Key.Left:
                case Key.Up:
                    targetIndex = (currentIndex - 1 + _tabs.Count) % _tabs.Count;
                    break;
                case Key.Right:
                case Key.Down:
                    targetIndex = (currentIndex + 1) % _tabs.Count;
                    break;
                case Key.Home:
                    targetIndex = 0;
                    break;
                case Key.End:
                    targetIndex = _tabs.Count - 1;
                    break;
                default:
                    targetIndex = -1;
                    break;
            }

            if (targetIndex >= 0)
            {
                _tabs[targetIndex].Focus();
                e.Handled = true;
                return;
            }
        }

        if (e.Key != Key.Enter)
            return;

        if (focused is ToggleButton toggle && !_tabs.Contains(toggle))
            toggle.IsChecked = toggle.IsChecked != true;
        focused.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, focused));
        e.Handled = true;
    }

    private void AddHint(Panel parent, string text) => parent.Children.Add(new TextBlock
    {
        Text = text,
        FontSize = 11,
        Foreground = (Brush)FindResource("TextSecondaryBrush"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(4, 6, 0, 0),
        MaxWidth = 300,
        HorizontalAlignment = HorizontalAlignment.Left,
    });

    private sealed record AudioDeviceRow(ComboBox Combo, string? CurrentId);
}
