using WinShot.App.Lifecycle;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class SessionRecoverySequencerTests
{
    [Fact]
    public async Task RecoveryWaitsForDelayedInterruptionDrain()
    {
        var drain = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? latest = drain.Task;

        Task<bool> recovery =
            SessionRecoverySequencer.WaitForStableDrainAsync(() => latest);

        Assert.False(recovery.IsCompleted);
        drain.SetResult(true);
        Assert.True(await recovery);
    }

    [Fact]
    public async Task RecoveryWaitsForReplacementDrain()
    {
        var first = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? latest = first.Task;

        Task<bool> recovery =
            SessionRecoverySequencer.WaitForStableDrainAsync(() => latest);
        latest = replacement.Task;
        first.SetResult(true);

        await Task.Yield();
        Assert.False(recovery.IsCompleted);
        replacement.SetResult(true);
        Assert.True(await recovery);
    }

    [Fact]
    public async Task FailedInterruptionDrainRefusesRecovery()
    {
        Task<bool>? latest = Task.FromResult(false);

        Assert.False(await SessionRecoverySequencer.WaitForStableDrainAsync(
            () => latest));
    }

    [Fact]
    public void FinalEnableCheckRefusesLaterTransitionOrReplacementDrain()
    {
        Assert.True(SessionRecoverySequencer.CanEnableRecovery(
            recoveryTicket: 4,
            latestTransitionTicket: 4,
            latestDrain: Task.FromResult(true)));
        Assert.False(SessionRecoverySequencer.CanEnableRecovery(
            recoveryTicket: 4,
            latestTransitionTicket: 5,
            latestDrain: Task.FromResult(true)));

        var replacement = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.False(SessionRecoverySequencer.CanEnableRecovery(
            recoveryTicket: 5,
            latestTransitionTicket: 5,
            latestDrain: replacement.Task));
        replacement.SetResult(true);
        Assert.True(SessionRecoverySequencer.CanEnableRecovery(
            recoveryTicket: 5,
            latestTransitionTicket: 5,
            latestDrain: replacement.Task));
    }
}
