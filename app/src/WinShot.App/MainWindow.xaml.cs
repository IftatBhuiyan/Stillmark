using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinShot.App.Recording;
using WinShot.Core.History;
using WinShot.Core.Settings;

namespace WinShot.App;

internal enum DashboardCloseBehavior
{
    HideToTray,
    CloseWindow,
}

/// <summary>
/// The launcher/dashboard window. Closing it hides it — the app keeps living in the tray.
/// </summary>
public partial class MainWindow : Window
{
    private readonly SettingsManager _settings;
    private readonly HistoryService _history;
    private readonly CaptureController _capture;
    private readonly RecordingController _recording;
    private readonly Action _openSettings;
    private readonly Action _openHistory;
    private readonly Func<
        string,
        CancellationToken,
        Task<RecentCaptureActivationResult>> _activateRecentCapture;
    private readonly DashboardCloseBehavior _closeBehavior;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _recentLoadCancellation;
    private int _recentLoadVersion;
    private object? _homeContent;
    internal System.Windows.Controls.UserControl? CurrentPage { get; private set; }

    internal void ShowPage(System.Windows.Controls.UserControl page)
    {
        _homeContent ??= Content;
        (CurrentPage as IDisposable)?.Dispose();
        CurrentPage = page;
        var layout = new DockPanel();
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 8, 16, 8) };
        void AddNavigation(string label, Action action, bool selected = false)
        {
            var button = new Button
            {
                Content = label,
                Style = (Style)FindResource("DashboardLinkButton"),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = 13,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            };
            if (selected) button.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            AutomationProperties.SetItemStatus(button, selected ? "Current page" : "");
            button.Click += (_, _) => action();
            navigation.Children.Add(button);
        }
        AddNavigation("Home", ShowHome);
        AddNavigation("History", _openHistory, page is HistoryWindow);
        AddNavigation("Settings", _openSettings, page is SettingsWindow);
        DockPanel.SetDock(navigation, Dock.Top);
        layout.Children.Add(navigation);
        layout.Children.Add(page);
        Content = layout;
        MinWidth = 760;
        MinHeight = 620;
        Width = Math.Max(Width, 920);
        Height = Math.Max(Height, 760);
    }

    internal void ShowHome()
    {
        if (CurrentPage is null) return;
        (CurrentPage as IDisposable)?.Dispose();
        CurrentPage = null;
        Content = _homeContent;
        QueueRecentRefresh();
    }

    /// <summary>Raised when the user closes the dashboard and it is hidden to the tray.</summary>
    public event Action? HiddenToTray;

    internal event Action? RecentCapturesRefreshed;

    public MainWindow(
        SettingsManager settings,
        HistoryService history,
        CaptureController capture,
        RecordingController recording,
        Action openSettings,
        Action openHistory)
        : this(
            settings,
            history,
            capture,
            recording,
            openSettings,
            openHistory,
            RecentCaptureActivator.OpenAsync,
            DashboardCloseBehavior.HideToTray)
    {
    }

    internal MainWindow(
        SettingsManager settings,
        HistoryService history,
        CaptureController capture,
        RecordingController recording,
        Action openSettings,
        Action openHistory,
        Func<
            string,
            CancellationToken,
            Task<RecentCaptureActivationResult>> activateRecentCapture,
        DashboardCloseBehavior closeBehavior = DashboardCloseBehavior.HideToTray)
    {
        InitializeComponent();
        WindowTheming.ApplyDarkTitleBar(this);
        _settings = settings;
        _history = history;
        _capture = capture;
        _recording = recording;
        _openSettings = openSettings;
        _openHistory = openHistory;
        _activateRecentCapture = activateRecentCapture;
        _closeBehavior = closeBehavior;

        BuildHeaderButtons();
        BuildActionCards();
        SizeChanged += (_, _) => UpdateActionLayout();
        Activated += (_, _) =>
        {
            // Settings may change while this resident window is hidden or behind Settings.
            AioHotkey.Text = _settings.Current.Hotkeys.AllInOne;
            ActionsGrid.Children.Clear();
            BuildActionCards();
        };

        AllInOneButton.Click += (_, _) => RunHidden(_capture.StartAllInOneCapture);
        OpenModesButton.Click += (_, _) => RunHidden(_capture.StartAllInOneCapture);
        AioHotkey.Text = settings.Current.Hotkeys.AllInOne;
        ViewAllButton.Click += (_, _) => _openHistory();

        _history.Changed += QueueRecentRefresh;
        // Visibility can change before the initial Loaded event. A fast cached thumbnail may
        // finish during that interval and be discarded by the publication guard below.
        Loaded += (_, _) => QueueRecentRefresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                QueueRecentRefresh();
            else
                CancelRecentLoad();
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_closeBehavior == DashboardCloseBehavior.CloseWindow)
        {
            CancelRecentLoad();
            base.OnClosing(e);
            return;
        }

        // The app lives in the tray; the dashboard just hides.
        e.Cancel = true;
        Hide();
        HiddenToTray?.Invoke();
    }

    protected override void OnClosed(EventArgs e)
    {
        (CurrentPage as IDisposable)?.Dispose();
        _history.Changed -= QueueRecentRefresh;
        _lifetime.Cancel();
        CancelRecentLoad();
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    private void BuildHeaderButtons()
    {
        HeaderButtons.Children.Add(MakeHeaderButton("", "History", _openHistory));
        HeaderButtons.Children.Add(MakeHeaderButton("", "Open captures folder", OpenCapturesFolder));
        HeaderButtons.Children.Add(MakeHeaderButton("", "Settings", _openSettings));
    }

    private Button MakeHeaderButton(string glyph, string tooltip, Action onClick)
    {
        var button = new Button
        {
            Style = (Style)FindResource("ToolbarButton"),
            ToolTip = tooltip,
            Width = 34,
            Height = 34,
            Margin = new Thickness(4, 0, 4, 0),
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 15,
            },
        };
        AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    private void BuildActionCards()
    {
        var h = _settings.Current.Hotkeys;
        AddCard("", "Area", "Select part of the screen", h.AreaCapture, false,
            () => RunHidden(_capture.StartAreaCapture));
        AddCard("", "Full screen", "Current display", h.FullscreenCapture, false,
            () => RunHidden(() => _capture.CaptureFullscreenAtCursor()));
        AddCard("", "All displays", "Entire desktop", h.AllMonitorsCapture, false,
            () => RunHidden(() => _capture.CaptureAllMonitors()));
        AddCard("", "Record", "Video or GIF", h.RecordScreen, true,
            () => RunHidden(_recording.StartRegionRecording));
        AddCard("", "Capture text", "Copy text with OCR", h.TextCapture, false,
            () => RunHidden(_capture.StartTextCapture));
        AddCard("", "Repeat area", "Use previous selection", h.RepeatLastArea, false,
            () => RunHidden(_capture.CaptureLastArea));
    }

    private void AddCard(
        string glyph,
        string title,
        string description,
        string hotkey,
        bool isRecording,
        Action onClick)
    {
        var glyphText = new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 20,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var icon = new Grid { Width = 40, Height = 38 };
        icon.Children.Add(glyphText);
        if (isRecording)
        {
            icon.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = (Brush)FindResource("RecordBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 3, 3),
            });
        }

        var copy = new StackPanel
        {
            Margin = new Thickness(13, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        copy.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
        });
        copy.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 11.5,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(icon);
        Grid.SetColumn(copy, 1);
        content.Children.Add(copy);
        if (!string.IsNullOrWhiteSpace(hotkey))
        {
            var shortcut = new TextBlock
            {
                Text = hotkey,
                FontSize = 10.5,
                Foreground = (Brush)FindResource("TextTertiaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
            };
            Grid.SetColumn(shortcut, 2);
            content.Children.Add(shortcut);
        }

        var card = new Button
        {
            Style = (Style)FindResource("ActionCard"),
            Content = content,
            MinHeight = 64,
        };
        AutomationProperties.SetName(card, $"{title}. {description}. {hotkey}");
        card.Click += (_, _) => onClick();
        ActionsGrid.Children.Add(card);
    }

    private void UpdateActionLayout()
    {
        bool singleColumn = ActualWidth < 700;
        ActionsGrid.Columns = singleColumn ? 1 : 2;
        ActionsDivider.Visibility = singleColumn ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Hide the dashboard first so it never appears in its own capture.</summary>
    private void RunHidden(Action action)
    {
        Hide();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    private void OpenCapturesFolder()
    {
        try
        {
            string dir = _settings.Current.SaveDirectory;
            Directory.CreateDirectory(dir);
            ShellHelper.OpenFolder(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            App.LogError(ex);
            MessageBox.Show(this,
                "The captures folder is unavailable. Connect the drive or choose another folder in Settings.",
                "Could not open captures folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void QueueRecentRefresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                QueueRecentRefresh,
                DispatcherPriority.DataBind);
            return;
        }

        _ = ObserveRecentRefreshAsync();
    }

    private async Task ObserveRecentRefreshAsync()
    {
        try
        {
            await RefreshRecentAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    private async Task RefreshRecentAsync()
    {
        var previouslyFocused = Keyboard.FocusedElement as RecentCaptureButton;
        bool recentHadFocus = previouslyFocused is not null
                              && RecentStrip.Children.Contains(previouslyFocused);
        string? previousEntryId = recentHadFocus ? previouslyFocused!.EntryId : null;
        int previousIndex = recentHadFocus
            ? RecentStrip.Children.IndexOf(previouslyFocused)
            : -1;

        CancelRecentLoad();
        if (!IsVisible || !IsLoaded)
            return;

        var cancellation = new CancellationTokenSource();
        _recentLoadCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        int loadVersion = ++_recentLoadVersion;
        RecentStrip.Children.Clear();
        var entries = _history.Entries.Take(5).ToList();
        RecentEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var cards = new List<(
            HistoryEntry Entry,
            RecentCaptureButton Button,
            int LoadingRevision)>();

        for (int index = 0; index < entries.Count; index++)
        {
            HistoryEntry entry = entries[index];
            var button = new RecentCaptureButton(
                RecentCaptureEntryIdentityPolicy.CreateFocusKey(
                    entry.Id,
                    index + 1),
                position: index + 1,
                count: entries.Count)
            {
                Style = (Style)FindResource("RecentCaptureCard"),
            };
            string path = entry.FilePath;
            DateTime timestamp = entry.Timestamp;
            int loadingRevision = button.ApplyPresentation(
                timestamp,
                RecentCapturePresentationState.Loading);
            button.Click += (_, _) =>
                _ = ObserveRecentActivationAsync(button, timestamp, path);
            RecentStrip.Children.Add(button);
            cards.Add((entry, button, loadingRevision));
        }

        RestoreRecentFocusIfNeeded(
            cards,
            previouslyFocused,
            previousEntryId,
            previousIndex,
            overrideDetachedFocus: true);

        var loads = cards.Select(async card =>
        {
            try
            {
                HistoryThumbnailResult result = await HistoryThumbnailLoader.LoadAsync(
                    card.Entry.FilePath,
                    token);
                return (
                    card.Entry,
                    card.Button,
                    card.LoadingRevision,
                    Result: result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return (
                    card.Entry,
                    card.Button,
                    card.LoadingRevision,
                    Result: new HistoryThumbnailResult(HistoryThumbnailStatus.Unreadable));
            }
        });
        try
        {
            var results = await Task.WhenAll(loads);
            if (token.IsCancellationRequested ||
                loadVersion != _recentLoadVersion ||
                !IsLoaded ||
                !IsVisible)
            {
                return;
            }

            var focusBeforeStatusChange =
                Keyboard.FocusedElement as RecentCaptureButton;
            int focusBeforeStatusIndex = focusBeforeStatusChange is null
                ? -1
                : cards.FindIndex(card =>
                    ReferenceEquals(card.Button, focusBeforeStatusChange));

            foreach (var result in results)
            {
                RecentCapturePresentationState state =
                    RecentCapturePresentationPolicy.FromThumbnailStatus(result.Result.Status);
                result.Button.TryApplyThumbnailPresentation(
                    result.LoadingRevision,
                    result.Entry.Timestamp,
                    state,
                    result.Result.Image);
            }

            if (focusBeforeStatusIndex >= 0
                && !cards[focusBeforeStatusIndex].Button.IsEnabled)
            {
                RestoreRecentFocusIfNeeded(
                    cards,
                    focusBeforeStatusChange,
                    focusBeforeStatusChange!.EntryId,
                    focusBeforeStatusIndex,
                    overrideDetachedFocus: true);
            }

            RecentCapturesRefreshed?.Invoke();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_recentLoadCancellation, cancellation))
                _recentLoadCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task ObserveRecentActivationAsync(
        RecentCaptureButton button,
        DateTime timestamp,
        string path)
    {
        if (!button.TryBeginActivation())
            return;

        try
        {
            RecentCaptureActivationResult result = await _activateRecentCapture(
                path,
                _lifetime.Token);
            if (result.Status == RecentCaptureActivationStatus.Failed
                && result.Exception is not null)
            {
                App.LogError(result.Exception);
            }

            if (!IsLoaded
                || !IsVisible
                || !RecentStrip.Children.Contains(button))
            {
                return;
            }

            switch (result.Status)
            {
                case RecentCaptureActivationStatus.Opened:
                    return;
                case RecentCaptureActivationStatus.Unavailable:
                    bool shouldMoveFocus = button.IsKeyboardFocused;
                    int unavailableIndex = RecentStrip.Children.IndexOf(button);
                    button.ApplyPresentation(
                        timestamp,
                        RecentCapturePresentationState.CaptureUnavailable);
                    button.AnnounceStatusChange();
                    if (shouldMoveFocus)
                        RecoverRecentFocusAfterUnavailable(unavailableIndex);
                    return;
                case RecentCaptureActivationStatus.Failed:
                    button.ApplyPresentation(
                        timestamp,
                        RecentCapturePresentationState.OpenFailed);
                    button.AnnounceStatusChange();
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(result));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            if (IsLoaded
                && IsVisible
                && RecentStrip.Children.Contains(button))
            {
                button.ApplyPresentation(
                    timestamp,
                    RecentCapturePresentationState.OpenFailed);
                button.AnnounceStatusChange();
            }
        }
        finally
        {
            button.EndActivation();
        }
    }

    private void RestoreRecentFocusIfNeeded(
        IReadOnlyList<(
            HistoryEntry Entry,
            RecentCaptureButton Button,
            int LoadingRevision)> cards,
        RecentCaptureButton? previouslyFocused,
        string? previousEntryId,
        int previousIndex,
        bool overrideDetachedFocus)
    {
        if (previouslyFocused is null)
            return;

        IInputElement? currentFocus = Keyboard.FocusedElement;
        if (!overrideDetachedFocus
            && currentFocus is not null
            && !ReferenceEquals(currentFocus, previouslyFocused))
            return;

        string[] entryIds = cards.Select(card => card.Button.EntryId).ToArray();
        bool[] actionable = cards.Select(card => card.Button.IsEnabled).ToArray();
        int? recoveryIndex = RecentCaptureFocusPolicy.FindRecoveryIndex(
            entryIds,
            actionable,
            previousEntryId,
            previousIndex);
        if (recoveryIndex is int index)
        {
            cards[index].Button.Focus();
            cards[index].Button.BringIntoView();
        }
        else
        {
            ViewAllButton.Focus();
        }
    }

    private void RecoverRecentFocusAfterUnavailable(int previousIndex)
    {
        RecentCaptureButton[] buttons =
            RecentStrip.Children.OfType<RecentCaptureButton>().ToArray();
        string[] entryIds = buttons.Select(button => button.EntryId).ToArray();
        bool[] actionable = buttons.Select(button => button.IsEnabled).ToArray();
        int? recoveryIndex = RecentCaptureFocusPolicy.FindRecoveryIndex(
            entryIds,
            actionable,
            previousEntryId: null,
            previousIndex);
        if (recoveryIndex is int index)
        {
            buttons[index].Focus();
            buttons[index].BringIntoView();
        }
        else
        {
            ViewAllButton.Focus();
        }
    }

    private void CancelRecentLoad()
    {
        _recentLoadVersion++;
        CancellationTokenSource? cancellation = _recentLoadCancellation;
        _recentLoadCancellation = null;
        cancellation?.Cancel();
    }

}
