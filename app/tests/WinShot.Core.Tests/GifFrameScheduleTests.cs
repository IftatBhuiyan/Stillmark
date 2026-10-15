using WinShot.Core.Media;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class GifFrameScheduleTests
{
    [Fact]
    public void FrameCount_CoversFullRecordingBeyondFormerFortySecondCap()
    {
        var schedule = new GifFrameSchedule(TimeSpan.FromSeconds(90), framesPerSecond: 15);

        Assert.Equal(1_350, schedule.FrameCount);
        Assert.Equal(TimeSpan.FromSeconds(89 + 14.0 / 15), schedule.GetTimestamp(1_349));
        Assert.Equal(9_000, Enumerable.Range(0, schedule.FrameCount)
            .Sum(schedule.GetDelayCentiseconds));
        Assert.Contains(6, Enumerable.Range(0, schedule.FrameCount)
            .Select(schedule.GetDelayCentiseconds));
        Assert.Contains(7, Enumerable.Range(0, schedule.FrameCount)
            .Select(schedule.GetDelayCentiseconds));
    }

    [Fact]
    public void FrameCount_RoundsUpPartialFrame()
    {
        var schedule = new GifFrameSchedule(TimeSpan.FromMilliseconds(101), framesPerSecond: 10);

        Assert.Equal(2, schedule.FrameCount);
        Assert.Equal(TimeSpan.FromMilliseconds(100), schedule.GetTimestamp(1));
    }

    [Fact]
    public void EmptyClip_StillSchedulesOneFrameAtZero()
    {
        var schedule = new GifFrameSchedule(TimeSpan.Zero, framesPerSecond: 15);

        Assert.Equal(1, schedule.FrameCount);
        Assert.Equal(TimeSpan.Zero, schedule.GetTimestamp(0));
    }

    [Fact]
    public void InvalidInputs_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GifFrameSchedule(TimeSpan.FromSeconds(-1), framesPerSecond: 15));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GifFrameSchedule(TimeSpan.FromSeconds(1), framesPerSecond: 0));
    }
}
