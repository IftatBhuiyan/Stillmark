using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using WinShot.App;
using WinShot.App.Editor;

namespace WinShot.EditorOutput.Perf;

internal static class HarnessSelfTest
{
    internal static void Run()
    {
        AssertStatistics();
        MeasuredSourceFingerprint fingerprint = MeasuredSource.Fingerprint();
        AssertSha256(fingerprint.AggregateSha256);
        if (fingerprint.Count != 13)
            throw new InvalidOperationException("Measured-source count changed.");
        MeasuredSourceFingerprint harnessFingerprint =
            HarnessSource.Fingerprint();
        AssertSha256(harnessFingerprint.AggregateSha256);
        if (harnessFingerprint.Count != 9)
            throw new InvalidOperationException("Harness-source count changed.");

        (EditorOutputSample first, EditorOutputSample second) =
            DispatcherPump.Run(RunPipelineTestsAsync);
        if (!string.Equals(
                first.InputPixelSha256,
                second.InputPixelSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                first.RenderedPixelSha256,
                second.RenderedPixelSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                first.PngSha256,
                second.PngSha256,
                StringComparison.Ordinal) ||
            first.StagedByteLength != second.StagedByteLength ||
            !first.GuardrailsPassed ||
            !second.GuardrailsPassed)
        {
            throw new InvalidOperationException(
                "Identical synthetic pipeline runs were not deterministic.");
        }

        DispatcherHeartbeatReport heartbeat =
            DispatcherHeartbeatBenchmark.Run(1920, 1080);
        if (!heartbeat.GuardrailsPassed ||
            heartbeat.HeartbeatCount < 2 ||
            heartbeat.MaximumHeartbeatGapMilliseconds <
                heartbeat.P95HeartbeatGapMilliseconds ||
            heartbeat.P95HeartbeatGapMilliseconds <
                heartbeat.P50HeartbeatGapMilliseconds)
        {
            throw new InvalidOperationException(
                "The dispatcher heartbeat failed its structural invariants.");
        }

        AssertPrivacySchema(
            first,
            fingerprint,
            harnessFingerprint,
            heartbeat);
    }

    private static async Task<(
        EditorOutputSample First,
        EditorOutputSample Second)> RunPipelineTestsAsync()
    {
        BitmapSource source = SyntheticBitmapFactory.Create(
            64,
            48,
            out uint seed);
        EditorOutputSample first =
            await EditorOutputBenchmark.MeasureSampleAsync(
                source,
                seed,
                cacheReuseCount: 3,
                stagingThreadObserver: null);
        EditorOutputSample second =
            await EditorOutputBenchmark.MeasureSampleAsync(
                source,
                seed,
                cacheReuseCount: 3,
                stagingThreadObserver: null);

        await AssertPartialWriteFailureAsync(source);
        await AssertShortSuccessfulWriteFailureAsync(source);
        await AssertCanceledPublicationAsync(source);
        return (first, second);
    }

    private static async Task AssertPartialWriteFailureAsync(
        BitmapSource source)
    {
        EncodedImagePayload payload =
            await StillImageEncodingService.EncodePngAsync(
                source,
                maximumPngBytes: EditorOutputBenchmark.MaximumPngBytes);
        using var workspace = new SafeTemporaryWorkspace();
        string destination = workspace.GetPath("existing.png");
        byte[] prior = [9, 8, 7, 6, 5];
        await File.WriteAllBytesAsync(destination, prior);
        workspace.TrackOwnedFile(destination);

        bool failed = false;
        try
        {
            using ImageSaveService.PreparedImageWrite _ =
                await ImageSaveService.PrepareExactPngAsync(
                    payload,
                    destination,
                    replaceExisting: true,
                    async (stream, cancellationToken) =>
                    {
                        int count = Math.Min(16, payload.PngLength);
                        await stream.WriteAsync(
                            payload.PngBytes[..count],
                            cancellationToken);
                        throw new IOException(
                            "Synthetic partial-write failure.");
                    });
        }
        catch (IOException)
        {
            failed = true;
        }

        byte[] observedDestination =
            await File.ReadAllBytesAsync(destination);
        if (!failed ||
            !prior.AsSpan().SequenceEqual(observedDestination))
        {
            throw new InvalidOperationException(
                "A partial-write failure changed a prior destination.");
        }

        workspace.AssertNoPartialFiles();
    }

    private static async Task AssertShortSuccessfulWriteFailureAsync(
        BitmapSource source)
    {
        EncodedImagePayload payload =
            await StillImageEncodingService.EncodePngAsync(
                source,
                maximumPngBytes: EditorOutputBenchmark.MaximumPngBytes);
        using var workspace = new SafeTemporaryWorkspace();
        string destination = workspace.GetPath("truncated.png");

        bool failed = false;
        try
        {
            using ImageSaveService.PreparedImageWrite _ =
                await ImageSaveService.PrepareExactPngAsync(
                    payload,
                    destination,
                    replaceExisting: false,
                    static async (stream, cancellationToken) =>
                    {
                        await stream.WriteAsync(
                            new byte[] { 0x89, 0x50, 0x4E, 0x47 },
                            cancellationToken);
                    });
        }
        catch (IOException)
        {
            failed = true;
        }

        if (!failed || File.Exists(destination))
        {
            throw new InvalidOperationException(
                "A short successful staging writer was accepted.");
        }

        workspace.AssertNoPartialFiles();
    }

    private static async Task AssertCanceledPublicationAsync(
        BitmapSource source)
    {
        EncodedImagePayload payload =
            await StillImageEncodingService.EncodePngAsync(
                source,
                maximumPngBytes: EditorOutputBenchmark.MaximumPngBytes);
        using var workspace = new SafeTemporaryWorkspace();
        using var coordinator = new EditorResultPreparationCoordinator();
        using EditorResultPreparationLease lease =
            coordinator.Begin(expectedRevision: 0);
        using (ImageSaveService.PreparedImageWrite staged =
            await ImageSaveService.PreparePngCopyAsync(
                payload,
                workspace.DirectoryPath,
                "canceled",
                lease.Token))
        {
            coordinator.CancelCurrent();
            if (lease.TryPublish(0, () => staged.Commit()))
            {
                throw new InvalidOperationException(
                    "A canceled preparation published a final output.");
            }
        }

        if (Directory.EnumerateFileSystemEntries(
                workspace.DirectoryPath).Any())
        {
            throw new InvalidOperationException(
                "Canceled publication retained output state.");
        }
    }

    private static void AssertStatistics()
    {
        if (BenchmarkStatistics.Median([9d, 1d, 5d]) != 5 ||
            BenchmarkStatistics.Median([1L, 9L, 5L, 3L]) != 4 ||
            BenchmarkStatistics.P50([9d, 1d, 5d, 3d]) != 3 ||
            BenchmarkStatistics.P95(
                Enumerable.Range(1, 20).Select(value => (double)value)) !=
                19)
        {
            throw new InvalidOperationException(
                "Benchmark statistics changed semantics.");
        }
    }

    private static void AssertPrivacySchema(
        EditorOutputSample sample,
        MeasuredSourceFingerprint fingerprint,
        MeasuredSourceFingerprint harnessFingerprint,
        DispatcherHeartbeatReport heartbeat)
    {
        var report = new BenchmarkReport(
            SchemaVersion: 1,
            EvidenceClass: "LocalSyntheticEditorOutputSelfTest",
            MeasurementScope: "Synthetic schema self-test",
            RuntimeVersion: Environment.Version.ToString(),
            ProcessArchitecture: "SelfTest",
            BuildConfiguration: "SelfTest",
            WarmupCountPerCase: 1,
            SampleCountPerCase: 1,
            CacheReuseCountPerSample: 3,
            PercentileMethod: "SelfTest",
            AllocationMethod: "SelfTest",
            PrivateBytesMethod: "SelfTest",
            MaximumPngBytes: EditorOutputBenchmark.MaximumPngBytes,
            MeasuredSourceCount: fingerprint.Count,
            MeasuredSourceSha256: fingerprint.AggregateSha256,
            HarnessSourceCount: harnessFingerprint.Count,
            HarnessSourceSha256: harnessFingerprint.AggregateSha256,
            Cases:
            [
                new BenchmarkCaseResult(
                    sample.Width,
                    sample.Height,
                    sample.SyntheticSeed,
                    sample.InputPixelFormat,
                    sample.RenderedPixelFormat,
                    sample.RenderMilliseconds,
                    sample.RenderMilliseconds,
                    sample.EncodeMilliseconds,
                    sample.EncodeMilliseconds,
                    sample.StageMilliseconds,
                    sample.StageMilliseconds,
                    sample.CommitMilliseconds,
                    sample.CommitMilliseconds,
                    sample.ColdPreparationMilliseconds,
                    sample.ColdPreparationMilliseconds,
                    BenchmarkStatistics.Median(
                        sample.CachedReuseMilliseconds),
                    BenchmarkStatistics.P95(
                        sample.CachedReuseMilliseconds),
                    sample.ManagedAllocatedBytes,
                    sample.ManagedAllocatedBytes,
                    sample.PeakPrivateByteDelta,
                    sample.PeakPrivateByteDelta,
                    sample.Gen0Collections,
                    sample.Gen1Collections,
                    sample.Gen2Collections,
                    sample.StagedByteLength,
                    sample.InputPixelSha256,
                    sample.RenderedPixelSha256,
                    sample.PngSha256,
                    sample.OutputFrozen,
                    sample.RenderInvocationCount,
                    sample.EncodeInvocationCount,
                    sample.StageInvocationCount,
                    sample.CommitInvocationCount,
                    sample.SuccessfulCacheReuseCount,
                    sample.PeakEncoderCount,
                    sample.CleanupPassed,
                    sample.GuardrailsPassed),
            ],
            OverallPassed: true);
        string benchmarkJson = JsonSerializer.Serialize(
            report,
            BenchmarkJsonContext.Default.BenchmarkReport);
        string heartbeatJson = JsonSerializer.Serialize(
            heartbeat,
            BenchmarkJsonContext.Default.DispatcherHeartbeatReport);

        using (JsonDocument benchmarkDocument =
            JsonDocument.Parse(benchmarkJson))
        {
            AssertExactProperties(
                benchmarkDocument.RootElement,
                BenchmarkReportProperties);
            AssertExactProperties(
                benchmarkDocument.RootElement
                    .GetProperty("cases")[0],
                BenchmarkCaseProperties);
        }

        using (JsonDocument heartbeatDocument =
            JsonDocument.Parse(heartbeatJson))
        {
            AssertExactProperties(
                heartbeatDocument.RootElement,
                HeartbeatReportProperties);
        }

        string userName = Environment.UserName;
        string tempPath = Path.GetFullPath(Path.GetTempPath());
        foreach (string json in new[] { benchmarkJson, heartbeatJson })
        {
            if ((!string.IsNullOrWhiteSpace(userName) &&
                    json.Contains(
                        userName,
                        StringComparison.OrdinalIgnoreCase)) ||
                json.Contains(tempPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The evidence schema exposed local identity or path data.");
            }
        }
    }

    private static void AssertExactProperties(
        JsonElement element,
        IReadOnlyList<string> expected)
    {
        string[] actual = element
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "An evidence schema property inventory changed.");
        }
    }

    private static void AssertSha256(string value)
    {
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new InvalidOperationException("A SHA-256 value was malformed.");
    }

    private static readonly string[] BenchmarkReportProperties =
    [
        "schemaVersion",
        "evidenceClass",
        "measurementScope",
        "runtimeVersion",
        "processArchitecture",
        "buildConfiguration",
        "warmupCountPerCase",
        "sampleCountPerCase",
        "cacheReuseCountPerSample",
        "percentileMethod",
        "allocationMethod",
        "privateBytesMethod",
        "maximumPngBytes",
        "measuredSourceCount",
        "measuredSourceSha256",
        "harnessSourceCount",
        "harnessSourceSha256",
        "cases",
        "overallPassed",
    ];

    private static readonly string[] BenchmarkCaseProperties =
    [
        "width",
        "height",
        "syntheticSeed",
        "inputPixelFormat",
        "renderedPixelFormat",
        "medianRenderMilliseconds",
        "p95RenderMilliseconds",
        "medianEncodeMilliseconds",
        "p95EncodeMilliseconds",
        "medianStageMilliseconds",
        "p95StageMilliseconds",
        "medianCommitMilliseconds",
        "p95CommitMilliseconds",
        "medianColdPreparationMilliseconds",
        "p95ColdPreparationMilliseconds",
        "medianCachedReuseMilliseconds",
        "p95CachedReuseMilliseconds",
        "medianManagedAllocatedBytes",
        "p95ManagedAllocatedBytes",
        "medianPeakPrivateByteDelta",
        "p95PeakPrivateByteDelta",
        "p95Gen0Collections",
        "p95Gen1Collections",
        "p95Gen2Collections",
        "stagedByteLength",
        "inputPixelSha256",
        "renderedPixelSha256",
        "pngSha256",
        "outputFrozen",
        "renderInvocationCountPerSample",
        "encodeInvocationCountPerSample",
        "stageInvocationCountPerSample",
        "commitInvocationCountPerSample",
        "successfulCacheReuseCountPerSample",
        "peakEncoderCount",
        "cleanupPassed",
        "guardrailsPassed",
    ];

    private static readonly string[] HeartbeatReportProperties =
    [
        "schemaVersion",
        "evidenceClass",
        "measurementScope",
        "runtimeVersion",
        "processArchitecture",
        "buildConfiguration",
        "width",
        "height",
        "heartbeatIntervalMilliseconds",
        "heartbeatCount",
        "p50HeartbeatGapMilliseconds",
        "p95HeartbeatGapMilliseconds",
        "maximumHeartbeatGapMilliseconds",
        "coldPreparationMilliseconds",
        "renderRanOnDispatcher",
        "stagingWriterRanOffDispatcher",
        "encoderObservedWhileDispatcherPumped",
        "peakEncoderCount",
        "stagedByteLength",
        "renderedPixelSha256",
        "pngSha256",
        "measuredSourceCount",
        "measuredSourceSha256",
        "harnessSourceCount",
        "harnessSourceSha256",
        "cleanupPassed",
        "guardrailsPassed",
    ];
}
