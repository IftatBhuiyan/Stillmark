namespace WinShot.Core.Update;

public enum ProcessExitIntent
{
    NormalQuit,
    UpdateRestart,
    UnexpectedTermination,
}

public readonly record struct ProcessExitDecision(
    bool AllowCoordinatedShutdown,
    bool UnregisterApplicationRestart,
    bool InvokeUpdaterHandoff,
    UpdateDeferralReason DeferralReasons)
;

/// <summary>
/// Keeps intentional exits distinct from Windows crash recovery. An update request is always
/// denied here because only UpdateHandoffCoordinator may consume a candidate and request shutdown
/// after live quiescence and two-phase child commit. An unexpected termination leaves Application
/// Restart registered and never launches an updater.
/// </summary>
public static class UpdateRestartPolicy
{
    public static ProcessExitDecision Decide(
        ProcessExitIntent intent,
        UpdateReadinessDecision? updateReadiness = null)
    {
        if (!Enum.IsDefined(intent))
            throw new ArgumentOutOfRangeException(nameof(intent));
        if (intent == ProcessExitIntent.UpdateRestart && updateReadiness is null)
        {
            throw new ArgumentNullException(
                nameof(updateReadiness),
                "An update restart requires a current activity decision.");
        }
        if (updateReadiness is { } readiness
            && readiness.IsReady != (readiness.DeferralReasons == UpdateDeferralReason.None))
        {
            throw new ArgumentException(
                "Update readiness and deferral reasons are inconsistent.",
                nameof(updateReadiness));
        }

        return intent switch
        {
            ProcessExitIntent.NormalQuit => new ProcessExitDecision(
                AllowCoordinatedShutdown: true,
                UnregisterApplicationRestart: true,
                InvokeUpdaterHandoff: false,
                DeferralReasons: UpdateDeferralReason.None),
            ProcessExitIntent.UpdateRestart => DecideUpdateRestart(
                updateReadiness!.Value),
            ProcessExitIntent.UnexpectedTermination => new ProcessExitDecision(
                AllowCoordinatedShutdown: false,
                UnregisterApplicationRestart: false,
                InvokeUpdaterHandoff: false,
                DeferralReasons: UpdateDeferralReason.None),
            _ => throw new ArgumentOutOfRangeException(nameof(intent)),
        };
    }

    private static ProcessExitDecision DecideUpdateRestart(
        UpdateReadinessDecision readiness)
    {
        if (!readiness.IsReady)
        {
            return new ProcessExitDecision(
                AllowCoordinatedShutdown: false,
                UnregisterApplicationRestart: false,
                InvokeUpdaterHandoff: false,
                DeferralReasons: readiness.DeferralReasons);
        }

        return new ProcessExitDecision(
            AllowCoordinatedShutdown: false,
            UnregisterApplicationRestart: false,
            InvokeUpdaterHandoff: false,
            DeferralReasons: UpdateDeferralReason.CandidateUnavailable);
    }
}
