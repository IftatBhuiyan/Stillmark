using WinShot.Core.Update;

namespace WinShot.App.Lifecycle;

internal enum ApplicationExitState
{
    Running,
    NormalQuit,
    SessionEnding,
    UpdatePreparing,
    UpdateCommitted,
    Exited,
}

/// <summary>
/// Owns process-exit intent across the dispatcher, Windows session callbacks, and a future updater
/// continuation. Exactly one terminal path may win.
/// </summary>
internal sealed class ApplicationExitGate
{
    private int _state = (int)ApplicationExitState.Running;

    internal ApplicationExitState State =>
        (ApplicationExitState)Volatile.Read(ref _state);

    internal bool AllowsSessionRecovery => State == ApplicationExitState.Running;

    internal bool IsTerminal =>
        State is ApplicationExitState.NormalQuit
            or ApplicationExitState.SessionEnding
            or ApplicationExitState.UpdateCommitted
            or ApplicationExitState.Exited;

    internal bool TryBeginNormalQuit() =>
        TryTransition(ApplicationExitState.Running, ApplicationExitState.NormalQuit);

    internal bool TryBeginSessionEnding()
    {
        while (true)
        {
            ApplicationExitState current = State;
            if (current is not (ApplicationExitState.Running
                or ApplicationExitState.UpdatePreparing))
            {
                return false;
            }

            if (TryTransition(current, ApplicationExitState.SessionEnding))
                return true;
        }
    }

    internal bool TryBeginUpdatePreparation() =>
        TryTransition(ApplicationExitState.Running, ApplicationExitState.UpdatePreparing);

    internal bool TryCancelUpdatePreparation() =>
        TryTransition(ApplicationExitState.UpdatePreparing, ApplicationExitState.Running);

    internal bool TryCommitUpdate() =>
        TryTransition(ApplicationExitState.UpdatePreparing, ApplicationExitState.UpdateCommitted);

    internal void MarkExited() =>
        Interlocked.Exchange(ref _state, (int)ApplicationExitState.Exited);

    private bool TryTransition(ApplicationExitState expected, ApplicationExitState next) =>
        Interlocked.CompareExchange(ref _state, (int)next, (int)expected) == (int)expected;
}

/// <summary>
/// Dispatcher-neutral update shutdown adapter. A successful commit owns process exit before its
/// shutdown callback is queued; a queueing failure therefore remains fail-closed.
/// </summary>
internal sealed class AppUpdateShutdownRequester : IUpdateShutdownRequester
{
    private readonly ApplicationExitGate _exitGate;
    private readonly Func<Action, bool> _trySchedule;
    private readonly Action _shutdown;

    internal AppUpdateShutdownRequester(
        ApplicationExitGate exitGate,
        Func<Action, bool> trySchedule,
        Action shutdown)
    {
        _exitGate = exitGate ?? throw new ArgumentNullException(nameof(exitGate));
        _trySchedule = trySchedule ?? throw new ArgumentNullException(nameof(trySchedule));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
    }

    public bool TryRequestUpdateShutdown()
    {
        if (!_exitGate.TryCommitUpdate())
            return false;

        try
        {
            return _trySchedule(_shutdown);
        }
        catch
        {
            // The exact child may already own commit. Keep UpdateCommitted and let the handoff
            // coordinator retain its safety hold instead of reopening ordinary process actions.
            return false;
        }
    }
}
