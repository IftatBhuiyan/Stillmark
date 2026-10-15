using ScreenRecorderLib;

namespace WinShot.App.Recording;

/// <summary>An audio device choice. A null <see cref="Id"/> means "let Windows pick the default".</summary>
public sealed record AudioDeviceEntry(string? Id, string Name);

public sealed record AudioDeviceCatalog(
    IReadOnlyList<AudioDeviceEntry> InputDevices,
    IReadOnlyList<AudioDeviceEntry> OutputDevices);

public enum AudioDeviceLoadStatus
{
    Ready,
    Failed,
    TimedOut,
}

public sealed record AudioDeviceLoadResult(
    AudioDeviceLoadStatus Status,
    AudioDeviceCatalog Catalog);

internal readonly record struct AudioDeviceCandidate(string? Id, string? DisplayName);

internal sealed record AudioDeviceCandidateCatalog(
    IEnumerable<AudioDeviceCandidate>? InputDevices,
    IEnumerable<AudioDeviceCandidate>? OutputDevices);

internal static class AudioDeviceNormalization
{
    internal const int MaximumDevicesPerSource = 32;
    internal const int MaximumCandidatesExamined = 128;
    internal const int MaximumIdLength = 512;
    internal const int MaximumDisplayNameLength = 128;

    private static readonly AudioDeviceEntry SystemDefault = new(null, "System default");

    internal static IReadOnlyList<AudioDeviceEntry> Normalize(
        IEnumerable<AudioDeviceCandidate>? candidates)
    {
        var devices = new List<AudioDeviceEntry>(MaximumDevicesPerSource + 1)
        {
            SystemDefault,
        };
        if (candidates is null)
            return devices;

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int examined = 0;
        using IEnumerator<AudioDeviceCandidate> enumerator = candidates.GetEnumerator();
        while (examined < MaximumCandidatesExamined
            && devices.Count < MaximumDevicesPerSource + 1
            && enumerator.MoveNext())
        {
            examined++;
            AudioDeviceCandidate candidate = enumerator.Current;

            string? id = NormalizeId(candidate.Id);
            if (id is null || !seenIds.Add(id))
                continue;

            devices.Add(new AudioDeviceEntry(
                id,
                NormalizeDisplayName(candidate.DisplayName, id)));
        }

        return devices;
    }

    private static string? NormalizeId(string? value)
    {
        if (value is null || value.Length == 0 || value.Length > MaximumIdLength)
            return null;

        string normalized = value.Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string NormalizeDisplayName(string? value, string fallbackId)
    {
        string candidate = value ?? string.Empty;
        if (candidate.Length > MaximumDisplayNameLength)
            candidate = candidate[..MaximumDisplayNameLength];

        string normalized = candidate.Trim();
        if (normalized.Length > 0)
            return normalized;

        string fallback = fallbackId.Length <= MaximumDisplayNameLength
            ? fallbackId
            : fallbackId[..MaximumDisplayNameLength];
        return fallback;
    }
}

internal sealed class AudioDeviceEnumerationCoordinator
{
    private readonly object _gate = new();
    private readonly Func<AudioDeviceCandidateCatalog> _enumerate;
    private Task<AudioDeviceLoadResult>? _inFlight;

    internal AudioDeviceEnumerationCoordinator(Func<AudioDeviceCandidateCatalog> enumerate)
    {
        _enumerate = enumerate ?? throw new ArgumentNullException(nameof(enumerate));
    }

    internal async Task<AudioDeviceLoadResult> GetDevicesAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(timeout));

        cancellationToken.ThrowIfCancellationRequested();
        Task<AudioDeviceLoadResult> worker = GetOrStartWorker();
        Task timeoutTask = Task.Delay(timeout, cancellationToken);
        Task completed = await Task.WhenAny(worker, timeoutTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (completed == worker || worker.IsCompleted)
            return await worker.ConfigureAwait(false);

        return CreateFallback(AudioDeviceLoadStatus.TimedOut);
    }

    private Task<AudioDeviceLoadResult> GetOrStartWorker()
    {
        lock (_gate)
        {
            if (_inFlight is null || _inFlight.IsCompleted)
            {
                // The native API is synchronous and cannot be aborted safely. A timed-out or
                // canceled caller leaves this one shared worker in place so later callers coalesce
                // onto it instead of creating unbounded blocked native workers.
                _inFlight = Task.Run(EnumerateSafely);
            }

            return _inFlight;
        }
    }

    private AudioDeviceLoadResult EnumerateSafely()
    {
        try
        {
            AudioDeviceCandidateCatalog raw = _enumerate();
            return new AudioDeviceLoadResult(
                AudioDeviceLoadStatus.Ready,
                new AudioDeviceCatalog(
                    AudioDeviceNormalization.Normalize(raw.InputDevices),
                    AudioDeviceNormalization.Normalize(raw.OutputDevices)));
        }
        catch
        {
            // Device identifiers and names are privacy-sensitive. Return a stable fallback without
            // forwarding the native exception or its payload to application diagnostics.
            return CreateFallback(AudioDeviceLoadStatus.Failed);
        }
    }

    private static AudioDeviceLoadResult CreateFallback(AudioDeviceLoadStatus status)
    {
        IReadOnlyList<AudioDeviceEntry> defaults =
            AudioDeviceNormalization.Normalize(candidates: null);
        return new AudioDeviceLoadResult(
            status,
            new AudioDeviceCatalog(defaults, defaults));
    }
}

internal sealed class AudioDeviceRequestGate
{
    private long _generation;
    private bool _closed;

    internal long Begin()
    {
        if (_closed)
            throw new InvalidOperationException("The audio-device request owner is closed.");

        return ++_generation;
    }

    internal void Close()
    {
        _closed = true;
        _generation++;
    }

    internal bool CanApply(long generation, bool cancellationRequested) =>
        !_closed && !cancellationRequested && generation == _generation;
}

/// <summary>Enumerates both machine audio-device sources away from the UI thread.</summary>
public static class AudioDeviceService
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private static readonly AudioDeviceEnumerationCoordinator Coordinator =
        new(EnumerateNativeDevices);

    public static Task<AudioDeviceLoadResult> GetDevicesAsync(
        CancellationToken cancellationToken = default) =>
        Coordinator.GetDevicesAsync(DefaultTimeout, cancellationToken);

    private static AudioDeviceCandidateCatalog EnumerateNativeDevices()
    {
        return new AudioDeviceCandidateCatalog(
            EnumerateNativeSource(AudioDeviceSource.InputDevices),
            EnumerateNativeSource(AudioDeviceSource.OutputDevices));
    }

    private static IReadOnlyList<AudioDeviceCandidate> EnumerateNativeSource(
        AudioDeviceSource source)
    {
        var candidates = new List<AudioDeviceCandidate>(
            AudioDeviceNormalization.MaximumCandidatesExamined);
        foreach (AudioDevice device in Recorder.GetSystemAudioDevices(source))
        {
            if (candidates.Count >= AudioDeviceNormalization.MaximumCandidatesExamined)
                break;

            candidates.Add(new AudioDeviceCandidate(
                device.DeviceName,
                device.FriendlyName));
        }

        return candidates;
    }
}
