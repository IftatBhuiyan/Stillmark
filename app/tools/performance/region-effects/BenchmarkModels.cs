using System.Text.Json.Serialization;

namespace WinShot.RegionEffects.Perf;

internal sealed record BenchmarkReport(
    int SchemaVersion,
    string EvidenceClass,
    string MeasurementScope,
    string RuntimeVersion,
    string ProcessArchitecture,
    string BuildConfiguration,
    int WarmupCountPerEffect,
    int SampleCountPerCase,
    string PercentileMethod,
    string AllocationMethod,
    string PrivateBytesMethod,
    string MeasuredSourceSha256,
    IReadOnlyList<BenchmarkCaseResult> Cases);

internal sealed record BenchmarkCaseResult(
    string Effect,
    int Width,
    int Height,
    int RegionX,
    int RegionY,
    int RegionWidth,
    int RegionHeight,
    int? PixelateBlockSize,
    int? BlurRadius,
    int? BlurPasses,
    double MedianWorkerMilliseconds,
    double P95WorkerMilliseconds,
    long MedianManagedAllocatedBytes,
    long P95ManagedAllocatedBytes,
    long MedianPeakPrivateByteDelta,
    long P95PeakPrivateByteDelta,
    long MaximumPeakPrivateByteDelta,
    string OutputPixelFormat,
    string OutputPixelSha256);

internal sealed record DispatcherHeartbeatReport(
    int SchemaVersion,
    string EvidenceClass,
    string MeasurementScope,
    string RuntimeVersion,
    string ProcessArchitecture,
    string BuildConfiguration,
    int Width,
    int Height,
    int BlurRadius,
    int BlurPasses,
    int HeartbeatIntervalMilliseconds,
    int HeartbeatCount,
    double P95HeartbeatGapMilliseconds,
    double MaximumHeartbeatGapMilliseconds,
    double PipelinePrepareMilliseconds,
    string PercentileMethod,
    string HeartbeatMethod,
    bool OutputFrozen,
    string OutputPixelFormat,
    string OutputPixelSha256,
    string PixelFiltersSourceSha256,
    string RegionEffectProcessingSourceSha256);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(BenchmarkReport))]
[JsonSerializable(typeof(DispatcherHeartbeatReport))]
internal partial class BenchmarkJsonContext : JsonSerializerContext;
