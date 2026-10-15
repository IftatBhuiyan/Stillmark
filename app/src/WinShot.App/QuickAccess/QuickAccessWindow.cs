using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WinShot.App.Overlays;

namespace WinShot.App.QuickAccess;

/// <summary>
/// One floating capture card: thumbnail, hover action bar (copy / save / open),
/// close button, drag-out to other apps, double-click to open.
/// </summary>
public sealed class QuickAccessWindow : Window
{
    private const double ShadowMargin = QuickAccessCardPresenter.ShadowMargin;

    private readonly QuickAccessManager _manager;
    private readonly CaptureDelivery _delivery;
    private readonly DispatcherTimer? _autoHideTimer;
    private readonly QuickAccessCardPresenter _cardPresenter;
    private readonly TextBlock _saveGlyph;
    private readonly List<Button> _actionButtons;
    private readonly Dictionary<TransientOverlayCommand, Button> _commandButtons;
    private readonly TransientOverlaySurface _keyboardSurface;

    private string _filePath;
    private string? _userSavedPath;
    private Point _dragStart;
    private bool _dragArmed;
    private bool _closing;
    private bool _operationInProgress;
    private bool _dragging;
    private bool _keepAfterFailure;
    private bool _fromRight;
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public double CardHeight => ActualHeight - ShadowMargin * 2;
    public double CardWidth => ActualWidth - ShadowMargin * 2;

    public QuickAccessWindow(QuickAccessManager manager, CaptureDelivery delivery, string filePath, int autoHideSeconds,
        double scale = 1.0)
    {
        _manager = manager;
        _delivery = delivery;
        _filePath = filePath;
        _userSavedPath = delivery.SavedPath;
        _keyboardSurface = delivery.IsRecording
            ? TransientOverlaySurface.QuickAccessRecording
            : TransientOverlaySurface.QuickAccessImage;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Opacity = 0;
        Title = "Stillmark Quick Access";
        AutomationProperties.SetName(this, "Quick Access capture");
        AutomationProperties.SetHelpText(
            this,
            "Review the recent capture. After activating this card, use Tab or arrow keys to move " +
            "through its actions, Enter or Space to activate, and Escape to dismiss.");
        OverlayChrome.MakeCrisp(this);
        PreviewKeyDown += OnPreviewKeyDown;

        var handlers = new Dictionary<TransientOverlayCommand, RoutedEventHandler>
        {
            [TransientOverlayCommand.Copy] = OnCopy,
            [TransientOverlayCommand.Annotate] = OnAnnotate,
            [TransientOverlayCommand.Pin] = OnPin,
            [TransientOverlayCommand.Save] = OnSaveOrReveal,
            [TransientOverlayCommand.Open] = (_, _) => OpenFile(),
            [TransientOverlayCommand.Close] = (_, _) => FadeOutAndClose(),
        };
        _cardPresenter = new QuickAccessCardPresenter(
            this,
            delivery.Image,
            delivery.Summary,
            delivery.IsRecording,
            _userSavedPath is not null,
            scale,
            handlers);
        _saveGlyph = _cardPresenter.SaveGlyph;
        _actionButtons = _cardPresenter.ActionButtons;
        _commandButtons = _cardPresenter.CommandButtons;
        _cardPresenter.Thumbnail.MouseLeftButtonDown += OnThumbMouseDown;
        _cardPresenter.Thumbnail.MouseMove += OnThumbMouseMove;
        Content = _cardPresenter.Root;
        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer.Stop();
            _cardPresenter.ShowFeedback(null);
            UpdateInteractionState();
        };

        MouseEnter += (_, _) => UpdateInteractionState();
        MouseLeave += (_, _) => UpdateInteractionState();
        IsKeyboardFocusWithinChanged += (_, _) => UpdateInteractionState();
        MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) FadeOutAndClose(); };

        if (autoHideSeconds > 0)
        {
            _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(autoHideSeconds) };
            _autoHideTimer.Tick += (_, _) =>
            {
                if (IsMouseOver || IsKeyboardFocusWithin || _operationInProgress || _dragging || _keepAfterFailure || _feedbackTimer.IsEnabled)
                {
                    _autoHideTimer.Stop();
                    return;
                }
                FadeOutAndClose();
            };
            _autoHideTimer.Start();
        }
    }

    internal static Border CreateCloseButtonChrome(Button closeButton) =>
        QuickAccessCardPresenter.CreateCloseButtonChrome(closeButton);

    private void UpdateInteractionState()
    {
        if (_closing)
            return;

        bool interacting = IsMouseOver || IsKeyboardFocusWithin;
        SetHoverVisible(interacting);
        if (_autoHideTimer is null)
            return;

        _autoHideTimer.Stop();
        if (!interacting && !_operationInProgress && !_dragging && !_keepAfterFailure && !_feedbackTimer.IsEnabled)
            _autoHideTimer.Start();
    }

    private void SetHoverVisible(bool visible)
    {
        _cardPresenter.SetInteractionChromeVisible(visible);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_dragging)
            return; // Escape belongs to the drag operation until it returns.
        if (TryMoveActionFocus(e))
            return;

        TransientOverlayCommand command = TransientOverlayKeyboardInteraction.Resolve(
            _keyboardSurface,
            MapKey(e.Key == Key.System ? e.SystemKey : e.Key),
            MapModifiers(Keyboard.Modifiers),
            e.IsRepeat);
        if (command == TransientOverlayCommand.None)
            return;

        ExecuteCommand(command);
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

    private void ExecuteCommand(TransientOverlayCommand command)
    {
        if (!_commandButtons.TryGetValue(command, out var control) || !control.IsEnabled)
            return;
        switch (command)
        {
            case TransientOverlayCommand.Copy:
                OnCopy(_commandButtons[command], new RoutedEventArgs());
                break;
            case TransientOverlayCommand.Annotate:
                OnAnnotate(_commandButtons[command], new RoutedEventArgs());
                break;
            case TransientOverlayCommand.Pin:
                OnPin(_commandButtons[command], new RoutedEventArgs());
                break;
            case TransientOverlayCommand.Save:
                OnSaveOrReveal(_commandButtons[command], new RoutedEventArgs());
                break;
            case TransientOverlayCommand.Open:
                OpenFile();
                break;
            case TransientOverlayCommand.Close:
                FadeOutAndClose();
                break;
        }
    }

    private static TransientOverlayKey MapKey(Key key) => key switch
    {
        Key.Escape => TransientOverlayKey.Escape,
        Key.C => TransientOverlayKey.C,
        Key.E => TransientOverlayKey.E,
        Key.O => TransientOverlayKey.O,
        Key.P => TransientOverlayKey.P,
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

    // ---- Actions ----

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress || _closing)
            return;
        try
        {
            if (_delivery.IsRecording)
            {
                if (!File.Exists(_filePath))
                    throw new FileNotFoundException("The recording is no longer available.");
                ClipboardService.SetFile(_filePath);
            }
            else if (_delivery.Payload is { } payload)
                ClipboardService.SetImage(payload, _filePath);
            else
                ClipboardService.SetImage(_delivery.Image, _filePath);
            ShowFeedback(_delivery.IsRecording ? "File copied" : "Copied");
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowFeedback("Couldn't copy. Try again.", error: true);
        }
    }

    private void OnAnnotate(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress || _closing)
            return;
        try
        {
            _manager.OpenEditor(_delivery);
            FadeOutAndClose();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowFeedback("Couldn't open the editor. Try again.", error: true);
        }
    }

    private void OnPin(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress || _closing)
            return;
        try
        {
            _manager.OpenPin(_delivery);
            FadeOutAndClose();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowFeedback("Couldn't pin this capture. Try again.", error: true);
        }
    }

    private async void OnSaveOrReveal(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress || _closing)
            return;
        var button = (Button)sender;
        SetOperationInProgress(true);
        try
        {
            if (_userSavedPath is null)
            {
                _cardPresenter.ShowFeedback("Saving…");
                _userSavedPath = _delivery.IsRecording
                    ? await _manager.SaveMediaAsync(_filePath)
                    : await _manager.SaveImageAsync(_delivery);
                _filePath = _userSavedPath;
                _saveGlyph.Text = "";
                UpdateSaveButtonAccessibility(button);
                ShowFeedback("Saved to captures folder");
            }
            else if (File.Exists(_userSavedPath))
            {
                ShellHelper.RevealInFolder(_userSavedPath);
            }
            else
            {
                if (_delivery.IsRecording)
                {
                    ShowFeedback("This recording was moved or removed.", error: true);
                    return;
                }
                // Images can be saved again from memory after the original file was removed.
                _userSavedPath = null;
                _saveGlyph.Text = "\uE74E";
                button.ToolTip = "Save to captures folder (Ctrl+S)";
                AutomationProperties.SetName(button, "Save to captures folder");
                AutomationProperties.SetHelpText(button, "Save this capture again with Ctrl+S.");
                ShowFeedback("File moved or removed. Save again to keep a copy.", error: true);
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowFeedback("Couldn't save. Check your folder and try again.", error: true);
        }
        finally
        {
            SetOperationInProgress(false);
        }
    }

    private static void UpdateSaveButtonAccessibility(Button button)
    {
        button.ToolTip = "Show in folder (Ctrl+S)";
        AutomationProperties.SetName(button, "Show in folder");
        AutomationProperties.SetHelpText(
            button,
            "Show the saved capture in its folder. Press Enter or Space to activate. " +
            "Shortcut: Ctrl+S.");
    }

    private void OpenFile()
    {
        if (_operationInProgress || _closing)
            return;
        try
        {
            if (!File.Exists(_filePath))
            {
                ShowFeedback("This file was moved or removed.", error: true);
                return;
            }
            if (_delivery.IsVideo)
                new VideoPlayerWindow(_filePath).Show(); // recordings open in WinShot's own player
            else
                Process.Start(new ProcessStartInfo(_filePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowFeedback("Couldn't open this file. Try again.", error: true);
        }
    }

    private void SetOperationInProgress(bool busy)
    {
        _operationInProgress = busy;
        if (busy) _feedbackTimer.Stop();
        foreach (var pair in _commandButtons)
            pair.Value.IsEnabled = !busy || pair.Key == TransientOverlayCommand.Close;
        UpdateInteractionState();
    }

    private void ShowFeedback(string message, bool error = false)
    {
        if (_closing)
            return;
        _keepAfterFailure = error;
        _feedbackTimer.Stop();
        _autoHideTimer?.Stop();
        _cardPresenter.ShowFeedback(message);
        if (!error) _feedbackTimer.Start();
    }

    // ---- Drag out / open ----

    private void OnThumbMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_operationInProgress || _closing)
            return;
        if (e.ClickCount == 2)
        {
            // Double-click: images open in the annotate editor; recording files open in their
            // appropriate player (WinShot for MP4, the Windows-associated viewer for GIF).
            if (_delivery.IsRecording)
            {
                OpenFile();
            }
            else
            {
                OnAnnotate(sender, e);
            }
            _dragArmed = false;
            return;
        }
        _dragStart = e.GetPosition(this);
        _dragArmed = true;
    }

    private void OnThumbMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || _operationInProgress || _closing || e.LeftButton != MouseButtonState.Pressed)
            return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStart.X) < 6 && Math.Abs(pos.Y - _dragStart.Y) < 6)
            return;

        _dragArmed = false;
        _dragging = true;
        _autoHideTimer?.Stop();
        try
        {
            DataObject data;
            if (_delivery.IsRecording)
            {
                if (!File.Exists(_filePath))
                    throw new FileNotFoundException("The recording is no longer available.");
                data = new DataObject();
                data.SetFileDropList(new System.Collections.Specialized.StringCollection { _filePath });
            }
            else
            {
                var payload = _delivery.Payload ?? StillImageEncodingService.EncodePng(_delivery.Image);
                data = ClipboardService.CreateImageDataObject(payload, _filePath);
            }
            DragDropEffects result = DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
            if (ShouldDismissAfterDrag(_manager.CloseAfterDrag, result))
                FadeOutAndClose();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowFeedback("Couldn't share this capture. Try copying it instead.", error: true);
        }
        finally
        {
            _dragging = false;
            UpdateInteractionState();
        }
    }

    internal static bool ShouldDismissAfterDrag(bool closeAfterDrag, DragDropEffects result) =>
        closeAfterDrag && (result & DragDropEffects.Copy) != 0;

    // ---- Placement & animation ----

    public void EnterAt(double cardLeft, double cardTop, bool fromRight = false)
    {
        _fromRight = fromRight;
        double targetLeft = cardLeft - ShadowMargin;
        Top = cardTop - ShadowMargin;
        if (!OverlayChrome.AnimationsEnabled)
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(OpacityProperty, null);
            Left = targetLeft;
            Opacity = 1;
            return;
        }
        Left = targetLeft + (fromRight ? 32 : -32);

        var slide = new DoubleAnimation(targetLeft, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(LeftProperty, slide);
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
    }

    public void AnimateTo(double cardLeft, double cardTop)
    {
        if (!OverlayChrome.AnimationsEnabled)
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = cardLeft - ShadowMargin;
            Top = cardTop - ShadowMargin;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(LeftProperty, new DoubleAnimation(cardLeft - ShadowMargin, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        BeginAnimation(TopProperty, new DoubleAnimation(cardTop - ShadowMargin, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    public void FadeOutAndClose()
    {
        if (_closing)
            return;
        _closing = true;
        _autoHideTimer?.Stop();
        _feedbackTimer.Stop();
        if (!OverlayChrome.AnimationsEnabled)
        {
            Close();
            return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
        BeginAnimation(LeftProperty, new DoubleAnimation(Left + (_fromRight ? 20 : -20), TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _autoHideTimer?.Stop();
        _feedbackTimer.Stop();
        _manager.NotifyClosed(this);
    }
}
