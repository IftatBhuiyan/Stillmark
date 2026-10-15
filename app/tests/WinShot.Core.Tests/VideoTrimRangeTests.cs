using WinShot.Core.Media;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class VideoTrimRangeTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(60);

    [Fact]
    public void NoTrim_UsesWholeSource()
    {
        var range = VideoTrimRange.Resolve(Duration, TimeSpan.Zero, TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, range.StartOffset);
        Assert.Equal(TimeSpan.Zero, range.EndOffset);
        Assert.Equal(Duration, range.OutputDuration);
    }

    [Fact]
    public void AbsoluteEnd_IsConvertedToOffsetFromSourceEnd()
    {
        var range = VideoTrimRange.Resolve(
            Duration, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(45));

        Assert.Equal(TimeSpan.FromSeconds(10), range.StartOffset);
        Assert.Equal(TimeSpan.FromSeconds(15), range.EndOffset);
        Assert.Equal(TimeSpan.FromSeconds(35), range.OutputDuration);
    }

    [Fact]
    public void StartOnlyTrim_LeavesNoEndOffset()
    {
        var range = VideoTrimRange.Resolve(
            Duration, TimeSpan.FromSeconds(10), TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(10), range.StartOffset);
        Assert.Equal(TimeSpan.Zero, range.EndOffset);
        Assert.Equal(TimeSpan.FromSeconds(50), range.OutputDuration);
    }

    [Theory]
    [InlineData(-1, 20)]
    [InlineData(60, 0)]
    [InlineData(10, 10)]
    [InlineData(20, 10)]
    [InlineData(10, 61)]
    public void InvalidBounds_AreRejected(double startSeconds, double endSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoTrimRange.Resolve(
            Duration, TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds)));
    }

    [Fact]
    public void NonPositiveSourceDuration_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoTrimRange.Resolve(
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero));
    }
}
