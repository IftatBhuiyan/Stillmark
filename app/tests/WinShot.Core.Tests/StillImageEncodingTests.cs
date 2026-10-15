using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.App;
using WinShot.Core.Settings;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class StillImageEncodingTests
{
    [Fact]
    public async Task EncodePngAsync_PreservesColorAndAlpha()
    {
        byte[] expectedPixels =
        [
            12, 34, 56, 78,
            90, 123, 210, 255,
        ];
        BitmapSource image = CreateBitmap(2, 1, expectedPixels);

        EncodedImagePayload payload =
            await StillImageEncodingService.EncodePngAsync(image);

        byte[] actualPixels = DecodeBgraPixels(payload);
        Assert.Equal(expectedPixels, actualPixels);
    }

    [Fact]
    public async Task ClipboardAndPngSave_ReuseTheExactPayloadBytes()
    {
        BitmapSource clipboardImage = CreateBitmap(1, 1, [0, 0, 255, 0]);
        EncodedImagePayload encodedBlue =
            await StillImageEncodingService.EncodePngAsync(
                CreateBitmap(1, 1, [255, 0, 0, 255]));
        var payload = new EncodedImagePayload(
            clipboardImage,
            encodedBlue.PngBytes.ToArray(),
            encodedBlue.PngLength,
            takeOwnership: true);

        DataObject data = ClipboardService.CreateImageDataObject(payload);
        using Stream clipboardPng = Assert.IsAssignableFrom<Stream>(
            data.GetData("PNG", autoConvert: false));
        using var clipboardBytes = new MemoryStream();
        clipboardPng.CopyTo(clipboardBytes);

        string root = CreateTestRoot();
        try
        {
            var settings = AppSettings.CreateDefault();
            settings.SaveDirectory = root;
            settings.ImageFormat = ImageFormatKind.Png;
            settings.FileNameTemplate = "payload-reuse";

            string path = await ImageSaveService.SaveAsync(
                payload,
                settings,
                new DateTime(2026, 7, 28, 12, 0, 0));

            Assert.Equal(payload.PngBytes.ToArray(), clipboardBytes.ToArray());
            Assert.Equal(payload.PngBytes.ToArray(), await File.ReadAllBytesAsync(path));

            BitmapSource dib = Assert.IsAssignableFrom<BitmapSource>(data.GetImage());
            byte[] dibPixel = new byte[4];
            dib.CopyPixels(dibPixel, 4, 0);
            Assert.Equal([255, 255, 255, 255], dibPixel);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EncodePngAsync_PreCanceledRequestProducesNoPayload()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            StillImageEncodingService.EncodePngAsync(
                CreateBitmap(1, 1, [0, 0, 0, 255]),
                cancellation.Token));
    }

    [Fact]
    public async Task EncodePngAsync_ConcurrentRequestsUseOneBoundedEncoderLane()
    {
        BitmapSource image = CreateBitmap(
            512,
            512,
            new byte[512 * 512 * 4]);

        EncodedImagePayload[] payloads = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(_ => StillImageEncodingService.EncodePngAsync(image)));

        Assert.Equal(8, payloads.Length);
        Assert.Equal(1, StillImageEncodingService.PeakEncoderCount);
        Assert.Equal(0, StillImageEncodingService.ActiveEncoderCount);
    }

    [Fact]
    public async Task EncodePngAsync_RejectsOutputBeyondConfiguredLimit()
    {
        ImagePayloadTooLargeException exception =
            await Assert.ThrowsAsync<ImagePayloadTooLargeException>(() =>
                StillImageEncodingService.EncodePngAsync(
                    CreateBitmap(1, 1, [0, 0, 0, 255]),
                    maximumPngBytes: 8));

        Assert.Equal(8, exception.MaximumBytes);
    }

    [Fact]
    public async Task EncodePngAsync_RejectsUnallocatableLimit()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            StillImageEncodingService.EncodePngAsync(
                CreateBitmap(1, 1, [0, 0, 0, 255]),
                maximumPngBytes: int.MaxValue));
    }

    [Fact]
    public async Task EncodePngAsync_RequiresFrozenSourceForWorkerSafety()
    {
        var image = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            StillImageEncodingService.EncodePngAsync(image));

        Assert.Equal("image", exception.ParamName);
    }

    [Fact]
    public async Task OpenPngStream_ReturnsIndependentReusableReaders()
    {
        EncodedImagePayload payload =
            await StillImageEncodingService.EncodePngAsync(
                CreateBitmap(1, 1, [5, 10, 15, 255]));

        using Stream first = payload.OpenPngStream();
        using Stream second = payload.OpenPngStream();
        Assert.Equal(first.ReadByte(), second.ReadByte());

        first.Position = first.Length;
        Assert.Equal(1, second.Position);
        Assert.Equal(payload.PngLength, first.Length);
        Assert.Equal(payload.PngLength, second.Length);
    }

    [Fact]
    public async Task SaveAsync_PreCanceledRequestCreatesNoOutput()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "WinShotTests",
            Guid.NewGuid().ToString("N"));
        EncodedImagePayload payload =
            await StillImageEncodingService.EncodePngAsync(
                CreateBitmap(1, 1, [5, 10, 15, 255]));
        var settings = AppSettings.CreateDefault();
        settings.SaveDirectory = root;
        settings.ImageFormat = ImageFormatKind.Png;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ImageSaveService.SaveAsync(
                payload,
                settings,
                new DateTime(2026, 7, 28, 12, 0, 0),
                cancellation.Token));

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task SaveAsync_ConcurrentWritersCommitDistinctCompleteFiles()
    {
        string root = CreateTestRoot();
        try
        {
            EncodedImagePayload payload =
                await StillImageEncodingService.EncodePngAsync(
                    CreateBitmap(1, 1, [5, 10, 15, 255]));
            var settings = AppSettings.CreateDefault();
            settings.SaveDirectory = root;
            settings.ImageFormat = ImageFormatKind.Png;
            settings.FileNameTemplate = "concurrent";
            DateTime timestamp = new(2026, 7, 28, 12, 0, 0);

            string[] paths = await Task.WhenAll(
                Enumerable.Range(0, 8)
                    .Select(_ => ImageSaveService.SaveAsync(payload, settings, timestamp)));

            Assert.Equal(8, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(paths, path =>
                Assert.Equal(payload.PngBytes.ToArray(), File.ReadAllBytes(path)));
            Assert.Empty(Directory.EnumerateFiles(root, "*.partial"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_ConcurrentJpegWritersCommitDistinctCompleteFiles()
    {
        string root = CreateTestRoot();
        try
        {
            EncodedImagePayload payload =
                await StillImageEncodingService.EncodePngAsync(
                    CreateOpaqueBitmap(1, 1, [5, 10, 15, 0]));
            var settings = AppSettings.CreateDefault();
            settings.SaveDirectory = root;
            settings.ImageFormat = ImageFormatKind.Jpg;
            settings.FileNameTemplate = "concurrent-jpeg";
            DateTime timestamp = new(2026, 7, 28, 12, 0, 0);

            string[] paths = await Task.WhenAll(
                Enumerable.Range(0, 8)
                    .Select(_ => ImageSaveService.SaveAsync(payload, settings, timestamp)));

            Assert.Equal(8, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(paths, path =>
            {
                byte[] bytes = File.ReadAllBytes(path);
                Assert.True(bytes.Length > 2);
                Assert.Equal(0xFF, bytes[0]);
                Assert.Equal(0xD8, bytes[1]);
            });
            Assert.Empty(Directory.EnumerateFiles(root, "*.partial"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SavePngCopyAsync_CreatesDirectoryAndSanitizesBaseName()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "WinShotTests",
            Guid.NewGuid().ToString("N"));
        EncodedImagePayload payload =
            await StillImageEncodingService.EncodePngAsync(
                CreateBitmap(1, 1, [5, 10, 15, 255]));

        try
        {
            string path = await ImageSaveService.SavePngCopyAsync(
                payload,
                root,
                "copy:one");

            Assert.Equal("copy-one.png", Path.GetFileName(path));
            Assert.Equal(payload.PngBytes.ToArray(), await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SavePngCopyAsync_ExhaustedNamesDeleteOnlyOwnedStagingFile()
    {
        string root = CreateTestRoot();
        try
        {
            const string baseName = "exhausted";
            for (int suffix = 1; suffix <= 1_000; suffix++)
            {
                string fileName = suffix == 1
                    ? baseName + ".png"
                    : $"{baseName} {suffix}.png";
                File.WriteAllText(Path.Combine(root, fileName), "caller-owned");
            }

            EncodedImagePayload payload =
                await StillImageEncodingService.EncodePngAsync(
                    CreateBitmap(1, 1, [5, 10, 15, 255]));

            await Assert.ThrowsAsync<IOException>(() =>
                ImageSaveService.SavePngCopyAsync(payload, root, baseName));

            Assert.Equal(1_000, Directory.EnumerateFiles(root, "*.png").Count());
            Assert.All(
                Directory.EnumerateFiles(root, "*.png"),
                path => Assert.Equal("caller-owned", File.ReadAllText(path)));
            Assert.Empty(Directory.EnumerateFiles(root, "*.partial"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareExactPng_CreateOnlyCollisionPreservesDestinationAndCleansStaging()
    {
        string root = CreateTestRoot();
        string destination = Path.Combine(root, "approved.png");
        byte[] original = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(destination, original);

        try
        {
            EncodedImagePayload payload =
                await StillImageEncodingService.EncodePngAsync(
                    CreateBitmap(1, 1, [5, 10, 15, 255]));
            using (ImageSaveService.PreparedImageWrite prepared =
                await ImageSaveService.PrepareExactPngAsync(
                    payload,
                    destination,
                    replaceExisting: false))
            {
                Assert.Throws<IOException>(() => prepared.Commit());
            }
            Assert.Equal(original, await File.ReadAllBytesAsync(destination));
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareExactPng_AtomicReplacePublishesExactPayloadWithoutSuffix()
    {
        string root = CreateTestRoot();
        string destination = Path.Combine(root, "approved.png");
        await File.WriteAllTextAsync(destination, "prior-content");

        try
        {
            EncodedImagePayload payload =
                await StillImageEncodingService.EncodePngAsync(
                    CreateBitmap(1, 1, [5, 10, 15, 255]));
            using ImageSaveService.PreparedImageWrite prepared =
                await ImageSaveService.PrepareExactPngAsync(
                    payload,
                    destination,
                    replaceExisting: true);

            Assert.Equal(destination, prepared.Commit());
            Assert.Equal(payload.PngBytes.ToArray(), await File.ReadAllBytesAsync(destination));
            Assert.Equal([destination], Directory.EnumerateFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareExactPng_RepeatedReplaceExposesOnlyCompleteOldOrNewPayload()
    {
        string root = CreateTestRoot();
        string destination = Path.Combine(root, "atomic.png");
        byte[] firstPixels = Enumerable.Repeat(
                new byte[] { 0, 0, 255, 255 },
                64 * 64)
            .SelectMany(static pixel => pixel)
            .ToArray();
        byte[] secondPixels = Enumerable.Repeat(
                new byte[] { 255, 0, 0, 255 },
                64 * 64)
            .SelectMany(static pixel => pixel)
            .ToArray();
        EncodedImagePayload first =
            await StillImageEncodingService.EncodePngAsync(
                CreateBitmap(64, 64, firstPixels));
        EncodedImagePayload second =
            await StillImageEncodingService.EncodePngAsync(
                CreateBitmap(64, 64, secondPixels));
        await File.WriteAllBytesAsync(destination, first.PngBytes.ToArray());
        var failures = new ConcurrentQueue<string>();
        var readerReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopReader = new CancellationTokenSource();
        int successfulReads = 0;

        try
        {
            Task reader = Task.Run(async () =>
            {
                while (!stopReader.IsCancellationRequested)
                {
                    try
                    {
                        await using var stream = new FileStream(
                            destination,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete,
                            bufferSize: 4 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        using var observed = new MemoryStream();
                        await stream.CopyToAsync(observed);
                        byte[] bytes = observed.ToArray();
                        if (!bytes.AsSpan().SequenceEqual(first.PngBytes.Span) &&
                            !bytes.AsSpan().SequenceEqual(second.PngBytes.Span))
                        {
                            failures.Enqueue($"Observed unexpected length {bytes.Length}.");
                        }
                        else
                        {
                            Interlocked.Increment(ref successfulReads);
                        }
                        readerReady.TrySetResult();
                    }
                    catch (FileNotFoundException)
                    {
                        await Task.Yield();
                    }
                    catch (IOException exception)
                        when ((exception.HResult & 0xFFFF) == 32 &&
                            File.Exists(destination))
                    {
                        await Task.Yield();
                    }
                }
            });

            await readerReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int iteration = 0; iteration < 20; iteration++)
            {
                EncodedImagePayload next = iteration % 2 == 0 ? second : first;
                using ImageSaveService.PreparedImageWrite prepared =
                    await ImageSaveService.PrepareExactPngAsync(
                        next,
                        destination,
                        replaceExisting: true);
                prepared.Commit();
            }

            stopReader.Cancel();
            await reader;
            Assert.True(successfulReads > 0);
            Assert.Empty(failures);
            Assert.Empty(Directory.EnumerateFiles(root, "*.partial"));
        }
        finally
        {
            stopReader.Cancel();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareExactPng_ReplaceAuthorizationCreatesMissingDestination()
    {
        string root = CreateTestRoot();
        string destination = Path.Combine(root, "approved.png");

        try
        {
            EncodedImagePayload payload =
                await StillImageEncodingService.EncodePngAsync(
                    CreateBitmap(1, 1, [5, 10, 15, 255]));
            using ImageSaveService.PreparedImageWrite prepared =
                await ImageSaveService.PrepareExactPngAsync(
                    payload,
                    destination,
                    replaceExisting: true);

            Assert.Equal(destination, prepared.Commit());
            Assert.Equal(payload.PngBytes.ToArray(), await File.ReadAllBytesAsync(destination));
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareExactPng_PreCanceledRequestCreatesNoDirectoryOrOutput()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "WinShotTests",
            Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(root, "approved.png");
        EncodedImagePayload payload =
            await StillImageEncodingService.EncodePngAsync(
                CreateBitmap(1, 1, [5, 10, 15, 255]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ImageSaveService.PrepareExactPngAsync(
                payload,
                destination,
                replaceExisting: false,
                cancellation.Token));

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task PreparePngCopy_MidWriteIOExceptionPublishesNothing()
    {
        string root = CreateTestRoot();
        string sentinel = Path.Combine(root, "caller-owned.txt");
        byte[] sentinelBytes = [9, 8, 7, 6];
        await File.WriteAllBytesAsync(sentinel, sentinelBytes);

        try
        {
            EncodedImagePayload payload =
                await StillImageEncodingService.EncodePngAsync(
                    CreateBitmap(1, 1, [5, 10, 15, 255]));

            await Assert.ThrowsAsync<IOException>(() =>
                ImageSaveService.PreparePngCopyAsync(
                    payload,
                    root,
                    "must-not-publish",
                    WritePrefixThenFailAsync));

            Assert.Equal(sentinelBytes, await File.ReadAllBytesAsync(sentinel));
            Assert.Equal([sentinel], Directory.EnumerateFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareExactPng_MidWriteIOExceptionPreservesExistingDestination()
    {
        string root = CreateTestRoot();
        string destination = Path.Combine(root, "approved.png");
        byte[] priorBytes = [1, 3, 5, 7, 9];
        await File.WriteAllBytesAsync(destination, priorBytes);

        try
        {
            EncodedImagePayload payload =
                await StillImageEncodingService.EncodePngAsync(
                    CreateBitmap(1, 1, [5, 10, 15, 255]));

            await Assert.ThrowsAsync<IOException>(() =>
                ImageSaveService.PrepareExactPngAsync(
                    payload,
                    destination,
                    replaceExisting: true,
                    WritePrefixThenFailAsync));

            Assert.Equal(priorBytes, await File.ReadAllBytesAsync(destination));
            Assert.Equal([destination], Directory.EnumerateFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareExactPng_MidWriteCancellationPublishesNothing()
    {
        string root = CreateTestRoot();
        string destination = Path.Combine(root, "canceled.png");
        using var cancellation = new CancellationTokenSource();

        try
        {
            EncodedImagePayload payload =
                await StillImageEncodingService.EncodePngAsync(
                    CreateBitmap(1, 1, [5, 10, 15, 255]));

            async ValueTask WritePrefixThenCancelAsync(
                Stream destinationStream,
                CancellationToken cancellationToken)
            {
                await destinationStream.WriteAsync(
                    payload.PngBytes[..8],
                    cancellationToken);
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ImageSaveService.PrepareExactPngAsync(
                    payload,
                    destination,
                    replaceExisting: false,
                    WritePrefixThenCancelAsync,
                    cancellation.Token));

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareExactPng_ShortSuccessfulWriterIsRefusedAndCleaned()
    {
        string root = CreateTestRoot();
        string destination = Path.Combine(root, "truncated.png");

        try
        {
            EncodedImagePayload payload =
                await StillImageEncodingService.EncodePngAsync(
                    CreateBitmap(1, 1, [5, 10, 15, 255]));

            await Assert.ThrowsAsync<IOException>(() =>
                ImageSaveService.PrepareExactPngAsync(
                    payload,
                    destination,
                    replaceExisting: false,
                    static async (stream, cancellationToken) =>
                    {
                        await stream.WriteAsync(
                            new byte[] { 0x89, 0x50, 0x4E, 0x47 },
                            cancellationToken);
                    }));

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareExactPng_DisposeWithoutCommitPublishesNothing()
    {
        string root = CreateTestRoot();
        string destination = Path.Combine(root, "not-published.png");

        try
        {
            EncodedImagePayload payload =
                await StillImageEncodingService.EncodePngAsync(
                    CreateBitmap(1, 1, [5, 10, 15, 255]));
            using (ImageSaveService.PreparedImageWrite prepared =
                await ImageSaveService.PrepareExactPngAsync(
                    payload,
                    destination,
                    replaceExisting: false))
            {
            }

            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_UniqueCommitRetainsCollisionSafeNaming()
    {
        string root = CreateTestRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "prepared.png"), "caller-owned");

        try
        {
            EncodedImagePayload payload =
                await StillImageEncodingService.EncodePngAsync(
                    CreateBitmap(1, 1, [5, 10, 15, 255]));
            var settings = AppSettings.CreateDefault();
            settings.SaveDirectory = root;
            settings.ImageFormat = ImageFormatKind.Png;
            settings.FileNameTemplate = "prepared";

            using ImageSaveService.PreparedImageWrite prepared =
                await ImageSaveService.PrepareAsync(
                    payload,
                    settings,
                    new DateTime(2026, 7, 28, 12, 0, 0));

            string path = prepared.Commit();
            Assert.Equal("prepared 2.png", Path.GetFileName(path));
            Assert.Equal(payload.PngBytes.ToArray(), await File.ReadAllBytesAsync(path));
            Assert.Equal("caller-owned", await File.ReadAllTextAsync(
                Path.Combine(root, "prepared.png")));
            Assert.Empty(Directory.EnumerateFiles(root, "*.partial"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareExactJpeg_PreservesOpaqueColorAndFlattensAlphaToWhite()
    {
        string root = CreateTestRoot();
        string destination = Path.Combine(root, "approved.jpg");

        try
        {
            const int width = 96;
            const int height = 32;
            byte[] pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int offset = ((y * width) + x) * 4;
                    if (x < 32)
                    {
                        pixels[offset + 2] = 255; // opaque red
                        pixels[offset + 3] = 255;
                    }
                    else if (x < 64)
                    {
                        pixels[offset + 1] = 255; // half-transparent green
                        pixels[offset + 3] = 128;
                    }
                    else
                    {
                        pixels[offset] = 255; // fully transparent blue
                    }
                }
            }

            BitmapSource mixedAlpha = CreateBitmap(width, height, pixels);
            using ImageSaveService.PreparedImageWrite prepared =
                await ImageSaveService.PrepareExactJpegAsync(
                    mixedAlpha,
                    destination,
                    quality: 90,
                    replaceExisting: false);

            Assert.Equal(destination, prepared.Commit());
            byte[] bytes = await File.ReadAllBytesAsync(destination);
            Assert.True(bytes.Length > 2);
            Assert.Equal(0xFF, bytes[0]);
            Assert.Equal(0xD8, bytes[1]);

            using var stream = new MemoryStream(bytes);
            var decoder = new JpegBitmapDecoder(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            BitmapSource decoded = new FormatConvertedBitmap(
                decoder.Frames[0],
                PixelFormats.Bgra32,
                null,
                0);
            byte[] decodedPixels = new byte[width * height * 4];
            decoded.CopyPixels(decodedPixels, width * 4, 0);

            int opaqueRed = ((16 * width) + 16) * 4;
            Assert.InRange(decodedPixels[opaqueRed], 0, 30);
            Assert.InRange(decodedPixels[opaqueRed + 1], 0, 30);
            Assert.InRange(decodedPixels[opaqueRed + 2], 225, 255);
            Assert.Equal(255, decodedPixels[opaqueRed + 3]);

            int halfGreen = ((16 * width) + 48) * 4;
            Assert.InRange(decodedPixels[halfGreen], 105, 150);
            Assert.InRange(decodedPixels[halfGreen + 1], 235, 255);
            Assert.InRange(decodedPixels[halfGreen + 2], 105, 150);
            Assert.Equal(255, decodedPixels[halfGreen + 3]);

            int transparentBlue = ((16 * width) + 80) * 4;
            Assert.InRange(decodedPixels[transparentBlue], 245, 255);
            Assert.InRange(decodedPixels[transparentBlue + 1], 245, 255);
            Assert.InRange(decodedPixels[transparentBlue + 2], 245, 255);
            Assert.Equal(255, decodedPixels[transparentBlue + 3]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SyntheticTimingHarness_ReportsThreeEncodesVersusOneReusableEncode()
    {
        const int width = 1920;
        const int height = 1080;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                pixels[offset] = (byte)((x * 17 + y * 3) & 0xFF);
                pixels[offset + 1] = (byte)((x * 5 + y * 11) & 0xFF);
                pixels[offset + 2] = (byte)((x * 7 + y * 13) & 0xFF);
                pixels[offset + 3] = (byte)(64 + ((x + y) % 192));
            }
        }

        BitmapSource image = CreateBitmap(width, height, pixels);
        int priorEncodeCount = 0;
        var priorStopwatch = Stopwatch.StartNew();
        byte[][] priorOutputs = new byte[3][];
        for (int index = 0; index < priorOutputs.Length; index++)
        {
            priorOutputs[index] = EncodePngLegacy(image);
            priorEncodeCount++;
        }
        priorStopwatch.Stop();

        int reusableEncodeCount = 0;
        var reusableStopwatch = Stopwatch.StartNew();
        EncodedImagePayload payload =
            await StillImageEncodingService.EncodePngAsync(image);
        reusableEncodeCount++;
        reusableStopwatch.Stop();

        Console.WriteLine(
            $"Synthetic PNG delivery: {width}x{height}; prior={priorEncodeCount} encodes, " +
            $"{priorOutputs.Sum(output => output.Length)} total bytes, " +
            $"{priorStopwatch.Elapsed.TotalMilliseconds:F1} ms; reusable={reusableEncodeCount} encode, " +
            $"{payload.PngLength} retained bytes, {reusableStopwatch.Elapsed.TotalMilliseconds:F1} ms.");

        Assert.Equal(3, priorEncodeCount);
        Assert.Equal(1, reusableEncodeCount);
        Assert.All(priorOutputs, output => Assert.Equal(payload.PngBytes.ToArray(), output));
    }

    private static BitmapSource CreateBitmap(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateOpaqueBitmap(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgr32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] DecodeBgraPixels(EncodedImagePayload payload)
    {
        using Stream stream = payload.OpenPngStream();
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        BitmapSource decoded = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[decoded.PixelWidth * decoded.PixelHeight * 4];
        decoded.CopyPixels(pixels, decoded.PixelWidth * 4, 0);
        return pixels;
    }

    private static byte[] EncodePngLegacy(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static async ValueTask WritePrefixThenFailAsync(
        Stream destination,
        CancellationToken cancellationToken)
    {
        await destination.WriteAsync(
            new byte[] { 0x89, 0x50, 0x4E, 0x47 },
            cancellationToken);
        throw new IOException("Synthetic mid-write failure.");
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
}
