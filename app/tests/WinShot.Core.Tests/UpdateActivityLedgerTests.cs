using WinShot.Core.Update;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class UpdateActivityLedgerTests
{
    [Fact]
    public void EmptyLedger_IsReady()
    {
        var ledger = new UpdateActivityLedger();

        UpdateActivitySnapshot snapshot = ledger.GetCurrentActivity();

        Assert.Equal(default, snapshot);
        Assert.True(UpdateCoordinationPolicy.Evaluate(snapshot).IsReady);
    }

    [Theory]
    [InlineData(UpdateActivityKind.Capture)]
    [InlineData(UpdateActivityKind.Recording)]
    [InlineData(UpdateActivityKind.Transcode)]
    public void Lease_ReportsExpectedActivityUntilDisposed(UpdateActivityKind kind)
    {
        var ledger = new UpdateActivityLedger();

        using (ledger.BeginActivity(kind))
            AssertKindIsOnlyActive(ledger.GetCurrentActivity(), kind);

        Assert.Equal(default, ledger.GetCurrentActivity());
    }

    [Fact]
    public void OverlappingLeases_DoNotClearOneAnother()
    {
        var ledger = new UpdateActivityLedger();
        UpdateActivityLease first = ledger.BeginActivity(UpdateActivityKind.Capture);
        using UpdateActivityLease second = ledger.BeginActivity(UpdateActivityKind.Capture);

        first.Dispose();

        Assert.True(ledger.GetCurrentActivity().CaptureActive);
        second.Dispose();
        Assert.Equal(default, ledger.GetCurrentActivity());
    }

    [Fact]
    public async Task Transition_MovesCategoryWithoutReadyGap()
    {
        var ledger = new UpdateActivityLedger();
        using UpdateActivityLease lease = ledger.BeginActivity(UpdateActivityKind.Recording);
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int readyObservations = 0;
        int snapshotCount = 0;
        Task reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                UpdateActivitySnapshot snapshot = ledger.GetCurrentActivity();
                Interlocked.Increment(ref snapshotCount);
                started.TrySetResult();
                if (UpdateCoordinationPolicy.Evaluate(snapshot).IsReady)
                    Interlocked.Increment(ref readyObservations);
            }
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            for (int i = 0; i < 10_000; i++)
            {
                lease.TransitionTo(UpdateActivityKind.Transcode);
                lease.TransitionTo(UpdateActivityKind.Recording);
            }
        }
        finally
        {
            stop.Cancel();
            await reader.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(Volatile.Read(ref snapshotCount) > 0);
        Assert.Equal(0, Volatile.Read(ref readyObservations));
    }

    [Fact]
    public void SameKindTransition_IsANoOp()
    {
        var ledger = new UpdateActivityLedger();
        using UpdateActivityLease lease = ledger.BeginActivity(UpdateActivityKind.Capture);

        lease.TransitionTo(UpdateActivityKind.Capture);

        Assert.Equal(UpdateActivityKind.Capture, lease.Kind);
        AssertKindIsOnlyActive(ledger.GetCurrentActivity(), UpdateActivityKind.Capture);
    }

    [Fact]
    public void ActiveWork_PreventsQuiescenceReservation()
    {
        var ledger = new UpdateActivityLedger();
        using UpdateActivityLease activity =
            ledger.BeginActivity(UpdateActivityKind.Recording);

        Assert.False(ledger.TryBeginQuiescence(out UpdateQuiescenceLease? quiescence));
        Assert.Null(quiescence);
        AssertKindIsOnlyActive(ledger.GetCurrentActivity(), UpdateActivityKind.Recording);
    }

    [Fact]
    public void QuiescenceReservation_BlocksNewWorkUntilReleased()
    {
        var ledger = new UpdateActivityLedger();
        Assert.True(ledger.TryBeginQuiescence(out UpdateQuiescenceLease? quiescence));
        Assert.NotNull(quiescence);

        Assert.False(
            ledger.TryBeginActivity(
                UpdateActivityKind.Capture,
                out UpdateActivityLease? refused));
        Assert.Null(refused);
        Assert.Throws<InvalidOperationException>(
            () => ledger.BeginActivity(UpdateActivityKind.Recording));
        Assert.False(ledger.TryBeginQuiescence(out _));
        Assert.Equal(default, ledger.GetCurrentActivity());

        quiescence.Dispose();
        quiescence.Dispose();
        using UpdateActivityLease admitted =
            ledger.BeginActivity(UpdateActivityKind.Transcode);
        AssertKindIsOnlyActive(ledger.GetCurrentActivity(), UpdateActivityKind.Transcode);
    }

    [Fact]
    public async Task ActivityAndQuiescenceRace_ExactlyOneAdmissionWins()
    {
        for (int i = 0; i < 250; i++)
        {
            var ledger = new UpdateActivityLedger();
            using var barrier = new Barrier(2);
            Task<(bool Success, UpdateActivityLease? Lease)> activityAttempt =
                Task.Run(() =>
                {
                    if (!barrier.SignalAndWait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Activity admission barrier timed out.");
                    bool success = ledger.TryBeginActivity(
                        UpdateActivityKind.Capture,
                        out UpdateActivityLease? lease);
                    return (success, lease);
                });
            Task<(bool Success, UpdateQuiescenceLease? Lease)> quiescenceAttempt =
                Task.Run(() =>
                {
                    if (!barrier.SignalAndWait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Quiescence admission barrier timed out.");
                    bool success =
                        ledger.TryBeginQuiescence(out UpdateQuiescenceLease? lease);
                    return (success, lease);
                });

            (bool activityWon, UpdateActivityLease? activity) =
                await activityAttempt.WaitAsync(TimeSpan.FromSeconds(5));
            (bool quiescenceWon, UpdateQuiescenceLease? quiescence) =
                await quiescenceAttempt.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotEqual(activityWon, quiescenceWon);
            if (activityWon)
            {
                Assert.NotNull(activity);
                Assert.Null(quiescence);
                activity.Dispose();
                Assert.True(ledger.TryBeginQuiescence(out quiescence));
                quiescence.Dispose();
            }
            else
            {
                Assert.Null(activity);
                Assert.NotNull(quiescence);
                quiescence.Dispose();
                Assert.True(
                    ledger.TryBeginActivity(
                        UpdateActivityKind.Recording,
                        out activity));
                activity.Dispose();
            }

            Assert.Equal(default, ledger.GetCurrentActivity());
        }
    }

    [Fact]
    public void QuiescenceLease_ConcurrentDisposeIsIdempotent()
    {
        IUpdateActivityCoordinator coordinator = new UpdateActivityLedger();
        Assert.True(
            coordinator.TryBeginQuiescence(
                out UpdateQuiescenceLease? quiescence));

        Parallel.Invoke(
            quiescence.Dispose,
            quiescence.Dispose,
            quiescence.Dispose);

        Assert.True(
            coordinator.TryBeginActivity(
                UpdateActivityKind.Capture,
                out UpdateActivityLease? activity));
        activity.Dispose();
        Assert.Equal(default, coordinator.GetCurrentActivity());
    }

    [Fact]
    public void ConcurrentIndependentLeases_ReturnToReady()
    {
        var ledger = new UpdateActivityLedger();

        Parallel.For(
            0,
            10_000,
            index =>
            {
                UpdateActivityKind kind = (UpdateActivityKind)(index % 3);
                using UpdateActivityLease lease = ledger.BeginActivity(kind);
                if ((index & 1) == 0)
                    lease.TransitionTo((UpdateActivityKind)((index + 1) % 3));
            });

        Assert.Equal(default, ledger.GetCurrentActivity());
    }

    [Fact]
    public void Lease_DisposeIsIdempotent_AndTransitionAfterDisposeIsRejected()
    {
        var ledger = new UpdateActivityLedger();
        UpdateActivityLease lease = ledger.BeginActivity(UpdateActivityKind.Capture);

        lease.Dispose();
        lease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = lease.Kind);
        Assert.Throws<ObjectDisposedException>(
            () => lease.TransitionTo(UpdateActivityKind.Recording));
        Assert.Equal(default, ledger.GetCurrentActivity());
    }

    [Fact]
    public void ConcurrentDisposeAndTransition_EndWithoutLeakingActivity()
    {
        for (int i = 0; i < 1_000; i++)
        {
            var ledger = new UpdateActivityLedger();
            UpdateActivityLease lease =
                ledger.BeginActivity(UpdateActivityKind.Capture);

            Parallel.Invoke(
                lease.Dispose,
                () =>
                {
                    try
                    {
                        lease.TransitionTo(UpdateActivityKind.Recording);
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });

            lease.Dispose();
            Assert.Equal(default, ledger.GetCurrentActivity());
        }
    }

    [Fact]
    public void UnknownKinds_AreRejectedWithoutChangingState()
    {
        var ledger = new UpdateActivityLedger();
        UpdateActivityKind invalid = (UpdateActivityKind)int.MaxValue;

        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.BeginActivity(invalid));

        using UpdateActivityLease lease = ledger.BeginActivity(UpdateActivityKind.Capture);
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.TransitionTo(invalid));
        AssertKindIsOnlyActive(ledger.GetCurrentActivity(), UpdateActivityKind.Capture);
    }

    private static void AssertKindIsOnlyActive(
        UpdateActivitySnapshot snapshot,
        UpdateActivityKind expected)
    {
        Assert.Equal(expected == UpdateActivityKind.Capture, snapshot.CaptureActive);
        Assert.Equal(expected == UpdateActivityKind.Recording, snapshot.RecordingActive);
        Assert.Equal(expected == UpdateActivityKind.Transcode, snapshot.TranscodeActive);
    }
}
