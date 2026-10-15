using WinShot.App;
using WinShot.Interop;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class WindowCaptureExclusionTests
{
    [Fact]
    public void TryApply_SupportedBuild_UsesFullExclusionFirst()
    {
        var attempted = new List<uint>();

        var result = WindowCaptureExclusion.TryApply(
            supportsFullExclusion: true,
            allowMonitorFallback: true,
            affinity =>
            {
                attempted.Add(affinity);
                return true;
            });

        Assert.Equal(WindowCaptureExclusionResult.Excluded, result);
        Assert.Equal([NativeMethods.WDA_EXCLUDEFROMCAPTURE], attempted);
    }

    [Fact]
    public void TryApply_FullExclusionFailure_UsesLegacyFallback()
    {
        var attempted = new List<uint>();

        var result = WindowCaptureExclusion.TryApply(
            supportsFullExclusion: true,
            allowMonitorFallback: true,
            affinity =>
            {
                attempted.Add(affinity);
                return affinity == NativeMethods.WDA_MONITOR;
            });

        Assert.Equal(WindowCaptureExclusionResult.MonitorFallback, result);
        Assert.Equal(
            [NativeMethods.WDA_EXCLUDEFROMCAPTURE, NativeMethods.WDA_MONITOR],
            attempted);
    }

    [Fact]
    public void TryApply_OlderBuild_UsesOnlyLegacyFallback()
    {
        var attempted = new List<uint>();

        var result = WindowCaptureExclusion.TryApply(
            supportsFullExclusion: false,
            allowMonitorFallback: true,
            affinity =>
            {
                attempted.Add(affinity);
                return true;
            });

        Assert.Equal(WindowCaptureExclusionResult.MonitorFallback, result);
        Assert.Equal([NativeMethods.WDA_MONITOR], attempted);
    }

    [Fact]
    public void TryApply_FullMonitorOverlay_DoesNotUseBlackoutFallback()
    {
        var attempted = new List<uint>();

        var result = WindowCaptureExclusion.TryApply(
            supportsFullExclusion: true,
            allowMonitorFallback: false,
            affinity =>
            {
                attempted.Add(affinity);
                return false;
            });

        Assert.Equal(WindowCaptureExclusionResult.Unavailable, result);
        Assert.Equal([NativeMethods.WDA_EXCLUDEFROMCAPTURE], attempted);
    }

    [Fact]
    public void TryApply_OlderBuildWithFallbackDisabled_DoesNotSetAffinity()
    {
        bool called = false;

        var result = WindowCaptureExclusion.TryApply(
            supportsFullExclusion: false,
            allowMonitorFallback: false,
            _ =>
            {
                called = true;
                return true;
            });

        Assert.Equal(WindowCaptureExclusionResult.Unavailable, result);
        Assert.False(called);
    }
}
