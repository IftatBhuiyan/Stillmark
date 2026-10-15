using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinShot.App.Editor;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class RegionEffectProcessingTests
{
    [Fact]
    public void PixelFilters_PreserveGoldenPixels_AndHonorPreCancellation()
    {
        BitmapSource source = CreateSeededSource(512, 512);
        var region = new Int32Rect(0, 0, 512, 512);

        BitmapSource pixelate = PixelFilters.Pixelate(source, region, blockSize: 32);
        BitmapSource blur = PixelFilters.BoxBlur(source, region, radius: 7, passes: 3);

        Assert.Equal(
            "5B3B60073226B484C9A4110992918C092A910B98794A0FD3C0B5A9873CC94A44",
            HashPixels(pixelate));
        Assert.Equal(
            "6DAC3F69B2314CF69F1EBD8D348056B9BD1B5DA8ABDD5DCAD1D93F99AC6303C8",
            HashPixels(blur));
        Assert.True(pixelate.IsFrozen);
        Assert.True(blur.IsFrozen);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => PixelFilters.Pixelate(
                source,
                region,
                blockSize: 32,
                canceled.Token));
        Assert.Throws<OperationCanceledException>(
            () => PixelFilters.BoxBlur(
                source,
                region,
                radius: 7,
                passes: 3,
                canceled.Token));
    }

    [Fact]
    public async Task Preparation_ComputesOnlyOffRender_AndCachesFrozenOutput()
    {
        var processor = new RecordingProcessor();
        using var document = new EditorDocument(
            CreateSeededSource(16, 12),
            processor);
        var annotation = new RegionEffectAnnotation
        {
            Region = new Rect(2, 3, 8, 6),
            IsPixelate = false,
        };
        document.Add(annotation);
        Assert.True(annotation.TryCreateRequest(
            document,
            out RegionEffectRequest request,
            out _));

        Assert.False(document.AreRegionEffectsPrepared());
        Assert.Equal(0, processor.CallCount);
        Assert.Throws<InvalidOperationException>(() => document.RenderFinal());
        Assert.Equal(0, processor.CallCount);

        await document.PrepareRegionEffectsAsync();

        Assert.True(document.AreRegionEffectsPrepared());
        Assert.Equal(1, processor.CallCount);
        Assert.True(document.TryGetPreparedRegionEffect(request, out BitmapSource? image));
        Assert.NotNull(image);
        Assert.True(image.IsFrozen);
        Assert.Equal((1, request.OutputByteCount), document.GetRegionEffectCacheUsage());

        await document.PrepareRegionEffectsAsync();
        Assert.Equal(1, processor.CallCount);
    }

    [Fact]
    public async Task NewerRequest_CancelsStaleWork_AndNeverOverlapsWorkers()
    {
        var processor = new SupersessionProcessor();
        using var document = new EditorDocument(
            CreateSeededSource(32, 24),
            processor);
        var annotation = new RegionEffectAnnotation
        {
            Region = new Rect(1, 1, 10, 8),
            IsPixelate = false,
        };
        document.Add(annotation);
        Assert.True(annotation.TryCreateRequest(
            document,
            out RegionEffectRequest staleRequest,
            out _));

        using var staleCaller = new CancellationTokenSource();
        Task stalePreparation = document.PrepareRegionEffectsAsync(
            staleCaller.Token);
        await processor.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        annotation.Region = new Rect(12, 10, 10, 8);
        document.NotifyChanged();
        Assert.True(annotation.TryCreateRequest(
            document,
            out RegionEffectRequest currentRequest,
            out _));
        Task currentPreparation = document.PrepareRegionEffectsAsync();
        staleCaller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stalePreparation);
        await currentPreparation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, processor.CallCount);
        Assert.Equal(1, processor.MaximumConcurrency);
        Assert.False(document.TryGetPreparedRegionEffect(staleRequest, out _));
        Assert.True(document.TryGetPreparedRegionEffect(currentRequest, out _));
    }

    [Fact]
    public async Task ExactPreparation_RetriesAfterInternalSupersession()
    {
        var processor = new SupersessionProcessor();
        using var document = new EditorDocument(
            CreateSeededSource(32, 24),
            processor);
        var annotation = new RegionEffectAnnotation
        {
            Region = new Rect(1, 1, 10, 8),
            IsPixelate = false,
        };
        document.Add(annotation);
        Assert.True(annotation.TryCreateRequest(
            document,
            out RegionEffectRequest staleRequest,
            out _));

        Task exactPreparation = document.PrepareRegionEffectsAsync();
        await processor.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        annotation.Region = new Rect(12, 10, 10, 8);
        document.NotifyChanged();
        Assert.True(annotation.TryCreateRequest(
            document,
            out RegionEffectRequest currentRequest,
            out _));
        Task currentPreparation = document.PrepareRegionEffectsAsync();

        await Task.WhenAll(exactPreparation, currentPreparation)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, processor.CallCount);
        Assert.Equal(1, processor.MaximumConcurrency);
        Assert.False(document.TryGetPreparedRegionEffect(staleRequest, out _));
        Assert.True(document.TryGetPreparedRegionEffect(currentRequest, out _));
    }

    [Fact]
    public async Task Cache_EvictsLeastRecentCompletedEntryWithinBounds()
    {
        var processor = new RecordingProcessor();
        using var document = new EditorDocument(
            CreateSeededSource(24, 12),
            processor,
            regionEffectCacheByteLimit: 4096,
            regionEffectCacheEntryLimit: 1);
        var annotation = new RegionEffectAnnotation
        {
            Region = new Rect(0, 0, 8, 8),
            IsPixelate = true,
        };
        document.Add(annotation);
        Assert.True(annotation.TryCreateRequest(
            document,
            out RegionEffectRequest firstRequest,
            out _));
        await document.PrepareRegionEffectsAsync();

        annotation.Region = new Rect(10, 0, 8, 8);
        document.NotifyChanged();
        Assert.True(annotation.TryCreateRequest(
            document,
            out RegionEffectRequest secondRequest,
            out _));
        await document.PrepareRegionEffectsAsync();

        Assert.Equal(2, processor.CallCount);
        Assert.False(document.TryGetPreparedRegionEffect(firstRequest, out _));
        Assert.True(document.TryGetPreparedRegionEffect(secondRequest, out _));
        Assert.Equal((1, secondRequest.OutputByteCount), document.GetRegionEffectCacheUsage());
    }

    [Fact]
    public async Task Preparation_RecomputesCurrentEntryEvictedByStaleCacheData()
    {
        var processor = new RecordingProcessor();
        using var document = new EditorDocument(
            CreateSeededSource(32, 16),
            processor,
            regionEffectCacheByteLimit: 4096,
            regionEffectCacheEntryLimit: 2);
        var retained = new RegionEffectAnnotation
        {
            Region = new Rect(0, 0, 6, 6),
            IsPixelate = true,
        };
        document.Add(retained);
        await document.PrepareRegionEffectsAsync();

        var stale = new RegionEffectAnnotation
        {
            Region = new Rect(8, 0, 6, 6),
            IsPixelate = true,
        };
        document.Add(stale);
        await document.PrepareRegionEffectsAsync();
        document.Delete(stale);
        var current = new RegionEffectAnnotation
        {
            Region = new Rect(16, 0, 6, 6),
            IsPixelate = true,
        };
        document.Add(current);

        await document.PrepareRegionEffectsAsync();

        Assert.True(document.AreRegionEffectsPrepared());
        Assert.Equal(4, processor.CallCount);
        Assert.True(retained.TryCreateRequest(
            document,
            out RegionEffectRequest retainedRequest,
            out _));
        Assert.True(current.TryCreateRequest(
            document,
            out RegionEffectRequest currentRequest,
            out _));
        Assert.True(document.TryGetPreparedRegionEffect(retainedRequest, out _));
        Assert.True(document.TryGetPreparedRegionEffect(currentRequest, out _));
        Assert.Equal(2, document.GetRegionEffectCacheUsage().EntryCount);
    }

    [Fact]
    public async Task Dispose_CancelsWorker_AndRefusesLateCompletion()
    {
        var processor = new SupersessionProcessor();
        var document = new EditorDocument(
            CreateSeededSource(16, 12),
            processor);
        document.Add(new RegionEffectAnnotation
        {
            Region = new Rect(1, 1, 10, 8),
            IsPixelate = false,
        });

        Task preparation = document.PrepareRegionEffectsAsync();
        await processor.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        document.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => preparation);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => document.PrepareRegionEffectsAsync());
    }

    [Fact]
    public async Task BatchBeyondCacheBudget_IsRefusedBeforeWorkerExecution()
    {
        var processor = new RecordingProcessor();
        using var document = new EditorDocument(
            CreateSeededSource(16, 16),
            processor,
            regionEffectCacheByteLimit: 128,
            regionEffectCacheEntryLimit: 4);
        document.Add(new RegionEffectAnnotation
        {
            Region = new Rect(0, 0, 8, 8),
            IsPixelate = false,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => document.PrepareRegionEffectsAsync());
        Assert.Equal(0, processor.CallCount);
    }

    [Fact]
    public async Task ExactOutput_RefusesAChangedActionTimeRevision()
    {
        using var document = new EditorDocument(
            CreateSeededSource(8, 8));
        long actionTimeRevision = document.Revision;
        document.NotifyChanged();

        await Assert.ThrowsAsync<EditorDocumentChangedDuringOutputException>(
            () => document.RenderExportAsync(
                actionTimeRevision,
                CancellationToken.None));
    }

    [Fact]
    public async Task PreparedSnapshot_RetainsExactPixelsAfterCacheEviction()
    {
        BitmapSource source = CreateSeededSource(16, 8);
        var processor = new RecordingProcessor();
        using var pipeline = new RegionEffectPipeline(
            processor,
            cacheByteLimit: 1024,
            cacheEntryLimit: 1);
        var first = new RegionEffectRequest(
            1,
            new Int32Rect(0, 0, 4, 4),
            RegionEffectKind.Pixelate,
            Parameter: 6,
            Passes: 1);
        var second = new RegionEffectRequest(
            1,
            new Int32Rect(8, 0, 4, 4),
            RegionEffectKind.Pixelate,
            Parameter: 6,
            Passes: 1);

        await pipeline.PrepareAsync(source, [first]);
        RegionEffectPreparedSnapshot exact =
            pipeline.CapturePreparedSnapshot([first]);
        await pipeline.PrepareAsync(source, [second]);

        Assert.True(exact.TryGet(first, out BitmapSource? retained));
        Assert.NotNull(retained);
        Assert.True(retained.IsFrozen);
        Assert.False(pipeline.TryGet(first, out _));
        Assert.True(pipeline.TryGet(second, out _));
    }

    [Fact]
    public async Task ReturningToCachedState_SupersedesIncompatibleWorker()
    {
        BitmapSource source = CreateSeededSource(16, 8);
        var processor = new StubbornSecondProcessor();
        using var pipeline = new RegionEffectPipeline(
            processor,
            cacheByteLimit: 1024,
            cacheEntryLimit: 1);
        var cached = new RegionEffectRequest(
            1,
            new Int32Rect(0, 0, 4, 4),
            RegionEffectKind.Pixelate,
            Parameter: 6,
            Passes: 1);
        var obsolete = new RegionEffectRequest(
            1,
            new Int32Rect(8, 0, 4, 4),
            RegionEffectKind.Pixelate,
            Parameter: 6,
            Passes: 1);
        await pipeline.PrepareAsync(source, [cached]);

        Task obsoletePreparation = pipeline.PrepareAsync(source, [obsolete]);
        await processor.SecondCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await pipeline.PrepareAsync(source, [cached]);
        RegionEffectPreparedSnapshot exact =
            pipeline.CapturePreparedSnapshot([cached]);
        processor.ReleaseSecondCall.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => obsoletePreparation);
        Assert.True(exact.TryGet(cached, out _));
        Assert.True(pipeline.TryGet(cached, out _));
        Assert.False(pipeline.TryGet(obsolete, out _));
    }

    [Fact]
    public async Task CallerOnlyCancellation_StopsTheActualWorker()
    {
        BitmapSource source = CreateSeededSource(16, 8);
        var processor = new SupersessionProcessor();
        using var pipeline = new RegionEffectPipeline(processor);
        var request = new RegionEffectRequest(
            1,
            new Int32Rect(0, 0, 8, 8),
            RegionEffectKind.BoxBlur,
            Parameter: 7,
            Passes: 3);
        using var cancellation = new CancellationTokenSource();

        Task preparation = pipeline.PrepareAsync(
            source,
            [request],
            cancellation.Token);
        await processor.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => preparation);
        Assert.True(SpinWait.SpinUntil(
            () => processor.CurrentConcurrency == 0,
            TimeSpan.FromSeconds(5)));
        Assert.False(pipeline.TryGet(request, out _));
    }

    [Fact]
    public async Task DisposeThenCallerCancellation_DoesNotTouchDisposedWorkerToken()
    {
        BitmapSource source = CreateSeededSource(16, 8);
        var processor = new SupersessionProcessor();
        var pipeline = new RegionEffectPipeline(processor);
        var request = new RegionEffectRequest(
            1,
            new Int32Rect(0, 0, 8, 8),
            RegionEffectKind.BoxBlur,
            Parameter: 7,
            Passes: 3);
        using var callerCancellation = new CancellationTokenSource();
        Task preparation = pipeline.PrepareAsync(
            source,
            [request],
            callerCancellation.Token);
        await processor.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        pipeline.Dispose();
        Exception? cancellationFailure = Record.Exception(
            callerCancellation.Cancel);

        Assert.Null(cancellationFailure);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => preparation);
    }

    [Fact]
    public async Task EntryBudget_RefusesThirtyThreeBeforeWork_AndAllowsThirtyTwo()
    {
        var processor = new RecordingProcessor();
        using var document = new EditorDocument(
            CreateSeededSource(64, 32),
            processor);
        for (int index = 0; index < 33; index++)
        {
            document.Add(new RegionEffectAnnotation
            {
                Region = new Rect(
                    (index % 16) * 4,
                    (index / 16) * 4,
                    2,
                    2),
                IsPixelate = true,
            });
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => document.PrepareRegionEffectsAsync());
        Assert.Equal(0, processor.CallCount);

        document.Delete(document.Annotations[^1]);
        await document.PrepareRegionEffectsAsync();
        Assert.Equal(32, processor.CallCount);
        Assert.True(document.AreRegionEffectsPrepared());
    }

    [Fact]
    public void ProcessingPlaceholder_IsOpaqueSystemColor()
    {
        Assert.Equal(1, SystemColors.ControlDarkBrush.Opacity);
        SolidColorBrush brush = Assert.IsType<SolidColorBrush>(
            SystemColors.ControlDarkBrush);
        Assert.Equal(byte.MaxValue, brush.Color.A);
    }

    [Fact]
    public void DisposedDocument_RefusesMutators()
    {
        var document = new EditorDocument(CreateSeededSource(8, 8));
        document.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => document.Add(new ShapeAnnotation()));
        Assert.Throws<ObjectDisposedException>(document.PushUndo);
        Assert.Throws<ObjectDisposedException>(document.Undo);
        Assert.Throws<ObjectDisposedException>(document.Redo);
        Assert.Throws<ObjectDisposedException>(document.NotifyChanged);
    }

    [Fact]
    public async Task DocumentMutation_FromNonOwnerThread_IsRefused()
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        var ownerContext = new DispatcherSynchronizationContext(
            Dispatcher.CurrentDispatcher);
        EditorDocument document;
        SynchronizationContext.SetSynchronizationContext(ownerContext);
        try
        {
            document = new EditorDocument(CreateSeededSource(8, 8));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        using (document)
        {
            Exception? failure = await Task.Run(
                () => Record.Exception(document.NotifyChanged));

            Assert.IsType<InvalidOperationException>(failure);
        }
    }

    [Fact]
    public void HostileOutputByteCount_UsesCheckedArithmetic()
    {
        var hostile = new RegionEffectRequest(
            1,
            new Int32Rect(0, 0, int.MaxValue, int.MaxValue),
            RegionEffectKind.BoxBlur,
            Parameter: 7,
            Passes: 3);

        Assert.Throws<OverflowException>(() => _ = hostile.OutputByteCount);
    }

    private static BitmapSource CreateSeededSource(int width, int height)
    {
        int stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        uint state = unchecked(
            0x9E3779B9u ^
            ((uint)width * 0x85EBCA6Bu) ^
            ((uint)height * 0xC2B2AE35u));
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = (byte)NextRandom(ref state);
            pixels[offset + 1] = (byte)NextRandom(ref state);
            pixels[offset + 2] = (byte)NextRandom(ref state);
            pixels[offset + 3] = 0;
        }

        BitmapSource source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgr32,
            palette: null,
            pixels,
            stride);
        source.Freeze();
        return source;
    }

    private static uint NextRandom(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private static string HashPixels(BitmapSource bitmap)
    {
        int stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private static BitmapSource CreateOutput(RegionEffectRequest request)
    {
        int stride = checked(request.PixelRegion.Width * 4);
        var pixels = new byte[checked(stride * request.PixelRegion.Height)];
        Array.Fill(pixels, (byte)0x5A);
        BitmapSource output = BitmapSource.Create(
            request.PixelRegion.Width,
            request.PixelRegion.Height,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride);
        output.Freeze();
        return output;
    }

    private sealed class RecordingProcessor : IRegionEffectProcessor
    {
        public int CallCount { get; private set; }

        public BitmapSource Process(
            BitmapSource source,
            RegionEffectRequest request,
            CancellationToken cancellationToken)
        {
            Assert.True(source.IsFrozen);
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return CreateOutput(request);
        }
    }

    private sealed class SupersessionProcessor : IRegionEffectProcessor
    {
        private int _callCount;
        private int _concurrency;
        private int _maximumConcurrency;

        public TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);
        public int CurrentConcurrency => Volatile.Read(ref _concurrency);
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public BitmapSource Process(
            BitmapSource source,
            RegionEffectRequest request,
            CancellationToken cancellationToken)
        {
            int concurrency = Interlocked.Increment(ref _concurrency);
            int observed = Volatile.Read(ref _maximumConcurrency);
            while (concurrency > observed)
            {
                int prior = Interlocked.CompareExchange(
                    ref _maximumConcurrency,
                    concurrency,
                    observed);
                if (prior == observed)
                    break;
                observed = prior;
            }

            int call = Interlocked.Increment(ref _callCount);
            try
            {
                if (call == 1)
                {
                    FirstCallStarted.TrySetResult();
                    cancellationToken.WaitHandle.WaitOne();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                cancellationToken.ThrowIfCancellationRequested();
                return CreateOutput(request);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }
    }

    private sealed class StubbornSecondProcessor : IRegionEffectProcessor
    {
        private int _callCount;

        public TaskCompletionSource SecondCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSecondCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BitmapSource Process(
            BitmapSource source,
            RegionEffectRequest request,
            CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _callCount);
            if (call == 2)
            {
                SecondCallStarted.TrySetResult();
                ReleaseSecondCall.Task.GetAwaiter().GetResult();
            }

            return CreateOutput(request);
        }
    }
}
