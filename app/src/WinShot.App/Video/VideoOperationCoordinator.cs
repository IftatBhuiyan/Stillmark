namespace WinShot.App.Video;

using WinShot.Core.Update;

/// <summary>
/// Owns the video editor's lifetime cancellation and one superseding operation at a time.
/// Callbacks use the lease so work from an older generation cannot update current UI state.
/// </summary>
internal sealed class VideoOperationCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly IUpdateActivityCoordinator _updateActivity;
    private CancellationTokenSource? _activeCancellation;
    private long _generation;
    private bool _disposed;

    public VideoOperationCoordinator(IUpdateActivityCoordinator updateActivity)
    {
        _updateActivity = updateActivity ??
            throw new ArgumentNullException(nameof(updateActivity));
    }

    public bool HasOperation
    {
        get
        {
            lock (_sync)
                return _activeCancellation is not null;
        }
    }

    public VideoOperationLease Begin()
    {
        if (!TryBegin(out VideoOperationLease? lease))
        {
            throw new InvalidOperationException(
                "Update quiescence is active; video conversion cannot begin.");
        }

        return lease;
    }

    public bool TryBegin(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out VideoOperationLease? lease)
    {
        if (!_updateActivity.TryBeginActivity(
                UpdateActivityKind.Transcode,
                out UpdateActivityLease? activity))
        {
            lease = null;
            return false;
        }

        CancellationTokenSource? superseded;
        CancellationTokenSource current;
        long generation;

        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                superseded = _activeCancellation;
                current = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
                _activeCancellation = current;
                generation = ++_generation;
            }
        }
        catch
        {
            activity.Dispose();
            throw;
        }

        TryCancel(superseded);
        lease = new VideoOperationLease(this, generation, current, activity);
        return true;
    }

    public void CancelCurrent()
    {
        CancellationTokenSource? current;
        lock (_sync)
            current = _activeCancellation;
        TryCancel(current);
    }

    internal bool TryRun(
        long generation,
        CancellationTokenSource cancellation,
        bool allowCanceled,
        Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (_sync)
        {
            bool isLatest = !_disposed
                && generation == _generation
                && ReferenceEquals(_activeCancellation, cancellation);
            if (!isLatest || (!allowCanceled && cancellation.IsCancellationRequested))
                return false;

            action();
            return true;
        }
    }

    internal void Release(long generation, CancellationTokenSource cancellation)
    {
        lock (_sync)
        {
            if (generation == _generation && ReferenceEquals(_activeCancellation, cancellation))
                _activeCancellation = null;
        }
        cancellation.Dispose();
    }

    public void Dispose()
    {
        CancellationTokenSource? active;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            active = _activeCancellation;
            _activeCancellation = null;
        }

        TryCancel(_lifetimeCancellation);
        TryCancel(active);
        _lifetimeCancellation.Dispose();
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
            return;
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

internal sealed class VideoOperationLease : IDisposable
{
    private readonly VideoOperationCoordinator _owner;
    private readonly long _generation;
    private CancellationTokenSource? _cancellation;
    private UpdateActivityLease? _activity;

    internal VideoOperationLease(
        VideoOperationCoordinator owner,
        long generation,
        CancellationTokenSource cancellation,
        UpdateActivityLease activity)
    {
        _owner = owner;
        _generation = generation;
        _cancellation = cancellation;
        _activity = activity;
    }

    public CancellationToken Token => _cancellation?.Token ?? new CancellationToken(canceled: true);
    public bool IsCancellationRequested => Token.IsCancellationRequested;

    /// <summary>Runs only while this is the latest non-canceled operation.</summary>
    public bool TryPublish(Action action)
    {
        CancellationTokenSource? cancellation = _cancellation;
        return cancellation is not null
            && _owner.TryRun(_generation, cancellation, allowCanceled: false, action);
    }

    /// <summary>
    /// Runs final UI cleanup for the latest operation, including a user-canceled operation.
    /// Superseded operations and callbacks after window disposal remain rejected.
    /// </summary>
    public bool TryFinalize(Action action)
    {
        CancellationTokenSource? cancellation = _cancellation;
        return cancellation is not null
            && _owner.TryRun(_generation, cancellation, allowCanceled: true, action);
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _cancellation, null);
        if (cancellation is not null)
            _owner.Release(_generation, cancellation);
        Interlocked.Exchange(ref _activity, null)?.Dispose();
    }
}
