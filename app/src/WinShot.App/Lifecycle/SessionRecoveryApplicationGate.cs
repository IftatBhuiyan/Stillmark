namespace WinShot.App.Lifecycle;

internal enum SessionRecoveryApplicationAction
{
    None,
    RestoreCaptureAndHotkeys,
    NotifyExhaustion,
}

/// <summary>
/// Owns the terminal side effect for one recovery ticket. Multiple Resume/Unlock/Connect waiters
/// may share a coordinator task, but only one may restore capture or publish exhaustion.
/// </summary>
internal sealed class SessionRecoveryApplicationGate
{
    private readonly object _gate = new();
    private long _restoredTicket;
    private long _notifiedTicket;

    internal SessionRecoveryApplicationAction Evaluate(
        SessionDrainRecoveryOutcome outcome,
        long recoveryTicket,
        long latestTransitionTicket,
        bool stableDrain,
        bool canContinue)
    {
        if (recoveryTicket <= 0)
            throw new ArgumentOutOfRangeException(nameof(recoveryTicket));

        if (!canContinue || recoveryTicket != latestTransitionTicket)
            return SessionRecoveryApplicationAction.None;

        lock (_gate)
        {
            if (outcome == SessionDrainRecoveryOutcome.Recovered && stableDrain)
            {
                if (_restoredTicket == recoveryTicket)
                    return SessionRecoveryApplicationAction.None;

                _restoredTicket = recoveryTicket;
                return SessionRecoveryApplicationAction.RestoreCaptureAndHotkeys;
            }

            if (outcome == SessionDrainRecoveryOutcome.Exhausted)
            {
                if (_notifiedTicket == recoveryTicket)
                    return SessionRecoveryApplicationAction.None;

                _notifiedTicket = recoveryTicket;
                return SessionRecoveryApplicationAction.NotifyExhaustion;
            }

            return SessionRecoveryApplicationAction.None;
        }
    }
}
