namespace WinShot.Capture;

/// <summary>
/// Performs allocation-free validation of a scrolling-capture frame against the managed output
/// budget. The stitcher repeats this check at its allocation boundary; the controller uses it
/// before invoking GDI so an oversized initial region fails without allocating a capture bitmap.
/// </summary>
public static class ScrollCaptureBudget
{
    public const int BytesPerPixel = 4;

    public static bool CanStart(
        int width,
        int height,
        long maxCanvasBytes,
        out long pixelCount,
        out long byteCount)
    {
        pixelCount = 0;
        byteCount = 0;
        if (width <= 0 || height <= 0 || maxCanvasBytes <= 0)
            return false;

        try
        {
            pixelCount = checked((long)width * height);
            byteCount = checked(pixelCount * BytesPerPixel);
            return byteCount <= maxCanvasBytes && byteCount <= Array.MaxLength;
        }
        catch (OverflowException)
        {
            pixelCount = 0;
            byteCount = 0;
            return false;
        }
    }
}
