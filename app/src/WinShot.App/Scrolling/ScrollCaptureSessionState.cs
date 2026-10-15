namespace WinShot.App.Scrolling;

internal enum ScrollCaptureState
{
    Capturing,
    LimitReached,
    Finishing,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>
/// Thread-safe terminal gate shared by the dispatcher and the one-in-flight capture worker.
/// </summary>
internal sealed class ScrollCaptureSessionState
{
    private int _state = (int)ScrollCaptureState.Capturing;

    public ScrollCaptureState State => (ScrollCaptureState)Volatile.Read(ref _state);

    public bool CanPublish =>
        State is ScrollCaptureState.Capturing or ScrollCaptureState.LimitReached;

    public bool TryMarkLimitReached()
    {
        return Interlocked.CompareExchange(
            ref _state,
            (int)ScrollCaptureState.LimitReached,
            (int)ScrollCaptureState.Capturing) == (int)ScrollCaptureState.Capturing;
    }

    public bool TryRequestFinish()
    {
        while (true)
        {
            ScrollCaptureState current = State;
            if (current is not (ScrollCaptureState.Capturing or ScrollCaptureState.LimitReached))
                return false;
            if (Interlocked.CompareExchange(
                    ref _state,
                    (int)ScrollCaptureState.Finishing,
                    (int)current) == (int)current)
                return true;
        }
    }

    public bool TryCancel()
    {
        while (true)
        {
            ScrollCaptureState current = State;
            if (current is not (ScrollCaptureState.Capturing or ScrollCaptureState.LimitReached))
                return false;
            if (Interlocked.CompareExchange(
                    ref _state,
                    (int)ScrollCaptureState.Cancelled,
                    (int)current) == (int)current)
                return true;
        }
    }

    public bool TryFail()
    {
        while (true)
        {
            ScrollCaptureState current = State;
            if (current is ScrollCaptureState.Completed or ScrollCaptureState.Cancelled or ScrollCaptureState.Failed)
                return false;
            if (Interlocked.CompareExchange(
                    ref _state,
                    (int)ScrollCaptureState.Failed,
                    (int)current) == (int)current)
                return true;
        }
    }

    public bool TryComplete()
    {
        return Interlocked.CompareExchange(
            ref _state,
            (int)ScrollCaptureState.Completed,
            (int)ScrollCaptureState.Finishing) == (int)ScrollCaptureState.Finishing;
    }
}
