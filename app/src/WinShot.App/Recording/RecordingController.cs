using System.IO;
using System.Windows.Media.Imaging;
using ScreenRecorderLib;
using WinShot.App.Overlays;
using WinShot.App.Video;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Core.Naming;
using WinShot.Core.Settings;
using WinShot.Core.Update;

namespace WinShot.App.Recording;

/// <summary>
/// Screen recording flows: pick a region with the selection overlay, count down,
/// record MP4 (H.264 + optional system/mic audio) via Windows.Graphics.Capture,
/// with a floating control bar and an out-of-frame region border.
/// </summary>
public sealed class RecordingController
{
    private readonly SettingsManager _settings;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly RecordingCapabilityGuard _capabilityGuard;
    private readonly IUpdateActivityCoordinator _updateActivity;
    private readonly RecorderCommandState _commandState = new();
    private readonly SerialRecorderCommandQueue _nativeCommands = new();
    // Accessed only from _nativeCommands operations.
    private readonly Dictionary<long, Recorder> _nativeRecorders = new();
    // Dispatcher-owned conversion operations. Each keeps its source MP4 until terminal cleanup.
    private readonly Dictionary<long, GifConversionOperation> _gifConversions = new();
    // Dispatcher-owned native output workspaces. The native recorder receives only NativePath;
    // completed media is published to its final collision-safe name before delivery.
    private readonly Dictionary<long, RecordingOutputOwnership> _recordingOutputs = new();

    private SelectionSession? _pickSession;
    private RecordingBarWindow? _bar;
    private RecordingFrameWindow? _frame;
    private KeystrokeOverlayWindow? _keystrokes;
    private System.Windows.Threading.DispatcherTimer? _countdown;
    private BitmapSource? _thumbnail;
    private PixelRect _region;
    private PixelRect _restartRegion;
    private bool _asGif;
    private bool _saveToFile;
    private bool _starting;
    // Dispatcher-owned. This lease starts before selection/countdown UI and is transferred to a
    // GIF operation when the native recording moves atomically into transcode finalization.
    private UpdateActivityLease? _recordingActivity;
    private long _terminalCompletionGeneration;
    private TaskCompletionSource<bool>? _terminalCompletion;
    private bool _finalizationInProgress;

    public bool IsRecording { get; private set; }

    /// <summary>Raised with the finished recording (thumbnail card + file path).</summary>
    public event Action<CaptureDelivery>? Delivered;

    public event Action<string, string, string?>? Notify;

    /// <summary>Same screen-preparation hooks as the capture controller (hide quick-access cards).</summary>
    public Action? PrepareScreen;
    public Action? ReleaseScreen;

    public RecordingController(
        SettingsManager settings,
        IUpdateActivityCoordinator updateActivity)
        : this(settings, RecordingCapabilityGuard.CreateDefault(), updateActivity)
    {
    }

    internal RecordingController(
        SettingsManager settings,
        RecordingCapabilityGuard capabilityGuard,
        IUpdateActivityCoordinator updateActivity)
    {
        _settings = settings;
        ArgumentNullException.ThrowIfNull(capabilityGuard);
        ArgumentNullException.ThrowIfNull(updateActivity);
        _capabilityGuard = capabilityGuard;
        _updateActivity = updateActivity;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
    }

    /// <summary>License gate (see <see cref="CaptureController.UsageGate"/>). Guards only the start
    /// paths — stopping an in-progress recording is never blocked.</summary>
    public Func<bool>? UsageGate { get; set; }

    /// <summary>
    /// Dispatcher snapshot used by the explicit recovery screen so an in-flight recorder workspace
    /// is never presented as abandoned. Paths remain internal and are not exposed by its UI model.
    /// </summary>
    internal IReadOnlySet<string> SnapshotActiveWorkspacePaths()
    {
        if (!_dispatcher.CheckAccess())
            throw new InvalidOperationException("Recording state must be read on the app dispatcher.");

        return _recordingOutputs.Values
            .Select(output => output.WorkspaceDirectory)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private bool Denied() => UsageGate is { } gate && !gate();

    private bool TryBeginRecordingActivity()
    {
        if (_recordingActivity is not null)
            return true;

        if (!_updateActivity.TryBeginActivity(
                UpdateActivityKind.Recording,
                out UpdateActivityLease? activity))
        {
            Notify?.Invoke(
                "Stillmark",
                "An update restart is being prepared. Try recording again in a moment.",
                null);
            return false;
        }

        _recordingActivity = activity;
        return true;
    }

    private void ReleaseRecordingActivity()
    {
        UpdateActivityLease? activity = _recordingActivity;
        _recordingActivity = null;
        activity?.Dispose();
    }

    private bool RecordingCapabilityDenied()
    {
        RecordingCapabilityResult result = _capabilityGuard.GetResult();
        if (result.IsAvailable)
            return false;

        Notify?.Invoke("Screen recording unavailable", result.UserMessage, null);
        ReleaseRecordingActivity();
        return true;
    }

    /// <summary>Hotkey entry point: starts region pick, or stops the active recording.</summary>
    public void ToggleRecording()
    {
        if (_finalizationInProgress
            || _commandState.HasActiveTake
            || IsRecording
            || _starting
            || _countdown is not null)
            Stop();
        else
            StartRegionRecording();
    }

    public void StartRegionRecording()
    {
        if (Denied()) return;
        if (_finalizationInProgress
            || _commandState.HasActiveTake
            || IsRecording
            || _starting
            || _pickSession is not null
            || _countdown is not null)
            return;
        if (RecordingCapabilityDenied()) return;
        if (!TryBeginRecordingActivity()) return;
        _asGif = false;
        try
        {
            var monitors = MonitorService.GetMonitors();
            var virtualBounds = MonitorService.GetVirtualScreenBounds();
            var topWindows = WindowService.GetTopLevelWindows(virtualBounds);

            PrepareScreen?.Invoke();
            BitmapSource frozen;
            try
            {
                frozen = GdiScreenCapture.CaptureRegion(virtualBounds);
            }
            finally
            {
                ReleaseScreen?.Invoke();
            }

            var session = new SelectionSession(monitors, virtualBounds, frozen, topWindows);
            _pickSession = session;
            session.Completed += result =>
            {
                _pickSession = null;
                if (result is null)
                {
                    ReleaseRecordingActivity();
                    return;
                }
                if (result.Rect.Width < 34 || result.Rect.Height < 34)
                {
                    ReleaseRecordingActivity();
                    Notify?.Invoke("Stillmark", "That area is too small to record.", null);
                    return;
                }
                BeginCountdown(result.Rect);
            };
            session.Show();
        }
        catch (Exception ex)
        {
            _pickSession = null;
            ReleaseRecordingActivity();
            App.LogError(ex);
            Notify?.Invoke(
                "Stillmark",
                RecordingFailureNotice.StartFailure(preservedPath: null),
                null);
        }
    }

    /// <summary>Record a region chosen elsewhere (all-in-one toolbar) — countdown then record.</summary>
    public void StartRegionRecordingOf(PixelRect region, bool asGif = false)
    {
        if (Denied()) return;
        if (_finalizationInProgress
            || _commandState.HasActiveTake
            || IsRecording
            || _starting
            || _countdown is not null)
            return;
        if (RecordingCapabilityDenied()) return;
        _asGif = asGif;
        BeginCountdown(region);
    }

    public void RecordFullscreenAtCursor()
    {
        if (Denied()) return;
        if (_finalizationInProgress
            || _commandState.HasActiveTake
            || IsRecording
            || _starting
            || _countdown is not null)
            return;
        if (RecordingCapabilityDenied()) return;
        _asGif = false;
        var (x, y) = MonitorService.GetCursorPosition();
        BeginCountdown(MonitorService.GetMonitorAt(x, y).Bounds);
    }

    private void BeginCountdown(PixelRect requestedRegion)
    {
        if (RecordingCapabilityDenied()) return;
        if (!RecordingRegionPolicy.TryUseSingleDisplay(
                requestedRegion,
                MonitorService.GetMonitors(),
                out _region))
        {
            ReleaseRecordingActivity();
            Notify?.Invoke(
                "Stillmark",
                "Recording areas must stay within one display. Select an area on a single screen.",
                null);
            return;
        }
        var monitor = MonitorService.GetMonitorAt(
            _region.X + _region.Width / 2,
            _region.Y + _region.Height / 2);

        if (!TryBeginRecordingActivity())
            return;

        long generation = 0;
        try
        {
            _thumbnail = GdiScreenCapture.CaptureRegion(_region);
            generation = _commandState.BeginTake();
            _terminalCompletionGeneration = generation;
            _terminalCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _frame = new RecordingFrameWindow(_region, _settings.Current.DimWhileRecording);
            _frame.Show();
            if (_settings.Current.ShowRecordingControls)
            {
                _bar = new RecordingBarWindow(_region,
                    onStop: Stop, onCancel: Stop, onRestart: Restart, onDelete: Delete,
                    onPauseToggle: OnPauseToggle, showTime: _settings.Current.DisplayRecordingTime);
                _bar.Show();
            }

            int remaining = 3;
            _bar?.ShowCountdown(remaining);
            _countdown = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _countdown.Tick += (_, _) =>
            {
                remaining--;
                if (remaining > 0)
                {
                    _bar?.ShowCountdown(remaining);
                    return;
                }
                _countdown!.Stop();
                _countdown = null;
                StartRecorder(monitor, generation);
            };
            _countdown.Start();
        }
        catch
        {
            if (generation != 0)
                CompleteWithoutNativeRecorder(generation, notifyFailure: false);
            else
                ReleaseRecordingActivity();
            throw;
        }
    }

    private void StartRecorder(MonitorInfo monitor, long generation)
    {
        if (!_commandState.TryBeginNativeStart(generation))
            return;
        _starting = true;
        try
        {
            var settings = _settings.Current;
            _saveToFile = settings.RecordingSaveToFile;
            string baseName = FileNameTemplate.Expand(settings.FileNameTemplate, DateTime.Now);
            string outputDirectory;
            if (_asGif || !_saveToFile)
            {
                // Keep transient MP4 output out of the user's capture folder.
                // GIF intermediates and unsaved recordings stay out of SaveDirectory and in an
                // app-owned temporary folder until their requested after-capture actions finish.
                string tmpDir = GetMediaTempDirectory();
                Directory.CreateDirectory(tmpDir);
                OwnedTransientFileRetention.PurgeMedia(DateTime.UtcNow);
                outputDirectory = tmpDir;
            }
            else
            {
                Directory.CreateDirectory(settings.SaveDirectory);
                outputDirectory = settings.SaveDirectory;
            }

            var output = RecordingOutputOwnership.Create(
                outputDirectory,
                baseName,
                ".mp4");
            _recordingOutputs[generation] = output;

            var source = new DisplayRecordingSource(monitor.DeviceName)
            {
                SourceRect = new ScreenRect(
                    _region.X - monitor.Bounds.X,
                    _region.Y - monitor.Bounds.Y,
                    _region.Width,
                    _region.Height),
            };

            var options = new RecorderOptions
            {
                SourceOptions = new SourceOptions { RecordingSources = { source } },
                OutputOptions = new OutputOptions { RecorderMode = RecorderMode.Video },
                VideoEncoderOptions = new VideoEncoderOptions
                {
                    Encoder = new H264VideoEncoder
                    {
                        BitrateMode = H264BitrateControlMode.Quality,
                        EncoderProfile = H264Profile.Main,
                    },
                    Quality = 70,
                    Framerate = Math.Clamp(settings.RecordingFps, 15, 60),
                    IsFixedFramerate = false,
                },
                AudioOptions = new AudioOptions
                {
                    IsAudioEnabled = settings.RecordSystemAudio || settings.RecordMicrophone,
                    IsOutputDeviceEnabled = settings.RecordSystemAudio,
                    IsInputDeviceEnabled = settings.RecordMicrophone,
                    // Null/empty falls back to the system default device.
                    AudioInputDevice = string.IsNullOrEmpty(settings.MicrophoneDeviceId) ? null : settings.MicrophoneDeviceId,
                    AudioOutputDevice = string.IsNullOrEmpty(settings.SystemAudioDeviceId) ? null : settings.SystemAudioDeviceId,
                },
                MouseOptions = new MouseOptions
                {
                    IsMousePointerEnabled = settings.RecordCursor,
                    IsMouseClicksDetected = settings.HighlightClicks,
                    MouseLeftClickDetectionColor = "#FFD60A",
                    MouseRightClickDetectionColor = "#4D8DFF",
                },
            };

            // Create, Record, Pause/Resume, Stop, and Dispose all share one background lane.
            // ScreenRecorderLib callbacks only publish generation-tagged work to the dispatcher.
            _ = StartNativeRecorderAsync(generation, options, output.NativePath);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            OnNativeStartFailed(generation);
        }
    }

    private Task StartNativeRecorderAsync(
        long generation,
        RecorderOptions options,
        string outputPath)
    {
        return _nativeCommands.Enqueue(() =>
        {
            Recorder? recorder = null;
            try
            {
                recorder = Recorder.CreateRecorder(options);
                _nativeRecorders[generation] = recorder;
                recorder.OnRecordingComplete += (_, e) =>
                    _ = _dispatcher.BeginInvoke(
                        () => OnComplete(generation, recorder, e.FilePath));
                recorder.OnRecordingFailed += (_, _) =>
                    _ = _dispatcher.BeginInvoke(
                        () => OnFailed(generation, recorder));
                recorder.Record(outputPath);
                _ = _dispatcher.BeginInvoke(() => OnNativeStarted(generation, recorder));
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                if (recorder is not null)
                {
                    if (_nativeRecorders.TryGetValue(generation, out Recorder? current)
                        && ReferenceEquals(current, recorder))
                    {
                        _nativeRecorders.Remove(generation);
                    }
                    try
                    {
                        recorder.Dispose();
                    }
                    catch (Exception disposeError)
                    {
                        App.LogError(disposeError);
                    }
                }
                _ = _dispatcher.BeginInvoke(() => OnNativeStartFailed(generation));
            }
        });
    }

    private void OnNativeStarted(long generation, Recorder recorder)
    {
        if (!_commandState.TryMarkNativeStarted(generation))
        {
            _ = DisposeNativeRecorderAsync(generation, recorder);
            return;
        }

        IsRecording = true;
        _starting = false;
        if (_commandState.Phase == RecorderTakePhase.Stopping)
            return;

        _bar?.EnterRecordingState();
        if (_settings.Current.ShowKeystrokes)
        {
            _keystrokes = new KeystrokeOverlayWindow(_region);
            _keystrokes.Show();
        }
    }

    private async Task PauseNativeRecorderAsync(long generation, bool paused)
    {
        try
        {
            await _nativeCommands.Enqueue(() =>
            {
                if (!_nativeRecorders.TryGetValue(generation, out Recorder? recorder))
                    throw new InvalidOperationException("The recorder is no longer active.");
                if (paused)
                    recorder.Pause();
                else
                    recorder.Resume();
            });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            _ = _dispatcher.BeginInvoke(
                () => _commandState.RollBackPause(generation, paused));
        }
    }

    private async Task StopNativeRecorderAsync(long generation)
    {
        Recorder? recorder = null;
        try
        {
            await _nativeCommands.Enqueue(() =>
            {
                if (_nativeRecorders.TryGetValue(generation, out recorder))
                    recorder.Stop();
            });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            if (recorder is not null)
            {
                Recorder failedRecorder = recorder;
                _ = _dispatcher.BeginInvoke(
                    () => OnFailed(generation, failedRecorder));
            }
        }
    }

    private Task DisposeNativeRecorderAsync(long generation, Recorder recorder)
    {
        return _nativeCommands.Enqueue(() =>
        {
            if (!_nativeRecorders.TryGetValue(generation, out Recorder? current)
                || !ReferenceEquals(current, recorder))
                return;

            _nativeRecorders.Remove(generation);
            try
            {
                recorder.Dispose();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        });
    }

    private void OnPauseToggle(bool paused)
    {
        long generation = _commandState.Generation;
        if (!_commandState.TryRequestPause(generation, paused))
            return;
        _ = PauseNativeRecorderAsync(generation, paused);
    }

    public void Stop() => StopInternal(RecorderStopIntent.Save);

    /// <summary>Discard the current take and immediately start a new one over the same region.</summary>
    public void Restart()
    {
        StopInternal(RecorderStopIntent.Restart);
    }

    /// <summary>Discard the current take without saving.</summary>
    public void Delete() => StopInternal(RecorderStopIntent.Delete);

    private void StopInternal(RecorderStopIntent intent)
    {
        long generation = _commandState.Generation;
        RecorderStopDecision decision = _commandState.RequestStop(generation, intent);
        if (!decision.Accepted)
            return;

        _countdown?.Stop();
        _countdown = null;
        if (intent == RecorderStopIntent.Restart)
            _restartRegion = _region;

        if (decision.RequiresNativeStop)
        {
            _ = StopNativeRecorderAsync(generation);
            return;
        }

        CompleteWithoutNativeRecorder(generation, notifyFailure: false);
    }

    private void OnComplete(long generation, Recorder recorder, string filePath)
    {
        Task nativeCleanup = DisposeNativeRecorderAsync(generation, recorder);
        if (!_commandState.TryAcceptTerminal(generation, out RecorderStopIntent intent))
            return;

        IsRecording = false;
        _starting = false;
        CloseUi();

        bool discard = intent is RecorderStopIntent.Restart or RecorderStopIntent.Delete;
        bool gif = _asGif;
        bool saveToFile = _saveToFile;
        bool gifActivityTransferred = false;
        PixelRect restartRegion = _restartRegion;
        try
        {
            if (discard)
            {
                DeleteOwnedNativeOutput(generation, filePath);
                _thumbnail = null;
                return;
            }

            filePath = PublishCompletedNativeOutput(generation, filePath);

            if (gif)
            {
                StartGifConversion(generation, filePath, saveToFile, nativeCleanup);
                gifActivityTransferred = true;
                return; // The MP4 remains owned until conversion reaches a terminal state.
            }

            if (!saveToFile)
            {
                // "Save to file" is off — keep only if the after-capture actions need it, else drop it.
                var s = _settings.Current;
                bool needsFile = RecordingDeliveryPolicy.NeedsTransientFile(s, isGif: false);
                if (!needsFile)
                {
                    TryDeleteFile(filePath);
                    _thumbnail = null;
                    Notify?.Invoke("Stillmark", "Recording finished (not saved).", null);
                    return;
                }
            }

            if (_thumbnail is not null && File.Exists(filePath))
            {
                long bytes = new FileInfo(filePath).Length;
                string summary = saveToFile
                    ? $"Recording saved as {Path.GetFileName(filePath)} ({bytes / 1024 / 1024.0:0.0} MB)"
                    : $"Recording ready ({bytes / 1024 / 1024.0:0.0} MB)";
                Delivered?.Invoke(new CaptureDelivery(
                    _thumbnail,
                    SavedPath: saveToFile ? filePath : null,
                    summary,
                    DateTime.Now,
                    _region,
                    IsVideo: true,
                    BackingPath: saveToFile ? null : filePath));
            }
            _thumbnail = null;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            string? preservedPath = PreserveFailedNativeOutput(generation);
            if (preservedPath is null && File.Exists(filePath))
                preservedPath = filePath;
            Notify?.Invoke(
                "Stillmark",
                RecordingFailureNotice.CompletionFailure(preservedPath),
                preservedPath);
            _thumbnail = null;
        }
        finally
        {
            if (gifActivityTransferred)
            {
                // StartGifConversion transferred the activity lease to a worker that also waits
                // for native recorder disposal before releasing transcode ownership.
                FinishAcceptedTake(generation);
            }
            else
            {
                _ = FinishAcceptedTakeAfterNativeCleanupAsync(
                    generation,
                    intent,
                    restartRegion,
                    nativeCleanup);
            }
        }
    }

    /// <summary>
    /// Converts the just-recorded MP4 to an animated GIF. The MP4 is removed only after the
    /// complete GIF reaches the configured save directory; conversion failures preserve it.
    /// </summary>
    private void StartGifConversion(
        long generation,
        string mp4Path,
        bool saveToFile,
        Task nativeCleanup)
    {
        UpdateActivityLease activity = _recordingActivity ??
            throw new InvalidOperationException(
                "The recording update-activity lease is unavailable.");
        activity.TransitionTo(UpdateActivityKind.Transcode);
        _recordingActivity = null;

        var operation = new GifConversionOperation(activity, nativeCleanup);
        _gifConversions[generation] = operation;
        PixelRect region = _region;
        BitmapSource? thumbnail = _thumbnail;
        _thumbnail = null;
        string saveDirectory = saveToFile
            ? _settings.Current.SaveDirectory
            : GetMediaTempDirectory();
        try
        {
            operation.Task = ConvertToGifAndDeliverAsync(
                generation,
                operation,
                mp4Path,
                saveToFile,
                saveDirectory,
                region,
                thumbnail);
        }
        catch
        {
            _gifConversions.Remove(generation);
            operation.Dispose();
            throw;
        }
        if (_commandState.ShouldCancelOwnedWork(generation))
            operation.Cancellation.Cancel();
    }

    private async Task ConvertToGifAndDeliverAsync(
        long generation,
        GifConversionOperation operation,
        string mp4Path,
        bool saveToFile,
        string saveDirectory,
        PixelRect region,
        BitmapSource? thumbnail)
    {
        // Defer execution so StartGifConversion can publish the owned Task before callbacks can
        // re-enter finalization through Notify/Delivered subscribers.
        await Task.Yield();
        await operation.NativeCleanup;
        CancellationToken cancellationToken = operation.Cancellation.Token;
        RecordingOutputOwnership? gifOutput = null;
        try
        {
            string baseName = Path.GetFileNameWithoutExtension(mp4Path);
            string? gifPath = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Notify?.Invoke("Stillmark", "Converting the full recording to GIF…", null);
                Directory.CreateDirectory(saveDirectory);
                gifOutput = RecordingOutputOwnership.Create(
                    saveDirectory,
                    baseName,
                    ".gif");
                await GifExportService.ConvertAsync(
                    mp4Path,
                    gifOutput.NativePath,
                    region.Width,
                    region.Height,
                    progress: null,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                gifPath = gifOutput.Publish();
            }
            catch (OperationCanceledException)
            {
                if (gifPath is not null)
                    TryDeleteFile(gifPath);
                else
                    TryDeleteOwnedOutput(gifOutput);
                await Task.Run(
                    () => PreserveFailedGifSource(mp4Path, saveDirectory),
                    CancellationToken.None);
                return;
            }
            catch (Exception ex)
            {
                TryDeleteOwnedOutput(gifOutput);
                App.LogError(ex);
                string preservedPath = await Task.Run(
                    () => PreserveFailedGifSource(mp4Path, saveDirectory),
                    CancellationToken.None);
                if (!IsCurrentGifOperation(generation, operation))
                    return;

                bool sourcePreserved = File.Exists(preservedPath);
                Notify?.Invoke(
                    "Stillmark",
                    RecordingFailureNotice.GifFailure(
                        sourcePreserved ? preservedPath : null),
                    sourcePreserved ? preservedPath : null);

                bool needsTransient = RecordingDeliveryPolicy.NeedsTransientFile(
                    _settings.Current,
                    isGif: false);
                if (thumbnail is not null && sourcePreserved && (saveToFile || needsTransient))
                {
                    long bytes = new FileInfo(preservedPath).Length;
                    string preservedName = Path.GetFileName(preservedPath);
                    string summary =
                        $"GIF conversion failed; MP4 preserved as {preservedName} ({bytes / 1024 / 1024.0:0.0} MB)";
                    Delivered?.Invoke(new CaptureDelivery(
                        thumbnail,
                        SavedPath: saveToFile ? preservedPath : null,
                        summary,
                        DateTime.Now,
                        region,
                        IsVideo: true,
                        BackingPath: saveToFile ? null : preservedPath));
                }
                return;
            }

            if (gifPath is null)
                return;
            if (!IsCurrentGifOperation(generation, operation))
            {
                TryDeleteFile(gifPath);
                await Task.Run(
                    () => PreserveFailedGifSource(mp4Path, saveDirectory),
                    CancellationToken.None);
                return;
            }

            TryDeleteFile(mp4Path);
            if (!saveToFile
                && !RecordingDeliveryPolicy.NeedsTransientFile(_settings.Current, isGif: true))
            {
                TryDeleteFile(gifPath);
                Notify?.Invoke("Stillmark", "GIF recording finished (not saved).", null);
                return;
            }

            if (thumbnail is not null && File.Exists(gifPath))
            {
                long bytes = new FileInfo(gifPath).Length;
                string size = bytes >= 1024 * 1024
                    ? $"{bytes / 1024 / 1024.0:0.0} MB"
                    : $"{bytes / 1024.0:0} KB";
                string summary = saveToFile
                    ? $"GIF saved as {Path.GetFileName(gifPath)} ({size})"
                    : $"GIF ready ({size})";
                Delivered?.Invoke(new CaptureDelivery(
                    thumbnail,
                    SavedPath: saveToFile ? gifPath : null,
                    summary,
                    DateTime.Now,
                    region,
                    IsGif: true,
                    BackingPath: saveToFile ? null : gifPath));
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            await Task.Run(
                () => PreserveFailedGifSource(mp4Path, saveDirectory),
                CancellationToken.None);
        }
        finally
        {
            if (_gifConversions.TryGetValue(generation, out GifConversionOperation? current)
                && ReferenceEquals(current, operation))
            {
                _gifConversions.Remove(generation);
            }
            operation.Dispose();
        }
    }

    private bool IsCurrentGifOperation(long generation, GifConversionOperation operation) =>
        !operation.Cancellation.IsCancellationRequested
        && _gifConversions.TryGetValue(generation, out GifConversionOperation? current)
        && ReferenceEquals(current, operation);

    private static string GetMediaTempDirectory() => OwnedTransientFileRetention.MediaDirectory;

    private string PublishCompletedNativeOutput(long generation, string callbackPath)
    {
        if (!_recordingOutputs.TryGetValue(
                generation,
                out RecordingOutputOwnership? output))
        {
            return callbackPath;
        }
        if (!output.OwnsNativePath(callbackPath))
        {
            throw new InvalidDataException(
                "The recorder returned a path outside its owned output workspace.");
        }

        string publishedPath = output.Publish();
        _recordingOutputs.Remove(generation);
        return publishedPath;
    }

    private void DeleteOwnedNativeOutput(long generation, string callbackPath)
    {
        if (!_recordingOutputs.Remove(
                generation,
                out RecordingOutputOwnership? output))
        {
            // Legacy/unowned callbacks are preserved rather than risking deletion of another file.
            return;
        }
        if (!output.OwnsNativePath(callbackPath))
        {
            output.LeaveRecoverableOutput();
            App.LogError(new InvalidDataException(
                "The recorder returned a path outside its owned output workspace."));
            return;
        }

        try
        {
            output.DeleteOwnedOutput();
        }
        catch (Exception ex)
        {
            // An explicit discard must never expand into recursive workspace deletion. Log any
            // exact-file cleanup failure without turning it into a misleading publish failure.
            App.LogError(ex);
        }
    }

    private string? PreserveFailedNativeOutput(long generation)
    {
        if (!_recordingOutputs.Remove(
                generation,
                out RecordingOutputOwnership? output))
        {
            return null;
        }
        if (!File.Exists(output.NativePath))
        {
            output.LeaveRecoverableOutput();
            return null;
        }

        try
        {
            if (new FileInfo(output.NativePath).Length == 0)
            {
                output.DeleteOwnedOutput();
                return null;
            }
            return output.Publish();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            output.LeaveRecoverableOutput();
            return output.NativePath;
        }
    }

    private static void TryDeleteOwnedOutput(RecordingOutputOwnership? output)
    {
        if (output is null || output.IsPublished)
            return;
        try
        {
            output.DeleteOwnedOutput();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    private static string PreserveFailedGifSource(string sourcePath, string saveDirectory)
    {
        if (!File.Exists(sourcePath))
            return sourcePath;

        try
        {
            string sourceFullPath = Path.GetFullPath(sourcePath);
            string destinationDirectory = Path.GetFullPath(saveDirectory);
            if (string.Equals(
                    Path.GetDirectoryName(sourceFullPath),
                    destinationDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                return sourcePath;
            }

            Directory.CreateDirectory(destinationDirectory);
            string destination = RecordingOutputOwnership.CopyAndPublish(
                sourceFullPath,
                destinationDirectory,
                Path.GetFileNameWithoutExtension(sourceFullPath),
                ".mp4");
            TryDeleteFile(sourcePath);
            return destination;
        }
        catch (IOException)
        {
            return sourcePath;
        }
        catch (UnauthorizedAccessException)
        {
            return sourcePath;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void OnNativeStartFailed(long generation)
    {
        if (!_commandState.TryAcceptTerminal(generation, out _))
            return;

        IsRecording = false;
        _starting = false;
        CloseUi();
        _thumbnail = null;
        try
        {
            App.LogError(new InvalidOperationException(
                "The native recorder failed during startup."));
            string? preservedPath = PreserveFailedNativeOutput(generation);
            Notify?.Invoke(
                "Stillmark",
                RecordingFailureNotice.StartFailure(preservedPath),
                preservedPath);
        }
        finally
        {
            FinishAcceptedTake(generation);
        }
    }

    private void OnFailed(long generation, Recorder recorder)
    {
        Task nativeCleanup = DisposeNativeRecorderAsync(generation, recorder);
        if (!_commandState.TryAcceptTerminal(generation, out _))
            return;

        IsRecording = false;
        _starting = false;
        CloseUi();
        _thumbnail = null;
        try
        {
            App.LogError(new InvalidOperationException(
                "The native recorder reported a failure."));
            string? preservedPath = PreserveFailedNativeOutput(generation);
            Notify?.Invoke(
                "Stillmark",
                RecordingFailureNotice.RecordingFailure(preservedPath),
                preservedPath);
        }
        finally
        {
            _ = FinishAcceptedTakeAfterNativeCleanupAsync(
                generation,
                RecorderStopIntent.Save,
                default,
                nativeCleanup);
        }
    }

    private void CompleteWithoutNativeRecorder(long generation, bool notifyFailure)
    {
        if (!_commandState.TryAcceptTerminal(generation, out RecorderStopIntent intent))
            return;

        IsRecording = false;
        _starting = false;
        CloseUi();
        _thumbnail = null;
        PixelRect restartRegion = _restartRegion;

        try
        {
            if (notifyFailure)
                Notify?.Invoke("Stillmark", "Recording failed before the recorder started.", null);
        }
        finally
        {
            FinishAcceptedTake(
                generation,
                releaseActivity: intent != RecorderStopIntent.Restart);
        }
        if (intent == RecorderStopIntent.Restart)
            BeginCountdown(restartRegion);
    }

    private void FinishAcceptedTake(long generation, bool releaseActivity = true)
    {
        _commandState.FinishTerminal(generation);
        if (releaseActivity)
            ReleaseRecordingActivity();
        SignalTerminal(generation);
    }

    private async Task FinishAcceptedTakeAfterNativeCleanupAsync(
        long generation,
        RecorderStopIntent intent,
        PixelRect restartRegion,
        Task nativeCleanup)
    {
        try
        {
            await nativeCleanup;
        }
        catch (Exception ex)
        {
            // DisposeNativeRecorderAsync handles recorder failures itself. This guard preserves
            // activity accounting if the serial command queue rejects the operation unexpectedly.
            App.LogError(ex);
        }

        FinishAcceptedTake(
            generation,
            releaseActivity: intent != RecorderStopIntent.Restart);
        if (intent != RecorderStopIntent.Restart)
            return;

        try
        {
            BeginCountdown(restartRegion);
        }
        catch (Exception ex)
        {
            ReleaseRecordingActivity();
            App.LogError(ex);
            Notify?.Invoke(
                "Stillmark",
                RecordingFailureNotice.StartFailure(preservedPath: null),
                null);
        }
    }

    private void SignalTerminal(long generation)
    {
        if (_terminalCompletionGeneration == generation)
            _terminalCompletion?.TrySetResult(true);
    }

    /// <summary>
    /// Requests a normal save/stop for any active take and waits for its terminal callback plus
    /// queued native cleanup. A timeout leaves the existing operation running so recoverable media
    /// is not destroyed by forced disposal.
    /// </summary>
    public async Task<bool> FinalizeAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Task terminalTask;
            if (_dispatcher.CheckAccess())
            {
                terminalTask = RequestFinalizationOnDispatcher();
            }
            else
            {
                TimeSpan dispatchRemaining = Remaining(timeout, stopwatch.Elapsed);
                if (dispatchRemaining <= TimeSpan.Zero)
                    return false;
                terminalTask = await _dispatcher
                    .InvokeAsync(RequestFinalizationOnDispatcher)
                    .Task
                    .WaitAsync(dispatchRemaining, cancellationToken);
            }

            TimeSpan remaining = Remaining(timeout, stopwatch.Elapsed);
            if (remaining <= TimeSpan.Zero)
                return false;
            await terminalTask.WaitAsync(remaining, cancellationToken);

            remaining = Remaining(timeout, stopwatch.Elapsed);
            if (remaining <= TimeSpan.Zero)
                return false;
            Task gifFinalization;
            if (_dispatcher.CheckAccess())
            {
                gifFinalization = CancelAndSnapshotGifOperations();
            }
            else
            {
                gifFinalization = await _dispatcher
                    .InvokeAsync(CancelAndSnapshotGifOperations)
                    .Task
                    .WaitAsync(remaining, cancellationToken);
            }
            remaining = Remaining(timeout, stopwatch.Elapsed);
            if (remaining <= TimeSpan.Zero)
                return false;
            await gifFinalization.WaitAsync(remaining, cancellationToken);

            remaining = Remaining(timeout, stopwatch.Elapsed);
            if (remaining <= TimeSpan.Zero)
                return false;
            await _nativeCommands.DrainAsync(remaining, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            if (_dispatcher.CheckAccess())
                _finalizationInProgress = false;
            else
                _ = _dispatcher.BeginInvoke(() => _finalizationInProgress = false);
        }
    }

    private Task RequestFinalizationOnDispatcher()
    {
        _finalizationInProgress = true;
        SelectionSession? pickSession = _pickSession;
        _pickSession = null;
        if (pickSession is not null)
        {
            try
            {
                pickSession.Cancel();
            }
            finally
            {
                // Selection cancellation normally raises Completed(null), but finalization owns a
                // fail-safe release in case the overlay is already tearing down.
                ReleaseRecordingActivity();
            }
        }
        CancelGifOperations();

        long generation = _commandState.Generation;
        RecorderFinalizationDecision decision =
            _commandState.RequestFinalization(generation);
        if (decision.HasActiveTake)
        {
            _countdown?.Stop();
            _countdown = null;
            if (decision.RequiresNativeStop)
                _ = StopNativeRecorderAsync(generation);
            else if (decision.CompleteWithoutNative)
                CompleteWithoutNativeRecorder(generation, notifyFailure: false);
        }

        return _terminalCompletionGeneration == _commandState.Generation
            && _terminalCompletion is not null
                ? _terminalCompletion.Task
                : Task.CompletedTask;
    }

    private Task CancelAndSnapshotGifOperations()
    {
        GifConversionOperation[] operations = _gifConversions.Values.ToArray();
        foreach (GifConversionOperation operation in operations)
            operation.Cancellation.Cancel();
        return Task.WhenAll(operations.Select(operation => operation.Task));
    }

    private void CancelGifOperations()
    {
        foreach (GifConversionOperation operation in _gifConversions.Values.ToArray())
            operation.Cancellation.Cancel();
    }

    private static TimeSpan Remaining(TimeSpan timeout, TimeSpan elapsed) =>
        timeout - elapsed;

    private sealed class GifConversionOperation : IDisposable
    {
        private UpdateActivityLease? _activity;

        internal GifConversionOperation(
            UpdateActivityLease activity,
            Task nativeCleanup)
        {
            _activity = activity;
            NativeCleanup = nativeCleanup;
        }

        internal CancellationTokenSource Cancellation { get; } = new();
        internal Task NativeCleanup { get; }
        internal Task Task { get; set; } = Task.CompletedTask;

        public void Dispose()
        {
            Cancellation.Dispose();
            Interlocked.Exchange(ref _activity, null)?.Dispose();
        }
    }

    private void CloseUi()
    {
        _bar?.Close();
        _frame?.Close();
        _keystrokes?.Close();
        _bar = null;
        _frame = null;
        _keystrokes = null;
    }
}
