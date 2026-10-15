using WinShot.App.Lifecycle;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class IntentionalQuitFinalizerTests
{
    [Fact]
    public async Task SuccessfulDrain_FinalizesOnceWithoutLogging()
    {
        var failures = new List<Exception>();
        int finalized = 0;

        await IntentionalQuitFinalizer.RunAsync(
            () => Task.FromResult(true),
            failures.Add,
            () => finalized++);

        Assert.Empty(failures);
        Assert.Equal(1, finalized);
    }

    [Fact]
    public async Task TimedOutDrain_LogsAndFinalizesOnce()
    {
        var failures = new List<Exception>();
        int finalized = 0;

        await IntentionalQuitFinalizer.RunAsync(
            () => Task.FromResult(false),
            failures.Add,
            () => finalized++);

        Assert.IsType<TimeoutException>(Assert.Single(failures));
        Assert.Equal(1, finalized);
    }

    [Fact]
    public async Task FaultedDrain_LogsAndFinalizesOnce()
    {
        var expected = new IOException("synthetic media drain failure");
        var failures = new List<Exception>();
        int finalized = 0;

        await IntentionalQuitFinalizer.RunAsync(
            () => Task.FromException<bool>(expected),
            failures.Add,
            () => finalized++);

        Assert.Same(expected, Assert.Single(failures));
        Assert.Equal(1, finalized);
    }

    [Fact]
    public async Task SynchronousDrainFailure_StillFinalizes()
    {
        var expected = new InvalidOperationException(
            "synthetic drain factory failure");
        var failures = new List<Exception>();
        int finalized = 0;

        await IntentionalQuitFinalizer.RunAsync(
            () => throw expected,
            failures.Add,
            () => finalized++);

        Assert.Same(expected, Assert.Single(failures));
        Assert.Equal(1, finalized);
    }

    [Fact]
    public async Task ThrowingLogger_CannotSuppressFinalization()
    {
        int finalized = 0;

        await IntentionalQuitFinalizer.RunAsync(
            () => Task.FromException<bool>(
                new IOException("synthetic media drain failure")),
            _ => throw new InvalidOperationException(
                "synthetic diagnostic failure"),
            () => finalized++);

        Assert.Equal(1, finalized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedSessionDrain_ReportsOnceAndQuitStillFinalizes(
        bool faulted)
    {
        var coordinator = new AppExitDrainCoordinator();
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new List<Exception>();
        int finalized = 0;
        bool observationCompleted = false;

        Task<bool> sessionDrain = coordinator.GetOrStart(
            () => completion.Task);
        Task sessionObservation = coordinator.GetOrStartObservation(
            sessionDrain,
            async drain =>
            {
                await ObserveSessionDrainAsync(drain, failures);
                observationCompleted = true;
            });

        Task quit = IntentionalQuitFinalizer.RunAsync(
            async () =>
            {
                Task<bool> shared = coordinator.GetOrStart(
                    () => throw new InvalidOperationException(
                        "Quit must share the active session drain."));
                Task observation = coordinator.GetOrStartObservation(
                    shared,
                    _ => throw new InvalidOperationException(
                        "Quit must share the active observation."));
                await observation;
                return true;
            },
            failures.Add,
            () =>
            {
                Assert.True(observationCompleted);
                finalized++;
            });

        if (faulted)
            completion.SetException(new IOException("synthetic shared failure"));
        else
            completion.SetResult(false);

        await Task.WhenAll(sessionObservation, quit);

        Assert.Single(failures);
        Assert.Equal(1, finalized);
    }

    [Fact]
    public async Task CompletedPriorDrainObservationFinishesBeforeQuitFinalizes()
    {
        var coordinator = new AppExitDrainCoordinator();
        var priorObservation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool priorObservationCompleted = false;
        int finalized = 0;

        Task<bool> priorDrain = coordinator.GetOrStart(
            () => Task.FromResult(false));
        Task priorTail = coordinator.GetOrStartObservation(
            priorDrain,
            async _ =>
            {
                await priorObservation.Task;
                priorObservationCompleted = true;
            });
        Assert.False(await priorDrain);
        Assert.False(priorTail.IsCompleted);

        Task quit = IntentionalQuitFinalizer.RunAsync(
            async () =>
            {
                Task<bool> quitDrain = coordinator.GetOrStart(
                    () => Task.FromResult(true));
                Task observationTail = coordinator.GetOrStartObservation(
                    quitDrain,
                    _ => Task.CompletedTask);
                await observationTail;
                return true;
            },
            _ => { },
            () =>
            {
                Assert.True(priorObservationCompleted);
                finalized++;
            });

        await Task.Yield();
        Assert.False(quit.IsCompleted);
        priorObservation.SetResult();
        await quit;

        Assert.Equal(1, finalized);
    }

    private static async Task ObserveSessionDrainAsync(
        Task<bool> drain,
        ICollection<Exception> failures)
    {
        try
        {
            if (!await drain)
            {
                failures.Add(new TimeoutException(
                    "synthetic session drain timeout"));
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }
}
