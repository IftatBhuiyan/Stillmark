using WinShot.App.Lifecycle;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class AppExitDrainCoordinatorTests
{
    [Fact]
    public async Task ParallelCallers_ShareOneInFlightDrain()
    {
        var coordinator = new AppExitDrainCoordinator();
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var start = new ManualResetEventSlim();
        int starts = 0;

        Task<Task<bool>>[] requests = Enumerable.Range(0, 16)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    start.Wait();
                    return coordinator.GetOrStart(() =>
                    {
                        Interlocked.Increment(ref starts);
                        return completion.Task;
                    });
                },
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default))
            .ToArray();

        start.Set();
        Task<bool>[] drains = await Task.WhenAll(requests);
        Assert.Equal(1, Volatile.Read(ref starts));
        Assert.All(drains, drain => Assert.Same(drains[0], drain));

        completion.SetResult(true);
        Assert.True(await drains[0]);
    }

    [Fact]
    public async Task CompletedDrain_AllowsLaterSessionCycle()
    {
        var coordinator = new AppExitDrainCoordinator();
        int starts = 0;

        Assert.True(await coordinator.GetOrStart(() =>
            Task.FromResult(Interlocked.Increment(ref starts) == 1)));
        Assert.False(await coordinator.GetOrStart(() =>
            Task.FromResult(Interlocked.Increment(ref starts) == 1)));

        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task StartedNewIdentifiesOneOwnerForSharedDrain()
    {
        var coordinator = new AppExitDrainCoordinator();
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> first = coordinator.GetOrStart(
            () => completion.Task,
            out bool firstStarted);
        Task<bool> shared = coordinator.GetOrStart(
            () => throw new InvalidOperationException(
                "A shared drain must not invoke another factory."),
            out bool sharedStarted);

        Assert.True(firstStarted);
        Assert.False(sharedStarted);
        Assert.Same(first, shared);

        completion.SetResult(true);
        Assert.True(await first);

        Task<bool> next = coordinator.GetOrStart(
            () => Task.FromResult(false),
            out bool nextStarted);
        Assert.True(nextStarted);
        Assert.False(await next);
    }

    [Fact]
    public async Task RecoveryAndTerminalCallersShareOneObservation()
    {
        var coordinator = new AppExitDrainCoordinator();
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int observations = 0;

        Task<bool> recoveryRetry = coordinator.GetOrStart(
            () => completion.Task);
        Task recoveryObservation = coordinator.GetOrStartObservation(
            recoveryRetry,
            async drain =>
            {
                Interlocked.Increment(ref observations);
                _ = await drain;
            });

        Task<bool> sessionEnding = coordinator.GetOrStart(
            () => throw new InvalidOperationException(
                "Session ending must share the recovery retry."));
        Assert.Same(recoveryRetry, sessionEnding);
        Task terminalObservation = coordinator.GetOrStartObservation(
            sessionEnding,
            _ => throw new InvalidOperationException(
                "A shared observation must not invoke another factory."));
        Assert.Same(recoveryObservation, terminalObservation);

        completion.SetResult(false);
        Assert.False(await recoveryRetry);
        await recoveryObservation;
        Assert.Equal(1, observations);
    }

    [Fact]
    public async Task NewDrainObservationWaitsForPriorPendingObservation()
    {
        var coordinator = new AppExitDrainCoordinator();
        var priorObservation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> firstDrain = coordinator.GetOrStart(
            () => Task.FromResult(false));
        Task firstTail = coordinator.GetOrStartObservation(
            firstDrain,
            _ => priorObservation.Task);
        Assert.False(await firstDrain);
        Assert.False(firstTail.IsCompleted);

        Task<bool> nextDrain = coordinator.GetOrStart(
            () => Task.FromResult(true));
        Task nextTail = coordinator.GetOrStartObservation(
            nextDrain,
            _ => Task.CompletedTask);
        Assert.True(await nextDrain);
        Assert.False(nextTail.IsCompleted);

        priorObservation.SetResult();
        await nextTail;
    }

    [Fact]
    public async Task SynchronousFactoryFailure_IsSharedAndLaterRetryIsAllowed()
    {
        var coordinator = new AppExitDrainCoordinator();

        Task<bool> failed = coordinator.GetOrStart(
            () => throw new InvalidOperationException("drain failed"));
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => failed);
        Assert.Equal("drain failed", error.Message);

        Assert.True(await coordinator.GetOrStart(() => Task.FromResult(true)));
    }

    [Fact]
    public async Task AsynchronousFailure_IsPreservedAndLaterRetryIsAllowed()
    {
        var coordinator = new AppExitDrainCoordinator();

        Task<bool> failed = coordinator.GetOrStart(
            () => Task.FromException<bool>(new IOException("media failure")));
        IOException error = await Assert.ThrowsAsync<IOException>(() => failed);
        Assert.Equal("media failure", error.Message);

        Assert.False(await coordinator.GetOrStart(() => Task.FromResult(false)));
    }

    [Fact]
    public async Task Cancellation_IsPreservedAndLaterRetryIsAllowed()
    {
        var coordinator = new AppExitDrainCoordinator();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Task<bool> failed = coordinator.GetOrStart(
            () => Task.FromCanceled<bool>(canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => failed);

        Assert.True(await coordinator.GetOrStart(() => Task.FromResult(true)));
    }
}
