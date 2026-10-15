using WinShot.App.Recording;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class AudioDeviceEnumerationTests
{
    [Fact]
    public async Task SuccessNormalizesBothSourcesInOneWorker()
    {
        int invocations = 0;
        var coordinator = new AudioDeviceEnumerationCoordinator(() =>
        {
            Interlocked.Increment(ref invocations);
            return new AudioDeviceCandidateCatalog(
                new[] { new AudioDeviceCandidate("mic", "Microphone") },
                new[] { new AudioDeviceCandidate("speaker", "Speakers") });
        });

        AudioDeviceLoadResult result = await coordinator.GetDevicesAsync(
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(AudioDeviceLoadStatus.Ready, result.Status);
        Assert.Equal(2, result.Catalog.InputDevices.Count);
        Assert.Equal(2, result.Catalog.OutputDevices.Count);
        Assert.Equal(1, Volatile.Read(ref invocations));
    }

    [Fact]
    public async Task NativeExceptionReturnsPrivacySafeDefaults()
    {
        var coordinator = new AudioDeviceEnumerationCoordinator(
            () => throw new InvalidOperationException(
                "private-device-id and private-device-name"));

        AudioDeviceLoadResult result = await coordinator.GetDevicesAsync(
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(AudioDeviceLoadStatus.Failed, result.Status);
        Assert.Null(Assert.Single(result.Catalog.InputDevices).Id);
        Assert.Null(Assert.Single(result.Catalog.OutputDevices).Id);
    }

    [Fact]
    public async Task TimeoutKeepsOneWorkerForLaterCoalescedCaller()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        int invocations = 0;
        var coordinator = new AudioDeviceEnumerationCoordinator(() =>
        {
            Interlocked.Increment(ref invocations);
            started.Set();
            release.Wait();
            finished.Set();
            return EmptyCatalog();
        });

        Task<AudioDeviceLoadResult> first = coordinator.GetDevicesAsync(
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);
        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));

        AudioDeviceLoadResult timedOut = await first;
        Assert.Equal(AudioDeviceLoadStatus.TimedOut, timedOut.Status);
        Assert.Null(Assert.Single(timedOut.Catalog.InputDevices).Id);

        Task<AudioDeviceLoadResult> coalesced = coordinator.GetDevicesAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref invocations));
        release.Set();

        AudioDeviceLoadResult completed = await coalesced;
        Assert.Equal(AudioDeviceLoadStatus.Ready, completed.Status);
        Assert.Equal(1, Volatile.Read(ref invocations));
        Assert.True(finished.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task CancellationReturnsWithoutStartingAnotherBlockedWorker()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        int invocations = 0;
        var coordinator = new AudioDeviceEnumerationCoordinator(() =>
        {
            Interlocked.Increment(ref invocations);
            started.Set();
            release.Wait();
            finished.Set();
            return EmptyCatalog();
        });

        Task<AudioDeviceLoadResult> pending = coordinator.GetDevicesAsync(
            TimeSpan.FromSeconds(2),
            cancellation.Token);
        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Task<AudioDeviceLoadResult> coalesced = coordinator.GetDevicesAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref invocations));
        release.Set();

        Assert.Equal(AudioDeviceLoadStatus.Ready, (await coalesced).Status);
        Assert.True(finished.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, Volatile.Read(ref invocations));
    }

    [Fact]
    public async Task PreCanceledRequestDoesNotStartNativeWork()
    {
        int invocations = 0;
        var coordinator = new AudioDeviceEnumerationCoordinator(() =>
        {
            Interlocked.Increment(ref invocations);
            return EmptyCatalog();
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.GetDevicesAsync(
                TimeSpan.FromSeconds(1),
                cancellation.Token));

        Assert.Equal(0, Volatile.Read(ref invocations));
    }

    [Fact]
    public void LatestGenerationAndClosedOwnerSuppressStaleResults()
    {
        var gate = new AudioDeviceRequestGate();
        long first = gate.Begin();
        long latest = gate.Begin();

        Assert.False(gate.CanApply(first, cancellationRequested: false));
        Assert.True(gate.CanApply(latest, cancellationRequested: false));
        Assert.False(gate.CanApply(latest, cancellationRequested: true));

        gate.Close();

        Assert.False(gate.CanApply(latest, cancellationRequested: false));
        Assert.Throws<InvalidOperationException>(() => gate.Begin());
    }

    private static AudioDeviceCandidateCatalog EmptyCatalog() =>
        new(Array.Empty<AudioDeviceCandidate>(), Array.Empty<AudioDeviceCandidate>());
}
