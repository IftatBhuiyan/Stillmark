namespace WinShot.Core.Update;

[Flags]
public enum UpdateDeferralReason
{
    None = 0,
    CaptureActive = 1 << 0,
    RecordingActive = 1 << 1,
    TranscodeActive = 1 << 2,
    CandidateUnavailable = 1 << 3,
}

public readonly record struct UpdateActivitySnapshot(
    bool CaptureActive,
    bool RecordingActive,
    bool TranscodeActive);

public readonly record struct UpdateReadinessDecision(
    bool IsReady,
    UpdateDeferralReason DeferralReasons);

/// <summary>
/// Adapter boundary for a host that owns live capture/recording/transcode state.
/// </summary>
public interface IUpdateActivitySource
{
    UpdateActivitySnapshot GetCurrentActivity();
}

/// <summary>
/// Live admission boundary used by an update handoff. A caller must retain the successful
/// quiescence lease through child acknowledgment and intentional-exit commit, or until a launched
/// child is proven inert and the handoff is safely abandoned.
/// </summary>
public interface IUpdateActivityCoordinator : IUpdateActivitySource
{
    bool TryBeginActivity(
        UpdateActivityKind kind,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out UpdateActivityLease? lease);

    bool TryBeginQuiescence(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out UpdateQuiescenceLease? lease);
}

public static class UpdateCoordinationPolicy
{
    public static UpdateReadinessDecision Evaluate(UpdateActivitySnapshot activity)
    {
        UpdateDeferralReason reasons = UpdateDeferralReason.None;
        if (activity.CaptureActive)
            reasons |= UpdateDeferralReason.CaptureActive;
        if (activity.RecordingActive)
            reasons |= UpdateDeferralReason.RecordingActive;
        if (activity.TranscodeActive)
            reasons |= UpdateDeferralReason.TranscodeActive;

        return new UpdateReadinessDecision(
            reasons == UpdateDeferralReason.None,
            reasons);
    }
}
