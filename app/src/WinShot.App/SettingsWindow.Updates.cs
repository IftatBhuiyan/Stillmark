using System.IO;
using System.Windows;
using WinShot.App.Updates;

namespace WinShot.App;

public partial class SettingsWindow
{
    private void AutomaticUpdates_Changed(object sender, RoutedEventArgs e) =>
        Apply(settings => settings.AutomaticallyCheckForUpdates = AutomaticUpdatesCheck.IsChecked == true);

    private VelopackUpdateBackend? _updateBackend;
    private VelopackUpdateTarget? _availableUpdate;
    private VelopackDownloadedUpdate? _readyUpdate;
    private CancellationTokenSource? _updateOperation;
    private Func<VelopackUpdateTarget, bool>? _updateEntitled;
    private Func<VelopackUpdateBackend, VelopackDownloadedUpdate, CancellationToken, Task<string?>>? _restartUpdate;

    internal void ConfigureUpdates(
        Func<VelopackUpdateTarget, bool> entitled,
        Func<VelopackUpdateBackend, VelopackDownloadedUpdate, CancellationToken, Task<string?>> restart)
    {
        _updateEntitled = entitled;
        _restartUpdate = restart;
        UpdateActionButton.IsEnabled = AppUpdateConfiguration.IsConfigured;
        UpdateStatusText.Text = AppUpdateConfiguration.IsConfigured
            ? "Updates download when you choose. Relic asks before restarting."
            : "Download free updates at github.com/IftatBhuiyan/Stillmark/releases.";
    }

    private async void UpdateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_updateOperation is not null || _restartUpdate is null || !AppUpdateConfiguration.IsConfigured) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        _updateOperation = operation;
        UpdateActionButton.IsEnabled = false;
        UpdateCancelButton.Visibility = Visibility.Visible;
        UpdateCancelButton.IsEnabled = true;
        try
        {
            _updateBackend ??= AppUpdateConfiguration.CreateBackend();
            if (!_updateBackend.IsInstalled)
            {
                UpdateStatusText.Text = "Install Stillmark to receive updates here.";
                return;
            }
            if (_readyUpdate is { } ready)
            {
                if (!IsUpdateCovered(ready.Target)) return;
                if (MessageBox.Show(Window.GetWindow(this), $"Restart Stillmark to install version {ready.Target.Catalog.Version}? Save your work and close the editor and floating captures first.",
                    "Install update", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
                UpdateStatusText.Text = "Checking the download before restarting…";
                string? failure = await _restartUpdate(_updateBackend, ready, operation.Token);
                if (failure is not null) UpdateStatusText.Text = failure;
            }
            else if (_availableUpdate is { } target)
            {
                if (!IsUpdateCovered(target)) return;
                UpdateStatusText.Text = "Downloading update…";
                var progress = new Progress<int>(percent =>
                {
                    if (_updateOperation == operation && !operation.IsCancellationRequested)
                        UpdateStatusText.Text = $"Downloading update · {Math.Clamp(percent, 0, 100)}%";
                });
                _readyUpdate = await _updateBackend.DownloadAsync(target, value => ((IProgress<int>)progress).Report(value), operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (_readyUpdate is null) throw new InvalidDataException("Downloaded update could not be verified.");
                UpdateActionButton.Content = "Restart & install";
                UpdateStatusText.Text = $"Version {target.Catalog.Version} is ready. Restart when it suits you.";
            }
            else
            {
                UpdateStatusText.Text = "Checking for updates…";
                _availableUpdate = await _updateBackend.CheckAsync(operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (_availableUpdate is null)
                {
                    UpdateStatusText.Text = $"You’re up to date · Version {AppInfo.Version}";
                    return;
                }
                if (!IsUpdateCovered(_availableUpdate)) return;
                UpdateActionButton.Content = "Download update";
                UpdateStatusText.Text = $"Version {_availableUpdate.Catalog.Version} is available · Released {_availableUpdate.Catalog.ReleasedAtUtc:MMM d, yyyy}. Included with your current access.";
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation means the user declined this operation. Discard the in-memory
            // candidate as well, otherwise a completed download racing the cancel request can
            // leave the action labelled "Check for updates" while the next click restarts.
            _availableUpdate = null;
            _readyUpdate = null;
            UpdateActionButton.Content = "Check for updates";
            if (!_windowLifetime.IsCancellationRequested)
                UpdateStatusText.Text = "Update cancelled. Your current version is unchanged.";
        }
        catch (InvalidDataException)
        {
            _availableUpdate = null;
            _readyUpdate = null;
            UpdateActionButton.Content = "Check for updates";
            UpdateStatusText.Text = "This update could not be verified. Your current version is unchanged. Please check again later.";
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            if (!_windowLifetime.IsCancellationRequested)
                UpdateStatusText.Text = "Could not complete the update. Your current version is unchanged. Please try again.";
        }
        finally
        {
            _updateOperation = null;
            if (!_windowLifetime.IsCancellationRequested)
            {
                UpdateActionButton.IsEnabled = true;
                UpdateCancelButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private bool IsUpdateCovered(VelopackUpdateTarget target)
    {
        if (_updateEntitled?.Invoke(target) == true) return true;
        _availableUpdate = null;
        _readyUpdate = null;
        UpdateActionButton.Content = "Check for updates";
        UpdateStatusText.Text = $"Version {target.Catalog.Version} is available, but isn’t covered by your current access. Your eligible versions remain yours to use. Check Account & license for details.";
        return false;
    }

    private void UpdateCancel_Click(object sender, RoutedEventArgs e)
    {
        UpdateCancelButton.IsEnabled = false;
        _updateOperation?.Cancel();
    }

    private void ManageLicense_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app) app.ShowActivation();
    }
}
