using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using WinShot.App.Recording;

namespace WinShot.App;

public partial class RecordingRecoveryWindow : Window
{
    private readonly RecordingRecoveryService _service;
    private readonly Func<RecordingRecoveryContext> _contextProvider;
    private readonly CancellationTokenSource _windowLifetime = new();
    private CancellationTokenSource? _refreshLifetime;
    private int _refreshGeneration;
    private bool _closed;
    private bool _actionInProgress;

    internal RecordingRecoveryWindow(
        RecordingRecoveryService service,
        Func<RecordingRecoveryContext> contextProvider)
    {
        InitializeComponent();
        WindowTheming.ApplyDarkTitleBar(this);
        _service = service;
        _contextProvider = contextProvider;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshButton.Focus();
        await RefreshAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_closed || _actionInProgress)
            return;

        _refreshLifetime?.Cancel();
        _refreshLifetime?.Dispose();
        _refreshLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        CancellationToken cancellationToken = _refreshLifetime.Token;
        int generation = ++_refreshGeneration;
        RecordingRecoveryContext context = _contextProvider();

        RefreshButton.IsEnabled = false;
        StatusText.Text = "Checking known Stillmark recording workspaces...";
        try
        {
            RecordingOrphanInventoryResult result = await Task.Run(
                () => _service.Inventory(context, DateTime.UtcNow, cancellationToken),
                cancellationToken);
            if (_closed || cancellationToken.IsCancellationRequested
                || generation != _refreshGeneration)
            {
                return;
            }

            RecoveryCard[] cards = result.Files
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => new RecoveryCard(file))
                .ToArray();
            Cards.ItemsSource = cards;
            EmptyText.Visibility = cards.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            StatusText.Text = BuildInventoryStatus(cards.Length, result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            if (!_closed && generation == _refreshGeneration)
            {
                Cards.ItemsSource = null;
                EmptyText.Visibility = Visibility.Visible;
                StatusText.Text =
                    "Stillmark could not check for interrupted recordings. Try again.";
            }
        }
        finally
        {
            if (!_closed && generation == _refreshGeneration)
                RefreshButton.IsEnabled = true;
        }
    }

    private async void OnRecover(object sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is { } card)
            await RunActionAsync(card, remove: false);
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is not { } card)
            return;

        MessageBoxResult confirmation = MessageBox.Show(
            this,
            $"Remove \"{card.FileName}\" from recording recovery?\n\n"
                + "This deletes only this item from its Stillmark workspace and cannot be undone.",
            "Remove recovery item",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation == MessageBoxResult.Yes)
            await RunActionAsync(card, remove: true);
    }

    private async Task RunActionAsync(RecoveryCard card, bool remove)
    {
        if (_closed || _actionInProgress)
            return;

        _actionInProgress = true;
        _refreshLifetime?.Cancel();
        RecoveryPanel.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        StatusText.Text = remove
            ? $"Removing {card.FileName}..."
            : $"Recovering {card.FileName}...";

        try
        {
            RecordingRecoveryContext context = _contextProvider();
            CancellationToken cancellationToken = _windowLifetime.Token;
            RecordingRecoveryActionResult result = await Task.Run(
                () => remove
                    ? _service.Remove(
                        card.Identity,
                        context,
                        DateTime.UtcNow,
                        userConfirmed: true,
                        cancellationToken: cancellationToken)
                    : _service.Recover(
                        card.Identity,
                        context,
                        DateTime.UtcNow,
                        cancellationToken),
                cancellationToken);
            if (_closed)
                return;
            if (result.Error is not null)
                App.LogError(result.Error);

            _actionInProgress = false;
            RecoveryPanel.IsEnabled = true;
            await RefreshAsync();
            StatusText.Text = result.PublishedFileName is { } published
                ? $"{result.Message} Saved as {published}."
                : result.Message;
            RefreshButton.Focus();
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
                StatusText.Text = "The operation was canceled.";
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            if (!_closed)
            {
                StatusText.Text =
                    "Stillmark could not complete that action. The source was left in place.";
            }
        }
        finally
        {
            _actionInProgress = false;
            if (!_closed)
            {
                RecoveryPanel.IsEnabled = true;
                RefreshButton.IsEnabled = true;
            }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _windowLifetime.Cancel();
        _refreshLifetime?.Cancel();
        Cards.ItemsSource = null;
        _refreshLifetime?.Dispose();
        _windowLifetime.Dispose();
    }

    private static RecoveryCard? CardFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as RecoveryCard;

    private static string BuildInventoryStatus(
        int count,
        RecordingOrphanInventoryResult result)
    {
        if (count == 0)
            return "No eligible interrupted recording files were found.";
        string summary = $"{count} recovery item{(count == 1 ? "" : "s")} found.";
        return result.Truncated
            ? summary + " The bounded scan reached its review limit; remove or recover items, then refresh."
            : summary;
    }

    private sealed class RecoveryCard
    {
        internal RecoveryCard(RecordingOrphanFile file)
        {
            Identity = file.Identity;
            FileName = file.FileName;
            IntegrityLabel = file.IntegrityLabel;
            IconGlyph = file.IsPartial ? "\uE783" : "\uE8B7";
            string location = file.RootKind == RecordingOrphanRootKind.MediaTemp
                ? "Temporary media"
                : "Captures folder";
            MetadataText =
                $"{FormatBytes(file.Length)} | {location} | {FormatAge(file.Age)} old";
        }

        internal RecordingOrphanIdentity Identity { get; }
        public string FileName { get; }
        public string IntegrityLabel { get; }
        public string IconGlyph { get; }
        public string MetadataText { get; }
        public string AutomationName =>
            $"{FileName}. {MetadataText}. {IntegrityLabel}";
        public string RecoverAutomationName => $"Recover {FileName}";
        public string RemoveAutomationName => $"Remove {FileName}";

        private static string FormatBytes(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB"];
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0
                ? $"{bytes:N0} {units[unit]}"
                : $"{value:0.#} {units[unit]}";
        }

        private static string FormatAge(TimeSpan age)
        {
            if (age.TotalDays >= 1)
                return $"{Math.Floor(age.TotalDays):N0}d";
            if (age.TotalHours >= 1)
                return $"{Math.Floor(age.TotalHours):N0}h";
            return $"{Math.Max(1, Math.Floor(age.TotalMinutes)):N0}m";
        }
    }
}
