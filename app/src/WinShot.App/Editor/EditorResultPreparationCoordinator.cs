namespace WinShot.App.Editor;

/// <summary>
/// Owns one superseding editor result preparation. A document edit or window close cancels the
/// active lease, and only the latest exact-revision lease may publish a cache entry.
/// </summary>
internal sealed class EditorResultPreparationCoordinator : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource? _activeCancellation;
    private long _generation;
    private bool _disposed;

    internal EditorResultPreparationLease Begin(long expectedRevision)
    {
        CancellationTokenSource? superseded;
        CancellationTokenSource current;
        long generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            superseded = _activeCancellation;
            current = new CancellationTokenSource();
            _activeCancellation = current;
            generation = ++_generation;
        }

        TryCancel(superseded);
        return new EditorResultPreparationLease(
            this,
            generation,
            expectedRevision,
            current);
    }

    internal void CancelCurrent()
    {
        CancellationTokenSource? current;
        lock (_sync)
        {
            if (_disposed)
                return;

            current = _activeCancellation;
            _activeCancellation = null;
            _generation++;
        }

        TryCancel(current);
    }

    internal bool TryPublish(
        long generation,
        long expectedRevision,
        long currentRevision,
        CancellationTokenSource cancellation,
        Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync)
        {
            if (_disposed ||
                generation != _generation ||
                expectedRevision != currentRevision ||
                !ReferenceEquals(_activeCancellation, cancellation) ||
                cancellation.IsCancellationRequested)
            {
                return false;
            }

            action();
            return true;
        }
    }

    internal void Release(
        long generation,
        CancellationTokenSource cancellation)
    {
        lock (_sync)
        {
            if (generation == _generation &&
                ReferenceEquals(_activeCancellation, cancellation))
            {
                _activeCancellation = null;
            }
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
            _generation++;
        }

        TryCancel(active);
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

internal sealed class EditorResultPreparationLease : IDisposable
{
    private readonly EditorResultPreparationCoordinator _owner;
    private readonly long _generation;
    private CancellationTokenSource? _cancellation;

    internal EditorResultPreparationLease(
        EditorResultPreparationCoordinator owner,
        long generation,
        long expectedRevision,
        CancellationTokenSource cancellation)
    {
        _owner = owner;
        _generation = generation;
        ExpectedRevision = expectedRevision;
        _cancellation = cancellation;
    }

    internal long ExpectedRevision { get; }

    internal CancellationToken Token =>
        _cancellation?.Token ?? new CancellationToken(canceled: true);

    internal void ThrowIfCannotPublish(long currentRevision)
    {
        CancellationTokenSource? cancellation = _cancellation;
        if (cancellation is null)
            throw new OperationCanceledException();

        cancellation.Token.ThrowIfCancellationRequested();
        if (currentRevision != ExpectedRevision)
            throw new EditorDocumentChangedDuringOutputException();

        if (!_owner.TryPublish(
                _generation,
                ExpectedRevision,
                currentRevision,
                cancellation,
                static () => { }))
        {
            cancellation.Token.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellation.Token);
        }
    }

    internal bool TryPublish(long currentRevision, Action action)
    {
        CancellationTokenSource? cancellation = _cancellation;
        return cancellation is not null &&
            _owner.TryPublish(
                _generation,
                ExpectedRevision,
                currentRevision,
                cancellation,
                action);
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation =
            Interlocked.Exchange(ref _cancellation, null);
        if (cancellation is not null)
            _owner.Release(_generation, cancellation);
    }
}
