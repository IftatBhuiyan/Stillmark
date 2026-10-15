using System.Diagnostics;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinShot.App.Recording;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Core.Update;

namespace WinShot.App.Scrolling;

/// <summary>
/// Live scrolling capture: marks the region, captures it repeatedly as the user scrolls, and
/// stitches the frames into one long image shown in a growing preview. Done delivers the result.
/// </summary>
public sealed class ScrollingCaptureController
{
    private static readonly TimeSpan CaptureInterval = TimeSpan.FromMilliseconds(110);
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(350);
    private const int PreviewMaxPixelWidth = 480;
    private const int PreviewMaxPixelHeight = 920;
    internal const bool DimOutsideSelectedRegion = true;

    private RecordingFrameWindow? _frame;
    private ScrollPreviewWindow? _preview;
    private ScrollControlBar? _controls;
    private ScrollStitcher? _stitcher;
    private ScrollCaptureSessionState? _session;
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private readonly IUpdateActivityCoordinator _updateActivities;
    private UpdateActivityLease? _updateActivity;
    private PixelRect _region;
    private bool _active;
    private bool _limitNotified;

    public ScrollingCaptureController(IUpdateActivityCoordinator updateActivities)
    {
        _updateActivities = updateActivities ??
            throw new ArgumentNullException(nameof(updateActivities));
    }

    /// <summary>Raised with the finished stitched image and its on-screen region.</summary>
    public event Action<BitmapSource, PixelRect>? Captured;

    public event Action<string, string, string?>? Notify;

    public bool IsActive => _active;

    /// <summary>
    /// Cancels an active scrolling session without delivering a partial image. Used when Windows
    /// locks, disconnects, suspends, or the resident application is intentionally shutting down.
    /// </summary>
    public void CancelActive() => Cancel();

    public void Start(PixelRect region)
    {
        if (_active)
            return;

        if (!ScrollCaptureBudget.CanStart(
                region.Width,
                region.Height,
                ScrollStitcher.DefaultMaxCanvasBytes,
                out _,
                out _))
        {
            Notify?.Invoke(
                "Stillmark",
                "This scrolling region is too large for the safe capture-memory limit. Select a smaller region.",
                null);
            return;
        }

        if (!_updateActivities.TryBeginActivity(
                UpdateActivityKind.Capture,
                out UpdateActivityLease? updateActivity))
        {
            Notify?.Invoke(
                "Stillmark",
                "An update is about to start. Try the scrolling capture again in a moment.",
                null);
            return;
        }

        _updateActivity = updateActivity;
        _region = region;
        _stitcher = new ScrollStitcher();
        _session = new ScrollCaptureSessionState();
        _captureCts = new CancellationTokenSource();
        _active = true;
        _limitNotified = false;

        try
        {
            _frame = new RecordingFrameWindow(region, dimOutside: DimOutsideSelectedRegion);
            _frame.Show();
            _preview = new ScrollPreviewWindow(region);
            _preview.Show();
            _controls = new ScrollControlBar(region, onDone: Finish, onCancel: Cancel);
            _controls.UpdateDims(region.Width, region.Height);
            _controls.Show();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ScrollCaptureSessionState session = _session;
            session.TryFail();
            CloseSession(session);
            Notify?.Invoke("Stillmark", "Scrolling capture could not open its controls.", null);
            return;
        }

        ScrollCaptureSessionState activeSession = _session;
        ScrollStitcher activeStitcher = _stitcher;
        CancellationToken token = _captureCts.Token;
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        _captureTask = Task.Run(
            () => RunCaptureLoopAsync(activeSession, activeStitcher, region, dispatcher, token),
            CancellationToken.None);
    }

    private async Task RunCaptureLoopAsync(
        ScrollCaptureSessionState session,
        ScrollStitcher stitcher,
        PixelRect region,
        Dispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        long lastPreviewTimestamp = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BitmapSource frame = GdiScreenCapture.CaptureRegion(region);
                bool appended = stitcher.Append(frame);

                BitmapSource? preview = null;
                if (appended)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (lastPreviewTimestamp == 0 ||
                        Stopwatch.GetElapsedTime(lastPreviewTimestamp, now) >= PreviewInterval)
                    {
                        preview = stitcher.ToPreviewBitmap(PreviewMaxPixelWidth, PreviewMaxPixelHeight);
                        lastPreviewTimestamp = now;
                    }

                    BitmapSource? previewForUi = preview;
                    await dispatcher.InvokeAsync(
                        () => PublishProgress(session, stitcher, previewForUi),
                        DispatcherPriority.Background,
                        cancellationToken);
                }

                if (stitcher.AtLimit)
                {
                    session.TryMarkLimitReached();
                    await dispatcher.InvokeAsync(
                        () => PublishLimit(session, stitcher.LimitReason),
                        DispatcherPriority.Background);
                    return;
                }

                await Task.Delay(CaptureInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            if (session.TryFail())
            {
                try
                {
                    await dispatcher.InvokeAsync(
                        () => HandleWorkerFailure(session),
                        DispatcherPriority.Send);
                }
                catch (Exception dispatchException)
                {
                    App.LogError(dispatchException);
                }
            }
        }
    }

    private void PublishProgress(
        ScrollCaptureSessionState session,
        ScrollStitcher stitcher,
        BitmapSource? preview)
    {
        if (!ReferenceEquals(_session, session) || !session.CanPublish)
            return;

        if (preview is not null)
            _preview?.UpdatePreview(preview);
        _controls?.UpdateDims(stitcher.PixelWidth, stitcher.PixelHeight);
    }

    private void PublishLimit(ScrollCaptureSessionState session, ScrollLimitReason reason)
    {
        if (!ReferenceEquals(_session, session) ||
            session.State != ScrollCaptureState.LimitReached ||
            _limitNotified)
            return;

        _limitNotified = true;
        string message = reason == ScrollLimitReason.MemoryBudget
            ? "Reached the safe scrolling-capture memory limit. Your partial capture is ready; press Done to finish."
            : "Reached the maximum scrolling-capture length. Your partial capture is ready; press Done to finish.";
        Notify?.Invoke("Stillmark", message, null);
    }

    private async void Finish()
    {
        ScrollCaptureSessionState? session = _session;
        ScrollStitcher? stitcher = _stitcher;
        if (session is null || stitcher is null || !session.TryRequestFinish())
            return;

        _captureCts?.Cancel();
        Task? captureTask = _captureTask;
        try
        {
            if (captureTask is not null)
                await captureTask;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            session.TryFail();
            CloseSession(session);
            Notify?.Invoke("Stillmark", "The scrolling capture could not be finalized.", null);
            return;
        }

        BitmapSource image;
        try
        {
            image = await Task.Run(stitcher.ToBitmap);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            session.TryFail();
            CloseSession(session);
            Notify?.Invoke("Stillmark", "The partial scrolling capture could not be finalized.", null);
            return;
        }

        if (!ReferenceEquals(_session, session) || !session.TryComplete())
            return;

        PixelRect region = _region;
        UpdateActivityLease? updateActivity = CloseSession(
            session,
            retainUpdateActivity: true);
        try
        {
            Captured?.Invoke(image, region);
        }
        finally
        {
            updateActivity?.Dispose();
        }
    }

    private void Cancel()
    {
        ScrollCaptureSessionState? session = _session;
        if (session is null || !session.TryCancel())
            return;

        _captureCts?.Cancel();
        CloseSession(session);
    }

    private void HandleWorkerFailure(ScrollCaptureSessionState session)
    {
        if (!ReferenceEquals(_session, session) || session.State != ScrollCaptureState.Failed)
            return;

        CloseSession(session);
        Notify?.Invoke("Stillmark", "Scrolling capture stopped after an unexpected capture error.", null);
    }

    private UpdateActivityLease? CloseSession(
        ScrollCaptureSessionState session,
        bool retainUpdateActivity = false)
    {
        if (!ReferenceEquals(_session, session))
            return null;

        _active = false;
        _limitNotified = false;
        UpdateActivityLease? updateActivity = _updateActivity;
        _updateActivity = null;
        CancellationTokenSource? captureCts = _captureCts;
        Task? captureTask = _captureTask;
        captureCts?.Cancel();
        if (captureCts is not null)
        {
            if (captureTask is null || captureTask.IsCompleted)
            {
                captureCts.Dispose();
            }
            else
            {
                _ = captureTask.ContinueWith(
                    _ => captureCts.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        _captureCts = null;
        _captureTask = null;
        _preview?.Close();
        _preview = null;
        _controls?.Close();
        _controls = null;
        _frame?.Close();
        _frame = null;
        _stitcher = null;
        _session = null;
        if (retainUpdateActivity)
            return updateActivity;

        if (updateActivity is not null)
        {
            if (captureTask is null || captureTask.IsCompleted)
            {
                updateActivity.Dispose();
            }
            else
            {
                _ = captureTask.ContinueWith(
                    _ => updateActivity.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        return null;
    }
}
