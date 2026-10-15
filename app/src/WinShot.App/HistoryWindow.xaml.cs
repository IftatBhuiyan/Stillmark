using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinShot.Core.History;
using WinShot.Core.Settings;

namespace WinShot.App;

public partial class HistoryWindow : System.Windows.Controls.UserControl, IDisposable
{
    private readonly HistoryService _history;
    private readonly SettingsManager _settings;
    private readonly CancellationTokenSource _windowLifetime = new();
    private CancellationTokenSource _generationLifetime;
    private int _generation;
    private bool _isClosed;
    private bool _actionInProgress;

    internal bool IsActionInProgress => _actionInProgress;

    public HistoryWindow(HistoryService history, SettingsManager settings)
    {
        InitializeComponent();
        _history = history;
        _settings = settings;
        _generationLifetime = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        _history.Changed += OnHistoryChanged;
        Rebuild();
    }

    private void OnHistoryChanged()
    {
        if (_isClosed)
            return;
        if (Dispatcher.CheckAccess())
            Rebuild();
        else
            _ = Dispatcher.BeginInvoke(Rebuild, DispatcherPriority.DataBind);
    }

    private void Rebuild()
    {
        if (_isClosed)
            return;

        _generationLifetime.Cancel();
        _generationLifetime.Dispose();
        _generationLifetime = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        _generation++;

        HistoryEntry[] entries = _history.Entries.ToArray();
        Cards.ItemsSource = entries.Select(entry => new HistoryCard(entry)).ToArray();

        bool empty = entries.Length == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearAllButton.IsEnabled = !empty && !_actionInProgress;
        CountText.Text = empty
            ? ""
            : $"{entries.Length} capture{(entries.Length == 1 ? "" : "s")} · Your most recent {HistoryService.MaxEntries} are kept here";
    }

    public void Dispose()
    {
        _isClosed = true;
        _history.Changed -= OnHistoryChanged;
        _windowLifetime.Cancel();
        _generationLifetime.Cancel();
        Cards.ItemsSource = null;
        _generationLifetime.Dispose();
        _windowLifetime.Dispose();
    }

    private void OnCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement container)
            StartThumbnailLoad(container);
    }

    private void OnCardUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement container)
            CancelThumbnailLoad(container);
    }

    private void OnCardDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement container)
            return;
        CancelThumbnailLoad(container);
        if (container.IsLoaded)
            StartThumbnailLoad(container);
    }

    private void StartThumbnailLoad(FrameworkElement container)
    {
        CancelThumbnailLoad(container);
        if (_isClosed
            || container.DataContext is not HistoryCard card
            || !card.TryBeginLoad(out int attempt))
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _generationLifetime.Token);
        var request = new ThumbnailRequest(card, attempt, _generation, cancellation);
        container.Tag = request;
        _ = LoadThumbnailAsync(container, request);
    }

    private async Task LoadThumbnailAsync(
        FrameworkElement container,
        ThumbnailRequest request)
    {
        try
        {
            HistoryThumbnailResult result = await HistoryThumbnailLoader.LoadAsync(
                request.Card.Entry.FilePath,
                request.Cancellation.Token);

            if (!_isClosed
                && request.Generation == _generation
                && ReferenceEquals(container.Tag, request)
                && ReferenceEquals(container.DataContext, request.Card))
            {
                request.Card.Apply(result, request.Attempt);
            }
        }
        catch (OperationCanceledException)
        {
            request.Card.ResetLoad(request.Attempt);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            if (!_isClosed
                && request.Generation == _generation
                && ReferenceEquals(container.DataContext, request.Card))
            {
                request.Card.Apply(
                    new HistoryThumbnailResult(HistoryThumbnailStatus.Unreadable),
                    request.Attempt);
            }
        }
        finally
        {
            if (ReferenceEquals(container.Tag, request))
                container.Tag = null;
            request.Cancellation.Dispose();
        }
    }

    private static void CancelThumbnailLoad(FrameworkElement container)
    {
        if (container.Tag is not ThumbnailRequest request)
            return;
        container.Tag = null;
        request.Cancellation.Cancel();
        request.Card.ResetLoad(request.Attempt);
    }

    private async void OnThumbnailMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && CardFrom(sender) is { CanOpen: true } card)
        {
            e.Handled = true;
            await RunShellActionAsync(card, reveal: false);
        }
    }

    private async void OnThumbnailKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Enter || e.Key == Key.Space)
            && CardFrom(sender) is { CanOpen: true } card)
        {
            e.Handled = true;
            await RunShellActionAsync(card, reveal: false);
        }
    }

    private async void OnCopyImage(object sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is { CanCopy: true } card)
            await RunImageActionAsync(card, edit: false);
    }

    private async void OnEditImage(object sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is { CanCopy: true } card)
            await RunImageActionAsync(card, edit: true);
    }

    private async void OnRevealFile(object sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is { CanOpen: true } card)
            await RunShellActionAsync(card, reveal: true);
    }

    private void OnRemoveEntry(object sender, RoutedEventArgs e)
    {
        if (_actionInProgress || CardFrom(sender) is not { } card)
            return;

        if (!_history.Remove(card.Entry.Id, out bool cleanupFailed))
        {
            ShowHistoryFailure(
                cleanupFailed
                    ? "The capture is still in History because its file is in use. Close it in another app and try again."
                    : "History could not be updated. Check the remaining entry and try again.");
        }
    }

    private static HistoryCard? CardFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as HistoryCard;

    private async Task RunShellActionAsync(HistoryCard card, bool reveal)
    {
        if (_actionInProgress || _isClosed) return;
        _actionInProgress = true;
        card.SetBusy(true);
        card.SetActionStatus(reveal ? "Opening folder…" : "Opening capture…");
        ClearAllButton.IsEnabled = false;
        CancellationToken cancellationToken = _windowLifetime.Token;
        try
        {
            RecentCaptureActivationResult result;
            if (reveal)
            {
                result = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!HistoryThumbnailPolicy.TryNormalizeImagePathForShellAction(card.Entry.FilePath, out string fullPath)
                        || !File.Exists(fullPath))
                        return new RecentCaptureActivationResult(RecentCaptureActivationStatus.Unavailable);
                    cancellationToken.ThrowIfCancellationRequested();
                    return new RecentCaptureActivationResult(ShellHelper.RevealInFolder(fullPath)
                        ? RecentCaptureActivationStatus.Opened : RecentCaptureActivationStatus.Failed);
                }, cancellationToken);
            }
            else
                result = await RecentCaptureActivator.OpenAsync(card.Entry.FilePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Exception is not null) App.LogError(result.Exception);
            card.SetActionStatus(result.Status switch
            {
                RecentCaptureActivationStatus.Opened => reveal ? "Opened in folder" : "Opened capture",
                RecentCaptureActivationStatus.Unavailable => "File unavailable. If it’s on a network drive, check the connection and try again.",
                _ => reveal ? "Couldn't open the folder. Try again." : "Windows couldn't open this capture. Try again.",
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is Win32Exception
                                   or InvalidOperationException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            App.LogError(ex);
            if (!_isClosed) card.SetActionStatus("Couldn't open this location. Check the connection and try again.");
        }
        finally
        {
            _actionInProgress = false;
            card.SetBusy(false);
            if (!_isClosed) ClearAllButton.IsEnabled = _history.Entries.Count > 0;
        }
    }

    private void ShowHistoryFailure(string message)
    {
        MessageBox.Show(
            Window.GetWindow(this),
            message,
            "History",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private async Task RunImageActionAsync(HistoryCard card, bool edit)
    {
        if (_actionInProgress || _isClosed)
            return;
        _actionInProgress = true;
        card.SetBusy(true);
        card.SetActionStatus(edit ? "Opening editor…" : "Copying image…");
        ClearAllButton.IsEnabled = false;
        CancellationToken cancellationToken = _windowLifetime.Token;
        try
        {
            BitmapSource image = await Task.Run(
                () => LoadFullImage(card.Entry.FilePath, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (edit)
            {
                new Editor.EditorWindow(image, _settings).Show();
                card.SetActionStatus("Editing a copy · Original kept");
                return;
            }

            EncodedImagePayload payload = await StillImageEncodingService.EncodePngAsync(image, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ClipboardService.SetImage(payload, card.Entry.FilePath);
            card.SetActionStatus("Copied image");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or ArgumentException
                                   or InvalidOperationException
                                   or COMException)
        {
            App.LogError(ex);
            if (!_isClosed)
                card.SetActionStatus(edit
                    ? "Couldn't open this image for editing. Try again."
                    : "Couldn't copy the image. Try again.");
        }
        finally
        {
            _actionInProgress = false;
            card.SetBusy(false);
            if (!_isClosed)
                ClearAllButton.IsEnabled = _history.Entries.Count > 0;
        }
    }

    internal static BitmapSource LoadFullImage(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HistoryThumbnailPolicy.TryNormalizeLocalImagePath(path, out string fullPath))
            throw new IOException("The capture path is unavailable.");
        using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > HistoryThumbnailPolicy.MaxSourceFileBytes)
            throw new IOException("The capture file cannot be decoded safely.");
        var decoder = BitmapDecoder.Create(stream,
            BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);
        BitmapFrame frame = decoder.Frames[0];
        if (!HistoryThumbnailPolicy.TryGetDecodeSize(frame.PixelWidth, frame.PixelHeight, out _))
            throw new IOException("The capture dimensions cannot be decoded safely.");
        cancellationToken.ThrowIfCancellationRequested();
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;
        image.EndInit();
        image.Freeze();
        cancellationToken.ThrowIfCancellationRequested();
        return image;
    }

    private sealed record ThumbnailRequest(
        HistoryCard Card,
        int Attempt,
        int Generation,
        CancellationTokenSource Cancellation);

    private sealed class HistoryCard : INotifyPropertyChanged
    {
        private int _attempt;
        private bool _isLoading;
        private bool _isResolved;
        private BitmapSource? _thumbnail;
        private string _placeholderText = "Loading preview...";
        private string _fileStatusText = "Checking local file...";
        private bool _canOpen;
        private bool _canCopy;
        private bool _isBusy;

        internal HistoryCard(HistoryEntry entry)
        {
            Entry = entry;
        }

        internal HistoryEntry Entry { get; }
        public string TimestampText => Entry.Timestamp.ToString("MMM d, h:mm tt");
        public string DimensionText => $"{Entry.Width:N0} x {Entry.Height:N0} pixels";

        public string AutomationName =>
            $"Capture from {Entry.Timestamp:f}, {Entry.Width} by {Entry.Height} pixels. {_fileStatusText}";

        public string OpenAutomationName =>
            $"Open capture from {Entry.Timestamp:f}, {Entry.Width} by {Entry.Height} pixels";

        public string OpenToolTip => _canOpen
            ? "Double-click or press Enter to open"
            : _fileStatusText;

        public BitmapSource? Thumbnail
        {
            get => _thumbnail;
            private set => SetField(ref _thumbnail, value);
        }

        public string PlaceholderText
        {
            get => _placeholderText;
            private set => SetField(ref _placeholderText, value);
        }

        public string FileStatusText
        {
            get => _fileStatusText;
            private set
            {
                if (!SetField(ref _fileStatusText, value))
                    return;
                OnPropertyChanged(nameof(AutomationName));
                OnPropertyChanged(nameof(OpenToolTip));
            }
        }

        public bool CanOpen
        {
            get => _canOpen && !_isBusy;
            private set
            {
                if (SetField(ref _canOpen, value))
                    OnPropertyChanged(nameof(OpenToolTip));
            }
        }

        public bool CanCopy
        {
            get => _canCopy && !_isBusy;
            private set => SetField(ref _canCopy, value);
        }

        public bool CanRemove => !_isBusy;

        internal void SetActionStatus(string message) => FileStatusText = message;

        internal void SetBusy(bool value)
        {
            _isBusy = value;
            OnPropertyChanged(nameof(CanOpen));
            OnPropertyChanged(nameof(CanCopy));
            OnPropertyChanged(nameof(CanRemove));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        internal bool TryBeginLoad(out int attempt)
        {
            attempt = _attempt;
            if (_isLoading || _isResolved)
                return false;

            _isLoading = true;
            attempt = ++_attempt;
            PlaceholderText = "Loading preview...";
            FileStatusText = "Checking local file...";
            return true;
        }

        internal void ResetLoad(int attempt)
        {
            if (attempt != _attempt || !_isLoading)
                return;
            _isLoading = false;
            PlaceholderText = "Loading preview...";
            FileStatusText = "Waiting to load preview...";
        }

        internal void Apply(HistoryThumbnailResult result, int attempt)
        {
            if (attempt != _attempt)
                return;

            _isLoading = false;
            _isResolved = true;
            switch (result.Status)
            {
                case HistoryThumbnailStatus.Loaded when result.Image is not null:
                    Thumbnail = result.Image;
                    PlaceholderText = "";
                    FileStatusText = Entry.IsManagedCopy
                        ? "Stored in capture history"
                        : "Saved capture";
                    CanOpen = true;
                    CanCopy = true;
                    break;
                case HistoryThumbnailStatus.Missing:
                    PlaceholderText = "File no longer exists";
                    FileStatusText = "The local file is no longer available.";
                    CanOpen = false;
                    CanCopy = false;
                    break;
                case HistoryThumbnailStatus.Rejected:
                    PlaceholderText = "Preview unavailable";
                    FileStatusText = "This is not a supported local image path.";
                    CanOpen = false;
                    CanCopy = false;
                    break;
                case HistoryThumbnailStatus.Remote:
                    PlaceholderText = "Network image";
                    FileStatusText = "Saved in a network location. Open the image or show it in its folder to access it.";
                    CanOpen = true;
                    CanCopy = false;
                    break;
                default:
                    PlaceholderText = "Preview unavailable";
                    FileStatusText = "The image cannot be previewed.";
                    CanOpen = true;
                    CanCopy = false;
                    break;
            }
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        if (_actionInProgress)
            return;
        if (MessageBox.Show(Window.GetWindow(this),
                "Remove all history entries? Unsaved capture copies will be deleted; files in your captures folder are kept.",
                "Clear History", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            if (!_history.Clear(out bool cleanupFailed))
            {
                ShowHistoryFailure(
                    cleanupFailed
                        ? "Some capture files are in use. Close them in another app and try again."
                        : "History could not be updated. Check the remaining entries and try again.");
            }
        }
    }
}
