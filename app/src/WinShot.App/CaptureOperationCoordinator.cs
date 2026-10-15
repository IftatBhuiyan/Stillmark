namespace WinShot.App;

internal readonly record struct CaptureOperation(
    long Generation,
    CancellationToken Token,
    CancellationTokenSource Source);

/// <summary>
/// Owns the current delayed/OCR operation. Starting a newer capture invalidates the prior
/// generation and cancels its active token; the operation wrapper disposes the source when its
/// task reaches a terminal state.
/// </summary>
internal sealed class CaptureOperationCoordinator
{
    private readonly object _gate = new();
    private long _generation;
    private long _startedGeneration = -1;
    private long _committedGeneration = -1;
    private CancellationTokenSource? _activeSource;

    public long BeginLatest()
    {
        lock (_gate)
        {
            _generation++;
            _activeSource?.Cancel();
            _activeSource = null;
            return _generation;
        }
    }

    public bool IsCurrent(long generation)
    {
        lock (_gate)
            return generation == _generation;
    }

    public bool TryStart(long generation, out CaptureOperation operation)
    {
        lock (_gate)
        {
            if (generation != _generation ||
                generation == _startedGeneration ||
                generation == _committedGeneration ||
                _activeSource is not null)
            {
                operation = default;
                return false;
            }

            var source = new CancellationTokenSource();
            _startedGeneration = generation;
            _activeSource = source;
            operation = new CaptureOperation(generation, source.Token, source);
            return true;
        }
    }

    public void Complete(CaptureOperation operation)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeSource, operation.Source))
                _activeSource = null;
        }

        operation.Source.Dispose();
    }

    public bool TryCommit(long generation)
    {
        lock (_gate)
        {
            if (generation != _generation || generation == _committedGeneration)
                return false;

            _committedGeneration = generation;
            return true;
        }
    }

    public bool TryCommit(CaptureOperation operation)
    {
        lock (_gate)
        {
            if (operation.Generation != _generation ||
                operation.Generation == _committedGeneration ||
                !ReferenceEquals(_activeSource, operation.Source) ||
                operation.Token.IsCancellationRequested)
                return false;

            _committedGeneration = operation.Generation;
            return true;
        }
    }

    public void CancelCurrent()
    {
        lock (_gate)
        {
            _generation++;
            _activeSource?.Cancel();
            _activeSource = null;
        }
    }
}
