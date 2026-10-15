using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinShot.App.Overlays;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Core.Settings;
using WinShot.Interop;

namespace WinShot.App;

/// <summary>
/// A capture pinned above all windows at its original screen position.
/// Drag to move, scroll to resize, Ctrl+scroll for opacity, double-click or Esc to close.
/// </summary>
public sealed class PinWindow : Window
{
    private const double ShadowMargin = 20;
    private const double MinZoom = 0.2, MaxZoom = 3.0;

    private readonly BitmapSource _image;
    private readonly SettingsManager _settings;
    private readonly PixelRect _sourceRect;
    private readonly Grid _hoverLayer;
    private readonly double _monitorScale;
    private readonly List<Button> _actionButtons = new();
    private readonly Dictionary<TransientOverlayCommand, Button> _commandButtons = new();
    private readonly TextBlock _feedback = new()
    {
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly Border _feedbackChrome;
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource _lifetime = new();
    private bool _saving;
    private double _zoom;

    public PinWindow(BitmapSource image, PixelRect sourceRect, SettingsManager settings)
    {
        _image = image;
        _sourceRect = sourceRect;
        _settings = settings;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "Stillmark Pinned Capture";
        AutomationProperties.SetName(this, "Pinned capture");
        AutomationProperties.SetHelpText(
            this,
            "An always-on-top pinned capture. After activating this pin, use Tab or arrow keys " +
            "to move through its actions, Enter or Space to activate, and Escape to close.");
        OverlayChrome.MakeCrisp(this);
        PreviewKeyDown += OnPreviewKeyDown;

        var monitor = MonitorService.GetMonitorAt(
            sourceRect.X + sourceRect.Width / 2, sourceRect.Y + sourceRect.Height / 2);
        _monitorScale = monitor.ScaleFactor;

        // Open at 1:1 unless that would swallow most of the screen.
        double baseWidth = image.PixelWidth / _monitorScale;
        double baseHeight = image.PixelHeight / _monitorScale;
        double maxWidth = monitor.WorkArea.Width / _monitorScale * 0.75;
        double maxHeight = monitor.WorkArea.Height / _monitorScale * 0.75;
        _zoom = Math.Min(1.0, Math.Min(maxWidth / baseWidth, maxHeight / baseHeight));

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Background = new ImageBrush(image) { Stretch = Stretch.Fill },
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.5 },
        };
        RenderOptions.SetBitmapScalingMode(card, BitmapScalingMode.HighQuality);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
        };
        KeyboardNavigation.SetTabNavigation(buttons, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(buttons, KeyboardNavigationMode.Cycle);
        AutomationProperties.SetName(buttons, "Pinned capture actions");
        buttons.Children.Add(MakeIconButton(
            "",
            TransientOverlayCommand.Copy,
            "Copy pinned capture",
            "Ctrl+C",
            CopyImage));
        buttons.Children.Add(MakeIconButton(
            "",
            TransientOverlayCommand.Save,
            "Save pinned capture",
            "Ctrl+S",
            SaveImage));
        buttons.Children.Add(MakeIconButton(
            "",
            TransientOverlayCommand.Close,
            "Close pinned capture",
            "Esc",
            Close));

        _hoverLayer = new Grid { Opacity = 0, IsHitTestVisible = false };
        _hoverLayer.Children.Add(buttons);

        var root = new Grid { Margin = new Thickness(ShadowMargin) };
        KeyboardNavigation.SetTabNavigation(root, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(root, KeyboardNavigationMode.Cycle);
        root.Children.Add(card);
        root.Children.Add(_hoverLayer);
        _feedbackChrome = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEB, 0x1F, 0x1F, 0x24)),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = _feedback,
        };
        AutomationProperties.SetLiveSetting(_feedback, AutomationLiveSetting.Polite);
        root.Children.Add(_feedbackChrome);
        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer.Stop();
            _feedbackChrome.Visibility = Visibility.Collapsed;
        };
        Content = root;
        ApplyZoom();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int margin = (int)Math.Round(ShadowMargin * _monitorScale);
            int width = (int)Math.Round(_image.PixelWidth * _zoom) + margin * 2;
            int height = (int)Math.Round(_image.PixelHeight * _zoom) + margin * 2;
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                _sourceRect.X - margin, _sourceRect.Y - margin, width, height,
                NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
        };

        MouseEnter += (_, _) => UpdateActionVisibility();
        MouseLeave += (_, _) => UpdateActionVisibility();
        IsKeyboardFocusWithinChanged += (_, _) => UpdateActionVisibility();
        MouseLeftButtonDown += OnDragOrClose;
        MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) Close(); };
        MouseWheel += OnWheel;
    }

    private Button MakeIconButton(
        string glyph,
        TransientOverlayCommand command,
        string name,
        string acceleratorKey,
        Action onClick)
    {
        var glyphText = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 11,
            Foreground = Brushes.White,
        };
        AutomationProperties.SetName(glyphText, string.Empty);
        var button = new Button
        {
            Style = (Style)FindResource("OverlayIconButton"),
            Width = 26,
            Height = 26,
            ToolTip = $"{name} ({acceleratorKey})",
            Content = glyphText,
            IsTabStop = true,
            TabIndex = _actionButtons.Count,
        };
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetHelpText(
            button,
            $"{name}. Press Enter or Space to activate. Shortcut: {acceleratorKey}.");
        AutomationProperties.SetAcceleratorKey(button, acceleratorKey);
        button.Click += (_, _) => onClick();
        _actionButtons.Add(button);
        _commandButtons.Add(command, button);
        return button;
    }

    private void UpdateActionVisibility() =>
        SetHoverVisible(IsMouseOver || IsKeyboardFocusWithin);

    private void SetHoverVisible(bool visible)
    {
        _hoverLayer.IsHitTestVisible = visible;
        if (!OverlayChrome.AnimationsEnabled)
        {
            _hoverLayer.BeginAnimation(OpacityProperty, null);
            _hoverLayer.Opacity = visible ? 1 : 0;
            return;
        }
        _hoverLayer.BeginAnimation(OpacityProperty,
            new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(140)));
    }

    private void ApplyZoom()
    {
        Width = _image.PixelWidth / _monitorScale * _zoom + ShadowMargin * 2;
        Height = _image.PixelHeight / _monitorScale * _zoom + ShadowMargin * 2;
    }

    private void OnDragOrClose(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Close();
            return;
        }
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove throws if the button was released mid-call; harmless.
        }
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? 0.1 : -0.1), 0.3, 1.0);
        }
        else
        {
            _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), MinZoom, MaxZoom);
            ApplyZoom();
        }
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TryMoveActionFocus(e))
            return;

        TransientOverlayCommand command = TransientOverlayKeyboardInteraction.Resolve(
            TransientOverlaySurface.Pin,
            MapKey(e.Key == Key.System ? e.SystemKey : e.Key),
            MapModifiers(Keyboard.Modifiers),
            e.IsRepeat);
        if (command == TransientOverlayCommand.None ||
            !_commandButtons.ContainsKey(command))
        {
            return;
        }

        switch (command)
        {
            case TransientOverlayCommand.Copy:
                CopyImage();
                break;
            case TransientOverlayCommand.Save:
                SaveImage();
                break;
            case TransientOverlayCommand.Close:
                Close();
                break;
        }
        e.Handled = true;
    }

    private bool TryMoveActionFocus(KeyEventArgs e)
    {
        if (MapModifiers(Keyboard.Modifiers) != TransientOverlayModifiers.None ||
            Keyboard.FocusedElement is not Button focused ||
            !_actionButtons.Contains(focused))
        {
            return false;
        }

        int currentIndex = _actionButtons.IndexOf(focused);
        TransientOverlayNavigation? navigation = e.Key switch
        {
            Key.Left => TransientOverlayNavigation.Previous,
            Key.Right => TransientOverlayNavigation.Next,
            Key.Home => TransientOverlayNavigation.First,
            Key.End => TransientOverlayNavigation.Last,
            _ => null,
        };
        if (navigation is null)
            return false;

        int targetIndex = TransientOverlayKeyboardInteraction.FindEnabledAction(
            currentIndex,
            _actionButtons.Select(static button => button.IsEnabled).ToArray(),
            navigation.Value);
        if (targetIndex >= 0)
            _actionButtons[targetIndex].Focus();
        e.Handled = true;
        return true;
    }

    private static TransientOverlayKey MapKey(Key key) => key switch
    {
        Key.Escape => TransientOverlayKey.Escape,
        Key.C => TransientOverlayKey.C,
        Key.S => TransientOverlayKey.S,
        _ => TransientOverlayKey.None,
    };

    private static TransientOverlayModifiers MapModifiers(ModifierKeys modifiers)
    {
        TransientOverlayModifiers result = TransientOverlayModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control))
            result |= TransientOverlayModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Shift))
            result |= TransientOverlayModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Alt))
            result |= TransientOverlayModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Windows))
            result |= TransientOverlayModifiers.Windows;
        return result;
    }

    private void CopyImage()
    {
        if (_saving) return;
        try
        {
            ClipboardService.SetImage(_image);
            ShowFeedback("Copied");
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowFeedback("Couldn't copy. Try again.", error: true);
        }
    }

    private async void SaveImage()
    {
        if (_saving) return;
        _saving = true;
        _commandButtons[TransientOverlayCommand.Save].IsEnabled = false;
        _commandButtons[TransientOverlayCommand.Copy].IsEnabled = false;
        ShowFeedback("Saving…", error: true);
        AppSettings settings = _settings.Current.Clone();
        DateTime timestamp = DateTime.Now;
        try
        {
            EncodedImagePayload payload = await StillImageEncodingService.EncodePngAsync(_image, _lifetime.Token);
            using ImageSaveService.PreparedImageWrite prepared = await ImageSaveService.PrepareAsync(
                payload, settings, timestamp, _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            prepared.Commit();
            ShowFeedback("Saved to captures folder");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowFeedback("Couldn't save. Check your folder and try again.", error: true);
        }
        finally
        {
            _saving = false;
            _commandButtons[TransientOverlayCommand.Save].IsEnabled = true;
            _commandButtons[TransientOverlayCommand.Copy].IsEnabled = true;
        }
    }

    private void ShowFeedback(string message, bool error = false)
    {
        if (_lifetime.IsCancellationRequested) return;
        _feedbackTimer.Stop();
        _feedback.Text = message;
        AutomationProperties.SetName(_feedback, message);
        _feedbackChrome.Visibility = Visibility.Visible;
        (UIElementAutomationPeer.FromElement(_feedback)
            ?? UIElementAutomationPeer.CreatePeerForElement(_feedback))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        if (!error) _feedbackTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _feedbackTimer.Stop();
        _lifetime.Cancel();
        base.OnClosed(e);
    }
}
