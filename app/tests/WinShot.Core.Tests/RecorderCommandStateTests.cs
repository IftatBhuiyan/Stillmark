using System.Collections.Concurrent;
using WinShot.App.Recording;
using Xunit;

namespace WinShot.Core.Tests;

public class RecorderCommandStateTests
{
    [Fact]
    public void FirstTerminalCommandWinsAndNativeStopIsIdempotent()
    {
        var state = new RecorderCommandState();
        long generation = state.BeginTake();
        Assert.True(state.TryBeginNativeStart(generation));
        Assert.True(state.TryMarkNativeStarted(generation));

        RecorderStopDecision first = state.RequestStop(
            generation,
            RecorderStopIntent.Restart);
        RecorderStopDecision second = state.RequestStop(
            generation,
            RecorderStopIntent.Delete);
        RecorderStopDecision third = state.RequestStop(
            generation,
            RecorderStopIntent.Save);

        Assert.True(first.Accepted);
        Assert.True(first.RequiresNativeStop);
        Assert.False(second.Accepted);
        Assert.False(third.Accepted);
        Assert.True(state.TryAcceptTerminal(generation, out RecorderStopIntent intent));
        Assert.Equal(RecorderStopIntent.Restart, intent);
        Assert.False(state.TryAcceptTerminal(generation, out _));
    }

    [Fact]
    public void CountdownCancellationDoesNotIssueNativeStop()
    {
        var state = new RecorderCommandState();
        long generation = state.BeginTake();

        RecorderStopDecision decision = state.RequestStop(
            generation,
            RecorderStopIntent.Delete);

        Assert.True(decision.Accepted);
        Assert.False(decision.RequiresNativeStop);
        Assert.True(state.TryAcceptTerminal(generation, out RecorderStopIntent intent));
        Assert.Equal(RecorderStopIntent.Delete, intent);
        Assert.True(state.FinishTerminal(generation));
        Assert.False(state.HasActiveTake);
    }

    [Fact]
    public void StopDuringStartupRemainsStoppingWhenNativeStartPublishes()
    {
        var state = new RecorderCommandState();
        long generation = state.BeginTake();
        Assert.True(state.TryBeginNativeStart(generation));

        RecorderStopDecision decision = state.RequestStop(
            generation,
            RecorderStopIntent.Save);

        Assert.True(decision.Accepted);
        Assert.True(decision.RequiresNativeStop);
        Assert.True(state.TryMarkNativeStarted(generation));
        Assert.True(state.HasNativeStarted);
        Assert.Equal(RecorderTakePhase.Stopping, state.Phase);
    }

    [Fact]
    public void PauseTransitionsAreOrderedAndRejectedAfterStop()
    {
        var state = new RecorderCommandState();
        long generation = state.BeginTake();
        Assert.True(state.TryBeginNativeStart(generation));
        Assert.True(state.TryMarkNativeStarted(generation));

        Assert.True(state.TryRequestPause(generation, paused: true));
        Assert.Equal(RecorderTakePhase.Paused, state.Phase);
        Assert.False(state.TryRequestPause(generation, paused: true));
        Assert.True(state.TryRequestPause(generation, paused: false));
        Assert.Equal(RecorderTakePhase.Recording, state.Phase);

        Assert.True(state.RequestStop(generation, RecorderStopIntent.Save).Accepted);
        Assert.False(state.TryRequestPause(generation, paused: true));
    }

    [Fact]
    public void StaleTerminalCallbackCannotAffectNewTake()
    {
        var state = new RecorderCommandState();
        long first = state.BeginTake();
        Assert.True(state.TryAcceptTerminal(first, out _));
        Assert.True(state.FinishTerminal(first));

        long second = state.BeginTake();

        Assert.NotEqual(first, second);
        Assert.False(state.TryAcceptTerminal(first, out _));
        Assert.Equal(RecorderTakePhase.Countdown, state.Phase);
        Assert.Equal(second, state.Generation);
    }

    [Fact]
    public void LifecycleFinalizationSuppressesPendingRestartWithoutSecondNativeStop()
    {
        var state = new RecorderCommandState();
        long generation = state.BeginTake();
        Assert.True(state.TryBeginNativeStart(generation));
        Assert.True(state.TryMarkNativeStarted(generation));
        Assert.True(state.RequestStop(generation, RecorderStopIntent.Restart).Accepted);

        RecorderFinalizationDecision finalization =
            state.RequestFinalization(generation);

        Assert.True(finalization.HasActiveTake);
        Assert.False(finalization.RequiresNativeStop);
        Assert.False(finalization.CompleteWithoutNative);
        Assert.True(state.TryAcceptTerminal(generation, out RecorderStopIntent intent));
        Assert.Equal(RecorderStopIntent.Save, intent);
    }

    [Fact]
    public void LifecycleFinalizationRequestsOneNativeStopForActiveTake()
    {
        var state = new RecorderCommandState();
        long generation = state.BeginTake();
        Assert.True(state.TryBeginNativeStart(generation));
        Assert.True(state.TryMarkNativeStarted(generation));

        RecorderFinalizationDecision finalization =
            state.RequestFinalization(generation);

        Assert.True(finalization.HasActiveTake);
        Assert.True(finalization.RequiresNativeStop);
        Assert.False(finalization.CompleteWithoutNative);
        Assert.True(state.TryAcceptTerminal(generation, out RecorderStopIntent intent));
        Assert.Equal(RecorderStopIntent.Save, intent);
    }

    [Fact]
    public void LifecycleFinalizationPreservesExplicitDelete()
    {
        var state = new RecorderCommandState();
        long generation = state.BeginTake();
        Assert.True(state.TryBeginNativeStart(generation));
        Assert.True(state.TryMarkNativeStarted(generation));
        Assert.True(state.RequestStop(generation, RecorderStopIntent.Delete).Accepted);

        RecorderFinalizationDecision finalization =
            state.RequestFinalization(generation);

        Assert.True(finalization.HasActiveTake);
        Assert.False(finalization.RequiresNativeStop);
        Assert.True(state.TryAcceptTerminal(generation, out RecorderStopIntent intent));
        Assert.Equal(RecorderStopIntent.Delete, intent);
    }

    [Fact]
    public void FinalizationCancellationScopeDoesNotPoisonNewGeneration()
    {
        var state = new RecorderCommandState();
        long interrupted = state.BeginTake();
        Assert.True(state.TryBeginNativeStart(interrupted));
        Assert.True(state.RequestFinalization(interrupted).HasActiveTake);
        Assert.True(state.TryAcceptTerminal(interrupted, out _));
        Assert.True(state.FinishTerminal(interrupted));

        long next = state.BeginTake();

        Assert.True(state.ShouldCancelOwnedWork(interrupted));
        Assert.False(state.ShouldCancelOwnedWork(next));
        Assert.Equal(interrupted, state.FinalizingThroughGeneration);
    }

    [Fact]
    public void FailedPauseCanRollbackOnlyBeforeTerminalCommand()
    {
        var state = new RecorderCommandState();
        long generation = state.BeginTake();
        Assert.True(state.TryBeginNativeStart(generation));
        Assert.True(state.TryMarkNativeStarted(generation));
        Assert.True(state.TryRequestPause(generation, paused: true));

        state.RollBackPause(generation, requestedPaused: true);

        Assert.Equal(RecorderTakePhase.Recording, state.Phase);
        Assert.True(state.TryRequestPause(generation, paused: true));
        Assert.True(state.RequestStop(generation, RecorderStopIntent.Save).Accepted);
        state.RollBackPause(generation, requestedPaused: true);
        Assert.Equal(RecorderTakePhase.Stopping, state.Phase);
    }

    [Fact]
    public async Task SerialQueueNeverOverlapsAndPreservesOrder()
    {
        var queue = new SerialRecorderCommandQueue();
        var events = new ConcurrentQueue<int>();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task first = queue.Enqueue(async () =>
        {
            events.Enqueue(1);
            firstStarted.SetResult();
            await releaseFirst.Task;
            events.Enqueue(2);
        });
        await firstStarted.Task;

        Task second = queue.Enqueue(() => events.Enqueue(3));
        Assert.Equal([1], events.ToArray());

        releaseFirst.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal([1, 2, 3], events.ToArray());
    }

    [Fact]
    public async Task FailedCommandDoesNotPoisonLaterCleanup()
    {
        var queue = new SerialRecorderCommandQueue();
        int cleanupCount = 0;

        Task failed = queue.Enqueue(() => throw new InvalidOperationException("fixture"));
        Task cleanup = queue.Enqueue(() => cleanupCount++);

        await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
        await cleanup;
        await queue.DrainAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, cleanupCount);
    }
}
