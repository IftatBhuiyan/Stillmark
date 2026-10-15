using System.Collections.Concurrent;
using WinShot.App.Recording;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class RecordingCapabilityPreflightTests
{
    [Fact]
    public void All_required_system_libraries_present_allows_recording()
    {
        var probed = new List<string>();

        RecordingCapabilityResult result = RecordingCapabilityPreflight.Evaluate(name =>
        {
            probed.Add(name);
            return RecordingSystemLibraryProbeStatus.Present;
        });

        Assert.True(result.IsAvailable);
        Assert.Equal(RecordingCapabilityReason.Available, result.Reason);
        Assert.Equal(7, probed.Count);
        Assert.Equal(
            probed.Count,
            probed.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.InRange(
            probed.Count,
            1,
            RecordingCapabilityPreflight.MaximumRequiredLibraryCount);
        Assert.All(probed, name =>
        {
            Assert.InRange(
                name.Length,
                1,
                RecordingCapabilityPreflight.MaximumLibraryNameLength);
            Assert.True(RecordingCapabilityPreflight.IsRequiredSystemLibraryName(name));
        });
    }

    [Theory]
    [InlineData("mfplat.dll")]
    [InlineData("mf.dll")]
    [InlineData("mfreadwrite.dll")]
    public void Any_missing_windows_media_library_has_media_feature_pack_copy(
        string missingLibrary)
    {
        RecordingCapabilityResult result = EvaluateWithMissing(missingLibrary);

        Assert.False(result.IsAvailable);
        Assert.Equal(RecordingCapabilityReason.MissingWindowsMediaComponents, result.Reason);
        Assert.Contains("Windows media components", result.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Media Feature Pack", result.UserMessage, StringComparison.Ordinal);
        Assert.Contains("restart Stillmark", result.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(missingLibrary, result.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("vcruntime140.dll")]
    [InlineData("vcruntime140_1.dll")]
    [InlineData("msvcp140.dll")]
    [InlineData("concrt140.dll")]
    public void Any_missing_visual_cpp_library_has_runtime_repair_copy(string missingLibrary)
    {
        RecordingCapabilityResult result = EvaluateWithMissing(missingLibrary);

        Assert.False(result.IsAvailable);
        Assert.Equal(RecordingCapabilityReason.MissingVisualCppRuntime, result.Reason);
        Assert.Contains("Visual C++ x64 Runtime", result.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Install or repair", result.UserMessage, StringComparison.Ordinal);
        Assert.Contains("restart Stillmark", result.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(missingLibrary, result.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_both_categories_has_deterministic_combined_copy()
    {
        RecordingCapabilityResult result = RecordingCapabilityPreflight.Evaluate(name =>
            name is "mfplat.dll" or "vcruntime140.dll"
                ? RecordingSystemLibraryProbeStatus.Missing
                : RecordingSystemLibraryProbeStatus.Present);

        Assert.Equal(RecordingCapabilityReason.MissingMultiplePrerequisites, result.Reason);
        Assert.Contains("Windows media components", result.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Visual C++ x64 Runtime", result.UserMessage, StringComparison.Ordinal);
        Assert.Contains("restart Stillmark", result.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Throwing_probe_fails_closed_without_private_exception_echo()
    {
        const string privateValue =
            @"C:\Users\Alice\Private\mfplat.dll api_key=customer-secret";

        RecordingCapabilityResult result = RecordingCapabilityPreflight.Evaluate(
            _ => throw new InvalidOperationException(privateValue));

        Assert.False(result.IsAvailable);
        Assert.Equal(RecordingCapabilityReason.ProbeUncertain, result.Reason);
        Assert.Contains("Restart Stillmark", result.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Screenshots are still available", result.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(privateValue, result.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice", result.UserMessage, StringComparison.Ordinal);
        Assert.InRange(
            result.UserMessage.Length,
            1,
            RecordingCapabilityPreflight.MaximumUserMessageLength);
    }

    [Fact]
    public void Unknown_probe_status_fails_closed()
    {
        RecordingCapabilityResult result = RecordingCapabilityPreflight.Evaluate(
            _ => (RecordingSystemLibraryProbeStatus)999);

        Assert.Equal(RecordingCapabilityReason.ProbeUncertain, result.Reason);
        Assert.False(result.IsAvailable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("kernel32.dll")]
    [InlineData(@"C:\Windows\System32\mfplat.dll")]
    [InlineData("../mfplat.dll")]
    [InlineData("mfplat.dll\0private")]
    public void Native_boundary_rejects_every_non_allowlisted_name(string libraryName)
    {
        RecordingSystemLibraryProbeStatus status =
            SystemRecordingLibraryProbe.Probe(libraryName);

        Assert.Equal(RecordingSystemLibraryProbeStatus.Uncertain, status);
    }

    [Fact]
    public void Capability_guard_evaluates_once_under_concurrent_access()
    {
        int evaluations = 0;
        var guard = new RecordingCapabilityGuard(() =>
        {
            Interlocked.Increment(ref evaluations);
            return RecordingCapabilityPreflight.Evaluate(
                _ => RecordingSystemLibraryProbeStatus.Present);
        });
        var results = new ConcurrentBag<RecordingCapabilityResult>();

        Parallel.For(0, 32, _ => results.Add(guard.GetResult()));

        Assert.Equal(1, evaluations);
        Assert.Equal(32, results.Count);
        Assert.All(results, result => Assert.True(result.IsAvailable));
    }

    [Fact]
    public void Capability_guard_caches_and_sanitizes_evaluator_failure()
    {
        const string privateValue = @"C:\Users\Alice\secret-recorder.dll";
        int evaluations = 0;
        var guard = new RecordingCapabilityGuard(() =>
        {
            evaluations++;
            throw new InvalidOperationException(privateValue);
        });

        RecordingCapabilityResult first = guard.GetResult();
        RecordingCapabilityResult second = guard.GetResult();

        Assert.Same(first, second);
        Assert.Equal(1, evaluations);
        Assert.Equal(RecordingCapabilityReason.ProbeUncertain, first.Reason);
        Assert.DoesNotContain(privateValue, first.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Capability_guard_canonicalizes_injected_copy()
    {
        const string privateValue =
            @"C:\Users\Alice\Private\mfplat.dll api_key=customer-secret";
        var guard = new RecordingCapabilityGuard(() =>
            new RecordingCapabilityResult(
                RecordingCapabilityReason.MissingWindowsMediaComponents,
                privateValue));

        RecordingCapabilityResult result = guard.GetResult();

        Assert.Equal(RecordingCapabilityReason.MissingWindowsMediaComponents, result.Reason);
        Assert.Contains("Media Feature Pack", result.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(privateValue, result.UserMessage, StringComparison.Ordinal);
    }

    private static RecordingCapabilityResult EvaluateWithMissing(string missingLibrary) =>
        RecordingCapabilityPreflight.Evaluate(name =>
            name.Equals(missingLibrary, StringComparison.OrdinalIgnoreCase)
                ? RecordingSystemLibraryProbeStatus.Missing
                : RecordingSystemLibraryProbeStatus.Present);
}
