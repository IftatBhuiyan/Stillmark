using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using WinShot.App;

namespace WinShot.EditorOutput.Perf;

internal static class DispatcherHeartbeatBenchmark
{
    private const int HeartbeatIntervalMilliseconds = 5;

    internal static DispatcherHeartbeatReport Run4K() =>
        Run(width: 3840, height: 2160);

    internal static DispatcherHeartbeatReport Run(
        int width,
        int height)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("A WPF STA thread is required.");

        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.VerifyAccess();
        var gaps = new List<double>();
        bool encoderObserved = false;
        int writerThreadId = 0;
        long previousHeartbeat = 0;

        void RecordHeartbeat()
        {
            long observed = Stopwatch.GetTimestamp();
            gaps.Add(
                Stopwatch.GetElapsedTime(
                    previousHeartbeat,
                    observed).TotalMilliseconds);
            previousHeartbeat = observed;
            if (StillImageEncodingService.ActiveEncoderCount > 0)
                encoderObserved = true;
        }

        var heartbeat = new DispatcherTimer(
            TimeSpan.FromMilliseconds(HeartbeatIntervalMilliseconds),
            DispatcherPriority.Normal,
            (_, _) => RecordHeartbeat(),
            dispatcher);
        heartbeat.Stop();

        var source = SyntheticBitmapFactory.Create(
            width,
            height,
            out uint seed);
        previousHeartbeat = Stopwatch.GetTimestamp();
        long started = Stopwatch.GetTimestamp();
        heartbeat.Start();
        _ = dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(RecordHeartbeat));
        EditorOutputSample sample;
        try
        {
            sample = DispatcherPump.Run(() =>
                EditorOutputBenchmark.MeasureSampleAsync(
                    source,
                    seed,
                    cacheReuseCount: 3,
                    observedThreadId => writerThreadId = observedThreadId));
        }
        finally
        {
            heartbeat.Stop();
        }

        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (gaps.Count < 2)
        {
            throw new InvalidOperationException(
                "The dispatcher did not pump during editor output preparation.");
        }

        int dispatcherThreadId = Environment.CurrentManagedThreadId;
        bool guardrailsPassed =
            sample.GuardrailsPassed &&
            sample.RenderRanOnDispatcher &&
            sample.StagingWriterRanOffDispatcher &&
            writerThreadId != 0 &&
            writerThreadId != dispatcherThreadId &&
            encoderObserved &&
            StillImageEncodingService.ActiveEncoderCount == 0 &&
            StillImageEncodingService.PeakEncoderCount == 1;
        MeasuredSourceFingerprint fingerprint = MeasuredSource.Fingerprint();
        MeasuredSourceFingerprint harnessFingerprint =
            HarnessSource.Fingerprint();
        return new DispatcherHeartbeatReport(
            SchemaVersion: 1,
            EvidenceClass: "LocalSyntheticDispatcherHeartbeat",
            MeasurementScope:
                "One synthetic editor render, bounded PNG encode, durable stage, commit, and cache publication while pumping WPF Dispatcher",
            RuntimeVersion: Environment.Version.ToString(),
            ProcessArchitecture:
                RuntimeInformation.ProcessArchitecture.ToString(),
#if DEBUG
            BuildConfiguration: "Debug",
#else
            BuildConfiguration: "Release",
#endif
            Width: width,
            Height: height,
            HeartbeatIntervalMilliseconds: HeartbeatIntervalMilliseconds,
            HeartbeatCount: gaps.Count,
            P50HeartbeatGapMilliseconds: BenchmarkStatistics.P50(gaps),
            P95HeartbeatGapMilliseconds: BenchmarkStatistics.P95(gaps),
            MaximumHeartbeatGapMilliseconds: gaps.Max(),
            ColdPreparationMilliseconds: elapsed,
            RenderRanOnDispatcher: sample.RenderRanOnDispatcher,
            StagingWriterRanOffDispatcher:
                sample.StagingWriterRanOffDispatcher,
            EncoderObservedWhileDispatcherPumped: encoderObserved,
            PeakEncoderCount: StillImageEncodingService.PeakEncoderCount,
            StagedByteLength: sample.StagedByteLength,
            RenderedPixelSha256: sample.RenderedPixelSha256,
            PngSha256: sample.PngSha256,
            MeasuredSourceCount: fingerprint.Count,
            MeasuredSourceSha256: fingerprint.AggregateSha256,
            HarnessSourceCount: harnessFingerprint.Count,
            HarnessSourceSha256: harnessFingerprint.AggregateSha256,
            CleanupPassed: sample.CleanupPassed,
            GuardrailsPassed: guardrailsPassed);
    }
}
