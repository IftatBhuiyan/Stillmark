namespace WinShot.Core.Media;

/// <summary>
/// Defines the complete, uncapped frame schedule for a GIF conversion.
/// </summary>
public sealed class GifFrameSchedule
{
    public GifFrameSchedule(TimeSpan duration, int framesPerSecond)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration cannot be negative.");
        if (framesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond), "Frame rate must be positive.");

        Duration = duration;
        FramesPerSecond = framesPerSecond;

        decimal exactFrameCount =
            (decimal)duration.Ticks * framesPerSecond / TimeSpan.TicksPerSecond;
        FrameCount = duration == TimeSpan.Zero
            ? 1
            : checked((int)Math.Ceiling(exactFrameCount));
    }

    public TimeSpan Duration { get; }

    public int FramesPerSecond { get; }

    public int FrameCount { get; }

    public TimeSpan GetTimestamp(int frameIndex)
    {
        if ((uint)frameIndex >= (uint)FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));

        decimal ticks =
            (decimal)TimeSpan.TicksPerSecond * frameIndex / FramesPerSecond;
        return TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>
    /// Gets the GIF delay in hundredths of a second. Quantizing cumulative frame boundaries,
    /// instead of rounding every frame independently, avoids long-term playback-speed drift.
    /// </summary>
    public int GetDelayCentiseconds(int frameIndex)
    {
        if ((uint)frameIndex >= (uint)FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));

        TimeSpan start = GetTimestamp(frameIndex);
        TimeSpan end = frameIndex + 1 < FrameCount
            ? GetTimestamp(frameIndex + 1)
            : Duration;
        int startBoundary = ToCentiseconds(start);
        int endBoundary = ToCentiseconds(end);

        // GIF's practical minimum delay is two centiseconds. This only extends a final
        // sub-20 ms partial frame; normal 15 fps scheduling alternates six and seven.
        return Math.Max(2, endBoundary - startBoundary);
    }

    private static int ToCentiseconds(TimeSpan value)
    {
        decimal exact =
            (decimal)value.Ticks * 100 / TimeSpan.TicksPerSecond;
        return checked((int)Math.Round(exact, MidpointRounding.AwayFromZero));
    }
}
