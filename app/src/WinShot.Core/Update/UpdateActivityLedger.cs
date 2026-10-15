namespace WinShot.Core.Update;

public enum UpdateActivityKind
{
    Capture,
    Recording,
    Transcode,
}

/// <summary>
/// Maintains one atomic, process-wide view of work that must finish before an update restart.
/// Each operation owns a lease so overlapping work cannot clear another operation's state.
/// </summary>
public sealed class UpdateActivityLedger : IUpdateActivityCoordinator
{
    private readonly object _gate = new();
    private int _captureCount;
    private int _recordingCount;
    private int _transcodeCount;
    private bool _quiescenceHeld;

    public UpdateActivityLease BeginActivity(UpdateActivityKind kind)
    {
        if (!TryBeginActivity(kind, out UpdateActivityLease? lease))
        {
            throw new InvalidOperationException(
                "Update quiescence is active; new work cannot begin.");
        }

        return lease!;
    }

    public bool TryBeginActivity(
        UpdateActivityKind kind,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out UpdateActivityLease? lease)
    {
        ValidateKind(kind);
        lock (_gate)
        {
            if (_quiescenceHeld)
            {
                lease = null;
                return false;
            }

            Increment(kind);
            lease = new UpdateActivityLease(this, kind);
            return true;
        }
    }

    /// <summary>
    /// Atomically reserves a zero-activity state through an update handoff. While held, new
    /// activities are refused. Dispose the returned lease if handoff aborts.
    /// </summary>
    public bool TryBeginQuiescence(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out UpdateQuiescenceLease? lease)
    {
        lock (_gate)
        {
            if (_quiescenceHeld ||
                _captureCount != 0 ||
                _recordingCount != 0 ||
                _transcodeCount != 0)
            {
                lease = null;
                return false;
            }

            _quiescenceHeld = true;
            lease = new UpdateQuiescenceLease(this);
            return true;
        }
    }

    public UpdateActivitySnapshot GetCurrentActivity()
    {
        lock (_gate)
        {
            return new UpdateActivitySnapshot(
                _captureCount != 0,
                _recordingCount != 0,
                _transcodeCount != 0);
        }
    }

    internal void Transition(UpdateActivityKind current, UpdateActivityKind next)
    {
        ValidateKind(next);
        lock (_gate)
        {
            if (current == next)
                return;

            ValidateCanDecrement(current);
            ValidateCanIncrement(next);
            Increment(next);
            Decrement(current);
        }
    }

    internal void Release(UpdateActivityKind kind)
    {
        lock (_gate)
            Decrement(kind);
    }

    internal void ReleaseQuiescence()
    {
        lock (_gate)
        {
            if (!_quiescenceHeld)
                throw new InvalidOperationException("Update quiescence lease accounting underflowed.");
            _quiescenceHeld = false;
        }
    }

    private void Increment(UpdateActivityKind kind)
    {
        switch (kind)
        {
            case UpdateActivityKind.Capture:
                _captureCount = checked(_captureCount + 1);
                break;
            case UpdateActivityKind.Recording:
                _recordingCount = checked(_recordingCount + 1);
                break;
            case UpdateActivityKind.Transcode:
                _transcodeCount = checked(_transcodeCount + 1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown update activity kind.");
        }
    }

    private void Decrement(UpdateActivityKind kind)
    {
        switch (kind)
        {
            case UpdateActivityKind.Capture when _captureCount > 0:
                _captureCount--;
                break;
            case UpdateActivityKind.Recording when _recordingCount > 0:
                _recordingCount--;
                break;
            case UpdateActivityKind.Transcode when _transcodeCount > 0:
                _transcodeCount--;
                break;
            case UpdateActivityKind.Capture:
            case UpdateActivityKind.Recording:
            case UpdateActivityKind.Transcode:
                throw new InvalidOperationException("Update activity lease accounting underflowed.");
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown update activity kind.");
        }
    }

    private void ValidateCanIncrement(UpdateActivityKind kind)
    {
        int count = kind switch
        {
            UpdateActivityKind.Capture => _captureCount,
            UpdateActivityKind.Recording => _recordingCount,
            UpdateActivityKind.Transcode => _transcodeCount,
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unknown update activity kind."),
        };
        if (count == int.MaxValue)
            throw new OverflowException("Update activity lease accounting overflowed.");
    }

    private void ValidateCanDecrement(UpdateActivityKind kind)
    {
        int count = kind switch
        {
            UpdateActivityKind.Capture => _captureCount,
            UpdateActivityKind.Recording => _recordingCount,
            UpdateActivityKind.Transcode => _transcodeCount,
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unknown update activity kind."),
        };
        if (count == 0)
            throw new InvalidOperationException("Update activity lease accounting underflowed.");
    }

    private static void ValidateKind(UpdateActivityKind kind)
    {
        if (kind is not (UpdateActivityKind.Capture or
            UpdateActivityKind.Recording or
            UpdateActivityKind.Transcode))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown update activity kind.");
        }
    }
}

public sealed class UpdateActivityLease : IDisposable
{
    private readonly object _gate = new();
    private UpdateActivityLedger? _owner;
    private UpdateActivityKind _kind;

    internal UpdateActivityLease(UpdateActivityLedger owner, UpdateActivityKind kind)
    {
        _owner = owner;
        _kind = kind;
    }

    public UpdateActivityKind Kind
    {
        get
        {
            lock (_gate)
            {
                if (_owner is null)
                    throw new ObjectDisposedException(nameof(UpdateActivityLease));
                return _kind;
            }
        }
    }

    /// <summary>
    /// Atomically moves this lease to another category. Readers cannot observe a ready gap.
    /// </summary>
    public void TransitionTo(UpdateActivityKind kind)
    {
        lock (_gate)
        {
            UpdateActivityLedger owner = _owner ??
                throw new ObjectDisposedException(nameof(UpdateActivityLease));
            owner.Transition(_kind, kind);
            _kind = kind;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            UpdateActivityLedger? owner = _owner;
            if (owner is null)
                return;

            owner.Release(_kind);
            _owner = null;
        }
    }
}

public sealed class UpdateQuiescenceLease : IDisposable
{
    private UpdateActivityLedger? _owner;

    internal UpdateQuiescenceLease(UpdateActivityLedger owner)
    {
        _owner = owner;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _owner, null)?.ReleaseQuiescence();
    }
}
