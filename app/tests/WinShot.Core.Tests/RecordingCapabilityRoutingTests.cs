using Xunit;

namespace WinShot.Core.Tests;

public sealed class RecordingCapabilityRoutingTests
{
    [Fact]
    public void Every_recording_selection_or_countdown_route_uses_capability_guard()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "src",
            "WinShot.App",
            "Recording",
            "RecordingController.cs"));

        AssertRouteContains(
            source,
            "public void StartRegionRecording()",
            "public void StartRegionRecordingOf(",
            "RecordingCapabilityDenied()");
        AssertRouteContains(
            source,
            "public void StartRegionRecordingOf(",
            "public void RecordFullscreenAtCursor()",
            "RecordingCapabilityDenied()");
        AssertRouteContains(
            source,
            "public void RecordFullscreenAtCursor()",
            "private void BeginCountdown(",
            "RecordingCapabilityDenied()");
        AssertRouteContains(
            source,
            "private void BeginCountdown(",
            "private void StartRecorder(",
            "RecordingCapabilityDenied()");
        AssertRouteContains(
            source,
            "public void ToggleRecording()",
            "public void StartRegionRecording()",
            "StartRegionRecording();");

        Assert.Equal(4, CountOccurrences(source, "if (RecordingCapabilityDenied()) return;"));
    }

    [Fact]
    public void Capability_boundary_contains_no_install_elevation_registry_or_url_action()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "src",
            "WinShot.App",
            "Recording",
            "RecordingCapabilityPreflight.cs"));

        Assert.DoesNotContain("Registry.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Verb = \"runas\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LoadLibrarySearchSystem32", source, StringComparison.Ordinal);
    }

    private static void AssertRouteContains(
        string source,
        string routeStart,
        string routeEnd,
        string expected)
    {
        int start = source.IndexOf(routeStart, StringComparison.Ordinal);
        int end = source.IndexOf(routeEnd, start + routeStart.Length, StringComparison.Ordinal);

        Assert.True(start >= 0, $"Route start was not found: {routeStart}");
        Assert.True(end > start, $"Route end was not found after: {routeStart}");
        Assert.Contains(expected, source[start..end], StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; depth < 12 && directory is not null; depth++, directory = directory.Parent)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "The repository source file required by this contract test was not found.");
    }
}
