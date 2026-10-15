using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.App.Diagnostics;
using WinShot.App.Lifecycle;
using WinShot.App.QuickAccess;
using WinShot.App.Recording;
using WinShot.Capture;
using WinShot.Core.History;
using WinShot.Core.Hotkeys;
using WinShot.Core.Settings;
using WinShot.Core.Startup;
using WinShot.Core.Update;

namespace WinShot.App;

public partial class App : Application
{
    private static readonly TimeSpan[] HotkeyRecoveryDelays =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
    ];
    private static readonly TimeSpan[] SessionDrainRecoveryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(8),
    ];
    private readonly UpdateActivityLedger _updateActivity = new();
    private readonly ApplicationExitGate _exitGate = new();
    private readonly AppExitDrainCoordinator _exitDrains = new();
    private readonly ApplicationDeploymentKind _deploymentKind =
        WindowsApplicationDeploymentDetector.Detect();
    private readonly WindowsApplicationRestartController _applicationRestart;
    private readonly SessionDrainRecoveryCoordinator _sessionDrainRecovery;
    private readonly SessionRecoveryApplicationGate _sessionRecoveryApplications = new();

    internal static IUpdateActivityCoordinator CurrentUpdateActivity =>
        Current is App app
            ? app._updateActivity
            : throw new InvalidOperationException("The Stillmark application is not running.");
    private static readonly TimeSpan ActiveWorkInterruptionTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan QuitFinalizationTimeout = TimeSpan.FromSeconds(6);

    private SingleInstanceGate? _singleInstance;
    private SettingsManager? _settings;
    private IApplicationStartupSettingsController? _startupSettings;
    private CaptureController? _capture;
    private RecordingController? _recording;
    private Scrolling.ScrollingCaptureController? _scrolling;
    private TrayIconController? _tray;
    private HotkeyService? _hotkeys;
    private HistoryService? _history;
    private QuickAccessManager? _quickAccess;
    private RecordingRecoveryWindow? _recordingRecoveryWindow;
    private DiagnosticSummaryWindow? _diagnosticSummaryWindow;
    private MainWindow? _mainWindow;
    private Thread? _showSignalThread;
    private System.Windows.Threading.DispatcherTimer? _hotkeyRecoveryTimer;
    private int _hotkeyRecoveryAttempt;
    private bool _hotkeyRecoveryFailureNotified;
    private bool _settingsReadOnlyWarningShown;
    private bool _startupRegistrationWarningShown;
    private bool _quitInProgress;
    private bool _sessionCaptureAvailable = true;
    private volatile bool _shuttingDown;
    private Task<bool>? _activeWorkInterruptionTask;
    private readonly SessionTransitionTicketSource _sessionTransitions = new();
    private readonly object _deliveryTasksGate = new();
    private readonly HashSet<Task> _deliveryTasks = [];
    private readonly CancellationTokenSource _licenseLifetime = new();
    private readonly string _diagnosticCorrelationId = Guid.NewGuid().ToString("D");
    public App()
    {
        _applicationRestart = new WindowsApplicationRestartController(
            new WindowsApplicationRestartNativeBoundary(),
            _deploymentKind,
            LogError);
        _sessionDrainRecovery = new SessionDrainRecoveryCoordinator(
            () => _activeWorkInterruptionTask,
            RetrySessionDrainAsync,
            CanContinueSessionDrainRecovery,
            SessionDrainRecoveryDelays,
            static (delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool isHermeticMainUiaPreview = false;
#if DEBUG
        isHermeticMainUiaPreview = IsHermeticMainUiaPreviewRequest(e.Args);
        if (isHermeticMainUiaPreview && !TryPrepareHermeticMainUiaDiagnostics())
        {
            Shutdown(1);
            return;
        }
#endif

        // Harden native DLL loading (ScreenRecorderLib et al.): search only System32 + the app dir.
        try
        {
            bool hardened = WinShot.Interop.NativeMethods.SetDefaultDllDirectories(
                WinShot.Interop.NativeMethods.LOAD_LIBRARY_SEARCH_SYSTEM32
                    | WinShot.Interop.NativeMethods.LOAD_LIBRARY_SEARCH_APPLICATION_DIR);
            if (!hardened)
                throw new System.ComponentModel.Win32Exception(
                    System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            LogError(ex);
            if (!isHermeticMainUiaPreview)
            {
                MessageBox.Show(
                    "Stillmark could not initialize protected native-library loading and will close. "
                        + "Restart Windows, then contact support if this continues.",
                    "Stillmark could not start safely",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            Shutdown();
            return;
        }

#if DEBUG
        // Hidden dev/CLI helpers (headless capture, standalone UI previews). Compiled out of release
        // builds: they must not ship as an arbitrary-write primitive, a license-gate bypass, or a
        // reverse-engineering surface in a paid product.
        if (TryRunHeadlessCapture(e.Args) || TryShowUiPreview(e.Args))
            return;
#endif


        if (!TryPrepareRecoveryStart(e.Args, out bool recoveredStart))
            return;

        AppLaunchIntent launchIntent = AppLaunchIntentPolicy.Classify(e.Args);
        string? imagePath = ImageOpenHandoff.GetPath(e.Args);

        // Own both brand generations so an older installed WinShot process and this renamed
        // process cannot become independent tray residents. Both handoff events are created before
        // mutex admission, retaining an interactive request while the primary initializes.
        _singleInstance = new SingleInstanceGate(
            ProductIdentity.SingleInstanceMutexName,
            ProductIdentity.LegacySingleInstanceMutexName,
            ProductIdentity.ShowMainSignalName,
            ProductIdentity.LegacyShowMainSignalName);
        if (!_singleInstance.IsPrimary)
        {
            // Another instance owns the resident services. Only an interactive launch should ask
            // it to show the dashboard; startup and recovery launches remain quiet.
            if (imagePath is not null)
            {
                try { ImageOpenHandoff.SendAsync(imagePath).GetAwaiter().GetResult(); }
                catch (Exception ex)
                {
                    LogError(ex);
                    MessageBox.Show("Stillmark could not open the image. Please restart Stillmark and try again.", "Open image");
                }
            }
            else if (AppLaunchIntentPolicy.ShouldShowExistingInstance(launchIntent))
                _singleInstance.SignalExistingInstance();
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            // Unknown UI-thread failures are fatal. Leaving Handled false terminates through WPF,
            // lets Windows Error Reporting observe the crash, and allows Windows to offer the
            // registered restart command when the failure is eligible and the user accepts.
        };
        // Background-thread and unobserved-async failures would otherwise be invisible.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LogError(ex);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogError(args.Exception);
            args.SetObserved();
        };

        CrashRecoveryDecision recovery = CrashRecoveryGuard.Evaluate(
            recoveredStart,
            DateTimeOffset.UtcNow,
            ProductIdentity.RecoveryStatePath);
        if (!recovery.Allowed)
        {
            bool persistenceUnavailable =
                recovery.DenialReason
                    == CrashRecoveryDenialReason.StatePersistenceUnavailable;
            MessageBox.Show(
                persistenceUnavailable
                    ? "Stillmark could not safely record this recovery attempt, so automatic "
                        + "recovery was stopped. Start Stillmark normally after checking available "
                        + "disk space, then contact support if this continues."
                    : "Stillmark stopped offering additional recovery starts after repeated "
                        + "failures. Start it normally after reviewing the local error log or "
                        + "contacting support.",
                persistenceUnavailable
                    ? "Stillmark recovery could not continue safely"
                    : "Stillmark recovery paused",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        // Register before fallible service/UI initialization. Windows Application Restart is a
        // best-effort, user-mediated recovery path for eligible crash/hang failures, not a watchdog.
        _applicationRestart.TryRegister();

        _settings = new SettingsManager(ProductIdentity.CreateSettingsStore());
        ApplicationTheme.Apply(_settings.Current.Appearance);
        _startupSettings = CreateStartupSettingsController(_settings);
        OwnedTransientFileRetention.PurgeAtStartup(DateTime.UtcNow);
        _history = new HistoryService(ProductIdentity.HistoryBaseDirectory);
        _capture = new CaptureController(_settings, _updateActivity);
        _recording = new RecordingController(_settings, _updateActivity);
        // Session safety remains enforced; the open-source edition has no license gate.
        _capture.UsageGate = EnsureLicensed;
        _recording.UsageGate = EnsureLicensed;
        _scrolling = new Scrolling.ScrollingCaptureController(_updateActivity);
        _quickAccess = new QuickAccessManager(_settings);
        _tray = new TrayIconController(
            _settings,
            _capture,
            _recording,
            ShowMainWindow,
            OpenSettings,
            OpenHistory,
            OpenRecordingRecovery,
            OpenDiagnosticSummary,
            OpenDiagnosticFolder,
            ShowActivation,
            Quit);

        bool hidDesktopIcons = false;
        Action prepareScreen = () =>
        {
            bool needsPause = _quickAccess.HideAll();
            if (_settings.Current.HideDesktopIconsWhileCapturing && DesktopIconsService.AreIconsVisible())
            {
                DesktopIconsService.Toggle();
                hidDesktopIcons = true;
                needsPause = true;
            }
            if (needsPause)
                Thread.Sleep(hidDesktopIcons ? 220 : 60); // give DWM a beat to remove them from screen
        };
        Action releaseScreen = () =>
        {
            _quickAccess.RestoreAll();
            if (hidDesktopIcons)
            {
                DesktopIconsService.Toggle();
                hidDesktopIcons = false;
            }
        };
        _capture.Notify += (title, message, path) => _tray.ShowBalloon(title, message, path);
        _capture.Delivered += OnCaptureDelivered;
        _capture.PrepareScreen = prepareScreen;
        _capture.ReleaseScreen = releaseScreen;
        _recording.Notify += (title, message, path) => _tray.ShowBalloon(title, message, path);
        _recording.Delivered += OnCaptureDelivered;
        _recording.PrepareScreen = prepareScreen;
        _recording.ReleaseScreen = releaseScreen;
        _scrolling.Notify += (title, message, path) => _tray.ShowBalloon(title, message, path);
        _scrolling.Captured += (image, rect) => _capture!.DeliverImage(image, rect);
        _capture.ScrollRequested += region =>
        {
            // Let the frozen all-in-one overlay finish closing before capturing the live screen.
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            timer.Tick += (_, _) => { timer.Stop(); _scrolling!.Start(region); };
            timer.Start();
        };
        _capture.RecordRequested += region => _recording.StartRegionRecordingOf(region);
        _capture.RecordGifRequested += region => _recording.StartRegionRecordingOf(region, asGif: true);

        _hotkeys = new HotkeyService();
        RegisterHotkeys();
        // Windows drops global RegisterHotKey registrations when it resumes from sleep or the session
        // is unlocked. Re-register on those events so the shortcuts keep working without relaunching.
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        ApplicationStartupSettingsResult startupResult =
            _startupSettings.SynchronizeUnpackagedAtApplicationStart();
        ObserveStartupSettingsResult(startupResult, explicitPreferenceChange: false);
        ListenForShowSignal();
        ImageFileRegistration.RegisterInstalled();
        _ = ImageOpenHandoff.ListenAsync(
            path => Dispatcher.BeginInvoke(new Action(() => OpenExternalImage(path))),
            _licenseLifetime.Token);
        _ = CheckForUpdatesPeriodicallyAsync(_licenseLifetime.Token);

        if (imagePath is not null)
        {
            OpenExternalImage(imagePath);
        }
        else if (launchIntent == AppLaunchIntent.Interactive)
        {
            ShowMainWindow();
        }
        else
        {
            // Background/startup launch — no window is shown; let the user know where the app lives.
            ShowBackgroundHintOnce();
        }
    }

    private async void OpenExternalImage(string path)
    {
        try
        {
            BitmapSource image = await Task.Run(() => HistoryWindow.LoadFullImage(path, _licenseLifetime.Token));
            if (_shuttingDown || _settings is null) return;
            var editor = new Editor.EditorWindow(image, _settings);
            editor.Show();
            editor.Activate();
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception ex)
        {
            LogError(ex);
            if (!_shuttingDown)
                MessageBox.Show("This image could not be opened. Choose a local PNG or JPEG image that is available on this computer.",
                    "Open image", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool TryPrepareRecoveryStart(
        IReadOnlyList<string> arguments,
        out bool recoveredStart)
    {
        recoveredStart = false;
        if (!InstalledRecoveryRedirectPolicy.ContainsRecoveryIntent(arguments))
            return true;

        try
        {
            string processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException(
                    "The current executable path is unavailable.");
            string localRoot = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            InstalledRecoveryLaunchDecision decision =
                InstalledRecoveryRedirectPolicy.Evaluate(
                    arguments,
                    processPath,
                    localRoot,
                    AppInfo.PackageId);
            ApplicationServicingDecision servicing =
                ApplicationServicingPolicy.Evaluate(
                    _deploymentKind);

            if (decision.Route is InstalledRecoveryLaunchRoute.ContinueRecovered)
            {
                recoveredStart = true;
                return true;
            }

            if (decision.Route is InstalledRecoveryLaunchRoute.RedirectToStableLauncher
                && servicing.AllowStableLauncherRouting
                && decision.StableLauncherPath is { } stableLauncherPath)
            {
                string workingDirectory = Path.GetDirectoryName(stableLauncherPath)
                    ?? throw new InvalidOperationException(
                        "The stable launcher directory is unavailable.");
                using FileStream stableLauncher =
                    OpenAuthenticatedRecoveryLauncher(
                        stableLauncherPath,
                        workingDirectory);
                var startInfo = new ProcessStartInfo
                {
                    FileName = stableLauncherPath,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                startInfo.ArgumentList.Add(
                    InstalledRecoveryRedirectPolicy.TrayArgument);
                startInfo.ArgumentList.Add(
                    InstalledRecoveryRedirectPolicy.PublicRecoveredArgument);
                using Process process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException(
                        "The stable recovery launcher did not start.");
                Shutdown();
                return false;
            }

            LogError(
                new InvalidOperationException(
                    "A malformed or untrusted recovery launch was refused."));
        }
        catch (Exception ex)
        {
            LogError(ex);
        }

        // A failed installed recovery redirect must never continue initializing a stale payload.
        // The next ordinary interactive launch can repair or explain the installation.
        Shutdown();
        return false;
    }

    private static FileStream OpenAuthenticatedRecoveryLauncher(
        string stableLauncherPath,
        string installRootPath)
    {
        if (AppInfo.ExpectedPublisherSubject.Equals(
                "UNSET",
                StringComparison.Ordinal)
            || AppInfo.ExpectedArtifactSigningIdentityEku.Equals(
                "UNSET",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The production recovery-launcher identity is not configured.");
        }

        FileAttributes rootAttributes = File.GetAttributes(installRootPath);
        FileAttributes launcherAttributes = File.GetAttributes(stableLauncherPath);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0
            || (launcherAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException(
                "The recovery launcher path contains a reparse point.");
        }

        var stream = new FileStream(
            stableLauncherPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        try
        {
            var verifier = new WindowsUpdateCandidateAuthenticityVerifier(
                new WindowsAuthenticodeExpectations(
                    AppInfo.ExpectedPublisherSubject,
                    AppInfo.ExpectedArtifactSigningIdentityEku));
            UpdateCandidateAuthenticityStatus trust = verifier.VerifyAsync(
                    stream.SafeFileHandle,
                    Path.GetFileName(stableLauncherPath),
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (trust is not UpdateCandidateAuthenticityStatus.Trusted)
            {
                throw new UnauthorizedAccessException(
                    "The recovery launcher signature was not trusted.");
            }

            FileVersionInfo version = FileVersionInfo.GetVersionInfo(
                stableLauncherPath);
            if (!RecoveryLauncherMetadataPolicy.IsAllowed(
                    new RecoveryLauncherMetadataEvidence(
                        version.ProductName,
                        version.CompanyName,
                        version.FileMajorPart,
                        version.FileMinorPart,
                        version.FileBuildPart,
                        version.FilePrivatePart),
                    AppInfo.Name,
                    AppInfo.CompanyName,
                    Version.Parse(AppInfo.Version),
                    AppInfo.MinimumStableLauncherSecurityEpoch))
            {
                throw new UnauthorizedAccessException(
                    "The recovery launcher product identity was not trusted.");
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private bool EnsureLicensed() =>
        _sessionCaptureAvailable && !_shuttingDown && !_exitGate.IsTerminal;

    public void ShowActivation() => MessageBox.Show(
        "Stillmark is free and open source under GNU GPLv3. No account or license key is needed.\n\nSource, downloads, and support: https://github.com/IftatBhuiyan/Stillmark",
        "About Stillmark", MessageBoxButton.OK, MessageBoxImage.Information);

    private void ListenForShowSignal()
    {
        if (_singleInstance is null)
            throw new InvalidOperationException("The single-instance handoff event is unavailable.");

        WaitHandle[] signals = _singleInstance.HandoffSignals;

        _showSignalThread = new Thread(() =>
        {
            while (!_shuttingDown)
            {
                WaitHandle.WaitAny(signals);
                if (_shuttingDown)
                    break;
                if (!Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(ShowMainWindow);
            }
        })
        {
            IsBackground = true,
            Name = "WinShot.ShowSignal",
        };
        _showSignalThread.Start();
    }

    private void ShowMainWindow()
    {
        if (!_exitGate.AllowsSessionRecovery) return;
        // Keep the same window through hide/show and queued launch handoffs. IsLoaded is
        // not a lifetime check; only Closed clears this reference.
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow(_settings!, _history!, _capture!, _recording!, OpenSettings, OpenHistory);
            _mainWindow.HiddenToTray += ShowBackgroundHintOnce;
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    /// <summary>One-time notice that WinShot keeps running in the tray, so closing the window or a
    /// background (startup) launch doesn't feel like the app quit.</summary>
    private void ShowBackgroundHintOnce()
    {
        if (_settings is null || _settings.Current.HasShownTrayHint)
            return;
        _settings.Current.HasShownTrayHint = true;
        _settings.SaveCurrent();
        _tray?.ShowBalloon("Stillmark is still running",
            "Stillmark lives in your system tray. Click its icon to reopen it — if you don't see it, click the ^ arrow near the clock and drag Stillmark out to keep it visible. Choose Quit in its menu to exit.",
            null);
    }

    private void OnCaptureDelivered(CaptureDelivery delivery)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnCaptureDelivered(delivery));
            return;
        }

        Task task = HandleCaptureDeliveredAsync(delivery);
        lock (_deliveryTasksGate)
            _deliveryTasks.Add(task);

        _ = task.ContinueWith(
            completed =>
            {
                lock (_deliveryTasksGate)
                    _deliveryTasks.Remove(completed);
                _ = completed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleCaptureDeliveredAsync(CaptureDelivery delivery)
    {
        try
        {
            AppSettings s = _settings!.Current.Clone();
            string? filePath;
            if (delivery.IsRecording)
            {
                filePath = delivery.FilePath
                    ?? throw new InvalidOperationException("A recording delivery requires a backing file.");
                if (s.RecordingCopyFile)
                    ClipboardService.SetFile(filePath);
                if (s.RecordingOpenPlayer && delivery.IsVideo)
                    new VideoPlayerWindow(filePath).Show();
            }
            else
            {
                string id = Guid.NewGuid().ToString("N")[..8];
                if (delivery.SavedPath is not null)
                {
                    filePath = delivery.SavedPath;
                    bool added = _history!.Add(new HistoryEntry(
                        id,
                        delivery.Timestamp,
                        filePath,
                        false,
                        delivery.Image.PixelWidth,
                        delivery.Image.PixelHeight));
                    if (!added)
                        ShowHistoryPersistenceWarning(delivery);
                }
                else if (s.KeepHistoryOfUnsavedCaptures)
                {
                    try
                    {
                        filePath = await SaveManagedCopyAsync(delivery, id);
                        bool added = _history!.Add(new HistoryEntry(
                            id,
                            delivery.Timestamp,
                            filePath,
                            true,
                            delivery.Image.PixelWidth,
                            delivery.Image.PixelHeight));
                        if (!added)
                        {
                            filePath = delivery.BackingPath;
                            ShowHistoryPersistenceWarning(delivery);
                        }
                    }
                    catch (Exception ex)
                    {
                        // A History write must not suppress an otherwise successful clipboard
                        // capture or its Quick Access card.
                        LogError(ex);
                        filePath = delivery.BackingPath;
                        ShowHistoryPersistenceWarning(delivery);
                    }
                }
                else
                {
                    // Privacy opt-out: don't persist an unsaved capture into browsable History.
                    // Capture delivery creates a bounded transient only when clipboard or
                    // Quick Access needs a real file.
                    filePath = delivery.BackingPath;
                }
                if (s.ScreenshotOpenEditor)
                    new Editor.EditorWindow(delivery.Image, _settings).Show();
                if (s.ScreenshotPin)
                    new PinWindow(delivery.Image, delivery.SourceRect, _settings).Show();
            }

            if (s.PlaySound)
                System.Media.SystemSounds.Asterisk.Play();

            bool showCard = delivery.IsRecording ? s.RecordingShowQuickAccess : s.QuickAccessEnabled;
            if (showCard)
            {
                if (filePath is null && !delivery.IsRecording)
                    filePath = await WriteTransientCopyAsync(delivery, Guid.NewGuid().ToString("N")[..8]);
                if (filePath is null)
                    throw new InvalidOperationException("Quick Access requires a backing file.");
                _quickAccess!.Show(delivery, filePath);
            }
            else if (s.ShowNotification)
                _tray!.ShowBalloon(delivery.IsRecording ? "Recording ready" : "Screenshot captured",
                    delivery.Summary, delivery.SavedPath);
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    private void ShowHistoryPersistenceWarning(CaptureDelivery delivery)
    {
        _tray?.ShowBalloon(
            "Screenshot history unavailable",
            "The screenshot was captured, but Stillmark could not add its History entry.",
            delivery.SavedPath);
    }

    internal static void RecordSavedEditorImage(string path, BitmapSource image)
    {
        if (Current is not App { _history: { } history } app)
            return;
        try
        {
            if (history.RecordSavedImage(path, DateTime.Now, image.PixelWidth, image.PixelHeight))
                return;
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
        app._tray?.ShowBalloon("Image saved",
            "Your edited image was saved, but it could not be added to History.", path);
    }

    private async Task<string> SaveManagedCopyAsync(CaptureDelivery delivery, string id)
    {
        EncodedImagePayload payload = delivery.Payload
            ?? await StillImageEncodingService.EncodePngAsync(delivery.Image);
        string baseName = $"{delivery.Timestamp:yyyyMMdd_HHmmss}_{id}";
        return await ImageSaveService.SavePngCopyAsync(
            payload,
            _history!.HistoryDirectory,
            baseName,
            CancellationToken.None);
    }

    /// <summary>Writes a short-lived PNG (kept: last 15, in the Clipboard temp folder, not the browsable
    /// History) so an unsaved capture still has a file for quick-access when history persistence is off.</summary>
    private async Task<string?> WriteTransientCopyAsync(CaptureDelivery delivery, string id)
    {
        try
        {
            string dir = OwnedTransientFileRetention.ClipboardDirectory;
            await Task.Run(() =>
            {
                Directory.CreateDirectory(dir);
                OwnedTransientFileRetention.PurgeClipboard(DateTime.UtcNow);
            });
            EncodedImagePayload payload = delivery.Payload
                ?? await StillImageEncodingService.EncodePngAsync(delivery.Image);
            string baseName = $"Relic_Screenshot_{delivery.Timestamp:yyyyMMdd_HHmmss}_{id}";
            return await ImageSaveService.SavePngCopyAsync(
                payload,
                dir,
                baseName,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogError(ex);
            return null;
        }
    }

#if DEBUG
    private static bool IsHermeticMainUiaPreviewRequest(string[] args)
    {
        return args.Length == 2
               && string.Equals(args[0], "--ui-preview", StringComparison.Ordinal)
               && string.Equals(args[1], "main-uia", StringComparison.Ordinal);
    }

    private static bool TryPrepareHermeticMainUiaDiagnostics()
    {
        try
        {
            string root = ValidateMainUiaRoot(
                Environment.GetEnvironmentVariable("WINSHOT_MAIN_UIA_ROOT"));
            _diagnosticDirectoryOverride = Path.Combine(root, "diagnostics");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Dev CLI: "Stillmark --ui-preview main|settings|history" opens one window standalone for design review.</summary>
    private bool TryShowUiPreview(string[] args)
    {
        int index = Array.IndexOf(args, "--ui-preview");
        if (index < 0)
            return false;

        ShutdownMode = ShutdownMode.OnLastWindowClose;
        string kind = args.Length > index + 1 ? args[index + 1] : "settings";
        bool isHermeticMainUia = string.Equals(
            kind,
            "main-uia",
            StringComparison.Ordinal);
        DispatcherUnhandledException += (_, ex) =>
        {
            LogError(ex.Exception);
            ex.Handled = !isHermeticMainUia;
        };
        try
        {
            if (isHermeticMainUia
                && (index != 0 || args.Length != 2))
            {
                throw new ArgumentException(
                    "The main UIA preview requires exactly two arguments.");
            }
            ShowPreviewWindow(kind);
        }
        catch (Exception ex)
        {
            if (!isHermeticMainUia || _diagnosticDirectoryOverride is not null)
                LogError(ex);
            Shutdown(1);
        }
        return true;
    }

    private void ShowPreviewWindow(string kind)
    {
        string? previewAppearance = Environment.GetEnvironmentVariable("WINSHOT_UI_PREVIEW_THEME");
        ApplicationTheme.Apply(
            string.Equals(previewAppearance, "Light", StringComparison.OrdinalIgnoreCase)
                ? AppearanceKind.Light
                : AppearanceKind.Dark);

        if (kind == "main-uia")
        {
            ShowHermeticMainUiaPreview();
            return;
        }

        // Overlay previews: open the transparent overlay standalone at a fixed spot for design review.
        if (kind is "recpanel" or "aiotoolbar")
        {
            var mon = MonitorService.GetMonitors().First(m => m.IsPrimary).Bounds;
            var region = new WinShot.Core.PixelRect(mon.X + mon.Width / 2 - 300, mon.Y + mon.Height / 2 - 220, 600, 440);
            var settings = new SettingsManager(new SettingsStore());
            if (kind == "recpanel")
                new Overlays.RecordingOptionsPanel(region, settings, onRecordVideo: () => { }, onRecordGif: () => { },
                    onBack: () => { }, onFullscreen: _ => { }, onAspect: _ => { }).Show();
            else
            {
                var toolbar = new Overlays.AllInOneToolbar(region, Overlays.RegionMode.Area,
                    _ => { }, _ => { }, () => { }, () => { }, () => { }, () => { })
                {
                    ShowActivated = true,
                    ShowInTaskbar = true,
                };
                toolbar.Show();
            }
            return;
        }

        Window window = kind switch
        {
            "main" => CreateMainPreview(),
            "history" => CreatePagePreview(new HistoryWindow(new HistoryService(), new SettingsManager(new SettingsStore()))),
            "editor" => new Editor.EditorWindow(
                GdiScreenCapture.CaptureRegion(MonitorService.GetMonitors().First(m => m.IsPrimary).Bounds),
                new SettingsManager(new SettingsStore())),
            "videoedit" => new Video.VideoEditorWindow(
                Directory.EnumerateFiles(new SettingsStore().Load().SaveDirectory, "*.mp4")
                    .OrderByDescending(File.GetLastWriteTime)
                    .First(),
                _updateActivity),
            "player" => new VideoPlayerWindow(
                Directory.EnumerateFiles(new SettingsStore().Load().SaveDirectory, "*.mp4")
                    .OrderByDescending(File.GetLastWriteTime)
                    .First()),
            _ => CreateSettingsPreview(),
        };
        window.Show();
    }

    private static Window CreateSettingsPreview()
    {
        var settings = new SettingsManager(new SettingsStore());
        var startupSettings = new ApplicationStartupSettingsController(
            ApplicationDeploymentKind.Unknown,
            settings,
            packaged: null,
            synchronizeUnpackaged: null);
        return CreatePagePreview(new SettingsWindow(
            settings,
            startupSettings,
            onHotkeysSaved: () => { },
            onStartupResult: (_, _) => { }));
    }

    private static Window CreatePagePreview(System.Windows.Controls.UserControl page)
    {
        var window = new Window { Content = page, Width = 920, Height = 760, Title = "Stillmark" };
        window.Closed += (_, _) => (page as IDisposable)?.Dispose();
        return window;
    }

    private Window CreateMainPreview()
    {
        var settings = new SettingsManager(new SettingsStore());
        string previewHistory = Path.Combine(
            Path.GetTempPath(),
            "WinShot",
            "UiPreview",
            "History");
        return new MainWindow(
            settings,
            new HistoryService(previewHistory),
            new CaptureController(settings, _updateActivity),
            new RecordingController(settings, _updateActivity),
            openSettings: () => { },
            openHistory: () => { });
    }

    private void ShowHermeticMainUiaPreview()
    {
        const string rootVariable = "WINSHOT_MAIN_UIA_ROOT";
        const string readyVariable = "WINSHOT_MAIN_UIA_READY_EVENT";
        const string shutdownVariable = "WINSHOT_MAIN_UIA_SHUTDOWN_EVENT";
        const string widthVariable = "WINSHOT_MAIN_UIA_WIDTH";

        string root = ValidateMainUiaRoot(
            Environment.GetEnvironmentVariable(rootVariable));
        string readyName = ValidateMainUiaEventName(
            Environment.GetEnvironmentVariable(readyVariable),
            "Ready");
        string shutdownName = ValidateMainUiaEventName(
            Environment.GetEnvironmentVariable(shutdownVariable),
            "Shutdown");
        double previewWidth = ValidateMainUiaWidth(
            Environment.GetEnvironmentVariable(widthVariable));

        if (Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("The main UIA fixture root must be empty.");

        _diagnosticDirectoryOverride = Path.Combine(root, "diagnostics");
        string profileRoot = Path.Combine(root, "profile");
        string fixtureRoot = Path.Combine(root, "fixtures");
        Directory.CreateDirectory(profileRoot);
        Directory.CreateDirectory(fixtureRoot);

        var settingsStore = new SettingsStore(
            Path.Combine(profileRoot, "settings.json"));
        var settings = new SettingsManager(settingsStore);
        AppSettings previewSettings = settings.Current.Clone();
        previewSettings.SaveDirectory = Path.Combine(root, "output");
        previewSettings.LaunchAtStartup = false;
        previewSettings.HasConfiguredStartup = true;
        if (!settings.Update(previewSettings).Succeeded)
            throw new IOException("Could not create the isolated UIA settings fixture.");

        string firstPath = Path.Combine(fixtureRoot, "capture-a.png");
        string secondPath = Path.Combine(fixtureRoot, "capture-b.png");
        string thirdPath = Path.Combine(fixtureRoot, "capture-c.png");
        string fourthPath = Path.Combine(fixtureRoot, "capture-d.png");
        string fifthPath = Path.Combine(fixtureRoot, "capture-e.png");
        SaveMainUiaFixturePng(firstPath, Color.FromRgb(99, 91, 238));
        SaveMainUiaFixturePng(secondPath, Color.FromRgb(78, 82, 94));
        SaveMainUiaFixturePng(thirdPath, Color.FromRgb(58, 64, 76));
        SaveMainUiaFixturePng(fourthPath, Color.FromRgb(42, 47, 57));
        SaveMainUiaFixturePng(fifthPath, Color.FromRgb(31, 35, 43));

        var history = new HistoryService(profileRoot);
        (string Id, DateTime Timestamp, string Path)[] fixtureEntries =
        [
            ("fixture-fifth", new DateTime(2026, 7, 29, 9, 11, 0, DateTimeKind.Local), fifthPath),
            ("fixture-fourth", new DateTime(2026, 7, 29, 9, 12, 0, DateTimeKind.Local), fourthPath),
            ("fixture-third", new DateTime(2026, 7, 29, 9, 13, 0, DateTimeKind.Local), thirdPath),
            ("fixture-second", new DateTime(2026, 7, 29, 9, 14, 0, DateTimeKind.Local), secondPath),
            ("fixture-first", new DateTime(2026, 7, 29, 9, 15, 0, DateTimeKind.Local), firstPath),
        ];
        if (fixtureEntries.Any(entry =>
                !history.Add(new HistoryEntry(
                    entry.Id,
                    entry.Timestamp,
                    entry.Path,
                    IsManagedCopy: false,
                    Width: 96,
                    Height: 60))))
        {
            throw new IOException("Could not create the isolated UIA history fixture.");
        }

        string activationPath = Path.Combine(root, "activation.ack");
        string[] allowedPaths =
        [
            Path.GetFullPath(firstPath),
            Path.GetFullPath(secondPath),
            Path.GetFullPath(thirdPath),
            Path.GetFullPath(fourthPath),
            Path.GetFullPath(fifthPath),
        ];
        Task<RecentCaptureActivationResult> RecordActivationAsync(
            string candidate,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fullPath = Path.GetFullPath(candidate);
                if (!allowedPaths.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                {
                    return new RecentCaptureActivationResult(
                        RecentCaptureActivationStatus.Unavailable);
                }

                File.AppendAllText(activationPath, "invoked" + Environment.NewLine);
                return new RecentCaptureActivationResult(
                    RecentCaptureActivationStatus.Opened);
            }, cancellationToken);
        }

        var ready = EventWaitHandle.OpenExisting(readyName);
        var shutdown = EventWaitHandle.OpenExisting(shutdownName);
        var window = new MainWindow(
            settings,
            history,
            new CaptureController(settings, _updateActivity),
            new RecordingController(settings, _updateActivity),
            openSettings: () => { },
            openHistory: () => { },
            RecordActivationAsync,
            DashboardCloseBehavior.CloseWindow);
        window.Width = previewWidth;
        int readySignaled = 0;
        window.RecentCapturesRefreshed += () =>
        {
            if (Interlocked.Exchange(ref readySignaled, 1) == 0)
                ready.Set();
        };
        window.Closed += (_, _) =>
        {
            ready.Dispose();
            shutdown.Dispose();
            Shutdown();
        };
        _ = Task.Run(() =>
        {
            shutdown.WaitOne();
            _ = Dispatcher.BeginInvoke(window.Close);
        });
        window.Show();
        window.Activate();
    }

    private static string ValidateMainUiaRoot(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            throw new ArgumentException("The main UIA fixture root is required.");

        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        string tempPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string? parent = Path.GetDirectoryName(fullPath);
        string leaf = Path.GetFileName(fullPath);
        const string prefix = "WinShotMainWindowUiaTests-";
        string suffix = leaf.StartsWith(prefix, StringComparison.Ordinal)
            ? leaf[prefix.Length..]
            : "";
        if (!string.Equals(parent, tempPath, StringComparison.OrdinalIgnoreCase)
            || suffix.Length != 32
            || suffix.Any(character => !Uri.IsHexDigit(character))
            || !Directory.Exists(fullPath)
            || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException("The main UIA fixture root is invalid.");
        }

        return fullPath;
    }

    private static double ValidateMainUiaWidth(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
            return 760;

        if (!int.TryParse(
                candidate,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int width)
            || width is not (640 or 760 or 900))
        {
            throw new ArgumentException("The main UIA fixture width is invalid.");
        }

        return width;
    }

    private static string ValidateMainUiaEventName(string? candidate, string role)
    {
        string prefix = $@"Local\WinShot.MainWindowUia.{role}.";
        string suffix = candidate?.StartsWith(prefix, StringComparison.Ordinal) == true
            ? candidate[prefix.Length..]
            : "";
        if (suffix.Length != 32 || suffix.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException($"The main UIA {role} event name is invalid.");
        return candidate!;
    }

    private static void SaveMainUiaFixturePng(string path, Color color)
    {
        const int width = 96;
        const int height = 60;
        const int bytesPerPixel = 4;
        int stride = width * bytesPerPixel;
        var pixels = new byte[stride * height];
        for (int offset = 0; offset < pixels.Length; offset += bytesPerPixel)
        {
            pixels[offset] = color.B;
            pixels[offset + 1] = color.G;
            pixels[offset + 2] = color.R;
            pixels[offset + 3] = byte.MaxValue;
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Hidden CLI: "Stillmark --capture-full [path]" captures all monitors headlessly and exits.</summary>
    private bool TryRunHeadlessCapture(string[] args)
    {
        int index = Array.IndexOf(args, "--capture-full");
        if (index < 0)
            return false;

        try
        {
            var image = GdiScreenCapture.CaptureRegion(MonitorService.GetVirtualScreenBounds());
            string path;
            if (args.Length > index + 1 && !args[index + 1].StartsWith("--"))
            {
                path = args[index + 1];
            }
            else
            {
                var settings = new SettingsStore().Load();
                Directory.CreateDirectory(settings.SaveDirectory);
                path = Path.Combine(settings.SaveDirectory, $"Relic_Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            LogError(ex);
            Environment.ExitCode = 1;
        }
        Shutdown();
        return true;
    }
#endif

    private IReadOnlyList<string> RegisterHotkeys(bool showFailureNotification = true)
    {
        _hotkeys!.UnregisterAll();
        var failures = new List<string>();
        Register(_settings!.Current.Hotkeys.AllInOne, "All-In-One", () => _capture!.StartAllInOneCapture());
        Register(_settings.Current.Hotkeys.AreaCapture, "Capture Area", () => _capture!.StartAreaCapture());
        Register(_settings.Current.Hotkeys.FullscreenCapture, "Capture Fullscreen", () => _capture!.CaptureFullscreenAtCursor());
        Register(_settings.Current.Hotkeys.AllMonitorsCapture, "Capture All Monitors", () => _capture!.CaptureAllMonitors());
        Register(_settings.Current.Hotkeys.RepeatLastArea, "Repeat Last Area", () => _capture!.CaptureLastArea());
        Register(_settings.Current.Hotkeys.TextCapture, "Capture Text", () => _capture!.StartTextCapture());
        Register(_settings.Current.Hotkeys.RecordScreen, "Record Screen", () => _recording!.ToggleRecording());

        if (showFailureNotification && failures.Count > 0)
            _tray!.ShowBalloon(ProductIdentity.DisplayName, "Hotkey(s) already in use by another app: " + string.Join(", ", failures), null);
        else if (failures.Count == 0)
            _hotkeyRecoveryFailureNotified = false;

        void Register(string gestureText, string actionName, Action action)
        {
            if (string.IsNullOrWhiteSpace(gestureText))
                return; // hotkey disabled by user
            if (!HotkeyGesture.TryParse(gestureText, out var gesture) || !_hotkeys.TryRegister(gesture, action))
                failures.Add($"{actionName} ({gestureText})");
        }

        return failures;
    }

    // ---- Keep global hotkeys alive across sleep/resume and lock/unlock ----

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (AppSessionPolicy.ShouldInterruptActiveWork(e.Mode))
            QueueActiveWorkInterruption();
        else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            QueueHotkeyRecovery();
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (AppSessionPolicy.ShouldInterruptActiveWork(e.Reason))
            QueueActiveWorkInterruption();
        else if (AppSessionPolicy.ShouldRecoverHotkeys(e.Reason))
            QueueHotkeyRecovery();
    }

    private void QueueHotkeyRecovery()
    {
        if (_shuttingDown || !_exitGate.AllowsSessionRecovery || Dispatcher.HasShutdownStarted)
            return;

        long recoveryTicket = _sessionTransitions.GetOrCreateRecoveryTicket();

        Dispatcher.BeginInvoke(
            () => _ = RecoverHotkeysAfterInterruptionAsync(recoveryTicket),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private async Task RecoverHotkeysAfterInterruptionAsync(long recoveryTicket)
    {
        SessionDrainRecoveryOutcome outcome =
            await _sessionDrainRecovery.RequestRecovery(recoveryTicket);
        if (outcome == SessionDrainRecoveryOutcome.Exhausted)
        {
            SessionRecoveryApplicationAction action =
                _sessionRecoveryApplications.Evaluate(
                    outcome,
                    recoveryTicket,
                    _sessionTransitions.LatestTicket,
                    stableDrain: false,
                    canContinue: CanContinueSessionDrainRecovery(recoveryTicket));
            if (action == SessionRecoveryApplicationAction.NotifyExhaustion)
                NotifySessionDrainRecoveryExhausted(recoveryTicket);
            return;
        }
        if (outcome != SessionDrainRecoveryOutcome.Recovered)
            return;

        // Let every already-queued normal-priority lock/suspend callback publish its drain before
        // the final availability decision.
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.Background);
        bool stableDrain;
        try
        {
            stableDrain = await SessionRecoverySequencer.WaitForStableDrainAsync(
                () => _activeWorkInterruptionTask);
        }
        catch (Exception ex)
        {
            LogError(ex);
            return;
        }

        Task<bool>? latestDrain = _activeWorkInterruptionTask;
        bool canEnable = stableDrain
            && CanContinueSessionDrainRecovery(recoveryTicket)
            && SessionRecoverySequencer.CanEnableRecovery(
                recoveryTicket,
                _sessionTransitions.LatestTicket,
                latestDrain);
        SessionRecoveryApplicationAction recoveredAction =
            _sessionRecoveryApplications.Evaluate(
                outcome,
                recoveryTicket,
                _sessionTransitions.LatestTicket,
                stableDrain,
                canEnable);
        if (recoveredAction
                != SessionRecoveryApplicationAction.RestoreCaptureAndHotkeys
            || !CanContinueSessionDrainRecovery(recoveryTicket))
        {
            return;
        }

        _sessionCaptureAvailable = true;
        BeginHotkeyRecovery();
    }

    private void QueueActiveWorkInterruption()
    {
        if (_shuttingDown || _exitGate.IsTerminal || Dispatcher.HasShutdownStarted)
            return;

        _sessionTransitions.BeginInterruption();
        _sessionDrainRecovery.CancelPending();
        Dispatcher.BeginInvoke(() =>
        {
            if (_shuttingDown || _exitGate.IsTerminal)
                return;

            _sessionCaptureAvailable = false;
            StopHotkeyRecovery();
            _hotkeys?.UnregisterAll();
            Task<bool> interruption = _exitDrains.GetOrStart(
                () => StopActiveWorkAsync(ActiveWorkInterruptionTimeout));
            _activeWorkInterruptionTask = interruption;
            _ = EnsureActiveWorkInterruptionObserved(interruption);
        });
    }

    private Task EnsureActiveWorkInterruptionObserved(
        Task<bool> interruption) =>
        _exitDrains.GetOrStartObservation(
            interruption,
            ObserveActiveWorkInterruptionAsync);

    private static async Task ObserveActiveWorkInterruptionAsync(Task<bool> interruption)
    {
        try
        {
            if (await interruption)
                return;
        }
        catch (OperationCanceledException)
        {
            LogError(new TimeoutException(
                "Active work cancellation did not complete before the Windows session deadline."));
            return;
        }
        catch (Exception ex)
        {
            LogError(ex);
            return;
        }

        LogError(new TimeoutException(
            "Active work did not drain before the Windows session deadline."));
    }

    private Task<bool> RetrySessionDrainAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "Session drain recovery must remain owned by the application dispatcher.");
        }

        Task<bool> retry = _exitDrains.GetOrStart(
            () => StopActiveWorkAsync(ActiveWorkInterruptionTimeout));
        _activeWorkInterruptionTask = retry;
        _ = EnsureActiveWorkInterruptionObserved(retry);
        return retry;
    }

    private bool CanContinueSessionDrainRecovery(long recoveryTicket) =>
        recoveryTicket == _sessionTransitions.LatestTicket
        && !_shuttingDown
        && _exitGate.AllowsSessionRecovery
        && !Dispatcher.HasShutdownStarted;

    private void NotifySessionDrainRecoveryExhausted(long recoveryTicket)
    {
        if (!CanContinueSessionDrainRecovery(recoveryTicket))
            return;

        LogError(new TimeoutException(
            "Session recovery remained unavailable after bounded active-work drain retries."));
        _tray?.ShowBalloon(
            "Stillmark recovery needs attention",
            "Stillmark could not safely resume capture after Windows interrupted active work. "
                + "Quit and reopen Stillmark before capturing again.",
            null);
    }

    /// <summary>
    /// Stops private capture and editor-output work before Windows locks, disconnects, or
    /// suspends. Recorder, capture, and editor drains run concurrently so the caller's timeout is
    /// a total bound, not one bound per subsystem. Scrolling capture is discarded because a
    /// partially observed locked session must never be delivered later.
    /// </summary>
    private async Task<bool> StopActiveWorkAsync(TimeSpan timeout)
    {
        _scrolling?.CancelActive();
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        Task<bool> captureDrain = _capture is null
            ? Task.FromResult(true)
            : _capture.CancelPendingOperationsAsync(timeout);
        Task<bool> recordingDrain = _recording is null
            ? Task.FromResult(true)
            : _recording.FinalizeAsync(timeout);
        Task editorDrain = Task.WhenAll(
            Windows
                .OfType<Editor.EditorWindow>()
                .Select(static editor => editor.CancelPendingOutputAsync()));

        try
        {
            Task subsystemDrains = Task.WhenAll(captureDrain, recordingDrain, editorDrain);
            Task completed = await Task.WhenAny(subsystemDrains, Task.Delay(timeout));
            if (!ReferenceEquals(completed, subsystemDrains))
                return false;

            await subsystemDrains;
            if (!captureDrain.Result || !recordingDrain.Result)
                return false;

            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero &&
                await DrainDeliveryTasksAsync(remaining);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogError(ex);
            return false;
        }
    }

    private async Task<bool> DrainDeliveryTasksAsync(TimeSpan timeout)
    {
        Task[] pending;
        lock (_deliveryTasksGate)
            pending = [.. _deliveryTasks];
        if (pending.Length == 0)
            return true;

        Task all = Task.WhenAll(pending);
        Task completed = await Task.WhenAny(all, Task.Delay(timeout));
        if (!ReferenceEquals(completed, all))
            return false;

        try
        {
            await all;
            return true;
        }
        catch (Exception ex)
        {
            LogError(ex);
            return false;
        }
    }

    /// <summary>Starts one bounded retry sequence after resume/unlock/reconnect. Repeated Windows
    /// events collapse into the active sequence instead of racing registrations or notifications.</summary>
    private void BeginHotkeyRecovery()
    {
        if (_shuttingDown ||
            !_exitGate.AllowsSessionRecovery ||
            _hotkeyRecoveryTimer is not null)
            return;

        _hotkeyRecoveryAttempt = 0;
        _hotkeyRecoveryTimer = new System.Windows.Threading.DispatcherTimer();
        _hotkeyRecoveryTimer.Tick += OnHotkeyRecoveryTick;
        ScheduleNextHotkeyRecoveryAttempt();
    }

    private void OnHotkeyRecoveryTick(object? sender, EventArgs e)
    {
        _hotkeyRecoveryTimer?.Stop();

        IReadOnlyList<string> failures;
        try
        {
            failures = RegisterHotkeys(showFailureNotification: false);
        }
        catch (Exception ex)
        {
            LogError(ex);
            failures = ["global shortcuts"];
        }

        _hotkeyRecoveryAttempt++;
        if (failures.Count == 0 || _shuttingDown || !_exitGate.AllowsSessionRecovery)
        {
            StopHotkeyRecovery();
            return;
        }

        if (_hotkeyRecoveryAttempt < HotkeyRecoveryDelays.Length)
        {
            ScheduleNextHotkeyRecoveryAttempt();
            return;
        }

        StopHotkeyRecovery();
        if (_hotkeyRecoveryFailureNotified)
            return;

        _hotkeyRecoveryFailureNotified = true;
        _tray?.ShowBalloon(
            "Stillmark shortcuts need attention",
            "Windows could not restore: " + string.Join(", ", failures)
                + ". Open Settings to review shortcuts or close the app using them.",
            null);
    }

    private void ScheduleNextHotkeyRecoveryAttempt()
    {
        if (_hotkeyRecoveryTimer is null)
            return;

        _hotkeyRecoveryTimer.Interval = HotkeyRecoveryDelays[_hotkeyRecoveryAttempt];
        _hotkeyRecoveryTimer.Start();
    }

    private void StopHotkeyRecovery()
    {
        if (_hotkeyRecoveryTimer is null)
            return;

        _hotkeyRecoveryTimer.Stop();
        _hotkeyRecoveryTimer.Tick -= OnHotkeyRecoveryTick;
        _hotkeyRecoveryTimer = null;
    }

    private IApplicationStartupSettingsController CreateStartupSettingsController(
        SettingsManager settings)
    {
        IPackagedStartupSettingsController? packaged = null;
        if (PackagedStartupRegistrationRoutePolicy.Evaluate(_deploymentKind)
            == StartupRegistrationRoute.PackagedStartupTask)
        {
            packaged = new PackagedStartupTaskController(
                new WindowsPackagedStartupTaskBackend(),
                failure => LogError(
                    new System.Runtime.InteropServices.COMException(
                        $"Packaged startup task {failure.Operation} failed.",
                        failure.HResult)));
        }

        return new ApplicationStartupSettingsController(
            _deploymentKind,
            settings,
            packaged,
            SynchronizeUnpackagedStartupRegistration);
    }

    private static StartupSynchronizationResult
        SynchronizeUnpackagedStartupRegistration(bool enabled)
    {
        try
        {
            string processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The current executable path is unavailable.");
            string localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            InstalledApplicationLayout? installedLayout =
                InstalledLaunchPolicy.DiscoverInstalledLayout(processPath, localAppData);
            StartupLaunchTarget launchTarget =
                InstalledLaunchPolicy.Resolve(processPath, installedLayout);
            return StartupService.SynchronizeBrandMigration(
                enabled,
                launchTarget,
                ProductIdentity.StartupValueName,
                ProductIdentity.LegacyStartupValueName,
                launchTarget.ExecutablePath);
        }
        catch (Exception ex)
        {
            return new StartupSynchronizationResult(
                StartupSynchronizationStatus.RegistryAccessFailed,
                ApplicationPathKind.Development,
                ex);
        }
    }

    private void ObserveStartupSettingsResult(
        ApplicationStartupSettingsResult result,
        bool explicitPreferenceChange)
    {
        if (result.SettingsSaveStatus == SettingsSaveStatus.ReadOnly)
            ShowSettingsReadOnlyWarningOnce();
        else if (result.SettingsSaveStatus == SettingsSaveStatus.Failed)
            LogError(new IOException("The startup preference could not be saved."));

        if (result.SynchronizationResult is not { } synchronization)
            return;

        if (synchronization.Failure is not null)
            LogError(synchronization.Failure);
        if (explicitPreferenceChange && synchronization.RequiresUserAttention)
            ShowStartupRegistrationWarningOnce(synchronization.Status);
    }

    private void OpenSettings()
    {
        if (!_exitGate.AllowsSessionRecovery) return;
        ShowMainWindow();
        if (_mainWindow!.CurrentPage is SettingsWindow) return;
        var page = new SettingsWindow(
            _settings!, _startupSettings!,
            onHotkeysSaved: () => { StopHotkeyRecovery(); RegisterHotkeys(); },
            ObserveStartupSettingsResult);
        page.ConfigureUpdates(IsUpdateCovered, RestartForUpdateAsync);
        _mainWindow.ShowPage(page);
    }

    private void OpenHistory()
    {
        if (!_exitGate.AllowsSessionRecovery) return;
        ShowMainWindow();
        if (_mainWindow!.CurrentPage is HistoryWindow) return;
        _mainWindow.ShowPage(new HistoryWindow(_history!, _settings!));
    }

    private void OpenRecordingRecovery()
    {
        if (!_exitGate.AllowsSessionRecovery) return;
        if (_recordingRecoveryWindow is { IsLoaded: true })
        {
            _recordingRecoveryWindow.Activate();
            return;
        }

        _recordingRecoveryWindow = new RecordingRecoveryWindow(
            new RecordingRecoveryService(),
            CreateRecordingRecoveryContext);
        _recordingRecoveryWindow.Closed += (_, _) => _recordingRecoveryWindow = null;
        _recordingRecoveryWindow.Show();
        _recordingRecoveryWindow.Activate();
    }

    private RecordingRecoveryContext CreateRecordingRecoveryContext()
    {
        string saveDirectory = _settings!.Current.SaveDirectory;
        return new RecordingRecoveryContext(
            saveDirectory,
            [
                new RecordingOrphanRoot(
                    RecordingOrphanRootKind.MediaTemp,
                    OwnedTransientFileRetention.MediaDirectory),
                new RecordingOrphanRoot(
                    RecordingOrphanRootKind.SaveDirectory,
                    saveDirectory),
            ],
            _recording!.SnapshotActiveWorkspacePaths());
    }

    private void OpenDiagnosticSummary()
    {
        if (_diagnosticSummaryWindow is { IsLoaded: true })
        {
            _diagnosticSummaryWindow.Activate();
            return;
        }

        Version osVersion = Environment.OSVersion.Version;
        string summary = PrivacySafeDiagnostics.FormatSupportSummary(
            new DiagnosticSupportSummaryInput(
                AppInfo.Version,
                osVersion.ToString(),
                osVersion.Build.ToString(System.Globalization.CultureInfo.InvariantCulture),
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                DateTimeOffset.UtcNow,
                _diagnosticCorrelationId));

        _diagnosticSummaryWindow = new DiagnosticSummaryWindow(summary);
        _diagnosticSummaryWindow.Closed += (_, _) => _diagnosticSummaryWindow = null;
        _diagnosticSummaryWindow.Show();
        _diagnosticSummaryWindow.Activate();
    }

    private static void OpenDiagnosticFolder()
    {
        try
        {
            string directory = ProductIdentity.DiagnosticDirectory;
            Directory.CreateDirectory(directory);
            ShellHelper.OpenFolder(directory);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException
                                   or InvalidOperationException
                                   or System.ComponentModel.Win32Exception)
        {
            LogError(ex);
            MessageBox.Show(
                "Stillmark could not open its local diagnostic folder.",
                "Diagnostic folder unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ShowSettingsReadOnlyWarningOnce()
    {
        if (_settingsReadOnlyWarningShown)
            return;
        _settingsReadOnlyWarningShown = true;
        _tray?.ShowBalloon(
            "Stillmark settings are read-only",
            "These settings were created by a newer Stillmark version. This older version will not modify them, and the existing startup registration was left unchanged.",
            null);
    }

    private void ShowStartupRegistrationWarningOnce(
        StartupSynchronizationStatus status)
    {
        if (_startupRegistrationWarningShown)
            return;
        _startupRegistrationWarningShown = true;

        (string title, string message) = status switch
        {
            StartupSynchronizationStatus.UnmanagedTarget => (
                "Install Stillmark to launch at startup",
                "This development or portable copy was not added to Windows startup. Install Stillmark, then enable this setting again."),
            StartupSynchronizationStatus.CommandTooLong => (
                "Windows startup path is too long",
                "Stillmark left the startup entry unchanged. Reinstall it in the standard location, then enable this setting again."),
            StartupSynchronizationStatus.ConflictingRegistrationPreserved => (
                "A different startup entry was preserved",
                "Stillmark did not remove a different entry with the same name. Review Windows Settings > Apps > Startup."),
            StartupSynchronizationStatus.RegistrationPartiallySynchronized => (
                "Windows startup needs attention",
                "Stillmark updated part of its startup registration, but legacy cleanup did not finish. Review Windows Settings > Apps > Startup."),
            _ => (
                "Windows startup setting was not updated",
                "Stillmark left the startup entry unchanged. Review Windows Settings > Apps > Startup, then try again."),
        };
        _tray?.ShowBalloon(title, message, null);
    }

    private async void Quit()
    {
        if (_quitInProgress || !_exitGate.TryBeginNormalQuit())
            return;

        _quitInProgress = true;
        _shuttingDown = true;
        _sessionCaptureAvailable = false;
        _licenseLifetime.Cancel();
        _sessionDrainRecovery.CancelPending();
        _applicationRestart.TryUnregister();
        StopHotkeyRecovery();
        _hotkeys?.UnregisterAll();

        await IntentionalQuitFinalizer.RunAsync(
            async () =>
            {
                Task<bool> drain = _exitDrains.GetOrStart(
                    () => StopActiveWorkAsync(QuitFinalizationTimeout));
                await EnsureActiveWorkInterruptionObserved(drain);
                return true;
            },
            LogError,
            () =>
            {
                try
                {
                    _tray?.Dispose();
                }
                catch (Exception ex)
                {
                    LogError(ex);
                }
                finally
                {
                    _tray = null;
                    Shutdown();
                }
            });
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Raise WPF's SessionEnding event first. If an application subscriber cancels the session,
        // recovery and background operation must remain enabled.
        base.OnSessionEnding(e);
        if (e.Cancel || !_exitGate.TryBeginSessionEnding())
            return;

        // Windows controls this deadline, so do not cancel logoff/shutdown or block the dispatcher.
        // Starting the same bounded, idempotent drain still gives the native recorder its best
        // opportunity to finalize a valid file before the process is terminated.
        _shuttingDown = true;
        _sessionCaptureAvailable = false;
        _licenseLifetime.Cancel();
        _sessionDrainRecovery.CancelPending();
        _applicationRestart.TryUnregister();
        StopHotkeyRecovery();
        _hotkeys?.UnregisterAll();
        Task<bool> interruption = _exitDrains.GetOrStart(
            () => StopActiveWorkAsync(ActiveWorkInterruptionTimeout));
        _activeWorkInterruptionTask = interruption;
        _ = EnsureActiveWorkInterruptionObserved(interruption);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        _shuttingDown = true;
        _licenseLifetime.Cancel();
        _sessionDrainRecovery.CancelPending();
        StopHotkeyRecovery();
        _scrolling?.CancelActive();
        if (_exitGate.IsTerminal)
            _applicationRestart.TryUnregister();
        try
        {
            _singleInstance?.WakeListener(); // wake the listener so it observes shutdown
            if (_showSignalThread is { IsAlive: true } signalThread &&
                signalThread != Thread.CurrentThread)
            {
                signalThread.Join(TimeSpan.FromMilliseconds(500));
            }
            _singleInstance?.Dispose();
        }
        catch { /* teardown is best-effort */ }
        _showSignalThread = null;
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _singleInstance = null;
        _licenseLifetime.Dispose();
        _exitGate.MarkExited();
        base.OnExit(e);
    }

    private static readonly object LogLock = new();
#if DEBUG
    private static string? _diagnosticDirectoryOverride;
#endif

    internal static void LogError(Exception ex)
    {
        try
        {
            string dir =
#if DEBUG
                _diagnosticDirectoryOverride ??
#endif
                ProductIdentity.DiagnosticDirectory;
            Directory.CreateDirectory(dir);
            string logPath = Path.Combine(dir, "error.log");
            string text = PrivacySafeDiagnostics.Format(ex, DateTimeOffset.UtcNow);

            lock (LogLock)
            {
                // Rotate at ~1 MB (error.log -> error.1.log) so the log can't grow without bound.
                try
                {
                    var info = new FileInfo(logPath);
                    if (info.Exists && info.Length > 1_000_000)
                    {
                        string rolled = Path.Combine(dir, "error.1.log");
                        if (File.Exists(rolled))
                            File.Delete(rolled);
                        File.Move(logPath, rolled);
                    }
                }
                catch { /* rotation is best-effort */ }
                File.AppendAllText(logPath, text);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

}
