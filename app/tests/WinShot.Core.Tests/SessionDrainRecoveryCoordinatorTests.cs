using WinShot.App.Lifecycle;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class SessionDrainRecoveryCoordinatorTests
{
    [Fact]
    public async Task SuccessfulCurrentDrainSkipsDelayAndRetry()
    {
        int delayCalls = 0;
        int retryCalls = 0;
        var coordinator = new SessionDrainRecoveryCoordinator(
            () => Task.FromResult(true),
            _ =>
            {
                retryCalls++;
                return Task.FromResult(true);
            },
            _ => true,
            [TimeSpan.FromSeconds(1)],
            (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            });

        Assert.Equal(
            SessionDrainRecoveryOutcome.Recovered,
            await coordinator.RequestRecovery(1));
        Assert.Equal(0, delayCalls);
        Assert.Equal(0, retryCalls);
    }

    [Fact]
    public async Task AbsentCurrentDrainSkipsDelayAndRetry()
    {
        int delayCalls = 0;
        int retryCalls = 0;
        var coordinator = new SessionDrainRecoveryCoordinator(
            () => null,
            _ =>
            {
                retryCalls++;
                return Task.FromResult(true);
            },
            _ => true,
            [TimeSpan.FromSeconds(1)],
            (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            });

        Assert.Equal(
            SessionDrainRecoveryOutcome.Recovered,
            await coordinator.RequestRecovery(1));
        Assert.Equal(0, delayCalls);
        Assert.Equal(0, retryCalls);
    }

    [Fact]
    public async Task FalseCurrentDrain_RetriesAndRecoversAfterSuccess()
    {
        var delays = new ControlledDelays();
        int attempts = 0;
        var coordinator = Create(
            currentDrain: () => Task.FromResult(false),
            retryDrain: _ =>
            {
                attempts++;
                return Task.FromResult(true);
            },
            delays,
            TimeSpan.FromMilliseconds(10));

        Task<SessionDrainRecoveryOutcome> recovery =
            coordinator.RequestRecovery(1);

        Assert.False(recovery.IsCompleted);
        delays.ReleaseNext();

        Assert.Equal(
            SessionDrainRecoveryOutcome.Recovered,
            await recovery);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultedOrCanceledCurrentDrain_RetriesAndRecovers(
        bool canceled)
    {
        var delays = new ControlledDelays();
        Task<bool> failed = canceled
            ? Task.FromCanceled<bool>(new CancellationToken(canceled: true))
            : Task.FromException<bool>(
                new InvalidOperationException("synthetic"));
        var coordinator = Create(
            currentDrain: () => failed,
            retryDrain: _ => Task.FromResult(true),
            delays,
            TimeSpan.Zero);

        Task<SessionDrainRecoveryOutcome> recovery =
            coordinator.RequestRecovery(1);
        delays.ReleaseNext();

        Assert.Equal(
            SessionDrainRecoveryOutcome.Recovered,
            await recovery);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultedOrCanceledRetryDrain_UsesNextBoundedAttempt(
        bool canceled)
    {
        var delays = new ControlledDelays();
        int attempts = 0;
        var coordinator = Create(
            currentDrain: () => Task.FromResult(false),
            retryDrain: _ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    return canceled
                        ? Task.FromCanceled<bool>(
                            new CancellationToken(canceled: true))
                        : Task.FromException<bool>(
                            new InvalidOperationException("synthetic"));
                }

                return Task.FromResult(true);
            },
            delays,
            TimeSpan.Zero,
            TimeSpan.Zero);

        Task<SessionDrainRecoveryOutcome> recovery =
            coordinator.RequestRecovery(1);
        delays.ReleaseNext();
        await WaitUntilAsync(() => delays.PendingCount == 1);
        delays.ReleaseNext();

        Assert.Equal(
            SessionDrainRecoveryOutcome.Recovered,
            await recovery);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task RepeatedRequestCollapsesAndNewerRequestDoesNotOverlapAttempt()
    {
        var delays = new ControlledDelays();
        var firstAttempt = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int activeAttempts = 0;
        int maximumActiveAttempts = 0;
        int attempts = 0;
        var coordinator = Create(
            currentDrain: () => Task.FromResult(false),
            retryDrain: async cancellationToken =>
            {
                int active = Interlocked.Increment(ref activeAttempts);
                UpdateMaximum(ref maximumActiveAttempts, active);
                int attempt = Interlocked.Increment(ref attempts);
                try
                {
                    if (attempt == 1)
                        return await firstAttempt.Task;

                    cancellationToken.ThrowIfCancellationRequested();
                    return true;
                }
                finally
                {
                    Interlocked.Decrement(ref activeAttempts);
                }
            },
            delays,
            TimeSpan.Zero);

        Task<SessionDrainRecoveryOutcome> first =
            coordinator.RequestRecovery(1);
        Assert.Same(first, coordinator.RequestRecovery(1));
        delays.ReleaseNext();
        await WaitUntilAsync(() => Volatile.Read(ref attempts) == 1);

        Task<SessionDrainRecoveryOutcome> newer =
            coordinator.RequestRecovery(2);
        await Task.Yield();
        Assert.Equal(1, Volatile.Read(ref attempts));

        firstAttempt.SetResult(true);
        Assert.Equal(
            SessionDrainRecoveryOutcome.Superseded,
            await first);

        await WaitUntilAsync(() => delays.PendingCount == 1);
        delays.ReleaseNext();
        Assert.Equal(
            SessionDrainRecoveryOutcome.Recovered,
            await newer);
        Assert.Equal(2, attempts);
        Assert.Equal(1, maximumActiveAttempts);
    }

    [Fact]
    public async Task NewerTicketSuppressesOlderSuccessfulDrain()
    {
        var current = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var delays = new ControlledDelays();
        var coordinator = Create(
            currentDrain: () => current.Task,
            retryDrain: _ => Task.FromResult(true),
            delays,
            TimeSpan.Zero);

        Task<SessionDrainRecoveryOutcome> first =
            coordinator.RequestRecovery(10);
        Task<SessionDrainRecoveryOutcome> newer =
            coordinator.RequestRecovery(11);
        current.SetResult(true);

        Assert.Equal(
            SessionDrainRecoveryOutcome.Superseded,
            await first);
        Assert.Equal(
            SessionDrainRecoveryOutcome.Recovered,
            await newer);
    }

    [Fact]
    public async Task CancelPendingStopsDelayAndReturnsCanceled()
    {
        var delays = new ControlledDelays();
        int attempts = 0;
        var coordinator = Create(
            currentDrain: () => Task.FromResult(false),
            retryDrain: _ =>
            {
                attempts++;
                return Task.FromResult(true);
            },
            delays,
            TimeSpan.FromSeconds(1));

        Task<SessionDrainRecoveryOutcome> recovery =
            coordinator.RequestRecovery(1);
        await WaitUntilAsync(() => delays.PendingCount == 1);
        coordinator.CancelPending();

        Assert.Equal(
            SessionDrainRecoveryOutcome.Canceled,
            await recovery);
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task ExhaustionOccursOnceForLatestCollapsedRequest()
    {
        var delays = new ControlledDelays();
        int attempts = 0;
        var coordinator = Create(
            currentDrain: () => Task.FromResult(false),
            retryDrain: _ =>
            {
                attempts++;
                return Task.FromResult(false);
            },
            delays,
            TimeSpan.Zero,
            TimeSpan.Zero);

        Task<SessionDrainRecoveryOutcome> first =
            coordinator.RequestRecovery(3);
        Task<SessionDrainRecoveryOutcome> duplicate =
            coordinator.RequestRecovery(3);
        Assert.Same(first, duplicate);

        delays.ReleaseNext();
        await WaitUntilAsync(() => delays.PendingCount == 1);
        delays.ReleaseNext();

        Assert.Equal(
            SessionDrainRecoveryOutcome.Exhausted,
            await first);
        Assert.Equal(
            SessionDrainRecoveryOutcome.Exhausted,
            await duplicate);
        Assert.Equal(2, attempts);
        Assert.Same(first, coordinator.RequestRecovery(3));
        Assert.Equal(
            SessionDrainRecoveryOutcome.Superseded,
            await coordinator.RequestRecovery(2));
    }

    [Fact]
    public async Task SynchronousRetryFactoryExceptionIsRetried()
    {
        var delays = new ControlledDelays();
        int attempts = 0;
        var coordinator = Create(
            currentDrain: () => Task.FromResult(false),
            retryDrain: _ =>
            {
                attempts++;
                if (attempts == 1)
                    throw new InvalidOperationException("synthetic");
                return Task.FromResult(true);
            },
            delays,
            TimeSpan.Zero,
            TimeSpan.Zero);

        Task<SessionDrainRecoveryOutcome> recovery =
            coordinator.RequestRecovery(1);
        delays.ReleaseNext();
        await WaitUntilAsync(() => delays.PendingCount == 1);
        delays.ReleaseNext();

        Assert.Equal(
            SessionDrainRecoveryOutcome.Recovered,
            await recovery);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task EmptyRetryScheduleExhaustsAfterFailedCurrentDrain()
    {
        var coordinator = Create(
            currentDrain: () => Task.FromResult(false),
            retryDrain: _ => throw new InvalidOperationException(
                "No retry should run."),
            new ControlledDelays());

        Assert.Equal(
            SessionDrainRecoveryOutcome.Exhausted,
            await coordinator.RequestRecovery(1));
    }

    [Fact]
    public void InvalidInputsAreRejected()
    {
        var delays = new ControlledDelays();

        Assert.Throws<ArgumentNullException>(() =>
            new SessionDrainRecoveryCoordinator(
                null!,
                _ => Task.FromResult(true),
                _ => true,
                [],
                delays.DelayAsync));
        Assert.Throws<ArgumentNullException>(() =>
            new SessionDrainRecoveryCoordinator(
                () => null,
                null!,
                _ => true,
                [],
                delays.DelayAsync));
        Assert.Throws<ArgumentNullException>(() =>
            new SessionDrainRecoveryCoordinator(
                () => null,
                _ => Task.FromResult(true),
                null!,
                [],
                delays.DelayAsync));
        Assert.Throws<ArgumentNullException>(() =>
            new SessionDrainRecoveryCoordinator(
                () => null,
                _ => Task.FromResult(true),
                _ => true,
                null!,
                delays.DelayAsync));
        Assert.Throws<ArgumentNullException>(() =>
            new SessionDrainRecoveryCoordinator(
                () => null,
                _ => Task.FromResult(true),
                _ => true,
                [],
                null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SessionDrainRecoveryCoordinator(
                () => null,
                _ => Task.FromResult(true),
                _ => true,
                [TimeSpan.FromTicks(-1)],
                delays.DelayAsync));

        var coordinator = Create(
            currentDrain: () => null,
            retryDrain: _ => Task.FromResult(true),
            delays);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = coordinator.RequestRecovery(0); });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = coordinator.RequestRecovery(-1); });
    }

    [Fact]
    public async Task CanContinuePreventsStaleRecovery()
    {
        bool admitted = true;
        var current = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new SessionDrainRecoveryCoordinator(
            () => current.Task,
            _ => Task.FromResult(true),
            _ => admitted,
            [TimeSpan.Zero],
            static (_, _) => Task.CompletedTask);

        Task<SessionDrainRecoveryOutcome> recovery =
            coordinator.RequestRecovery(1);
        admitted = false;
        current.SetResult(true);

        Assert.Equal(
            SessionDrainRecoveryOutcome.Superseded,
            await recovery);
    }

    [Fact]
    public async Task DelayAndRetryFactoriesPreserveRequestSynchronizationContext()
    {
        SynchronizationContext? prior = SynchronizationContext.Current;
        var context = new TrackingSynchronizationContext();
        var delays = new ControlledDelays();
        SynchronizationContext? delayContext = null;
        SynchronizationContext? retryContext = null;
        Task<SessionDrainRecoveryOutcome> recovery;
        var coordinator = new SessionDrainRecoveryCoordinator(
            () => Task.FromResult(false),
            _ =>
            {
                retryContext = SynchronizationContext.Current;
                return Task.FromResult(true);
            },
            _ => true,
            [TimeSpan.Zero],
            (delay, cancellationToken) =>
            {
                delayContext = SynchronizationContext.Current;
                return delays.DelayAsync(delay, cancellationToken);
            });

        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            recovery = coordinator.RequestRecovery(1);
            Assert.Same(context, delayContext);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prior);
        }

        delays.ReleaseNext();

        Assert.Equal(
            SessionDrainRecoveryOutcome.Recovered,
            await recovery);
        Assert.Same(context, retryContext);
    }

    private static SessionDrainRecoveryCoordinator Create(
        Func<Task<bool>?> currentDrain,
        Func<CancellationToken, Task<bool>> retryDrain,
        ControlledDelays delays,
        params TimeSpan[] retryDelays) =>
        new(
            currentDrain,
            retryDrain,
            _ => true,
            retryDelays,
            delays.DelayAsync);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }

        throw new TimeoutException("The expected asynchronous state was not reached.");
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref maximum);
            if (candidate <= observed)
                return;
        }
        while (Interlocked.CompareExchange(
            ref maximum,
            candidate,
            observed) != observed);
    }

    private sealed class ControlledDelays
    {
        private readonly object _gate = new();
        private readonly Queue<TaskCompletionSource> _pending = new();

        internal int PendingCount
        {
            get
            {
                lock (_gate)
                    return _pending.Count;
            }
        }

        internal Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
                _pending.Enqueue(completion);

            cancellationToken.Register(
                static state =>
                    ((TaskCompletionSource)state!).TrySetCanceled(),
                completion);
            return completion.Task;
        }

        internal void ReleaseNext()
        {
            TaskCompletionSource completion;
            lock (_gate)
            {
                Assert.NotEmpty(_pending);
                completion = _pending.Dequeue();
            }

            completion.TrySetResult();
        }
    }

    private sealed class TrackingSynchronizationContext
        : SynchronizationContext
    {
        public override void Post(
            SendOrPostCallback callback,
            object? state)
        {
            ThreadPool.QueueUserWorkItem(
                _ =>
                {
                    SynchronizationContext? prior =
                        SynchronizationContext.Current;
                    try
                    {
                        SynchronizationContext.SetSynchronizationContext(this);
                        callback(state);
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(prior);
                    }
                });
        }
    }
}
