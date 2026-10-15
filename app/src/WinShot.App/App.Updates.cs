using System.Windows;
using WinShot.App.Lifecycle;
using WinShot.App.Updates;
using WinShot.Core.Update;

namespace WinShot.App;

public partial class App
{
    private async Task CheckForUpdatesPeriodicallyAsync(CancellationToken cancellationToken)
    {
        var settings = _settings;
        if (!AppUpdateConfiguration.IsConfigured || settings is null) return;
        try
        {
            // Give startup and license refresh time to settle before doing network work.
            await Task.Delay(TimeSpan.FromSeconds(45), cancellationToken);
            using var backend = AppUpdateConfiguration.CreateBackend();
            if (!backend.IsInstalled) return;
            Version? lastNotifiedVersion = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (settings.Current.AutomaticallyCheckForUpdates && _sessionCaptureAvailable && !_shuttingDown)
                    {
                        var target = await backend.CheckAsync(cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (settings.Current.AutomaticallyCheckForUpdates && !_shuttingDown
                            && target is not null && IsUpdateCovered(target)
                            && target.Catalog.Version != lastNotifiedVersion)
                        {
                            lastNotifiedVersion = target.Catalog.Version;
                            _tray?.ShowBalloon("Stillmark update available",
                                $"Version {target.Catalog.Version} is available. Open Settings > About to download and install it.", null);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception ex) { LogError(ex); }
                await Task.Delay(TimeSpan.FromDays(1), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { LogError(ex); }
    }

    private bool IsUpdateCovered(VelopackUpdateTarget target) =>
        AppUpdateConfiguration.IsConfigured
        && Version.TryParse(AppInfo.Version, out Version? installed)
        && target.Catalog.Version > installed
        && target.Catalog.ReleasedAtUtc <= DateTimeOffset.UtcNow;

    private async Task<string?> RestartForUpdateAsync(
        VelopackUpdateBackend backend,
        VelopackDownloadedUpdate downloaded,
        CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _licenseLifetime.Token);
        UpdateQuiescenceLease? quiescence = null;
        var disabledWindows = new List<Window>();
        bool restartWasRegistered = false;
        bool prepared = false;
        string? failure = null;
        try
        {
            bool applied = await backend.ApplyAndRestartAsync(downloaded, ["--tray"], lifetime.Token,
                beforeRestart: () => Dispatcher.Invoke(() =>
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    if (!_sessionCaptureAvailable || _shuttingDown || _exitGate.State != ApplicationExitState.Running)
                    {
                        failure = "Stillmark cannot restart right now. Please try again when capture is available.";
                        return false;
                    }
                    if (!IsUpdateCovered(downloaded.Target))
                    {
                        failure = "Your license access changed. Check Account & license before installing this update.";
                        return false;
                    }
                    if (_mainWindow?.CurrentPage is HistoryWindow { IsActionInProgress: true })
                    {
                        failure = "Wait for the current History action to finish before restarting.";
                        return false;
                    }
                    // Hidden floating captures can still hold an unsaved image, so include loaded
                    // windows even when not visible. Settings, History and the dashboard hold no edits.
                    if (Windows.OfType<Window>().Any(window => window.IsLoaded
                        && window != _mainWindow))
                    {
                        failure = "Save your work, then close the editor, recordings, and floating captures before restarting.";
                        return false;
                    }
                    if (!_updateActivity.TryBeginQuiescence(out quiescence)
                        || !_exitGate.TryBeginUpdatePreparation())
                    {
                        failure = "Finish the current capture, recording, or export before restarting.";
                        return false;
                    }
                    prepared = true;
                    restartWasRegistered = _applicationRestart.IsRegistered;
                    if (!_applicationRestart.TryUnregister())
                    {
                        failure = "Windows could not prepare Relic for a safe restart. Please try again.";
                        return false;
                    }
                    foreach (Window window in Windows.OfType<Window>().Where(w => w.IsEnabled))
                    {
                        window.IsEnabled = false;
                        disabledWindows.Add(window);
                    }
                    // The ledger rejects new capture/export work until the installed updater exits
                    // this process. Tray navigation also observes UpdatePreparing.
                    return true;
                }));
            if (!applied) return failure ?? "This update could not be verified. Your current version is unchanged.";
            // ApplyUpdatesAndRestart normally exits the process itself. Keep the exit intent
            // explicit if a supported host returns after scheduling the replacement.
            _exitGate.TryCommitUpdate();
            Shutdown();
            return null;
        }
        finally
        {
            if (_exitGate.State != ApplicationExitState.UpdateCommitted)
            {
                bool restored = !prepared || _exitGate.TryCancelUpdatePreparation();
                quiescence?.Dispose();
                if (restored)
                {
                    foreach (Window window in disabledWindows.Where(w => w.IsLoaded)) window.IsEnabled = true;
                    if (restartWasRegistered && _exitGate.AllowsSessionRecovery) _applicationRestart.TryRegister();
                }
            }
        }
    }
}
