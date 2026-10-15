using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinShot.App.Overlays;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Core.Settings;
using WinShot.Core.Update;

namespace WinShot.App;

/// <summary>A finished capture, after clipboard/save delivery. SourceRect is the physical screen region it came from.</summary>
public sealed record CaptureDelivery(
    BitmapSource Image,
    string? SavedPath,
    string Summary,
    DateTime Timestamp,
    PixelRect SourceRect,
    bool IsVideo = false,
    bool IsGif = false,
    string? BackingPath = null,
    EncodedImagePayload? Payload = null)
{
    /// <summary>True for both MP4 and animated-GIF recording results.</summary>
    public bool IsRecording => IsVideo || IsGif;

    /// <summary>
    /// The file that currently backs this delivery. <see cref="SavedPath"/> is a user-requested
    /// export; <see cref="BackingPath"/> is an app-owned transient used when "Save to file" is off.
    /// </summary>
    public string? FilePath => SavedPath ?? BackingPath;
}

/// <summary>Orchestrates capture flows: freeze screen → select → crop → clipboard/save → hand off.</summary>
public sealed class CaptureController
{
    private readonly SettingsManager _settings;
    private readonly CaptureOperationCoordinator _operations = new();
    private readonly IUpdateActivityCoordinator _updateActivities;
    private readonly object _operationTasksGate = new();
    private readonly HashSet<Task> _operationTasks = [];
    private readonly OcrProgressOwnership _ocrProgressOwnership = new();
    private SelectionSession? _activeSession;
    private CountdownWindow? _countdown;
    private OcrProgressWindow? _ocrProgress;

    /// <summary>Raised after a capture lands (clipboard/file done) so the UI can show quick access / history.</summary>
    public event Action<CaptureDelivery>? Delivered;

    /// <summary>(title, message, savedFilePath) for errors and informational messages.</summary>
    public event Action<string, string, string?>? Notify;

    /// <summary>Hooks for hiding our own floating UI (quick-access cards) while the screen is frozen/captured.</summary>
    public Action? PrepareScreen;
    public Action? ReleaseScreen;

    public CaptureController(
        SettingsManager settings,
        IUpdateActivityCoordinator updateActivities)
    {
        _settings = settings;
        _updateActivities = updateActivities ??
            throw new ArgumentNullException(nameof(updateActivities));
    }

    private bool TryBeginCaptureActivity(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out CaptureActivityReference? activity)
    {
        if (!_updateActivities.TryBeginActivity(
                UpdateActivityKind.Capture,
                out UpdateActivityLease? lease))
        {
            Notify?.Invoke(
                "Stillmark",
                "An update is about to start. Try the capture again in a moment.",
                null);
            activity = null;
            return false;
        }

        activity = CaptureActivityReference.Create(lease);
        return true;
    }

    /// <summary>
    /// Invalidates delayed capture/OCR work, closes pending capture UI, and waits no longer than
    /// <paramref name="timeout"/> for already-running operations to observe cancellation.
    /// Returns false when work has not drained by the deadline.
    /// </summary>
    public async Task<bool> CancelPendingOperationsAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(timeout));

        _operations.CancelCurrent();

        void CancelUi()
        {
            _countdown?.CancelCountdown();
            _countdown = null;
            _activeSession?.Cancel();
            _activeSession = null;
            CloseCurrentOcrProgress();
        }

        var dispatcher = Application.Current?.Dispatcher;
        try
        {
            if (dispatcher is not null && !dispatcher.CheckAccess())
                await dispatcher.InvokeAsync(CancelUi);
            else
                CancelUi();
        }
        catch (Exception ex)
        {
            // Dispatcher shutdown may already be in progress. The operation generation/token has
            // still been invalidated, so record the UI-cleanup failure and continue the bound.
            App.LogError(ex);
        }

        Task[] pending;
        lock (_operationTasksGate)
            pending = [.. _operationTasks];
        if (pending.Length == 0)
            return true;

        Task all = Task.WhenAll(pending);
        Task completed = await Task.WhenAny(all, Task.Delay(timeout));
        if (!ReferenceEquals(completed, all))
            return false;

        try
        {
            await all;
        }
        catch (Exception ex)
        {
            // Operation wrappers normally absorb failures. Observe and privacy-safe log any
            // unexpected wrapper fault rather than leaking it through shutdown coordination.
            App.LogError(ex);
        }

        return true;
    }

    private long BeginLatestCapture()
    {
        long generation = _operations.BeginLatest();
        _countdown?.CancelCountdown();
        _countdown = null;
        SelectionSession? session = _activeSession;
        _activeSession = null;
        session?.Cancel();
        CloseCurrentOcrProgress();
        return generation;
    }

    private void StartObservedOperation(
        long generation,
        CaptureActivityReference activity,
        Func<CaptureOperation, CaptureActivityReference, Task> operation,
        string failureMessage)
    {
        if (!_operations.TryStart(generation, out CaptureOperation context))
            return;

        CaptureActivityReference operationActivity = activity.Retain();
        Task task = RunOperationAsync(
            context,
            operationActivity,
            operation,
            failureMessage);
        TrackOperationTask(task);
    }

    private void TrackOperationTask(Task task)
    {
        lock (_operationTasksGate)
            _operationTasks.Add(task);

        _ = task.ContinueWith(
            completed =>
            {
                lock (_operationTasksGate)
                    _operationTasks.Remove(completed);
                _ = completed.Exception; // Observe any fault not absorbed by RunOperationAsync.
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunOperationAsync(
        CaptureOperation context,
        CaptureActivityReference activity,
        Func<CaptureOperation, CaptureActivityReference, Task> operation,
        string failureMessage)
    {
        try
        {
            await operation(context, activity);
        }
        catch (OperationCanceledException) when (context.Token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            if (_operations.IsCurrent(context.Generation))
                Notify?.Invoke("Stillmark", failureMessage, null);
        }
        finally
        {
            _operations.Complete(context);
            activity.Dispose();
        }
    }

    /// <summary>
    /// License gate. Returns true when capture is permitted. When it returns false (e.g. the trial
    /// has expired), every public capture entry point aborts before showing any UI. Set by App to
    /// route blocked users to the activation window. Null = unrestricted (used in previews/tests).
    /// </summary>
    public Func<bool>? UsageGate { get; set; }

    private bool Denied() => UsageGate is { } gate && !gate();

    /// <summary>Raised when the all-in-one toolbar asks to record the selected region.</summary>
    public event Action<PixelRect>? RecordRequested;

    /// <summary>Raised when the recording panel asks to record the region as an animated GIF.</summary>
    public event Action<PixelRect>? RecordGifRequested;

    /// <summary>Raised when the all-in-one toolbar asks to start a scrolling capture on the region.</summary>
    public event Action<PixelRect>? ScrollRequested;

    /// <summary>Deliver an externally-produced image (e.g. a stitched scrolling capture) through the normal pipeline.</summary>
    public void DeliverImage(BitmapSource image, PixelRect sourceRect)
    {
        if (!TryBeginCaptureActivity(out CaptureActivityReference? activity))
            return;
        using (activity)
        {
            long generation = BeginLatestCapture();
            if (_operations.TryCommit(generation))
        Deliver(image, sourceRect, activity);
        }
    }

    public void StartAreaCapture()
    {
        if (Denied()) return;
        if (_activeSession is not null)
            return;
        if (!TryBeginCaptureActivity(out CaptureActivityReference? activity))
            return;
        StartSelection(textRecognition: false, BeginLatestCapture(), activity);
    }

    /// <summary>Area selection whose result is OCR'd to the clipboard instead of delivered as an image.</summary>
    public void StartTextCapture()
    {
        if (Denied()) return;
        if (_activeSession is not null)
            return;
        if (!TryBeginCaptureActivity(out CaptureActivityReference? activity))
            return;
        StartSelection(textRecognition: true, BeginLatestCapture(), activity);
    }

    /// <summary>
    /// All-in-one mode: the previous area comes pre-selected and adjustable, with a
    /// toolbar of capture modes (area / fullscreen / window / timer / OCR / record).
    /// </summary>
    public void StartAllInOneCapture()
    {
        if (Denied()) return;
        if (_activeSession is not null)
            return;
        if (!TryBeginCaptureActivity(out CaptureActivityReference? activity))
            return;
        long generation = BeginLatestCapture();

        try
        {
            var monitors = MonitorService.GetMonitors();
            var virtualBounds = MonitorService.GetVirtualScreenBounds();
            var topWindows = WindowService.GetTopLevelWindows(virtualBounds);

            PrepareScreen?.Invoke();
            BitmapSource frozen;
            try
            {
                frozen = GdiScreenCapture.CaptureRegion(
                    virtualBounds,
                    _settings.Current.ShowCursorOnScreenshots);
            }
            finally
            {
                ReleaseScreen?.Invoke();
            }

            var session = new SelectionSession(monitors, virtualBounds, frozen, topWindows, interactive: true);
            _activeSession = session;
            AllInOneToolbar? toolbar = null;
            RecordingOptionsPanel? recordPanel = null;
            PixelRect? preFullscreen = null;

            void CloseFloaters()
            {
                toolbar?.Close();
                toolbar = null;
                recordPanel?.Close();
                recordPanel = null;
            }

            bool timerOn = false;
            bool awaitingOcrArea = false;
            PixelRect? customRegion = null;

            static bool IsFullMonitor(PixelRect r)
            {
                var m = MonitorService.GetMonitorAt(r.X + r.Width / 2, r.Y + r.Height / 2);
                return r.X == m.Bounds.X && r.Y == m.Bounds.Y && r.Width == m.Bounds.Width && r.Height == m.Bounds.Height;
            }

            void CaptureRegion(PixelRect rect)
            {
                if (!_operations.TryCommit(generation))
                    return;
                using CaptureActivityReference handoffActivity = activity.Retain();
                CloseFloaters();
                session.Cancel(); // closes the overlay; Completed(null) fires
                var crop = new CroppedBitmap(frozen,
                    new System.Windows.Int32Rect(rect.X - virtualBounds.X, rect.Y - virtualBounds.Y, rect.Width, rect.Height));
                crop.Freeze();
        DeliverSelectionImage(
            crop,
            new SelectionResult(rect, session.CommittedWindow),
            handoffActivity);
            }

            // Execute the current selection as a capture — the ONLY thing that actually captures/saves.
            void DoCapture()
            {
                if (!_operations.IsCurrent(generation))
                {
                    CloseFloaters();
                    session.Cancel();
                    return;
                }
                if (session.Committed is not PixelRect rect)
                    return;
                try
                {
                    if (timerOn)
                    {
                        using CaptureActivityReference handoffActivity = activity.Retain();
                        CloseFloaters();
                        session.Cancel();
                        StartTimedCaptureOfRegion(rect, generation, handoffActivity);
                    }
                    else
                    {
                        CaptureRegion(rect);
                    }
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                    CloseFloaters();
                    session.Cancel();
                    Notify?.Invoke("Stillmark", "Capture failed. Try selecting the region again.", null);
                }
            }

            void RequestFreshOcrArea()
            {
                if (!_operations.IsCurrent(generation))
                    return;

                awaitingOcrArea = true;
                customRegion = null;
                preFullscreen = null;
                recordPanel?.Close();
                recordPanel = null;
                toolbar?.SetRegionMode(RegionMode.Area);
                session.BeginFreshAreaSelection();
            }

            // Region modes only change the selection — they never capture.
            void OnRegionMode(RegionMode mode)
            {
                try
                {
                    switch (mode)
                    {
                        case RegionMode.Area:
                            // Switch back to a custom area; restore the last one if we were in fullscreen.
                            if (session.Committed is PixelRect cur && IsFullMonitor(cur) && customRegion is PixelRect cr)
                                session.CommitSelection(cr);
                            break;

                        case RegionMode.Fullscreen:
                            if (session.Committed is PixelRect c2 && !IsFullMonitor(c2))
                                customRegion = c2;
                            int fcx, fcy;
                            if (session.Committed is PixelRect cc) { fcx = cc.X + cc.Width / 2; fcy = cc.Y + cc.Height / 2; }
                            else { (fcx, fcy) = MonitorService.GetCursorPosition(); }
                            session.CommitSelection(MonitorService.GetMonitorAt(fcx, fcy).Bounds);
                            break;

                        case RegionMode.Window:
                            session.ClearSelection(); // hover-pick; next click commits the window's bounds
                            break;
                    }
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                }
            }

            void OpenRecordPanel()
            {
                if (session.Committed is not PixelRect rect)
                    return;
                try
                {
                    toolbar?.Hide();
                    recordPanel = new RecordingOptionsPanel(rect, _settings,
                        onRecordVideo: () =>
                        {
                            using CaptureActivityReference handoffActivity = activity.Retain();
                            var cur = session.Committed ?? rect;
                            CloseFloaters();
                            session.Cancel();
                            RecordRequested?.Invoke(cur);
                        },
                        onRecordGif: () =>
                        {
                            using CaptureActivityReference handoffActivity = activity.Retain();
                            var cur = session.Committed ?? rect;
                            CloseFloaters();
                            session.Cancel();
                            RecordGifRequested?.Invoke(cur);
                        },
                        onBack: () =>
                        {
                            recordPanel?.Close();
                            recordPanel = null;
                            toolbar?.Show();
                        },
                        onFullscreen: full =>
                        {
                            if (full)
                            {
                                preFullscreen = session.Committed;
                                var c = session.Committed ?? rect;
                                var mon = MonitorService.GetMonitorAt(c.X + c.Width / 2, c.Y + c.Height / 2);
                                session.CommitSelection(mon.Bounds);
                            }
                            else if (preFullscreen is PixelRect pr)
                            {
                                session.CommitSelection(pr);
                            }
                        },
                        onAspect: ratio => ApplyAspect(session, ratio));
                    recordPanel.Show();
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                    CloseFloaters();
                    session.Cancel();
                    Notify?.Invoke("Stillmark", "Recording setup failed. Try selecting the region again.", null);
                }
            }

            session.SelectionEstablished += rect =>
            {
                if (awaitingOcrArea)
                {
                    awaitingOcrArea = false;
                    try
                    {
                        using CaptureActivityReference handoffActivity = activity.Retain();
                        CloseFloaters();
                        session.Cancel();
                        var ocrCrop = new CroppedBitmap(
                            frozen,
                            new System.Windows.Int32Rect(
                                rect.X - virtualBounds.X,
                                rect.Y - virtualBounds.Y,
                                rect.Width,
                                rect.Height));
                        ocrCrop.Freeze();
                        StartObservedOperation(
                            generation,
                            handoffActivity,
                            (operation, _) => RecognizeAndCopyAsync(ocrCrop, rect, operation),
                            "Text recognition failed.");
                    }
                    catch (Exception ex)
                    {
                        App.LogError(ex);
                        CloseFloaters();
                        session.Cancel();
                        Notify?.Invoke(
                            "Stillmark",
                            "Text recognition failed. Try selecting the region again.",
                            null);
                    }
                    return;
                }

                bool full = IsFullMonitor(rect);
                if (!full && session.CommittedWindow is null)
                {
                    // Only custom areas are remembered; a fullscreen selection must not leak into next time.
                    _settings.Current.LastArea = new[] { rect.X, rect.Y, rect.Width, rect.Height };
                    _settings.SaveCurrent();
                    customRegion = rect;
                }

                if (recordPanel is not null)
                {
                    recordPanel.UpdateRegion(rect);
                    return;
                }

                void StartScroll()
                {
                    if (_operations.IsCurrent(generation) && session.Committed is PixelRect r)
                    {
                        using CaptureActivityReference handoffActivity = activity.Retain();
                        CloseFloaters();
                        session.Cancel();
                        ScrollRequested?.Invoke(r);
                    }
                }

                var activeMode = full ? RegionMode.Fullscreen : RegionMode.Area;
                if (toolbar is null)
                {
                    toolbar = new AllInOneToolbar(rect, activeMode, OnRegionMode,
                        on => timerOn = on, RequestFreshOcrArea, OpenRecordPanel, StartScroll, DoCapture);
                    toolbar.Show();
                }
                else
                {
                    toolbar.UpdateRegion(rect);
                    toolbar.SetRegionMode(activeMode);
                    toolbar.Show();
                }
            };
            session.SelectionChanged += rect =>
            {
                toolbar?.UpdateRegion(rect);
                recordPanel?.UpdateRegion(rect); // keep the recording panel centered as the area moves/resizes
            };
            session.SelectionCleared += () => toolbar?.Hide();
            session.ConfirmRequested += () =>
            {
                if (recordPanel is not null)
                {
                    if (session.Committed is PixelRect r)
                    {
                        using CaptureActivityReference handoffActivity = activity.Retain();
                        CloseFloaters();
                        session.Cancel();
                        RecordRequested?.Invoke(r);
                    }
                }
                else
                {
                    DoCapture();
                }
            };
            session.Completed += _ =>
            {
                _activeSession = null;
                CloseFloaters();
                activity.Dispose();
            };

            session.Show();
            int[]? last = _settings.Current.LastArea;
            if (last is { Length: 4 })
            {
                var seed = new PixelRect(last[0], last[1], last[2], last[3]).Intersect(virtualBounds);
                if (seed is { Width: >= 20, Height: >= 20 })
                {
                    customRegion = seed;
                    session.CommitSelection(seed);
                }
            }
        }
        catch (Exception ex)
        {
            _activeSession = null;
            App.LogError(ex);
            Notify?.Invoke("Stillmark", "Capture failed. Try again.", null);
            activity.Dispose();
        }
    }

    /// <summary>Resize the current all-in-one selection to an aspect ratio (w/h), keeping its center.</summary>
    private static void ApplyAspect(SelectionSession session, double ratio)
    {
        if (session.Committed is not PixelRect cur || ratio <= 0)
            return;
        var mon = MonitorService.GetMonitorAt(cur.X + cur.Width / 2, cur.Y + cur.Height / 2);
        int cx = cur.X + cur.Width / 2, cy = cur.Y + cur.Height / 2;

        int w = cur.Width;
        int h = (int)Math.Round(w / ratio);
        if (h > mon.Bounds.Height)
        {
            h = mon.Bounds.Height;
            w = (int)Math.Round(h * ratio);
        }
        if (w > mon.Bounds.Width)
        {
            w = mon.Bounds.Width;
            h = (int)Math.Round(w / ratio);
        }
        w = Math.Max(20, w);
        h = Math.Max(20, h);
        int x = Math.Clamp(cx - w / 2, mon.Bounds.X, Math.Max(mon.Bounds.X, mon.Bounds.Right - w));
        int y = Math.Clamp(cy - h / 2, mon.Bounds.Y, Math.Max(mon.Bounds.Y, mon.Bounds.Bottom - h));
        session.CommitSelection(new PixelRect(x, y, w, h));
    }

    private void StartTimedCaptureOfRegion(
        PixelRect rect,
        long generation,
        CaptureActivityReference activity)
    {
        if (!_operations.IsCurrent(generation))
            return;
        _countdown?.CancelCountdown();
        var monitor = MonitorService.GetMonitorAt(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        int seconds = Math.Clamp(_settings.Current.TimerSeconds, 1, 60);
        CaptureActivityReference countdownActivity = activity.Retain();
        int countdownCompleted = 0;
        CountdownWindow? countdown = null;
        try
        {
            countdown = new CountdownWindow(seconds, monitor, onFinished: () =>
            {
                if (Interlocked.Exchange(ref countdownCompleted, 1) != 0)
                    return;
                if (ReferenceEquals(_countdown, countdown))
                    _countdown = null;
                try
                {
                    StartObservedOperation(
                        generation,
                        countdownActivity,
                        (operation, operationActivity) => CaptureRectAsync(
                            () => rect,
                            TimeSpan.FromMilliseconds(150),
                            operation,
                            operationActivity),
                        "Timed capture failed.");
                }
                finally
                {
                    countdownActivity.Dispose();
                }
            }, onCancelled: () =>
            {
                if (Interlocked.Exchange(ref countdownCompleted, 1) != 0)
                    return;
                if (ReferenceEquals(_countdown, countdown))
                    _countdown = null;
                countdownActivity.Dispose();
            });
            _countdown = countdown;
            countdown.Show();
        }
        catch
        {
            if (Interlocked.Exchange(ref countdownCompleted, 1) == 0)
                countdownActivity.Dispose();
            if (ReferenceEquals(_countdown, countdown))
                _countdown = null;
            throw;
        }
    }

    private void StartSelection(
        bool textRecognition,
        long generation,
        CaptureActivityReference activity)
    {
        if (_activeSession is not null || !_operations.IsCurrent(generation))
        {
            activity.Dispose();
            return;
        }

        try
        {
            var monitors = MonitorService.GetMonitors();
            var virtualBounds = MonitorService.GetVirtualScreenBounds();
            var topWindows = WindowService.GetTopLevelWindows(virtualBounds);

            PrepareScreen?.Invoke();
            BitmapSource frozen;
            try
            {
                frozen = GdiScreenCapture.CaptureRegion(
                    virtualBounds,
                    !textRecognition && _settings.Current.ShowCursorOnScreenshots);
            }
            finally
            {
                ReleaseScreen?.Invoke();
            }

            bool freeze = _settings.Current.FreezeScreen;
            var session = new SelectionSession(monitors, virtualBounds, frozen, topWindows, showFrozen: freeze);
            _activeSession = session;
            session.Completed += result =>
            {
                try
                {
                    if (ReferenceEquals(_activeSession, session))
                        _activeSession = null;
                    if (result is null || !_operations.IsCurrent(generation))
                        return;

                    if (!freeze)
                    {
                        // See-through selection: grab the live pixels now that the overlay has closed.
                        StartObservedOperation(
                            generation,
                            activity,
                            (operation, operationActivity) => CompleteLiveSelectionAsync(
                                result,
                                textRecognition,
                                operation,
                                operationActivity),
                            "Capture failed.");
                        return;
                    }

                    var r = result.Rect;
                    var crop = new CroppedBitmap(frozen,
                        new System.Windows.Int32Rect(r.X - virtualBounds.X, r.Y - virtualBounds.Y, r.Width, r.Height));
                    crop.Freeze();

                    if (textRecognition)
                    {
                        StartObservedOperation(
                            generation,
                            activity,
                            (operation, _) => RecognizeAndCopyAsync(crop, r, operation),
                            "Text recognition failed.");
                        return;
                    }

                    if (_operations.TryCommit(generation))
                        DeliverSelectionImage(crop, result, activity);
                }
                finally
                {
                    activity.Dispose();
                }
            };
            session.Show();
        }
        catch (Exception ex)
        {
            _activeSession = null;
            App.LogError(ex);
            Notify?.Invoke("Stillmark", "Capture failed. Try again.", null);
            activity.Dispose();
        }
    }

    /// <summary>Applies window beautification, remembers a custom area, and delivers a selection image.</summary>
    private void DeliverSelectionImage(
        BitmapSource image,
        SelectionResult result,
        CaptureActivityReference activity)
    {
        var r = result.Rect;
        var s = _settings.Current;
        if (result.Window is null)
        {
            s.LastArea = new[] { r.X, r.Y, r.Width, r.Height };
            _settings.SaveCurrent();
        }
        Deliver(image, r, activity, result.Window is not null);
    }

    /// <summary>Freeze-off path: after the see-through overlay closes, grab the region from the live screen.</summary>
    private async Task CompleteLiveSelectionAsync(
        SelectionResult result,
        bool textRecognition,
        CaptureOperation operation,
        CaptureActivityReference activity)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(80), operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        if (!_operations.IsCurrent(operation.Generation))
            return;

        var r = result.Rect;
        PrepareScreen?.Invoke();
        BitmapSource image;
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            bool cursor = !textRecognition && _settings.Current.ShowCursorOnScreenshots;
            image = GdiScreenCapture.CaptureRegion(r, cursor);
        }
        finally
        {
            ReleaseScreen?.Invoke();
        }

        operation.Token.ThrowIfCancellationRequested();
        if (!_operations.IsCurrent(operation.Generation))
            return;

        if (textRecognition)
            await RecognizeAndCopyAsync(image, r, operation);
        else if (_operations.TryCommit(operation))
            DeliverSelectionImage(image, result, activity);
    }

    private async Task RecognizeAndCopyAsync(
        BitmapSource image,
        PixelRect sourceRect,
        CaptureOperation operation)
    {
        OcrProgressWindow? progress = ShowOcrProgress(operation.Generation, sourceRect);
        try
        {
            string text = await OcrService.RecognizeAsync(image, operation.Token);
            await InvokeOnUiAsync(() =>
            {
                operation.Token.ThrowIfCancellationRequested();
                if (!_operations.TryCommit(operation))
                    return;

                if (string.IsNullOrWhiteSpace(text))
                {
                    Notify?.Invoke("Stillmark", "No text found in the selection.", null);
                    return;
                }

                ClipboardService.SetText(text, sensitive: true);
                // Do not echo OCR content into a toast: Windows may retain notifications or show them
                // on the lock screen after the capture UI is gone.
                Notify?.Invoke("Text copied to clipboard", "Recognized text is ready to paste.", null);
            });
        }
        finally
        {
            await CloseOcrProgressAsync(operation.Generation, progress);
        }
    }

    private static Task InvokeOnUiAsync(Action action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private OcrProgressWindow? ShowOcrProgress(long generation, PixelRect sourceRect)
    {
        if (!_operations.IsCurrent(generation) || !_ocrProgressOwnership.TryStart(generation))
            return null;

        OcrProgressWindow? prior = _ocrProgress;
        var progress = new OcrProgressWindow(sourceRect);
        _ocrProgress = progress;
        prior?.Close();
        progress.Show();
        return progress;
    }

    private async Task CloseOcrProgressAsync(long generation, OcrProgressWindow? progress)
    {
        if (progress is null)
            return;

        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            CloseOcrProgressOnUi(generation, progress);
            return;
        }

        await dispatcher.InvokeAsync(() => CloseOcrProgressOnUi(generation, progress));
    }

    private void CloseOcrProgressOnUi(long generation, OcrProgressWindow progress)
    {
        if (!ReferenceEquals(_ocrProgress, progress) ||
            !_ocrProgressOwnership.TryComplete(generation))
        {
            return;
        }

        _ocrProgress = null;
        progress.Close();
    }

    private void CloseCurrentOcrProgress()
    {
        _ocrProgressOwnership.CancelCurrent();
        OcrProgressWindow? progress = _ocrProgress;
        _ocrProgress = null;
        progress?.Close();
    }

    public void CaptureFullscreenAtCursor(bool delayForMenuClose = false)
    {
        if (Denied()) return;
        if (!TryBeginCaptureActivity(out CaptureActivityReference? activity))
            return;
        using (activity)
        {
        long generation = BeginLatestCapture();
        var (x, y) = MonitorService.GetCursorPosition();
        StartObservedOperation(
            generation,
                activity,
                (operation, operationActivity) => CaptureRectAsync(
                () => MonitorService.GetMonitorAt(x, y).Bounds,
                delayForMenuClose ? TimeSpan.FromMilliseconds(300) : TimeSpan.Zero,
                    operation,
                    operationActivity),
            "Capture failed.");
        }
    }

    public void CaptureAllMonitors(bool delayForMenuClose = false)
    {
        if (Denied()) return;
        if (!TryBeginCaptureActivity(out CaptureActivityReference? activity))
            return;
        using (activity)
        {
        long generation = BeginLatestCapture();
        StartObservedOperation(
            generation,
                activity,
                (operation, operationActivity) => CaptureRectAsync(
                MonitorService.GetVirtualScreenBounds,
                delayForMenuClose ? TimeSpan.FromMilliseconds(300) : TimeSpan.Zero,
                    operation,
                    operationActivity),
            "Capture failed.");
        }
    }

    public void CaptureLastArea()
    {
        if (Denied()) return;
        if (!TryBeginCaptureActivity(out CaptureActivityReference? activity))
            return;
        using (activity)
        {
        long generation = BeginLatestCapture();
        int[]? last = _settings.Current.LastArea;
        if (last is not { Length: 4 })
        {
            Notify?.Invoke("Stillmark", "No previous area yet — make an area selection first.", null);
            return;
        }
        var rect = new PixelRect(last[0], last[1], last[2], last[3])
            .Intersect(MonitorService.GetVirtualScreenBounds());
        if (rect.IsEmpty)
        {
            Notify?.Invoke("Stillmark", "The previous area is no longer on screen.", null);
            return;
        }
        StartObservedOperation(
            generation,
                activity,
                (operation, operationActivity) => CaptureRectAsync(
                    () => rect,
                    TimeSpan.Zero,
                    operation,
                    operationActivity),
            "Capture failed.");
        }
    }

    /// <summary>Fullscreen capture after a visible countdown. A second invocation restarts the timer.</summary>
    public void StartTimedCapture(int seconds)
    {
        if (Denied()) return;
        if (!TryBeginCaptureActivity(out CaptureActivityReference? activity))
            return;
        using (activity)
        {
        long generation = BeginLatestCapture();
        var (x, y) = MonitorService.GetCursorPosition();
        var monitor = MonitorService.GetMonitorAt(x, y);
        CaptureActivityReference countdownActivity = activity.Retain();
        int countdownCompleted = 0;
        CountdownWindow? countdown = null;
        try
        {
            countdown = new CountdownWindow(seconds, monitor, onFinished: () =>
            {
                if (Interlocked.Exchange(ref countdownCompleted, 1) != 0)
                    return;
                if (ReferenceEquals(_countdown, countdown))
                    _countdown = null;
                try
                {
                    StartObservedOperation(
                        generation,
                        countdownActivity,
                        (operation, operationActivity) => CaptureRectAsync(
                            () => MonitorService.GetMonitorAt(x, y).Bounds,
                            TimeSpan.FromMilliseconds(150),
                            operation,
                            operationActivity),
                        "Timed capture failed.");
                }
                finally
                {
                    countdownActivity.Dispose();
                }
            }, onCancelled: () =>
            {
                if (Interlocked.Exchange(ref countdownCompleted, 1) != 0)
                    return;
                if (ReferenceEquals(_countdown, countdown))
                    _countdown = null;
                countdownActivity.Dispose();
            });
            _countdown = countdown;
            countdown.Show();
        }
        catch
        {
            if (Interlocked.Exchange(ref countdownCompleted, 1) == 0)
                countdownActivity.Dispose();
            if (ReferenceEquals(_countdown, countdown))
                _countdown = null;
            throw;
        }
        }
    }

    private async Task CaptureRectAsync(
        Func<PixelRect> getBounds,
        TimeSpan initialDelay,
        CaptureOperation operation,
        CaptureActivityReference activity)
    {
        if (initialDelay > TimeSpan.Zero)
            await Task.Delay(initialDelay, operation.Token);

        operation.Token.ThrowIfCancellationRequested();
        if (!_operations.IsCurrent(operation.Generation))
            return;

        PrepareScreen?.Invoke();
        BitmapSource image;
        PixelRect bounds;
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            bounds = getBounds();
            image = GdiScreenCapture.CaptureRegion(bounds, _settings.Current.ShowCursorOnScreenshots);
        }
        finally
        {
            ReleaseScreen?.Invoke();
        }

        operation.Token.ThrowIfCancellationRequested();
        if (_operations.TryCommit(operation))
            Deliver(image, bounds, activity);
    }

    private void Deliver(
        BitmapSource image,
        PixelRect sourceRect,
        CaptureActivityReference activity,
        bool isWindowCapture = false)
    {
        ArgumentNullException.ThrowIfNull(image);

        BitmapSource threadSafeImage = image;
        if (!threadSafeImage.IsFrozen)
        {
            if (!threadSafeImage.CanFreeze)
                throw new InvalidOperationException("The captured image cannot be frozen for delivery.");
            threadSafeImage = threadSafeImage.Clone();
            threadSafeImage.Freeze();
        }

        AppSettings settingsSnapshot = _settings.Current.Clone();
        DateTime timestamp = DateTime.Now;
        CaptureActivityReference deliveryActivity = activity.Retain();
        Task task;
        try
        {
            task = RunCommittedDeliveryAsync(
                threadSafeImage,
                sourceRect,
                settingsSnapshot,
                timestamp,
                isWindowCapture,
                deliveryActivity);
        }
        catch
        {
            deliveryActivity.Dispose();
            throw;
        }
        TrackOperationTask(task);
    }

    private async Task RunCommittedDeliveryAsync(
        BitmapSource image,
        PixelRect sourceRect,
        AppSettings settings,
        DateTime timestamp,
        bool isWindowCapture,
        CaptureActivityReference activity)
    {
        try
        {
            image = await ApplyCapturePresentationAsync(
                image,
                sourceRect,
                settings,
                isWindowCapture);
            EncodedImagePayload payload =
                await StillImageEncodingService.EncodePngAsync(image);
            string? savedPath = null;
            string? backingPath = null;
            var actions = new List<string>();

            if (settings.SaveToFile)
            {
                try
                {
                    savedPath = await ImageSaveService.SaveAsync(
                        payload,
                        settings,
                        timestamp);
                    actions.Add("saved as " + Path.GetFileName(savedPath));
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                    Notify?.Invoke(
                        "Screenshot not saved",
                        "Stillmark could not write the screenshot to the selected folder.",
                        null);
                }
            }

            bool needsTransientBacking =
                savedPath is null &&
                (settings.CopyToClipboard ||
                 settings.KeepHistoryOfUnsavedCaptures ||
                 settings.QuickAccessEnabled);
            if (needsTransientBacking)
            {
                try
                {
                    backingPath = await CreateTransientBackingAsync(payload, timestamp);
                }
                catch (Exception ex)
                {
                    // PNG/DIB clipboard formats and editor/pin still work without a file-drop
                    // representation. Preserve those successful parts of the capture.
                    App.LogError(ex);
                }
            }

            if (settings.CopyToClipboard)
            {
                try
                {
                    ClipboardService.SetImage(payload, savedPath ?? backingPath);
                    actions.Insert(0, "copied to clipboard");
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                    Notify?.Invoke(
                        "Clipboard unavailable",
                        "The screenshot was captured, but another app kept the clipboard busy.",
                        savedPath);
                }
            }

            string summary = actions.Count > 0 ? string.Join(" and ", actions) : "captured";
            summary = char.ToUpper(summary[0]) + summary[1..];
            Delivered?.Invoke(new CaptureDelivery(
                image,
                savedPath,
                summary,
                timestamp,
                sourceRect,
                BackingPath: backingPath,
                Payload: payload));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            Notify?.Invoke("Stillmark", "Capture delivery failed. Try again.", null);
        }
        finally
        {
            activity.Dispose();
        }
    }

    private static async Task<BitmapSource> ApplyCapturePresentationAsync(
        BitmapSource image,
        PixelRect sourceRect,
        AppSettings settings,
        bool isWindowCapture)
    {
        bool shouldApply = isWindowCapture || settings.ApplyWindowPresentationToAreaCaptures;
        bool hasPresentation = settings.WindowRoundedCorners ||
            settings.WindowShadow ||
            settings.WindowBackground != WindowBackgroundKind.Transparent ||
            settings.WindowPadding > 0;
        if (!shouldApply || !hasPresentation)
            return image;

        double scale = MonitorService.GetMonitorAt(
            sourceRect.X + sourceRect.Width / 2,
            sourceRect.Y + sourceRect.Height / 2).ScaleFactor;
        Brush? background = WallpaperService.ResolveBackground(settings);
        Func<BitmapSource> render = () => ImageBeautifier.BeautifyWindowCapture(
            image,
            scale,
            settings.WindowRoundedCorners,
            settings.WindowShadow,
            settings.WindowPadding,
            background);

        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            return render();

        return await dispatcher.InvokeAsync(render);
    }

    private static async Task<string> CreateTransientBackingAsync(
        EncodedImagePayload payload,
        DateTime timestamp)
    {
        string directory = OwnedTransientFileRetention.ClipboardDirectory;
        await Task.Run(() =>
        {
            Directory.CreateDirectory(directory);
            OwnedTransientFileRetention.PurgeClipboard(DateTime.UtcNow);
        });

        string id = Guid.NewGuid().ToString("N")[..8];
        string baseName = $"Relic_Screenshot_{timestamp:yyyyMMdd_HHmmss_fff}_{id}";
        return await ImageSaveService.SavePngCopyAsync(
            payload,
            directory,
            baseName,
            CancellationToken.None);
    }

    /// <summary>
    /// Keeps one Core activity lease alive across selection, countdown, capture, OCR, and delivery
    /// handoffs. Each asynchronous owner receives a one-shot reference, so superseding one
    /// generation cannot release another generation's update deferral.
    /// </summary>
    private sealed class CaptureActivityReference : IDisposable
    {
        private readonly object _gate = new();
        private CaptureActivityScope? _owner;

        private CaptureActivityReference(CaptureActivityScope owner)
        {
            _owner = owner;
        }

        public static CaptureActivityReference Create(UpdateActivityLease lease)
        {
            var owner = new CaptureActivityScope(lease);
            return new CaptureActivityReference(owner);
        }

        public CaptureActivityReference Retain()
        {
            lock (_gate)
            {
                CaptureActivityScope owner = _owner ??
                    throw new ObjectDisposedException(nameof(CaptureActivityReference));
                owner.AddReference();
                return new CaptureActivityReference(owner);
            }
        }

        public void Dispose()
        {
            CaptureActivityScope? owner;
            lock (_gate)
            {
                owner = _owner;
                _owner = null;
            }

            owner?.ReleaseReference();
        }
    }

    private sealed class CaptureActivityScope
    {
        private readonly object _gate = new();
        private UpdateActivityLease? _lease;
        private int _referenceCount = 1;

        public CaptureActivityScope(UpdateActivityLease lease)
        {
            _lease = lease;
        }

        public void AddReference()
        {
            lock (_gate)
            {
                if (_lease is null || _referenceCount == 0)
                    throw new ObjectDisposedException(nameof(CaptureActivityScope));
                _referenceCount = checked(_referenceCount + 1);
            }
        }

        public void ReleaseReference()
        {
            UpdateActivityLease? lease = null;
            lock (_gate)
            {
                if (_referenceCount <= 0)
                    throw new InvalidOperationException("Capture activity reference accounting underflowed.");

                _referenceCount--;
                if (_referenceCount == 0)
                {
                    lease = _lease;
                    _lease = null;
                }
            }

            lease?.Dispose();
        }
    }
}
