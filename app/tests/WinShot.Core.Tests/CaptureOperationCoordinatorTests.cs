using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.App;
using WinShot.Capture;
using WinShot.Core.Settings;
using WinShot.Core.Update;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class CaptureOperationCoordinatorTests
{
    [Fact]
    public void NewGenerationCancelsAndInvalidatesPriorOperation()
    {
        var coordinator = new CaptureOperationCoordinator();
        long firstGeneration = coordinator.BeginLatest();
        Assert.True(coordinator.TryStart(firstGeneration, out CaptureOperation first));

        long secondGeneration = coordinator.BeginLatest();

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(firstGeneration));
        Assert.True(coordinator.IsCurrent(secondGeneration));
        Assert.False(coordinator.TryStart(firstGeneration, out _));
        Assert.False(coordinator.TryCommit(first));
        Assert.True(coordinator.TryStart(secondGeneration, out CaptureOperation second));
        Assert.True(coordinator.TryCommit(second));
        Assert.False(coordinator.TryCommit(second));

        coordinator.Complete(first);
        coordinator.Complete(second);
    }

    [Fact]
    public void GenerationStartsAtMostOneObservedOperation()
    {
        var coordinator = new CaptureOperationCoordinator();
        long generation = coordinator.BeginLatest();

        Assert.True(coordinator.TryStart(generation, out CaptureOperation operation));
        coordinator.Complete(operation);
        Assert.False(coordinator.TryStart(generation, out _));
    }

    [Fact]
    public void CancelCurrentCancelsTokenAndRejectsOldGeneration()
    {
        var coordinator = new CaptureOperationCoordinator();
        long generation = coordinator.BeginLatest();
        Assert.True(coordinator.TryStart(generation, out CaptureOperation operation));

        coordinator.CancelCurrent();

        Assert.True(operation.Token.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(generation));
        Assert.False(coordinator.TryStart(generation, out _));
        Assert.False(coordinator.TryCommit(operation));
        coordinator.Complete(operation);
    }

    [Fact]
    public void SynchronousGenerationCommitsExactlyOnce()
    {
        var coordinator = new CaptureOperationCoordinator();
        long generation = coordinator.BeginLatest();

        Assert.True(coordinator.TryCommit(generation));
        Assert.False(coordinator.TryCommit(generation));
        Assert.False(coordinator.TryStart(generation, out _));
    }

    [Fact]
    public async Task ControllerCancellationReturnsImmediatelyWhenNoWorkIsPending()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinShotTests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsManager(new SettingsStore(Path.Combine(root, "settings.json")));
            var controller = new CaptureController(settings, new UpdateActivityLedger());

            Assert.True(await controller.CancelPendingOperationsAsync(TimeSpan.Zero));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => controller.CancelPendingOperationsAsync(TimeSpan.FromMilliseconds(-1)));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ControllerRefusesCaptureBeforeOpeningUiDuringUpdateQuiescence()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinShotTests", Guid.NewGuid().ToString("N"));
        try
        {
            var activities = new UpdateActivityLedger();
            Assert.True(activities.TryBeginQuiescence(out UpdateQuiescenceLease? quiescence));
            using (quiescence)
            {
                var settings = new SettingsManager(
                    new SettingsStore(Path.Combine(root, "settings.json")));
                var controller = new CaptureController(settings, activities);
                string? message = null;
                controller.Notify += (_, body, _) => message = body;

                controller.StartAreaCapture();

                Assert.NotNull(message);
                Assert.Contains("update", message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(
                    new UpdateActivitySnapshot(false, false, false),
                    activities.GetCurrentActivity());
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OcrRejectsPreCancelledWorkBeforePlatformActivation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        BitmapSource image = CreateBitmap();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => OcrService.RecognizeAsync(image, source.Token));
    }

    [Fact]
    public void OcrSizingBoundsSquareImageByWorkingBuffer()
    {
        OcrImageSize size = OcrImageSizing.Calculate(10_000, 10_000, maxDimension: 7_680);

        Assert.Equal(size.Width, size.Height);
        Assert.InRange(size.Width, 1, 4_096);
        Assert.InRange(size.BgraBytes, 4, OcrImageSizing.DefaultMaxBgraBytes);
    }

    [Fact]
    public void OcrSizingUsesUniformDimensionScaleForPanorama()
    {
        OcrImageSize size = OcrImageSizing.Calculate(10_000, 1_000, maxDimension: 7_680);

        Assert.Equal(7_680, size.Width);
        Assert.Equal(768, size.Height);
        Assert.Equal(10, (double)size.Width / size.Height, precision: 6);
        Assert.InRange(size.BgraBytes, 4, OcrImageSizing.DefaultMaxBgraBytes);
    }

    [Fact]
    public void OcrSizingHandlesExtremeDimensionsWithoutOverflow()
    {
        OcrImageSize size = OcrImageSizing.Calculate(
            int.MaxValue,
            int.MaxValue,
            maxDimension: 7_680);

        Assert.InRange(size.Width, 1, 4_096);
        Assert.InRange(size.Height, 1, 4_096);
        Assert.InRange(size.BgraBytes, 4, OcrImageSizing.DefaultMaxBgraBytes);

        OcrImageSize thin = OcrImageSizing.Calculate(
            int.MaxValue,
            1,
            maxDimension: 100);
        Assert.Equal(100, thin.Width);
        Assert.Equal(1, thin.Height);
    }

    private static BitmapSource CreateBitmap()
    {
        var pixels = new byte[16];
        var image = BitmapSource.Create(
            2,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            8);
        image.Freeze();
        return image;
    }
}
