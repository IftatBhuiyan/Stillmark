using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.App.Editor;

namespace WinShot.RegionEffects.Perf;

internal static class RegionEffectBenchmark
{
    private const string MeasuredSourceResourceName =
        "WinShot.MeasuredPixelFilters.cs";
    private const int BlurRadius = 7;
    private const int BlurPasses = 3;
    private static readonly (int Width, int Height)[] Sizes =
    [
        (512, 512),
        (1920, 1080),
        (3840, 2160),
    ];

    public static BenchmarkReport Run(int sampleCount)
    {
        WarmUp();
        var results = new List<BenchmarkCaseResult>(Sizes.Length * 2);

        foreach ((int width, int height) in Sizes)
        {
            BitmapSource source = CreateSeededSource(width, height);
            var region = new Int32Rect(0, 0, width, height);
            results.Add(MeasureCase(
                RegionEffectKind.Pixelate,
                source,
                region,
                sampleCount));
            results.Add(MeasureCase(
                RegionEffectKind.BoxBlur,
                source,
                region,
                sampleCount));
        }

        return new BenchmarkReport(
            SchemaVersion: 1,
            EvidenceClass: "LocalDeterministicBaseline",
            MeasurementScope:
                "Existing PixelFilters full-region CPU behavior; no UI dispatcher measurement",
            RuntimeVersion: Environment.Version.ToString(),
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
#if DEBUG
            BuildConfiguration: "Debug",
#else
            BuildConfiguration: "Release",
#endif
            WarmupCountPerEffect: 1,
            SampleCountPerCase: sampleCount,
            PercentileMethod: "Nearest-rank over independent worker samples",
            AllocationMethod:
                "GC.GetAllocatedBytesForCurrentThread around PixelFilters only",
            PrivateBytesMethod:
                "Best-effort 1 ms process sampling during each worker invocation",
            MeasuredSourceSha256: HashMeasuredSource(),
            Cases: results);
    }

    public static void RunSelfTest()
    {
        WarmUp();
        BitmapSource source = CreateSeededSource(64, 48);
        var region = new Int32Rect(3, 2, 57, 41);

        WorkerSample firstPixelate = MeasureSample(
            RegionEffectKind.Pixelate,
            source,
            region);
        WorkerSample secondPixelate = MeasureSample(
            RegionEffectKind.Pixelate,
            source,
            region);
        WorkerSample firstBlur = MeasureSample(
            RegionEffectKind.BoxBlur,
            source,
            region);
        WorkerSample secondBlur = MeasureSample(
            RegionEffectKind.BoxBlur,
            source,
            region);

        AssertSampleShape(firstPixelate, region);
        AssertSampleShape(secondPixelate, region);
        AssertSampleShape(firstBlur, region);
        AssertSampleShape(secondBlur, region);
        if (!string.Equals(
                firstPixelate.OutputPixelSha256,
                secondPixelate.OutputPixelSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                firstBlur.OutputPixelSha256,
                secondBlur.OutputPixelSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Identical seeded inputs did not produce deterministic hashes.");
        }
        if (string.Equals(
                firstPixelate.OutputPixelSha256,
                firstBlur.OutputPixelSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pixelate and blur self-test outputs unexpectedly matched.");
        }
        if (firstPixelate.ManagedAllocatedBytes <= 0 ||
            firstBlur.ManagedAllocatedBytes <= 0)
        {
            throw new InvalidOperationException(
                "The managed allocation probe did not observe the filter buffers.");
        }

        string sourceHash = HashMeasuredSource();
        if (sourceHash.Length != 64 ||
            !sourceHash.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                "The measured-source fingerprint was malformed.");
        }

        BenchmarkReport report = Run(sampleCount: 3);
        string json = System.Text.Json.JsonSerializer.Serialize(
            report,
            BenchmarkJsonContext.Default.BenchmarkReport);
        if (string.IsNullOrWhiteSpace(json) ||
            report.Cases.Count != Sizes.Length * 2 ||
            report.Cases.Any(result =>
                result.OutputPixelSha256.Length != 64 ||
                result.MedianWorkerMilliseconds < 0 ||
                result.P95WorkerMilliseconds < result.MedianWorkerMilliseconds ||
                result.MedianManagedAllocatedBytes <= 0 ||
                result.P95ManagedAllocatedBytes <
                    result.MedianManagedAllocatedBytes ||
                result.MedianPeakPrivateByteDelta < 0 ||
                result.P95PeakPrivateByteDelta <
                    result.MedianPeakPrivateByteDelta))
        {
            throw new InvalidOperationException(
                "The benchmark report failed its bounded schema invariants.");
        }
    }

    private static void WarmUp()
    {
        BitmapSource source = CreateSeededSource(32, 24);
        var region = new Int32Rect(0, 0, 32, 24);
        _ = RunFilter(RegionEffectKind.Pixelate, source, region);
        _ = RunFilter(RegionEffectKind.BoxBlur, source, region);
    }

    private static BenchmarkCaseResult MeasureCase(
        RegionEffectKind effect,
        BitmapSource source,
        Int32Rect region,
        int sampleCount)
    {
        var samples = new List<WorkerSample>(sampleCount);
        string? expectedHash = null;
        for (int index = 0; index < sampleCount; index++)
        {
            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Forced,
                blocking: true,
                compacting: false);
            GC.WaitForPendingFinalizers();

            WorkerSample sample = MeasureSample(effect, source, region);
            expectedHash ??= sample.OutputPixelSha256;
            if (!string.Equals(
                    expectedHash,
                    sample.OutputPixelSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A benchmark case produced inconsistent output hashes.");
            }
            samples.Add(sample);
        }

        int blockSize = PixelateBlockSize(region);
        string outputHash = expectedHash
            ?? throw new InvalidOperationException(
                "The benchmark case produced no samples.");
        return new BenchmarkCaseResult(
            Effect: effect == RegionEffectKind.Pixelate
                ? "Pixelate"
                : "BoxBlur",
            Width: source.PixelWidth,
            Height: source.PixelHeight,
            RegionX: region.X,
            RegionY: region.Y,
            RegionWidth: region.Width,
            RegionHeight: region.Height,
            PixelateBlockSize: effect == RegionEffectKind.Pixelate
                ? blockSize
                : null,
            BlurRadius: effect == RegionEffectKind.BoxBlur
                ? BlurRadius
                : null,
            BlurPasses: effect == RegionEffectKind.BoxBlur
                ? BlurPasses
                : null,
            MedianWorkerMilliseconds: Median(
                samples.Select(sample => sample.WorkerMilliseconds)),
            P95WorkerMilliseconds: P95(
                samples.Select(sample => sample.WorkerMilliseconds)),
            MedianManagedAllocatedBytes: Median(
                samples.Select(sample => sample.ManagedAllocatedBytes)),
            P95ManagedAllocatedBytes: P95(
                samples.Select(sample => sample.ManagedAllocatedBytes)),
            MedianPeakPrivateByteDelta: Median(
                samples.Select(sample => sample.PeakPrivateByteDelta)),
            P95PeakPrivateByteDelta: P95(
                samples.Select(sample => sample.PeakPrivateByteDelta)),
            MaximumPeakPrivateByteDelta:
                samples.Max(sample => sample.PeakPrivateByteDelta),
            OutputPixelFormat: PixelFormats.Bgra32.ToString(),
            OutputPixelSha256: outputHash);
    }

    private static WorkerSample MeasureSample(
        RegionEffectKind effect,
        BitmapSource source,
        Int32Rect region)
    {
        using var privateBytes = new PeakPrivateBytesMonitor();
        privateBytes.Start();
        FilterExecution execution = Task.Run(
                () => ExecuteFilter(effect, source, region))
            .GetAwaiter()
            .GetResult();
        long peakPrivateByteDelta = privateBytes.Stop();

        BitmapSource output = execution.Output;
        if (!output.IsFrozen)
        {
            throw new InvalidOperationException(
                "PixelFilters returned an unfrozen bitmap.");
        }

        return new WorkerSample(
            execution.WorkerMilliseconds,
            execution.ManagedAllocatedBytes,
            peakPrivateByteDelta,
            output.PixelWidth,
            output.PixelHeight,
            output.Format,
            HashPixels(output));
    }

    private static FilterExecution ExecuteFilter(
        RegionEffectKind effect,
        BitmapSource source,
        Int32Rect region)
    {
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        BitmapSource output = RunFilter(effect, source, region);
        long completed = Stopwatch.GetTimestamp();
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        return new FilterExecution(
            output,
            Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds,
            Math.Max(0, allocatedAfter - allocatedBefore));
    }

    private static BitmapSource RunFilter(
        RegionEffectKind effect,
        BitmapSource source,
        Int32Rect region) =>
        effect switch
        {
            RegionEffectKind.Pixelate => PixelFilters.Pixelate(
                source,
                region,
                PixelateBlockSize(region)),
            RegionEffectKind.BoxBlur => PixelFilters.BoxBlur(
                source,
                region,
                BlurRadius,
                BlurPasses),
            _ => throw new ArgumentOutOfRangeException(nameof(effect)),
        };

    private static int PixelateBlockSize(Int32Rect region) =>
        Math.Max(
            6,
            (int)(Math.Min(region.Width, region.Height) / 16.0));

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

    private static string HashMeasuredSource()
    {
        using Stream source = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(MeasuredSourceResourceName)
            ?? throw new InvalidOperationException(
                "The measured PixelFilters source was not embedded.");
        return Convert.ToHexString(SHA256.HashData(source));
    }

    private static void AssertSampleShape(
        WorkerSample sample,
        Int32Rect expectedRegion)
    {
        if (sample.OutputWidth != expectedRegion.Width ||
            sample.OutputHeight != expectedRegion.Height ||
            sample.OutputFormat != PixelFormats.Bgra32 ||
            sample.WorkerMilliseconds < 0 ||
            sample.ManagedAllocatedBytes < 0 ||
            sample.PeakPrivateByteDelta < 0 ||
            sample.OutputPixelSha256.Length != 64)
        {
            throw new InvalidOperationException(
                "A worker sample failed its output invariants.");
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        int midpoint = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[midpoint]
            : (sorted[midpoint - 1] + sorted[midpoint]) / 2;
    }

    private static long Median(IEnumerable<long> values)
    {
        long[] sorted = values.Order().ToArray();
        int midpoint = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[midpoint]
            : checked((sorted[midpoint - 1] / 2) + (sorted[midpoint] / 2) +
                (sorted[midpoint - 1] % 2 + sorted[midpoint] % 2) / 2);
    }

    private static double P95(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted[NearestRankIndex(sorted.Length, 0.95)];
    }

    private static long P95(IEnumerable<long> values)
    {
        long[] sorted = values.Order().ToArray();
        return sorted[NearestRankIndex(sorted.Length, 0.95)];
    }

    private static int NearestRankIndex(int count, double percentile) =>
        Math.Clamp((int)Math.Ceiling(percentile * count) - 1, 0, count - 1);

    private enum RegionEffectKind
    {
        Pixelate,
        BoxBlur,
    }

    private sealed record FilterExecution(
        BitmapSource Output,
        double WorkerMilliseconds,
        long ManagedAllocatedBytes);

    private sealed record WorkerSample(
        double WorkerMilliseconds,
        long ManagedAllocatedBytes,
        long PeakPrivateByteDelta,
        int OutputWidth,
        int OutputHeight,
        PixelFormat OutputFormat,
        string OutputPixelSha256);

    private sealed class PeakPrivateBytesMonitor : IDisposable
    {
        private readonly ManualResetEventSlim _started = new(false);
        private readonly ManualResetEventSlim _stop = new(false);
        private Thread? _thread;
        private long _baseline;
        private long _peak;
        private bool _stopped;

        public void Start()
        {
            if (_thread is not null)
                throw new InvalidOperationException(
                    "The private-byte monitor was already started.");

            _baseline = ReadPrivateBytes();
            _peak = _baseline;
            _thread = new Thread(Sample)
            {
                IsBackground = true,
                Name = "WinShot.RegionEffect.PrivateBytes",
            };
            _thread.Start();
            if (!_started.Wait(TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException(
                    "The private-byte monitor did not start.");
            }
        }

        public long Stop()
        {
            if (_thread is null)
                throw new InvalidOperationException(
                    "The private-byte monitor was not started.");
            if (_stopped)
                return Math.Max(0, _peak - _baseline);

            _stopped = true;
            _stop.Set();
            if (!_thread.Join(TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException(
                    "The private-byte monitor did not stop.");
            }

            RecordPeak(ReadPrivateBytes());
            return Math.Max(0, _peak - _baseline);
        }

        public void Dispose()
        {
            if (_thread is not null && !_stopped)
                _ = Stop();
            _started.Dispose();
            _stop.Dispose();
        }

        private void Sample()
        {
            _started.Set();
            while (!_stop.Wait(TimeSpan.FromMilliseconds(1)))
                RecordPeak(ReadPrivateBytes());
            RecordPeak(ReadPrivateBytes());
        }

        private void RecordPeak(long value)
        {
            long observed = Volatile.Read(ref _peak);
            while (value > observed)
            {
                long previous = Interlocked.CompareExchange(
                    ref _peak,
                    value,
                    observed);
                if (previous == observed)
                    return;
                observed = previous;
            }
        }

        private static long ReadPrivateBytes()
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            return process.PrivateMemorySize64;
        }
    }
}
