using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinShot.App;
using WinShot.App.Editor;

namespace WinShot.EditorOutput.Perf;

internal static class EditorOutputBenchmark
{
    internal const int MaximumPngBytes = 128 * 1024 * 1024;
    internal const int CacheReuseCount = 31;
    internal const long MaximumManagedAllocatedBytes = 1024L * 1024 * 1024;
    internal const long MaximumPeakPrivateByteDelta = 1024L * 1024 * 1024;

    private static readonly (int Width, int Height)[] Sizes =
    [
        (1920, 1080),
        (3840, 2160),
    ];

    internal static BenchmarkReport Run(int sampleCount) =>
        DispatcherPump.Run(() => RunAsync(sampleCount));

    private static async Task<BenchmarkReport> RunAsync(int sampleCount)
    {
        MeasuredSourceFingerprint fingerprint = MeasuredSource.Fingerprint();
        MeasuredSourceFingerprint harnessFingerprint =
            HarnessSource.Fingerprint();
        var cases = new List<BenchmarkCaseResult>(Sizes.Length);
        foreach ((int width, int height) in Sizes)
        {
            BitmapSource source = SyntheticBitmapFactory.Create(
                width,
                height,
                out uint seed);
            _ = await MeasureSampleAsync(
                source,
                seed,
                cacheReuseCount: 3,
                stagingThreadObserver: null);

            var samples = new List<EditorOutputSample>(sampleCount);
            for (int index = 0; index < sampleCount; index++)
            {
                GC.Collect(
                    GC.MaxGeneration,
                    GCCollectionMode.Forced,
                    blocking: true,
                    compacting: false);
                GC.WaitForPendingFinalizers();
                samples.Add(await MeasureSampleAsync(
                    source,
                    seed,
                    CacheReuseCount,
                    stagingThreadObserver: null));
            }

            cases.Add(AggregateCase(samples));
        }

        return new BenchmarkReport(
            SchemaVersion: 1,
            EvidenceClass: "LocalSyntheticEditorOutput",
            MeasurementScope:
                "Exact linked editor render, bounded PNG encode, durable stage, namespace commit, and revision-cache reuse",
            RuntimeVersion: Environment.Version.ToString(),
            ProcessArchitecture:
                RuntimeInformation.ProcessArchitecture.ToString(),
#if DEBUG
            BuildConfiguration: "Debug",
#else
            BuildConfiguration: "Release",
#endif
            WarmupCountPerCase: 1,
            SampleCountPerCase: sampleCount,
            CacheReuseCountPerSample: CacheReuseCount,
            PercentileMethod:
                "Median and nearest-rank p95 over independent cold samples",
            AllocationMethod:
                "GC.GetTotalAllocatedBytes(true) process-wide delta after nonallocating private-byte sampler startup",
            PrivateBytesMethod:
                "Best-effort 1 ms process sampling across cold preparation",
            MaximumPngBytes: MaximumPngBytes,
            MeasuredSourceCount: fingerprint.Count,
            MeasuredSourceSha256: fingerprint.AggregateSha256,
            HarnessSourceCount: harnessFingerprint.Count,
            HarnessSourceSha256: harnessFingerprint.AggregateSha256,
            Cases: cases,
            OverallPassed: cases.All(result => result.GuardrailsPassed));
    }

    internal static async Task<EditorOutputSample> MeasureSampleAsync(
        BitmapSource source,
        uint syntheticSeed,
        int cacheReuseCount,
        Action<int>? stagingThreadObserver)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsFrozen)
            throw new ArgumentException("The synthetic source must be frozen.", nameof(source));
        if (cacheReuseCount < 1)
            throw new ArgumentOutOfRangeException(nameof(cacheReuseCount));

        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.VerifyAccess();
        int dispatcherThreadId = Environment.CurrentManagedThreadId;
        using var workspace = new SafeTemporaryWorkspace();
        using var document = new EditorDocument(source);
        using var coordinator = new EditorResultPreparationCoordinator();
        long revision = document.Revision;
        using EditorResultPreparationLease lease = coordinator.Begin(revision);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                lease.Token,
                timeout.Token);
        CancellationToken token = linkedCancellation.Token;
        var cache = new EditorResultPreparationCache();

        int renderInvocations = 0;
        int encodeInvocations = 0;
        int stageInvocations = 0;
        int commitInvocations = 0;
        bool renderRanOnDispatcher = false;
        bool stagingWriterRanOffDispatcher = false;

        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        using var privateBytes = new PeakPrivateBytesMonitor();
        privateBytes.Start();
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        long totalStarted = Stopwatch.GetTimestamp();

        renderInvocations++;
        renderRanOnDispatcher = dispatcher.CheckAccess();
        long renderStarted = Stopwatch.GetTimestamp();
        BitmapSource rendered = await document.RenderExportAsync(
            revision,
            token);
        double renderMilliseconds = Stopwatch.GetElapsedTime(
            renderStarted).TotalMilliseconds;
        lease.ThrowIfCannotPublish(document.Revision);

        encodeInvocations++;
        long encodeStarted = Stopwatch.GetTimestamp();
        EncodedImagePayload payload =
            await StillImageEncodingService.EncodePngAsync(
                rendered,
                token,
                MaximumPngBytes);
        double encodeMilliseconds = Stopwatch.GetElapsedTime(
            encodeStarted).TotalMilliseconds;
        lease.ThrowIfCannotPublish(document.Revision);

        stageInvocations++;
        long stageStarted = Stopwatch.GetTimestamp();
        using ImageSaveService.PreparedImageWrite staged =
            stagingThreadObserver is null
                ? await ImageSaveService.PreparePngCopyAsync(
                    payload,
                    workspace.DirectoryPath,
                    "editor-output",
                    token)
                : await ImageSaveService.PreparePngCopyAsync(
                    payload,
                    workspace.DirectoryPath,
                    "editor-output",
                    async (destination, cancellationToken) =>
                    {
                        int writerThreadId =
                            Environment.CurrentManagedThreadId;
                        stagingWriterRanOffDispatcher =
                            writerThreadId != dispatcherThreadId;
                        stagingThreadObserver(writerThreadId);
                        await payload.CopyPngToAsync(
                            destination,
                            cancellationToken);
                    },
                    token);
        double stageMilliseconds = Stopwatch.GetElapsedTime(
            stageStarted).TotalMilliseconds;
        lease.ThrowIfCannotPublish(document.Revision);

        string? committedPath = null;
        PreparedEditorResult? prepared = null;
        long commitStarted = Stopwatch.GetTimestamp();
        if (!lease.TryPublish(
                document.Revision,
                () =>
                {
                    commitInvocations++;
                    committedPath = staged.Commit();
                    workspace.TrackOwnedFile(committedPath);
                    if (!cache.TryCommit(
                            revision,
                            document.Revision,
                            committedPath,
                            out prepared))
                    {
                        throw new InvalidOperationException(
                            "The exact-revision cache refused a valid commit.");
                    }
                }))
        {
            token.ThrowIfCancellationRequested();
            throw new OperationCanceledException(token);
        }

        double commitMilliseconds = Stopwatch.GetElapsedTime(
            commitStarted).TotalMilliseconds;
        double coldPreparationMilliseconds = Stopwatch.GetElapsedTime(
            totalStarted).TotalMilliseconds;
        long managedAllocatedBytes = Math.Max(
            0,
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
        int gen0Collections = GC.CollectionCount(0) - gen0Before;
        int gen1Collections = GC.CollectionCount(1) - gen1Before;
        int gen2Collections = GC.CollectionCount(2) - gen2Before;
        long peakPrivateByteDelta = privateBytes.Stop();

        if (prepared is null || committedPath is null)
            throw new InvalidOperationException("The output was not published.");

        long initialLength = new FileInfo(committedPath).Length;
        DateTime initialWriteTime = File.GetLastWriteTimeUtc(committedPath);
        string initialHash = BenchmarkHash.File(committedPath);
        var cacheMilliseconds = new List<double>(cacheReuseCount);
        int successfulCacheReuses = 0;
        for (int index = 0; index < cacheReuseCount; index++)
        {
            long cacheStarted = Stopwatch.GetTimestamp();
            bool found =
                cache.TryGet(revision, out PreparedEditorResult? cached) &&
                cached is not null &&
                string.Equals(
                    cached.FilePath,
                    committedPath,
                    StringComparison.OrdinalIgnoreCase) &&
                File.Exists(cached.FilePath);
            cacheMilliseconds.Add(
                Stopwatch.GetElapsedTime(cacheStarted).TotalMilliseconds);
            if (!found)
                throw new InvalidOperationException("Cached reuse failed.");
            successfulCacheReuses++;
        }

        if (new FileInfo(committedPath).Length != initialLength ||
            File.GetLastWriteTimeUtc(committedPath) != initialWriteTime ||
            !string.Equals(
                BenchmarkHash.File(committedPath),
                initialHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Cached reuse changed the prepared backing output.");
        }

        string inputPixelHash = BenchmarkHash.Bitmap(source);
        string renderedPixelHash = BenchmarkHash.Bitmap(rendered);
        string pngHash = BenchmarkHash.Bytes(payload.PngBytes.Span);
        if (initialLength != payload.PngLength ||
            !string.Equals(initialHash, pngHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Staged bytes differed from the reusable PNG payload.");
        }

        BitmapSource decoded = BenchmarkHash.DecodePng(payload.PngBytes);
        string decodedPixelHash = BenchmarkHash.Bitmap(decoded);
        if (!string.Equals(
                decodedPixelHash,
                renderedPixelHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The PNG round trip changed rendered pixels.");
        }

        workspace.AssertNoPartialFiles();
        bool guardrailsPassed =
            rendered.IsFrozen &&
            rendered.PixelWidth == source.PixelWidth &&
            rendered.PixelHeight == source.PixelHeight &&
            renderInvocations == 1 &&
            encodeInvocations == 1 &&
            stageInvocations == 1 &&
            commitInvocations == 1 &&
            successfulCacheReuses == cacheReuseCount &&
            payload.PngLength <= MaximumPngBytes &&
            managedAllocatedBytes <= MaximumManagedAllocatedBytes &&
            peakPrivateByteDelta <= MaximumPeakPrivateByteDelta &&
            StillImageEncodingService.PeakEncoderCount == 1;

        return new EditorOutputSample(
            Width: source.PixelWidth,
            Height: source.PixelHeight,
            SyntheticSeed: syntheticSeed,
            InputPixelFormat: source.Format.ToString(),
            RenderedPixelFormat: rendered.Format.ToString(),
            RenderMilliseconds: renderMilliseconds,
            EncodeMilliseconds: encodeMilliseconds,
            StageMilliseconds: stageMilliseconds,
            CommitMilliseconds: commitMilliseconds,
            ColdPreparationMilliseconds: coldPreparationMilliseconds,
            CachedReuseMilliseconds: cacheMilliseconds,
            ManagedAllocatedBytes: managedAllocatedBytes,
            PeakPrivateByteDelta: peakPrivateByteDelta,
            Gen0Collections: gen0Collections,
            Gen1Collections: gen1Collections,
            Gen2Collections: gen2Collections,
            StagedByteLength: payload.PngLength,
            InputPixelSha256: inputPixelHash,
            RenderedPixelSha256: renderedPixelHash,
            PngSha256: pngHash,
            OutputFrozen: rendered.IsFrozen,
            RenderInvocationCount: renderInvocations,
            EncodeInvocationCount: encodeInvocations,
            StageInvocationCount: stageInvocations,
            CommitInvocationCount: commitInvocations,
            SuccessfulCacheReuseCount: successfulCacheReuses,
            PeakEncoderCount: StillImageEncodingService.PeakEncoderCount,
            RenderRanOnDispatcher: renderRanOnDispatcher,
            StagingWriterRanOffDispatcher: stagingWriterRanOffDispatcher,
            CleanupPassed: true,
            GuardrailsPassed: guardrailsPassed);
    }

    private static BenchmarkCaseResult AggregateCase(
        IReadOnlyList<EditorOutputSample> samples)
    {
        EditorOutputSample first = samples[0];
        if (samples.Any(sample =>
                sample.Width != first.Width ||
                sample.Height != first.Height ||
                sample.SyntheticSeed != first.SyntheticSeed ||
                !string.Equals(
                    sample.InputPixelSha256,
                    first.InputPixelSha256,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    sample.RenderedPixelSha256,
                    first.RenderedPixelSha256,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    sample.PngSha256,
                    first.PngSha256,
                    StringComparison.Ordinal) ||
                sample.StagedByteLength != first.StagedByteLength))
        {
            throw new InvalidOperationException(
                "Identical benchmark inputs produced inconsistent output.");
        }

        return new BenchmarkCaseResult(
            Width: first.Width,
            Height: first.Height,
            SyntheticSeed: first.SyntheticSeed,
            InputPixelFormat: first.InputPixelFormat,
            RenderedPixelFormat: first.RenderedPixelFormat,
            MedianRenderMilliseconds: BenchmarkStatistics.Median(
                samples.Select(sample => sample.RenderMilliseconds)),
            P95RenderMilliseconds: BenchmarkStatistics.P95(
                samples.Select(sample => sample.RenderMilliseconds)),
            MedianEncodeMilliseconds: BenchmarkStatistics.Median(
                samples.Select(sample => sample.EncodeMilliseconds)),
            P95EncodeMilliseconds: BenchmarkStatistics.P95(
                samples.Select(sample => sample.EncodeMilliseconds)),
            MedianStageMilliseconds: BenchmarkStatistics.Median(
                samples.Select(sample => sample.StageMilliseconds)),
            P95StageMilliseconds: BenchmarkStatistics.P95(
                samples.Select(sample => sample.StageMilliseconds)),
            MedianCommitMilliseconds: BenchmarkStatistics.Median(
                samples.Select(sample => sample.CommitMilliseconds)),
            P95CommitMilliseconds: BenchmarkStatistics.P95(
                samples.Select(sample => sample.CommitMilliseconds)),
            MedianColdPreparationMilliseconds: BenchmarkStatistics.Median(
                samples.Select(sample => sample.ColdPreparationMilliseconds)),
            P95ColdPreparationMilliseconds: BenchmarkStatistics.P95(
                samples.Select(sample => sample.ColdPreparationMilliseconds)),
            MedianCachedReuseMilliseconds: BenchmarkStatistics.Median(
                samples.SelectMany(sample => sample.CachedReuseMilliseconds)),
            P95CachedReuseMilliseconds: BenchmarkStatistics.P95(
                samples.SelectMany(sample => sample.CachedReuseMilliseconds)),
            MedianManagedAllocatedBytes: BenchmarkStatistics.Median(
                samples.Select(sample => sample.ManagedAllocatedBytes)),
            P95ManagedAllocatedBytes: BenchmarkStatistics.P95(
                samples.Select(sample => sample.ManagedAllocatedBytes)),
            MedianPeakPrivateByteDelta: BenchmarkStatistics.Median(
                samples.Select(sample => sample.PeakPrivateByteDelta)),
            P95PeakPrivateByteDelta: BenchmarkStatistics.P95(
                samples.Select(sample => sample.PeakPrivateByteDelta)),
            P95Gen0Collections: BenchmarkStatistics.P95(
                samples.Select(sample => sample.Gen0Collections)),
            P95Gen1Collections: BenchmarkStatistics.P95(
                samples.Select(sample => sample.Gen1Collections)),
            P95Gen2Collections: BenchmarkStatistics.P95(
                samples.Select(sample => sample.Gen2Collections)),
            StagedByteLength: first.StagedByteLength,
            InputPixelSha256: first.InputPixelSha256,
            RenderedPixelSha256: first.RenderedPixelSha256,
            PngSha256: first.PngSha256,
            OutputFrozen: samples.All(sample => sample.OutputFrozen),
            RenderInvocationCountPerSample:
                samples.Max(sample => sample.RenderInvocationCount),
            EncodeInvocationCountPerSample:
                samples.Max(sample => sample.EncodeInvocationCount),
            StageInvocationCountPerSample:
                samples.Max(sample => sample.StageInvocationCount),
            CommitInvocationCountPerSample:
                samples.Max(sample => sample.CommitInvocationCount),
            SuccessfulCacheReuseCountPerSample:
                samples.Min(sample => sample.SuccessfulCacheReuseCount),
            PeakEncoderCount:
                samples.Max(sample => sample.PeakEncoderCount),
            CleanupPassed: samples.All(sample => sample.CleanupPassed),
            GuardrailsPassed: samples.All(sample => sample.GuardrailsPassed));
    }
}

internal sealed record EditorOutputSample(
    int Width,
    int Height,
    uint SyntheticSeed,
    string InputPixelFormat,
    string RenderedPixelFormat,
    double RenderMilliseconds,
    double EncodeMilliseconds,
    double StageMilliseconds,
    double CommitMilliseconds,
    double ColdPreparationMilliseconds,
    IReadOnlyList<double> CachedReuseMilliseconds,
    long ManagedAllocatedBytes,
    long PeakPrivateByteDelta,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int StagedByteLength,
    string InputPixelSha256,
    string RenderedPixelSha256,
    string PngSha256,
    bool OutputFrozen,
    int RenderInvocationCount,
    int EncodeInvocationCount,
    int StageInvocationCount,
    int CommitInvocationCount,
    int SuccessfulCacheReuseCount,
    int PeakEncoderCount,
    bool RenderRanOnDispatcher,
    bool StagingWriterRanOffDispatcher,
    bool CleanupPassed,
    bool GuardrailsPassed);
