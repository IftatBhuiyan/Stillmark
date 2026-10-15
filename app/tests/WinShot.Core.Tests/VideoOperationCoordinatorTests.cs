using WinShot.App.Video;
using WinShot.Core.Update;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class VideoOperationCoordinatorTests
{
    [Fact]
    public void Begin_NewerOperationCancelsAndSuppressesOlderGeneration()
    {
        using var coordinator =
            new VideoOperationCoordinator(new UpdateActivityLedger());
        using VideoOperationLease first = coordinator.Begin();
        int published = 0;

        using VideoOperationLease second = coordinator.Begin();

        Assert.True(first.IsCancellationRequested);
        Assert.False(first.TryPublish(() => published++));
        Assert.False(first.TryFinalize(() => published++));
        Assert.True(second.TryPublish(() => published++));
        Assert.Equal(1, published);
    }

    [Fact]
    public void CancelCurrent_RejectsProgressButAllowsLatestFinalization()
    {
        using var coordinator =
            new VideoOperationCoordinator(new UpdateActivityLedger());
        using VideoOperationLease operation = coordinator.Begin();
        int progress = 0;
        int finalization = 0;

        coordinator.CancelCurrent();

        Assert.True(operation.IsCancellationRequested);
        Assert.False(operation.TryPublish(() => progress++));
        Assert.True(operation.TryFinalize(() => finalization++));
        Assert.Equal(0, progress);
        Assert.Equal(1, finalization);
    }

    [Fact]
    public void Dispose_CancelsActiveOperationAndRejectsAllCallbacks()
    {
        var coordinator =
            new VideoOperationCoordinator(new UpdateActivityLedger());
        using VideoOperationLease operation = coordinator.Begin();

        coordinator.Dispose();

        Assert.True(operation.IsCancellationRequested);
        Assert.False(operation.TryPublish(() => throw new InvalidOperationException()));
        Assert.False(operation.TryFinalize(() => throw new InvalidOperationException()));
    }

    [Fact]
    public void DisposingSupersededLease_DoesNotReleaseLatestOperation()
    {
        var activity = new UpdateActivityLedger();
        using var coordinator = new VideoOperationCoordinator(activity);
        VideoOperationLease first = coordinator.Begin();
        using VideoOperationLease second = coordinator.Begin();

        first.Dispose();

        Assert.True(coordinator.HasOperation);
        Assert.True(second.TryPublish(() => { }));
        Assert.True(activity.GetCurrentActivity().TranscodeActive);
        second.Dispose();
        Assert.False(activity.GetCurrentActivity().TranscodeActive);
    }

    [Fact]
    public void TryBegin_RefusesWorkWhileUpdateQuiescenceIsHeld()
    {
        var activity = new UpdateActivityLedger();
        using var coordinator = new VideoOperationCoordinator(activity);
        Assert.True(
            activity.TryBeginQuiescence(
                out UpdateQuiescenceLease? quiescence));
        using (quiescence)
        {
            Assert.False(coordinator.TryBegin(out VideoOperationLease? operation));
            Assert.Null(operation);
            Assert.False(coordinator.HasOperation);
        }

        Assert.True(coordinator.TryBegin(out VideoOperationLease? admitted));
        admitted.Dispose();
        Assert.False(activity.GetCurrentActivity().TranscodeActive);
    }

    [Theory]
    [InlineData(@"C:\clips\capture.mp4", @"C:\clips\capture (edited).mp4", true)]
    [InlineData(@"C:\clips\capture.mp4", @"C:\clips\capture (edited) (2).mp4", true)]
    [InlineData(@"C:\clips\capture.mp4", @"C:\clips\capture.mp4", false)]
    [InlineData(@"C:\clips\capture.mp4", @"C:\clips\another (edited).mp4", false)]
    [InlineData(@"C:\clips\capture.mp4", @"C:\clips\capture (edited)-unrelated.mp4", false)]
    [InlineData(@"C:\clips\capture.mp4", @"C:\clips\capture (edited) (x).mp4", false)]
    [InlineData(@"C:\clips\capture.mp4", @"C:\clips\capture (edited) (1).mp4", false)]
    [InlineData(@"C:\clips\capture.mp4", @"D:\clips\capture (edited).mp4", false)]
    [InlineData(@"C:\clips\capture.mp4", @"C:\clips\capture (edited).gif", false)]
    public void OutputOwnership_OnlyAcceptsSiblingEditedMp4(
        string sourcePath,
        string outputPath,
        bool expected)
    {
        Assert.Equal(expected, VideoOutputOwnership.IsGeneratedEditedOutput(sourcePath, outputPath));
    }

    [Fact]
    public async Task StaleOutputCleanup_DeletesOnlyGeneratedOutputAndPreservesSource()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "WinShotVideoOperationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "capture.mp4");
        string output = Path.Combine(directory, "capture (edited).mp4");

        try
        {
            await File.WriteAllTextAsync(source, "source");
            await File.WriteAllTextAsync(output, "generated");

            await VideoOutputOwnership.TryDeleteStaleOutputAsync(source, output);

            Assert.True(File.Exists(source));
            Assert.False(File.Exists(output));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
