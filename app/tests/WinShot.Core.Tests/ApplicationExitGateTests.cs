using WinShot.App.Lifecycle;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class ApplicationExitGateTests
{
    [Fact]
    public void NormalQuit_IsSingleTerminalWinner()
    {
        var gate = new ApplicationExitGate();

        Assert.True(gate.TryBeginNormalQuit());
        Assert.False(gate.TryBeginNormalQuit());
        Assert.False(gate.TryBeginSessionEnding());
        Assert.False(gate.TryBeginUpdatePreparation());
        Assert.True(gate.IsTerminal);
        Assert.False(gate.AllowsSessionRecovery);
        Assert.Equal(ApplicationExitState.NormalQuit, gate.State);
    }

    [Fact]
    public void SessionEnding_BecomesTerminalAndSuppressesRecovery()
    {
        var gate = new ApplicationExitGate();

        Assert.True(gate.AllowsSessionRecovery);
        Assert.True(gate.TryBeginSessionEnding());

        Assert.True(gate.IsTerminal);
        Assert.False(gate.AllowsSessionRecovery);
        Assert.Equal(ApplicationExitState.SessionEnding, gate.State);
    }

    [Fact]
    public void UpdatePreparation_CanAbortBeforeCommit()
    {
        var gate = new ApplicationExitGate();

        Assert.True(gate.TryBeginUpdatePreparation());
        Assert.False(gate.IsTerminal);
        Assert.False(gate.AllowsSessionRecovery);
        Assert.True(gate.TryCancelUpdatePreparation());

        Assert.Equal(ApplicationExitState.Running, gate.State);
        Assert.True(gate.AllowsSessionRecovery);
    }

    [Fact]
    public void SessionEnding_PreemptsUncommittedUpdatePreparation()
    {
        var gate = new ApplicationExitGate();

        Assert.True(gate.TryBeginUpdatePreparation());
        Assert.True(gate.TryBeginSessionEnding());

        Assert.Equal(ApplicationExitState.SessionEnding, gate.State);
        Assert.True(gate.IsTerminal);
        Assert.False(gate.TryCommitUpdate());
    }

    [Fact]
    public void UpdateCommit_OwnsTerminalExitAndCannotBeReopened()
    {
        var gate = new ApplicationExitGate();

        Assert.True(gate.TryBeginUpdatePreparation());
        Assert.True(gate.TryCommitUpdate());
        Assert.False(gate.TryCancelUpdatePreparation());
        Assert.False(gate.TryBeginNormalQuit());
        Assert.False(gate.TryBeginSessionEnding());
        Assert.True(gate.IsTerminal);
        Assert.Equal(ApplicationExitState.UpdateCommitted, gate.State);
    }

    [Fact]
    public async Task ConcurrentTerminalRequests_HaveExactlyOneWinner()
    {
        var gate = new ApplicationExitGate();
        using var start = new ManualResetEventSlim();

        Task<bool>[] attempts =
        [
            Task.Run(() => { start.Wait(); return gate.TryBeginNormalQuit(); }),
            Task.Run(() => { start.Wait(); return gate.TryBeginSessionEnding(); }),
        ];

        start.Set();
        bool[] results = await Task.WhenAll(attempts);

        Assert.Single(results, static won => won);
        Assert.True(gate.IsTerminal);
    }

    [Fact]
    public void MarkExited_IsIdempotentFromAnyState()
    {
        var gate = new ApplicationExitGate();

        gate.MarkExited();
        gate.MarkExited();

        Assert.Equal(ApplicationExitState.Exited, gate.State);
        Assert.True(gate.IsTerminal);
        Assert.False(gate.TryBeginNormalQuit());
    }
}

public sealed class AppUpdateShutdownRequesterTests
{
    [Fact]
    public void AcceptedUpdate_QueuesShutdownAfterClaimingExit()
    {
        var gate = new ApplicationExitGate();
        Assert.True(gate.TryBeginUpdatePreparation());
        ApplicationExitState stateAtSchedule = ApplicationExitState.Running;
        bool shutdownCalled = false;
        var requester = new AppUpdateShutdownRequester(
            gate,
            callback =>
            {
                stateAtSchedule = gate.State;
                callback();
                return true;
            },
            () => shutdownCalled = true);

        Assert.True(requester.TryRequestUpdateShutdown());

        Assert.Equal(ApplicationExitState.UpdateCommitted, stateAtSchedule);
        Assert.True(shutdownCalled);
        Assert.True(gate.IsTerminal);
    }

    [Fact]
    public void SchedulingFailure_LeavesCommittedStateFailClosed()
    {
        var gate = new ApplicationExitGate();
        Assert.True(gate.TryBeginUpdatePreparation());
        var requester = new AppUpdateShutdownRequester(gate, _ => false, () => { });

        Assert.False(requester.TryRequestUpdateShutdown());

        Assert.Equal(ApplicationExitState.UpdateCommitted, gate.State);
        Assert.False(gate.TryBeginNormalQuit());
    }

    [Fact]
    public void SchedulingException_LeavesCommittedStateFailClosed()
    {
        var gate = new ApplicationExitGate();
        Assert.True(gate.TryBeginUpdatePreparation());
        var requester = new AppUpdateShutdownRequester(
            gate,
            _ => throw new InvalidOperationException("dispatcher unavailable"),
            () => { });

        Assert.False(requester.TryRequestUpdateShutdown());

        Assert.Equal(ApplicationExitState.UpdateCommitted, gate.State);
    }

    [Fact]
    public void RequestWithoutPreparation_IsRefusedWithoutScheduling()
    {
        var gate = new ApplicationExitGate();
        bool scheduled = false;
        var requester = new AppUpdateShutdownRequester(
            gate,
            _ =>
            {
                scheduled = true;
                return true;
            },
            () => { });

        Assert.False(requester.TryRequestUpdateShutdown());
        Assert.False(scheduled);
        Assert.Equal(ApplicationExitState.Running, gate.State);
    }
}
