using System.Runtime.InteropServices;

namespace WinShot.App.Recording;

internal enum RecordingCapabilityReason
{
    Available,
    MissingWindowsMediaComponents,
    MissingVisualCppRuntime,
    MissingMultiplePrerequisites,
    ProbeUncertain,
}

internal sealed record RecordingCapabilityResult(
    RecordingCapabilityReason Reason,
    string UserMessage)
{
    internal bool IsAvailable => Reason == RecordingCapabilityReason.Available;
}

internal enum RecordingSystemLibraryProbeStatus
{
    Present,
    Missing,
    Uncertain,
}

internal enum RecordingSystemLibraryCategory
{
    WindowsMedia,
    VisualCppRuntime,
}

internal readonly record struct RecordingSystemLibraryRequirement(
    string FileName,
    RecordingSystemLibraryCategory Category);

/// <summary>
/// Pure policy over an injectable system-library boundary. The result exposes only fixed,
/// privacy-safe copy: library names, paths, native errors, and exceptions never leave the probe.
/// </summary>
internal static class RecordingCapabilityPreflight
{
    internal const int MaximumRequiredLibraryCount = 8;
    internal const int MaximumLibraryNameLength = 32;
    internal const int MaximumUserMessageLength = 320;

    private static readonly RecordingSystemLibraryRequirement[] Requirements =
    [
        new("mfplat.dll", RecordingSystemLibraryCategory.WindowsMedia),
        new("mf.dll", RecordingSystemLibraryCategory.WindowsMedia),
        new("mfreadwrite.dll", RecordingSystemLibraryCategory.WindowsMedia),
        new("vcruntime140.dll", RecordingSystemLibraryCategory.VisualCppRuntime),
        new("vcruntime140_1.dll", RecordingSystemLibraryCategory.VisualCppRuntime),
        new("msvcp140.dll", RecordingSystemLibraryCategory.VisualCppRuntime),
        new("concrt140.dll", RecordingSystemLibraryCategory.VisualCppRuntime),
    ];

    private const string AvailableMessage = "Screen recording is available.";
    private const string MissingMediaMessage =
        "Screen recording needs Windows media components. On Windows N or KN, install the " +
        "Media Feature Pack, restart Windows, then restart Stillmark.";
    private const string MissingRuntimeMessage =
        "Screen recording needs the current Microsoft Visual C++ x64 Runtime. Install or repair " +
        "it, then restart Stillmark.";
    private const string MissingMultipleMessage =
        "Screen recording needs Windows media components and the current Microsoft Visual C++ " +
        "x64 Runtime. Install or repair both, restart Windows, then restart Stillmark.";
    private const string UncertainMessage =
        "Stillmark could not verify the required Windows recording components. Restart Stillmark and " +
        "try again. Screenshots are still available.";

    internal static RecordingCapabilityResult Evaluate(
        Func<string, RecordingSystemLibraryProbeStatus> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (Requirements.Length is 0 or > MaximumRequiredLibraryCount)
            return Create(RecordingCapabilityReason.ProbeUncertain, UncertainMessage);

        bool missingMedia = false;
        bool missingRuntime = false;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (RecordingSystemLibraryRequirement requirement in Requirements)
        {
            if (!IsRequiredSystemLibraryName(requirement.FileName)
                || !visited.Add(requirement.FileName))
            {
                return Create(RecordingCapabilityReason.ProbeUncertain, UncertainMessage);
            }

            RecordingSystemLibraryProbeStatus status;
            try
            {
                status = probe(requirement.FileName);
            }
            catch
            {
                return Create(RecordingCapabilityReason.ProbeUncertain, UncertainMessage);
            }

            switch (status)
            {
                case RecordingSystemLibraryProbeStatus.Present:
                    break;
                case RecordingSystemLibraryProbeStatus.Missing:
                    if (requirement.Category == RecordingSystemLibraryCategory.WindowsMedia)
                        missingMedia = true;
                    else
                        missingRuntime = true;
                    break;
                default:
                    return Create(RecordingCapabilityReason.ProbeUncertain, UncertainMessage);
            }
        }

        if (missingMedia && missingRuntime)
        {
            return Create(
                RecordingCapabilityReason.MissingMultiplePrerequisites,
                MissingMultipleMessage);
        }
        if (missingMedia)
        {
            return Create(
                RecordingCapabilityReason.MissingWindowsMediaComponents,
                MissingMediaMessage);
        }
        if (missingRuntime)
        {
            return Create(
                RecordingCapabilityReason.MissingVisualCppRuntime,
                MissingRuntimeMessage);
        }

        return Create(RecordingCapabilityReason.Available, AvailableMessage);
    }

    internal static RecordingCapabilityResult EvaluateSystem() =>
        Evaluate(SystemRecordingLibraryProbe.Probe);

    internal static RecordingCapabilityResult ProbeUncertain() =>
        Create(RecordingCapabilityReason.ProbeUncertain, UncertainMessage);

    internal static RecordingCapabilityResult Canonicalize(
        RecordingCapabilityResult? result) =>
        result?.Reason switch
        {
            RecordingCapabilityReason.Available =>
                Create(RecordingCapabilityReason.Available, AvailableMessage),
            RecordingCapabilityReason.MissingWindowsMediaComponents =>
                Create(RecordingCapabilityReason.MissingWindowsMediaComponents, MissingMediaMessage),
            RecordingCapabilityReason.MissingVisualCppRuntime =>
                Create(RecordingCapabilityReason.MissingVisualCppRuntime, MissingRuntimeMessage),
            RecordingCapabilityReason.MissingMultiplePrerequisites =>
                Create(RecordingCapabilityReason.MissingMultiplePrerequisites, MissingMultipleMessage),
            _ => ProbeUncertain(),
        };

    internal static bool IsRequiredSystemLibraryName(string value) =>
        IsStrictLibraryName(value)
        && Requirements.Any(requirement =>
            requirement.FileName.Equals(value, StringComparison.OrdinalIgnoreCase));

    private static bool IsStrictLibraryName(string value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > MaximumLibraryNameLength
            || !value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (char character in value)
        {
            bool allowed = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '_'
                or '.';
            if (!allowed)
                return false;
        }

        return true;
    }

    private static RecordingCapabilityResult Create(
        RecordingCapabilityReason reason,
        string userMessage)
    {
        if (userMessage.Length is 0 or > MaximumUserMessageLength)
            throw new InvalidOperationException("Recording capability copy exceeds its bound.");

        return new RecordingCapabilityResult(reason, userMessage);
    }
}

/// <summary>
/// Loads only allow-listed names from System32. It performs no installation, elevation, registry
/// access, or persistence and collapses all native details to a three-state result.
/// </summary>
internal static class SystemRecordingLibraryProbe
{
    private const uint LoadLibrarySearchSystem32 = 0x00000800;
    private const int ErrorModuleNotFound = 126;

    internal static RecordingSystemLibraryProbeStatus Probe(string fileName)
    {
        if (!RecordingCapabilityPreflight.IsRequiredSystemLibraryName(fileName))
            return RecordingSystemLibraryProbeStatus.Uncertain;

        nint handle = 0;
        try
        {
            handle = LoadLibraryEx(fileName, 0, LoadLibrarySearchSystem32);
            if (handle == 0)
            {
                return Marshal.GetLastWin32Error() == ErrorModuleNotFound
                    ? RecordingSystemLibraryProbeStatus.Missing
                    : RecordingSystemLibraryProbeStatus.Uncertain;
            }

            return FreeLibrary(handle)
                ? RecordingSystemLibraryProbeStatus.Present
                : RecordingSystemLibraryProbeStatus.Uncertain;
        }
        catch
        {
            return RecordingSystemLibraryProbeStatus.Uncertain;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true)]
    private static extern nint LoadLibraryEx(
        [MarshalAs(UnmanagedType.LPWStr)] string fileName,
        nint reserved,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(nint module);
}

/// <summary>
/// Process-lifetime cache. Prerequisite changes require a WinShot restart, matching the user copy,
/// so every route receives one bounded result and repeated entry attempts avoid repeated loads.
/// </summary>
internal sealed class RecordingCapabilityGuard
{
    private readonly Func<RecordingCapabilityResult> _evaluate;
    private readonly object _sync = new();
    private RecordingCapabilityResult? _cached;

    internal RecordingCapabilityGuard(Func<RecordingCapabilityResult> evaluate)
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        _evaluate = evaluate;
    }

    internal static RecordingCapabilityGuard CreateDefault() =>
        new(RecordingCapabilityPreflight.EvaluateSystem);

    internal RecordingCapabilityResult GetResult()
    {
        lock (_sync)
        {
            if (_cached is not null)
                return _cached;

            try
            {
                _cached = RecordingCapabilityPreflight.Canonicalize(_evaluate());
            }
            catch
            {
                _cached = RecordingCapabilityPreflight.ProbeUncertain();
            }

            return _cached;
        }
    }
}
