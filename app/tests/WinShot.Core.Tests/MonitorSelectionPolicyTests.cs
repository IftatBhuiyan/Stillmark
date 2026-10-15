using WinShot.Capture;
using WinShot.Core;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class MonitorSelectionPolicyTests
{
    [Fact]
    public void EnsureAvailable_PreservesExistingInventory()
    {
        MonitorInfo existing = Monitor("DISPLAY2", new PixelRect(100, 200, 800, 600), primary: true);
        MonitorInfo[] monitors = [existing];

        IReadOnlyList<MonitorInfo> result = MonitorSelectionPolicy.EnsureAvailable(
            monitors,
            new PixelRect(0, 0, 1, 1));

        Assert.Same(monitors, result);
        Assert.Same(existing, result[0]);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 1, 1)]
    [InlineData(-1920, -200, 3840, 1280, 3840, 1280)]
    public void EnsureAvailable_CreatesOneSafePrimaryFallback(
        int x,
        int y,
        int width,
        int height,
        int expectedWidth,
        int expectedHeight)
    {
        IReadOnlyList<MonitorInfo> result = MonitorSelectionPolicy.EnsureAvailable(
            [],
            new PixelRect(x, y, width, height));

        MonitorInfo monitor = Assert.Single(result);
        Assert.Equal("DISPLAY", monitor.DeviceName);
        Assert.Equal(new PixelRect(x, y, expectedWidth, expectedHeight), monitor.Bounds);
        Assert.Equal(monitor.Bounds, monitor.WorkArea);
        Assert.True(monitor.IsPrimary);
        Assert.Equal((uint)96, monitor.DpiX);
        Assert.Equal((uint)96, monitor.DpiY);
    }

    [Fact]
    public void Select_PrefersContainingMonitorThenPrimary()
    {
        MonitorInfo primary = Monitor("DISPLAY1", new PixelRect(0, 0, 100, 100), primary: true);
        MonitorInfo secondary = Monitor("DISPLAY2", new PixelRect(100, 0, 100, 100), primary: false);
        MonitorInfo[] monitors = [primary, secondary];

        Assert.Same(secondary, MonitorSelectionPolicy.Select(monitors, 150, 50));
        Assert.Same(primary, MonitorSelectionPolicy.Select(monitors, 500, 500));
    }

    [Fact]
    public void Select_RejectsUnnormalizedEmptyInventory()
    {
        Assert.Throws<ArgumentException>(() => MonitorSelectionPolicy.Select([], 0, 0));
    }

    private static MonitorInfo Monitor(string name, PixelRect bounds, bool primary) =>
        new(name, bounds, bounds, primary, 96, 96);
}
