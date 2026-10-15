namespace WinShot.Capture;

public readonly record struct OcrImageSize(
    int Width,
    int Height,
    double Scale,
    long BgraBytes);

/// <summary>
/// Computes one uniform OCR downscale from both the Windows OCR dimension limit and an explicit
/// BGRA working-buffer budget before any pixel array is allocated.
/// </summary>
public static class OcrImageSizing
{
    public const long DefaultMaxBgraBytes = 64L * 1024 * 1024;
    private const int BytesPerPixel = 4;

    public static OcrImageSize Calculate(
        int width,
        int height,
        int maxDimension,
        long maxBgraBytes = DefaultMaxBgraBytes)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (maxDimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDimension));
        if (maxBgraBytes < BytesPerPixel)
            throw new ArgumentOutOfRangeException(nameof(maxBgraBytes));

        long pixelCount = checked((long)width * height);
        long maxPixels = maxBgraBytes / BytesPerPixel;
        double dimensionScale = Math.Min(1, (double)maxDimension / Math.Max(width, height));
        double pixelScale = pixelCount <= maxPixels
            ? 1
            : Math.Sqrt((double)maxPixels / pixelCount);
        double scale = Math.Min(dimensionScale, pixelScale);

        int targetWidth;
        int targetHeight;
        if (dimensionScale <= pixelScale && dimensionScale < 1)
        {
            if (width >= height)
            {
                targetWidth = maxDimension;
                targetHeight = Math.Max(1, (int)((long)height * maxDimension / width));
            }
            else
            {
                targetHeight = maxDimension;
                targetWidth = Math.Max(1, (int)((long)width * maxDimension / height));
            }
        }
        else
        {
            targetWidth = Math.Max(1, (int)Math.Floor(width * scale));
            targetHeight = Math.Max(1, (int)Math.Floor(height * scale));
        }

        scale = Math.Min((double)targetWidth / width, (double)targetHeight / height);
        long targetBytes = checked((long)targetWidth * targetHeight * BytesPerPixel);

        return new OcrImageSize(targetWidth, targetHeight, scale, targetBytes);
    }
}
