using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinShot.App.Editor;

namespace WinShot.RegionEffects.Perf;

internal static class DispatcherHeartbeatBenchmark
{
    private const string PixelFiltersSourceResourceName =
        "WinShot.MeasuredPixelFilters.cs";
    private const string PipelineSourceResourceName =
        "WinShot.MeasuredRegionEffectProcessing.cs";
    private const string Expected1080pBlurSha256 =
        "51F253042F275704D541627FB9AE9C39855884390FD9029DB4F0DE34920268B6";
    private const string Expected4KBlurSha256 =
        "CA666627A6A2398E1639676B273725742A0AEE7F3CDBB9BC4DA9FFE552CDC86B";
    private const int BlurRadius = 7;
    private const int BlurPasses = 3;
    private const int HeartbeatIntervalMilliseconds = 5;

    internal static DispatcherHeartbeatReport Run4K() =>
        Run(
            width: 3840,
            height: 2160,
            Expected4KBlurSha256);

    internal static void RunSelfTest()
    {
        DispatcherHeartbeatReport report = Run(
            width: 1920,
            height: 1080,
            Expected1080pBlurSha256);
        string json = System.Text.Json.JsonSerializer.Serialize(
            report,
            BenchmarkJsonContext.Default.DispatcherHeartbeatReport);

        if (string.IsNullOrWhiteSpace(json) ||
            report.SchemaVersion != 1 ||
            report.HeartbeatCount <= 0 ||
            report.P95HeartbeatGapMilliseconds < 0 ||
            report.MaximumHeartbeatGapMilliseconds <
                report.P95HeartbeatGapMilliseconds ||
            report.PipelinePrepareMilliseconds < 0 ||
            !report.OutputFrozen ||
            !string.Equals(
                report.OutputPixelFormat,
                PixelFormats.Bgra32.ToString(),
                StringComparison.Ordinal) ||
            !string.Equals(
                report.OutputPixelSha256,
                Expected1080pBlurSha256,
                StringComparison.Ordinal) ||
            !IsSha256(report.PixelFiltersSourceSha256) ||
            !IsSha256(report.RegionEffectProcessingSourceSha256))
        {
            throw new InvalidOperationException(
                "The dispatcher-heartbeat report failed its correctness invariants.");
        }
    }

    private static DispatcherHeartbeatReport Run(
        int width,
        int height,
        string expectedOutputSha256)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException(
                "The dispatcher-heartbeat measurement requires an STA thread.");
        }

        BitmapSource source = CreateSeededSource(width, height);
        var request = new RegionEffectRequest(
            BackgroundIdentity: 1,
            PixelRegion: new Int32Rect(0, 0, width, height),
            Kind: RegionEffectKind.BoxBlur,
            Parameter: BlurRadius,
            Passes: BlurPasses);

        HeartbeatExecution execution = Measure(source, request);
        BitmapSource output = execution.Output;
        string outputHash = HashPixels(output);
        if (!output.IsFrozen ||
            output.PixelWidth != width ||
            output.PixelHeight != height ||
            output.Format != PixelFormats.Bgra32 ||
            !string.Equals(
                outputHash,
                expectedOutputSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pipeline heartbeat run did not produce the expected frozen pixels.");
        }

        return new DispatcherHeartbeatReport(
            SchemaVersion: 1,
            EvidenceClass: "LocalDispatcherHeartbeat",
            MeasurementScope:
                "RegionEffectPipeline full-region BoxBlur preparation while pumping a WPF Dispatcher",
            RuntimeVersion: Environment.Version.ToString(),
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
#if DEBUG
            BuildConfiguration: "Debug",
#else
            BuildConfiguration: "Release",
#endif
            Width: width,
            Height: height,
            BlurRadius: BlurRadius,
            BlurPasses: BlurPasses,
            HeartbeatIntervalMilliseconds: HeartbeatIntervalMilliseconds,
            HeartbeatCount: execution.HeartbeatGapsMilliseconds.Count,
            P95HeartbeatGapMilliseconds:
                P95(execution.HeartbeatGapsMilliseconds),
            MaximumHeartbeatGapMilliseconds:
                execution.HeartbeatGapsMilliseconds.Max(),
            PipelinePrepareMilliseconds: execution.PrepareMilliseconds,
            PercentileMethod:
                "Nearest-rank over dispatcher heartbeat gaps",
            HeartbeatMethod:
                "DispatcherTimer at Normal priority; Stopwatch gaps include start-to-first tick",
            OutputFrozen: output.IsFrozen,
            OutputPixelFormat: output.Format.ToString(),
            OutputPixelSha256: outputHash,
            PixelFiltersSourceSha256:
                HashEmbeddedSource(PixelFiltersSourceResourceName),
            RegionEffectProcessingSourceSha256:
                HashEmbeddedSource(PipelineSourceResourceName));
    }

    private static HeartbeatExecution Measure(
        BitmapSource source,
        RegionEffectRequest request)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.VerifyAccess();

        using var pipeline = new RegionEffectPipeline();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var heartbeatGaps = new List<double>();
        var frame = new DispatcherFrame();
        long started = Stopwatch.GetTimestamp();
        long previousHeartbeat = started;

        void RecordHeartbeat()
        {
            long observed = Stopwatch.GetTimestamp();
            heartbeatGaps.Add(
                Stopwatch.GetElapsedTime(
                    previousHeartbeat,
                    observed).TotalMilliseconds);
            previousHeartbeat = observed;
        }

        var heartbeat = new DispatcherTimer(
            TimeSpan.FromMilliseconds(HeartbeatIntervalMilliseconds),
            DispatcherPriority.Normal,
            (_, _) => RecordHeartbeat(),
            dispatcher);
        heartbeat.Stop();

        Task preparation = pipeline.PrepareAsync(
            source,
            [request],
            timeout.Token);
        heartbeat.Start();

        _ = dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(RecordHeartbeat));
        _ = preparation.ContinueWith(
            completed =>
            {
                _ = dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    new Action(() =>
                    {
                        try
                        {
                            completed.GetAwaiter().GetResult();
                        }
                        finally
                        {
                            frame.Continue = false;
                        }
                    }));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        Dispatcher.PushFrame(frame);
        heartbeat.Stop();
        preparation.GetAwaiter().GetResult();
        long completed = Stopwatch.GetTimestamp();

        if (heartbeatGaps.Count == 0)
        {
            throw new InvalidOperationException(
                "The dispatcher did not execute a heartbeat during pipeline preparation.");
        }

        RegionEffectPreparedSnapshot snapshot =
            pipeline.CapturePreparedSnapshot([request]);
        if (!snapshot.TryGet(request, out BitmapSource? output) ||
            output is null)
        {
            throw new InvalidOperationException(
                "The pipeline did not commit its prepared result.");
        }

        return new HeartbeatExecution(
            output,
            heartbeatGaps,
            Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds);
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

    private static string HashEmbeddedSource(string resourceName)
    {
        using Stream source = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"The measured source '{resourceName}' was not embedded.");
        return Convert.ToHexString(SHA256.HashData(source));
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static double P95(IReadOnlyList<double> values)
    {
        double[] sorted = values.Order().ToArray();
        int index = Math.Clamp(
            (int)Math.Ceiling(0.95 * sorted.Length) - 1,
            0,
            sorted.Length - 1);
        return sorted[index];
    }

    private sealed record HeartbeatExecution(
        BitmapSource Output,
        IReadOnlyList<double> HeartbeatGapsMilliseconds,
        double PrepareMilliseconds);
}
