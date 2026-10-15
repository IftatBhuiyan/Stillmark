using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.App;
using Xunit;

namespace WinShot.Core.Tests;

public class HistoryThumbnailPolicyTests
{
    [Fact]
    public void LocalSupportedImagePath_IsNormalized()
    {
        string candidate = Path.Combine(
            Path.GetTempPath(),
            "WinShotTests",
            "capture.PNG");

        bool accepted = HistoryThumbnailPolicy.TryNormalizeLocalImagePath(
            candidate,
            out string fullPath);

        Assert.True(accepted);
        Assert.Equal(Path.GetFullPath(candidate), fullPath);
    }

    [Theory]
    [InlineData("capture.png")]
    [InlineData(@"\\server\share\capture.png")]
    [InlineData(@"\\?\C:\capture.png")]
    [InlineData(@"C:\capture.exe")]
    [InlineData("https://example.test/capture.png")]
    public void UnsafeOrUnsupportedImagePath_IsRejected(string candidate)
    {
        Assert.False(HistoryThumbnailPolicy.TryNormalizeLocalImagePath(
            candidate,
            out string fullPath));
        Assert.Equal("", fullPath);
    }

    [Theory]
    [InlineData(@"\\server\share\capture.png")]
    [InlineData(@"Z:\captures\capture.jpg")]
    public void ShellActionPath_AllowsExplicitLocalOrNetworkImagePath(string candidate)
    {
        Assert.True(HistoryThumbnailPolicy.TryNormalizeImagePathForShellAction(
            candidate,
            out string fullPath));
        Assert.Equal(Path.GetFullPath(candidate), fullPath);
    }

    [Theory]
    [InlineData(@"\\?\C:\capture.png")]
    [InlineData(@"\\.\C:\capture.png")]
    [InlineData(@"Z:\captures\capture.png:metadata")]
    [InlineData(@"C:\captures\capture?.png")]
    [InlineData(@"C:\captures\capture*.png")]
    [InlineData(@"C:\captures\capture<.png")]
    [InlineData(@"C:\captures\capture>.png")]
    [InlineData(@"C:\captures\capture|.png")]
    [InlineData("C:\\captures\\capture\".png")]
    [InlineData("C:\\captures\\capture\u0001.png")]
    [InlineData(@"C:\captures:name\capture.png")]
    [InlineData(@"\\server:name\share\capture.png")]
    [InlineData(@"1:\captures\capture.png")]
    public void ShellActionPath_RejectsDeviceAndAlternateDataStreamPaths(string candidate)
    {
        Assert.False(HistoryThumbnailPolicy.TryNormalizeImagePathForShellAction(
            candidate,
            out string fullPath));
        Assert.Equal("", fullPath);
    }

    [Fact]
    public void MappedDrive_IsClassifiedRemoteBeforeAttributeAccess()
    {
        int attributeReads = 0;
        HistoryThumbnailPathResolution result = HistoryThumbnailPolicy.ResolveForThumbnail(
            @"Z:\captures\capture.png",
            _ => DriveType.Network,
            _ =>
            {
                attributeReads++;
                throw new InvalidOperationException("attribute access should not occur");
            });

        Assert.False(result.IsAccepted);
        Assert.Equal(HistoryThumbnailStatus.Remote, result.RejectionStatus);
        Assert.Equal(0, attributeReads);
    }

    [Fact]
    public void ReparsePointComponent_IsRejectedBeforeOpeningTarget()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        string candidate = Path.Combine(root, "safe", "linked", "capture.png");

        bool safe = HistoryThumbnailPolicy.HasNoReparsePointComponents(
            candidate,
            path => Path.GetFileName(path).Equals(
                    "linked",
                    StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : FileAttributes.Directory);

        Assert.False(safe);
    }

    [Fact]
    public void ExcessivePathComponents_AreRejectedWithoutAttributeWalk()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        string candidate = Enumerable.Range(
                0,
                HistoryThumbnailPolicy.MaxPathComponents + 1)
            .Select(index => $"part{index}")
            .Aggregate(root, Path.Combine);
        int reads = 0;

        bool safe = HistoryThumbnailPolicy.HasNoReparsePointComponents(
            candidate,
            _ =>
            {
                reads++;
                return FileAttributes.Directory;
            });

        Assert.False(safe);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void MissingComponent_IsSafeToClassifyAsMissingWithoutWalkingDeeper()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        string candidate = Path.Combine(root, "safe", "missing", "capture.png");
        var visited = new List<string>();

        bool safe = HistoryThumbnailPolicy.HasNoReparsePointComponents(
            candidate,
            path =>
            {
                visited.Add(path);
                if (Path.GetFileName(path).Equals(
                        "missing",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new DirectoryNotFoundException();
                }
                return FileAttributes.Directory;
            });

        Assert.True(safe);
        Assert.DoesNotContain(
            visited,
            path => Path.GetFileName(path).Equals(
                "capture.png",
                StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(1920, 1080, 392, 0)]
    [InlineData(1080, 1920, 0, 244)]
    [InlineData(392, 244, 392, 0)]
    public void DecodeSize_PreservesAspectRatioWithOneBoundedDimension(
        int sourceWidth,
        int sourceHeight,
        int expectedWidth,
        int expectedHeight)
    {
        bool accepted = HistoryThumbnailPolicy.TryGetDecodeSize(
            sourceWidth,
            sourceHeight,
            out HistoryThumbnailDecodeSize size);

        Assert.True(accepted);
        Assert.Equal(expectedWidth, size.DecodePixelWidth);
        Assert.Equal(expectedHeight, size.DecodePixelHeight);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(50000, 50000)]
    public void InvalidOrExcessiveDimensions_AreRejected(int width, int height)
    {
        Assert.False(HistoryThumbnailPolicy.TryGetDecodeSize(
            width,
            height,
            out _));
    }

    [Fact]
    public async Task LoadAsync_ValidLocalPng_ReturnsFrozenBoundedThumbnail()
    {
        string root = CreateTestRoot();
        try
        {
            string path = Path.Combine(root, "fixture.png");
            SavePng(path, width: 800, height: 400);

            HistoryThumbnailResult result = await HistoryThumbnailLoader.LoadAsync(
                path,
                CancellationToken.None);

            Assert.Equal(HistoryThumbnailStatus.Loaded, result.Status);
            Assert.NotNull(result.Image);
            Assert.True(result.Image.IsFrozen);
            Assert.InRange(result.Image.PixelWidth, 1, HistoryThumbnailPolicy.MaxDecodeWidth);
            Assert.InRange(result.Image.PixelHeight, 1, HistoryThumbnailPolicy.MaxDecodeHeight);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_MissingLocalPng_ReturnsMissing()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "WinShotTests",
            Guid.NewGuid().ToString("N"),
            "missing.png");

        HistoryThumbnailResult result = await HistoryThumbnailLoader.LoadAsync(
            path,
            CancellationToken.None);

        Assert.Equal(HistoryThumbnailStatus.Missing, result.Status);
        Assert.Null(result.Image);
    }

    [Fact]
    public async Task LoadAsync_PreCanceledRequest_DoesNotRead()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HistoryThumbnailLoader.LoadAsync(
                Path.Combine(Path.GetTempPath(), "capture.png"),
                cancellation.Token));
    }

    [Fact]
    public async Task LoadAsync_PathResolutionRunsOffCallerWithoutBlockingReturn()
    {
        using var resolverStarted = new ManualResetEventSlim();
        using var releaseResolver = new ManualResetEventSlim();
        int callerThreadId = Environment.CurrentManagedThreadId;
        int resolverThreadId = callerThreadId;
        Task<HistoryThumbnailResult>? load = null;

        try
        {
            load = HistoryThumbnailLoader.LoadAsync(
                "opaque-candidate",
                CancellationToken.None,
                _ =>
                {
                    resolverThreadId = Environment.CurrentManagedThreadId;
                    resolverStarted.Set();
                    Assert.True(
                        releaseResolver.Wait(TimeSpan.FromSeconds(10)),
                        "Resolver release timed out.");
                    return new HistoryThumbnailPathResolution(false, "");
                });

            Assert.True(
                resolverStarted.Wait(TimeSpan.FromSeconds(10)),
                "Resolver did not start.");
            Assert.NotEqual(callerThreadId, resolverThreadId);
            Assert.False(load.IsCompleted);
        }
        finally
        {
            releaseResolver.Set();
        }

        HistoryThumbnailResult result = await load;
        Assert.Equal(HistoryThumbnailStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task LoadAsync_PreCanceledRequest_DoesNotInvokeInjectedResolver()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HistoryThumbnailLoader.LoadAsync(
                "opaque-candidate",
                cancellation.Token,
                _ =>
                {
                    calls++;
                    return new HistoryThumbnailPathResolution(false, "");
                }));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task LoadAsync_CanceledWhileDecodeSlotsBusy_DoesNotInvokeResolver()
    {
        using var threeResolversStarted = new CountdownEvent(3);
        using var releaseResolvers = new ManualResetEventSlim();
        Task<HistoryThumbnailResult>[] occupyingLoads = Enumerable.Range(0, 3)
            .Select(index => HistoryThumbnailLoader.LoadAsync(
                $"occupied-{index}",
                CancellationToken.None,
                _ =>
                {
                    threeResolversStarted.Signal();
                    Assert.True(
                        releaseResolvers.Wait(TimeSpan.FromSeconds(10)),
                        "Resolver release timed out.");
                    return new HistoryThumbnailPathResolution(false, "");
                }))
            .ToArray();
        using var queuedCancellation = new CancellationTokenSource();
        int queuedResolverCalls = 0;

        try
        {
            Assert.True(
                threeResolversStarted.Wait(TimeSpan.FromSeconds(10)),
                "Decode slots were not occupied.");
            Task<HistoryThumbnailResult> queued = HistoryThumbnailLoader.LoadAsync(
                "queued",
                queuedCancellation.Token,
                _ =>
                {
                    Interlocked.Increment(ref queuedResolverCalls);
                    return new HistoryThumbnailPathResolution(false, "");
                });
            queuedCancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.Equal(0, Volatile.Read(ref queuedResolverCalls));
        }
        finally
        {
            releaseResolvers.Set();
            await Task.WhenAll(occupyingLoads);
        }
    }

    private static string CreateTestRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "WinShotTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void SavePng(string path, int width, int height)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
