namespace WinShot.App.Recording;

internal enum RecorderTakePhase
{
    Idle,
    Countdown,
    Starting,
    Recording,
    Paused,
    Stopping,
    Terminal,
}

internal enum RecorderStopIntent
{
    None,
    Save,
    Restart,
    Delete,
}

internal readonly record struct RecorderStopDecision(
    bool Accepted,
    bool RequiresNativeStop);

internal readonly record struct RecorderFinalizationDecision(
    bool HasActiveTake,
    bool RequiresNativeStop,
    bool CompleteWithoutNative);

/// <summary>
/// Dispatcher-owned state for one recording take. The first terminal command wins, so a later
/// stop/restart/delete click cannot rewrite whether an already-stopping take is saved or discarded.
/// </summary>
internal sealed class RecorderCommandState
{
    private long _lastGeneration;

    internal long Generation { get; private set; }
    internal RecorderTakePhase Phase { get; private set; } = RecorderTakePhase.Idle;
    internal RecorderStopIntent StopIntent { get; private set; }
    internal bool HasActiveTake => Phase != RecorderTakePhase.Idle;
    internal bool HasNativeStarted { get; private set; }
    internal long FinalizingThroughGeneration { get; private set; }

    internal long BeginTake()
    {
        if (HasActiveTake)
            throw new InvalidOperationException("A recording take is already active.");

        Generation = ++_lastGeneration;
        Phase = RecorderTakePhase.Countdown;
        StopIntent = RecorderStopIntent.None;
        HasNativeStarted = false;
        return Generation;
    }

    internal bool TryBeginNativeStart(long generation)
    {
        if (!IsCurrent(generation) || Phase != RecorderTakePhase.Countdown)
            return false;
        Phase = RecorderTakePhase.Starting;
        return true;
    }

    internal bool TryMarkNativeStarted(long generation)
    {
        if (!IsCurrent(generation)
            || (Phase != RecorderTakePhase.Starting && Phase != RecorderTakePhase.Stopping))
        {
            return false;
        }

        HasNativeStarted = true;
        if (Phase == RecorderTakePhase.Starting)
            Phase = RecorderTakePhase.Recording;
        return true;
    }

    internal bool TryRequestPause(long generation, bool paused)
    {
        if (!IsCurrent(generation) || StopIntent != RecorderStopIntent.None)
            return false;

        if (paused && Phase == RecorderTakePhase.Recording)
        {
            Phase = RecorderTakePhase.Paused;
            return true;
        }
        if (!paused && Phase == RecorderTakePhase.Paused)
        {
            Phase = RecorderTakePhase.Recording;
            return true;
        }
        return false;
    }

    internal void RollBackPause(long generation, bool requestedPaused)
    {
        if (!IsCurrent(generation) || StopIntent != RecorderStopIntent.None)
            return;
        if (requestedPaused && Phase == RecorderTakePhase.Paused)
            Phase = RecorderTakePhase.Recording;
        else if (!requestedPaused && Phase == RecorderTakePhase.Recording)
            Phase = RecorderTakePhase.Paused;
    }

    internal RecorderStopDecision RequestStop(long generation, RecorderStopIntent intent)
    {
        if (intent == RecorderStopIntent.None
            || !IsCurrent(generation)
            || Phase is RecorderTakePhase.Idle or RecorderTakePhase.Terminal
            || StopIntent != RecorderStopIntent.None)
        {
            return default;
        }

        bool requiresNativeStop = Phase != RecorderTakePhase.Countdown;
        StopIntent = intent;
        Phase = RecorderTakePhase.Stopping;
        return new RecorderStopDecision(Accepted: true, requiresNativeStop);
    }

    /// <summary>
    /// Lifecycle finalization never resurrects capture work. A pending Restart is promoted to Save;
    /// an explicit Delete remains destructive; and an already-issued native Stop is not repeated.
    /// </summary>
    internal RecorderFinalizationDecision RequestFinalization(long generation)
    {
        if (!IsCurrent(generation))
            return default;

        FinalizingThroughGeneration = Math.Max(FinalizingThroughGeneration, generation);
        if (Phase is RecorderTakePhase.Idle or RecorderTakePhase.Terminal)
            return default;

        if (StopIntent == RecorderStopIntent.Restart)
            StopIntent = RecorderStopIntent.Save;

        if (StopIntent != RecorderStopIntent.None)
        {
            return new RecorderFinalizationDecision(
                HasActiveTake: true,
                RequiresNativeStop: false,
                CompleteWithoutNative: false);
        }

        bool requiresNativeStop = Phase != RecorderTakePhase.Countdown;
        StopIntent = RecorderStopIntent.Save;
        Phase = RecorderTakePhase.Stopping;
        return new RecorderFinalizationDecision(
            HasActiveTake: true,
            RequiresNativeStop: requiresNativeStop,
            CompleteWithoutNative: !requiresNativeStop);
    }

    internal bool TryAcceptTerminal(long generation, out RecorderStopIntent intent)
    {
        intent = RecorderStopIntent.None;
        if (!IsCurrent(generation)
            || Phase is RecorderTakePhase.Idle or RecorderTakePhase.Terminal)
        {
            return false;
        }

        intent = StopIntent;
        Phase = RecorderTakePhase.Terminal;
        return true;
    }

    internal bool FinishTerminal(long generation)
    {
        if (!IsCurrent(generation) || Phase != RecorderTakePhase.Terminal)
            return false;

        Phase = RecorderTakePhase.Idle;
        StopIntent = RecorderStopIntent.None;
        HasNativeStarted = false;
        return true;
    }

    internal bool IsCurrent(long generation) =>
        generation != 0 && generation == Generation;

    internal bool ShouldCancelOwnedWork(long generation) =>
        generation != 0 && generation <= FinalizingThroughGeneration;
}

/// <summary>
/// Process-local single-lane executor for native recorder calls. A failed operation completes its
/// own returned task but cannot poison or bypass the lane for later cleanup/finalization commands.
/// </summary>
internal sealed class SerialRecorderCommandQueue
{
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;

    internal Task Enqueue(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Enqueue(() =>
        {
            operation();
            return Task.CompletedTask;
        });
    }

    internal Task Enqueue(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (_sync)
        {
            Task next = _tail.ContinueWith(
                _ => operation(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).Unwrap();
            _tail = next.ContinueWith(
                static _ => { },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return next;
        }
    }

    internal Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task snapshot;
        lock (_sync)
            snapshot = _tail;
        return snapshot.WaitAsync(timeout, cancellationToken);
    }
}
