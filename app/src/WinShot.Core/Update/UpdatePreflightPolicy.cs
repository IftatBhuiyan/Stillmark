namespace WinShot.Core.Update;

public sealed record UpdatePreflightInput(
    Version CurrentVersion,
    Version CandidateVersion,
    int EntitledMajor,
    DateTimeOffset? UpdatesUntilUtc = null,
    DateTimeOffset? CurrentReleasedAtUtc = null,
    DateTimeOffset? CandidateReleasedAtUtc = null);

public enum UpdatePreflightStatus
{
    Ready,
    NotNewer,
    CurrentVersionNotEntitled,
    CandidateMajorNotEntitled,
    ReleaseDateMissing,
    CurrentReleaseNotEntitled,
    CandidateReleaseNotEntitled,
}

public readonly record struct UpdatePreflightDecision(UpdatePreflightStatus Status)
{
    public bool CanStage => Status == UpdatePreflightStatus.Ready;
}

/// <summary>
/// Checks newer versions against dated update coverage or legacy paid-major entitlements.
/// Signature, channel, and package-integrity verification remain mandatory adapter/release gates.
/// </summary>
public static class UpdatePreflightPolicy
{
    public static UpdatePreflightDecision Evaluate(UpdatePreflightInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.CurrentVersion);
        ArgumentNullException.ThrowIfNull(input.CandidateVersion);
        if (input.EntitledMajor <= 0)
            throw new ArgumentOutOfRangeException(nameof(input), "Entitled major must be positive.");
        if (input.CurrentVersion.Major <= 0 || input.CandidateVersion.Major <= 0)
        {
            throw new ArgumentException(
                "Current and candidate versions must use a positive paid-major number.",
                nameof(input));
        }

        if (input.UpdatesUntilUtc is { } cutoff)
        {
            if (cutoff.ToUnixTimeSeconds() <= 0
                || input.CurrentReleasedAtUtc is not { } currentDate
                || input.CandidateReleasedAtUtc is not { } candidateDate
                || currentDate.ToUnixTimeSeconds() <= 0
                || candidateDate.ToUnixTimeSeconds() <= 0)
                return new(UpdatePreflightStatus.ReleaseDateMissing);
            if (currentDate >= cutoff)
                return new(UpdatePreflightStatus.CurrentReleaseNotEntitled);
            if (input.CandidateVersion <= input.CurrentVersion)
                return new(UpdatePreflightStatus.NotNewer);
            return new(candidateDate < cutoff
                ? UpdatePreflightStatus.Ready
                : UpdatePreflightStatus.CandidateReleaseNotEntitled);
        }

        if (input.CurrentVersion.Major > input.EntitledMajor)
        {
            return new UpdatePreflightDecision(
                UpdatePreflightStatus.CurrentVersionNotEntitled);
        }

        if (input.CandidateVersion <= input.CurrentVersion)
            return new UpdatePreflightDecision(UpdatePreflightStatus.NotNewer);

        if (input.CandidateVersion.Major > input.EntitledMajor)
        {
            return new UpdatePreflightDecision(
                UpdatePreflightStatus.CandidateMajorNotEntitled);
        }

        return new UpdatePreflightDecision(UpdatePreflightStatus.Ready);
    }
}
