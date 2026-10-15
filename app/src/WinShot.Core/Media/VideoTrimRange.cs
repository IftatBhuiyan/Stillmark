namespace WinShot.Core.Media;

/// <summary>
/// Validated absolute trim selection plus the offsets expected by MediaTranscoder.
/// </summary>
public readonly record struct VideoTrimRange(
    TimeSpan StartOffset,
    TimeSpan EndOffset,
    TimeSpan OutputDuration)
{
    public static VideoTrimRange Resolve(
        TimeSpan sourceDuration,
        TimeSpan requestedStart,
        TimeSpan requestedEnd)
    {
        if (sourceDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(sourceDuration), "Source duration must be positive.");
        if (requestedStart < TimeSpan.Zero || requestedStart >= sourceDuration)
            throw new ArgumentOutOfRangeException(
                nameof(requestedStart), "Trim start must be within the source video.");

        TimeSpan absoluteEnd = requestedEnd == TimeSpan.Zero
            ? sourceDuration
            : requestedEnd;
        if (absoluteEnd <= requestedStart || absoluteEnd > sourceDuration)
            throw new ArgumentOutOfRangeException(
                nameof(requestedEnd), "Trim end must be after the start and within the source video.");

        return new VideoTrimRange(
            requestedStart,
            sourceDuration - absoluteEnd,
            absoluteEnd - requestedStart);
    }
}
