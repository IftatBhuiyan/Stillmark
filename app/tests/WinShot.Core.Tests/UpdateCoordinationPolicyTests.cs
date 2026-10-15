using WinShot.Core.Update;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class UpdateCoordinationPolicyTests
{
    [Theory]
    [InlineData(false, false, false, UpdateDeferralReason.None)]
    [InlineData(true, false, false, UpdateDeferralReason.CaptureActive)]
    [InlineData(false, true, false, UpdateDeferralReason.RecordingActive)]
    [InlineData(false, false, true, UpdateDeferralReason.TranscodeActive)]
    [InlineData(
        true,
        true,
        true,
        UpdateDeferralReason.CaptureActive
            | UpdateDeferralReason.RecordingActive
            | UpdateDeferralReason.TranscodeActive)]
    public void Readiness_ReportsEveryActivePrivateWorkReason(
        bool captureActive,
        bool recordingActive,
        bool transcodeActive,
        UpdateDeferralReason expectedReasons)
    {
        UpdateReadinessDecision decision = UpdateCoordinationPolicy.Evaluate(
            new UpdateActivitySnapshot(captureActive, recordingActive, transcodeActive));

        Assert.Equal(expectedReasons == UpdateDeferralReason.None, decision.IsReady);
        Assert.Equal(expectedReasons, decision.DeferralReasons);
    }

    [Fact]
    public void UpdateRestart_WhenIdleButCandidateMissing_DeniesHandoff()
    {
        UpdateReadinessDecision readiness = UpdateCoordinationPolicy.Evaluate(default);

        ProcessExitDecision decision = UpdateRestartPolicy.Decide(
            ProcessExitIntent.UpdateRestart,
            readiness);

        Assert.False(decision.AllowCoordinatedShutdown);
        Assert.False(decision.UnregisterApplicationRestart);
        Assert.False(decision.InvokeUpdaterHandoff);
        Assert.Equal(UpdateDeferralReason.CandidateUnavailable, decision.DeferralReasons);
    }

    [Fact]
    public void UpdateRestart_WithActiveWork_IsDeferredWithoutChangingRecovery()
    {
        UpdateReadinessDecision readiness = UpdateCoordinationPolicy.Evaluate(
            new UpdateActivitySnapshot(
                CaptureActive: true,
                RecordingActive: true,
                TranscodeActive: false));

        ProcessExitDecision decision = UpdateRestartPolicy.Decide(
            ProcessExitIntent.UpdateRestart,
            readiness);

        Assert.False(decision.AllowCoordinatedShutdown);
        Assert.False(decision.UnregisterApplicationRestart);
        Assert.False(decision.InvokeUpdaterHandoff);
        Assert.Equal(
            UpdateDeferralReason.CaptureActive | UpdateDeferralReason.RecordingActive,
            decision.DeferralReasons);
    }

    [Fact]
    public void UpdateRestartPolicy_NeverAuthorizesExitBeforeCoordinatorCommit()
    {
        UpdateReadinessDecision readiness = UpdateCoordinationPolicy.Evaluate(default);

        ProcessExitDecision decision = UpdateRestartPolicy.Decide(
            ProcessExitIntent.UpdateRestart,
            readiness);

        Assert.False(decision.AllowCoordinatedShutdown);
        Assert.False(decision.UnregisterApplicationRestart);
    }

    [Fact]
    public void NormalQuit_IsIntentionalButNeverInvokesUpdater()
    {
        ProcessExitDecision decision = UpdateRestartPolicy.Decide(
            ProcessExitIntent.NormalQuit);

        Assert.True(decision.AllowCoordinatedShutdown);
        Assert.True(decision.UnregisterApplicationRestart);
        Assert.False(decision.InvokeUpdaterHandoff);
        Assert.Equal(UpdateDeferralReason.None, decision.DeferralReasons);
    }

    [Fact]
    public void UnexpectedTermination_LeavesWindowsRecoveryRegistered()
    {
        ProcessExitDecision decision = UpdateRestartPolicy.Decide(
            ProcessExitIntent.UnexpectedTermination);

        Assert.False(decision.AllowCoordinatedShutdown);
        Assert.False(decision.UnregisterApplicationRestart);
        Assert.False(decision.InvokeUpdaterHandoff);
    }

    [Fact]
    public void InconsistentReadinessInput_IsRejected()
    {
        var invalid = new UpdateReadinessDecision(
            IsReady: true,
            DeferralReasons: UpdateDeferralReason.RecordingActive);

        Assert.Throws<ArgumentException>(
            () => UpdateRestartPolicy.Decide(ProcessExitIntent.UpdateRestart, invalid));
    }

    [Fact]
    public void UpdateRestart_RequiresCurrentActivityDecision()
    {
        Assert.Throws<ArgumentNullException>(
            () => UpdateRestartPolicy.Decide(ProcessExitIntent.UpdateRestart));
    }
}
