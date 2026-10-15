using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinShot.App.Overlays;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Interop;

namespace WinShot.App.Recording;

/// <summary>
/// Floating control bar for a recording: countdown, elapsed timer, pause/resume, stop.
/// Placed just outside the recorded region so it stays out of the video.
/// </summary>
public sealed class RecordingBarWindow : Window
{
    private const double ShadowMargin = 14;

    private readonly PixelRect _region;
    private readonly double _scale;
    private readonly Action _onStop;
    private readonly Action _onCancel;
    private readonly Action _onRestart;
    private readonly Action _onDelete;
    private readonly Action<bool> _onPauseToggle;
    private readonly bool _showTime;

    private readonly TextBlock _timerText;
    private readonly Ellipse _recordDot;
    private readonly Button _pauseButton;
    private readonly Button _restartButton;
    private readonly Button _deleteButton;
    private readonly Button _stopButton;
    private readonly TextBlock _pauseGlyph;
    private readonly DispatcherTimer _clock;
    private readonly Stopwatch _recordingClock = new();

    private bool _isPaused;
    private bool _isCountingDown = true;

    public RecordingBarWindow(PixelRect region, Action onStop, Action onCancel, Action onRestart, Action onDelete,
        Action<bool> onPauseToggle, bool showTime)
    {
        _region = region;
        _onStop = onStop;
        _onCancel = onCancel;
        _onRestart = onRestart;
        _onDelete = onDelete;
        _onPauseToggle = onPauseToggle;
        _showTime = showTime;
        _scale = MonitorService.GetMonitorAt(region.X + region.Width / 2, region.Y + region.Height / 2).ScaleFactor;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "Stillmark Recording Controls";
        AutomationProperties.SetName(this, "Stillmark recording controls");
        AutomationProperties.SetHelpText(
            this,
            "Recording controls stay outside the selected area and do not activate when first shown.");
        // Fixed size (not SizeToContent) so SetWindowPos placement is deterministic.
        Width = 320;
        Height = 74;
        OverlayChrome.MakeCrisp(this);

        _recordDot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 8, 0),
        };
        _timerText = new TextBlock
        {
            Text = "3",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 42,
        };
        AutomationProperties.SetName(_timerText, "Recording countdown: 3 seconds");
        AutomationProperties.SetHelpText(
            _timerText,
            "Shows the recording countdown, elapsed time, and paused state.");
        AutomationProperties.SetLiveSetting(_timerText, AutomationLiveSetting.Assertive);

        _pauseGlyph = MakeGlyph("");
        _pauseButton = MakeButton(
            _pauseGlyph,
            "Pause",
            "Pause recording",
            "Temporarily pause the active recording.",
            tabIndex: 0,
            onClick: TogglePause);
        _pauseButton.IsEnabled = false;

        var restartGlyph = MakeGlyph("");
        _restartButton = MakeButton(
            restartGlyph,
            "Restart recording",
            "Restart recording",
            "Discard the current take and record the same area again after confirmation.",
            tabIndex: 1,
            onClick: ConfirmRestart);
        _restartButton.IsEnabled = false;

        var deleteGlyph = MakeGlyph("");
        _deleteButton = MakeButton(
            deleteGlyph,
            "Cancel countdown",
            "Cancel recording countdown",
            "Cancel the countdown without saving a recording.",
            tabIndex: 2,
            onClick: ConfirmDelete);

        var stopGlyph = MakeGlyph("");
        stopGlyph.Foreground = Brushes.White;
        _stopButton = MakeButton(
            stopGlyph,
            "Cancel countdown",
            "Cancel recording countdown",
            "Cancel the countdown. You can also press the configured Record Screen shortcut.",
            tabIndex: 3,
            onClick: () => { if (!_isCountingDown) _onStop(); else _onCancel(); });

        var divider = new Border
        {
            Width = 1,
            Margin = new Thickness(4, 7, 4, 7),
            Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);
        AutomationProperties.SetName(panel, "Recording actions");
        panel.Children.Add(_recordDot);
        panel.Children.Add(_timerText);
        panel.Children.Add(divider);
        panel.Children.Add(_pauseButton);
        panel.Children.Add(_restartButton);
        panel.Children.Add(_deleteButton);
        panel.Children.Add(_stopButton);

        var card = OverlayChrome.ShadowedCard(
            panel, cornerRadius: 18,
            background: new SolidColorBrush(Color.FromArgb(0xF5, 0x20, 0x22, 0x24)),
            border: new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            padding: new Thickness(14, 7, 8, 7),
            blur: 14, depth: 3, shadowOpacity: 0.5);
        Content = new Grid { Margin = new Thickness(ShadowMargin), Children = { card } };

        SourceInitialized += (_, _) => PositionOutsideRegion();

        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
    }

    private static TextBlock MakeGlyph(string glyph)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            Foreground = Brushes.White,
        };
        AutomationProperties.SetName(text, string.Empty);
        return text;
    }

    private Button MakeButton(
        TextBlock glyph,
        string tooltip,
        string automationName,
        string helpText,
        int tabIndex,
        Action onClick)
    {
        var button = new Button
        {
            Style = (Style)FindResource("OverlayIconButton"),
            Width = 28,
            Height = 28,
            Margin = new Thickness(2, 0, 0, 0),
            ToolTip = tooltip,
            Content = glyph,
            IsTabStop = true,
            TabIndex = tabIndex,
        };
        AutomationProperties.SetName(button, automationName);
        AutomationProperties.SetHelpText(button, helpText);
        button.Click += (_, _) => onClick();
        return button;
    }

    private void PositionOutsideRegion()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_TOOLWINDOW);
        WindowCaptureExclusion.Apply(hwnd);

        int width = (int)Math.Round(Width * _scale);
        int height = (int)Math.Round(Height * _scale);
        var monitor = MonitorService.GetMonitorAt(_region.X + _region.Width / 2, _region.Y + _region.Height / 2);

        // Bottom-right of the region, like the reference layout.
        int x = _region.Right - width + (int)(6 * _scale);
        x = Math.Clamp(x, monitor.WorkArea.X, Math.Max(monitor.WorkArea.X, monitor.WorkArea.Right - width));

        int below = _region.Bottom + (int)(10 * _scale);
        int above = _region.Y - height - (int)(10 * _scale);
        int y = below + height <= monitor.WorkArea.Bottom ? below
            : above >= monitor.WorkArea.Y ? above
            : _region.Bottom - height - (int)(12 * _scale); // fullscreen: sits inside, bottom edge

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, width, height,
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Switch from countdown display to the running timer.</summary>
    public void EnterRecordingState()
    {
        _isCountingDown = false;
        _isPaused = false;
        _recordingClock.Restart();
        _pauseButton.IsEnabled = true;
        _restartButton.IsEnabled = true;
        SetButtonAccessibility(
            _deleteButton,
            "Discard recording",
            "Discard recording",
            "Delete the current take after confirmation.");
        SetButtonAccessibility(
            _stopButton,
            "Stop and save",
            "Stop and save recording",
            "Stop and save. You can also press the configured Record Screen shortcut.");
        UpdateClock();
        AnnounceState("Recording started.");
    }

    public void ShowCountdown(int secondsLeft)
    {
        _timerText.Text = $"in {secondsLeft}…";
        _pauseButton.IsEnabled = false;
        _restartButton.IsEnabled = false;
        SetButtonAccessibility(
            _deleteButton,
            "Cancel countdown",
            "Cancel recording countdown",
            "Cancel the countdown without saving a recording.");
        SetButtonAccessibility(
            _stopButton,
            "Cancel countdown",
            "Cancel recording countdown",
            "Cancel the countdown. You can also press the configured Record Screen shortcut.");
        AnnounceState($"Recording starts in {secondsLeft} seconds.");
    }

    private void UpdateClock()
    {
        if (_isCountingDown)
            return;
        TimeSpan elapsed = _recordingClock.Elapsed;
        _timerText.Text = _showTime
            ? elapsed.ToString(elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss")
            : (_isPaused ? "Paused" : "Rec");
        AutomationProperties.SetName(
            _timerText,
            _showTime
                ? $"{(_isPaused ? "Recording paused at" : "Recording elapsed")} {_timerText.Text}."
                : (_isPaused ? "Recording paused." : "Recording active."));
        _recordDot.Opacity = _isPaused ? 0.4 : (elapsed.Seconds % 2 == 0 ? 1.0 : 0.35);
    }

    private void TogglePause()
    {
        if (_isCountingDown)
            return;
        _isPaused = !_isPaused;
        if (_isPaused)
        {
            _recordingClock.Stop();
            _pauseGlyph.Text = "";
            _pauseButton.ToolTip = "Resume";
            AutomationProperties.SetName(_pauseButton, "Resume recording");
            AutomationProperties.SetHelpText(_pauseButton, "Continue the paused recording.");
            _recordDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
        }
        else
        {
            _recordingClock.Start();
            _pauseGlyph.Text = "";
            _pauseButton.ToolTip = "Pause";
            AutomationProperties.SetName(_pauseButton, "Pause recording");
            AutomationProperties.SetHelpText(_pauseButton, "Temporarily pause the active recording.");
            _recordDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
        }
        _pauseButton.IsEnabled = true;
        _onPauseToggle(_isPaused);
        UpdateClock();
        AnnounceState(_isPaused ? "Recording paused." : "Recording resumed.");
    }

    private static void SetButtonAccessibility(
        Button button,
        string tooltip,
        string automationName,
        string helpText)
    {
        button.ToolTip = tooltip;
        AutomationProperties.SetName(button, automationName);
        AutomationProperties.SetHelpText(button, helpText);
    }

    private void AnnounceState(string message)
    {
        AutomationProperties.SetName(_timerText, message);
        if (!IsLoaded)
            return;

        AutomationPeer? peer =
            UIElementAutomationPeer.FromElement(_timerText)
            ?? UIElementAutomationPeer.CreatePeerForElement(_timerText);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void ConfirmRestart()
    {
        if (_isCountingDown)
            return;
        bool wasPaused = _isPaused;
        if (!wasPaused)
            TogglePause();
        var result = MessageBox.Show(
            "Discard the current take and start recording this area again?",
            "Restart Recording", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
            _onRestart();
        else if (!wasPaused)
            TogglePause();
    }

    private void ConfirmDelete()
    {
        if (_isCountingDown)
        {
            _onCancel();
            return;
        }
        bool wasPaused = _isPaused;
        if (!wasPaused)
            TogglePause();
        var result = MessageBox.Show(
            "Delete this recording? It won't be saved.",
            "Discard Recording", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
            _onDelete();
        else if (!wasPaused)
            TogglePause();
    }
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _clock.Stop();
    }
}
