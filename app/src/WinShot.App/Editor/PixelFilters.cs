using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinShot.App.Editor;

/// <summary>CPU pixel filters for the blur/pixelate annotations — deterministic in both preview and final render.</summary>
public static class PixelFilters
{
    public static BitmapSource Pixelate(
        BitmapSource source,
        Int32Rect region,
        int blockSize,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (byte[] pixels, int width, int height) = ExtractRegion(source, region);
        blockSize = Math.Max(2, blockSize);

        for (int by = 0; by < height; by += blockSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int bh = Math.Min(blockSize, height - by);
            for (int bx = 0; bx < width; bx += blockSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int bw = Math.Min(blockSize, width - bx);
                long b = 0, g = 0, r = 0, a = 0;
                int count = bw * bh;

                for (int y = by; y < by + bh; y++)
                {
                    int row = (y * width + bx) * 4;
                    for (int x = 0; x < bw; x++)
                    {
                        b += pixels[row];
                        g += pixels[row + 1];
                        r += pixels[row + 2];
                        a += pixels[row + 3];
                        row += 4;
                    }
                }

                byte ab = (byte)(b / count), ag = (byte)(g / count), ar = (byte)(r / count), aa = (byte)(a / count);
                for (int y = by; y < by + bh; y++)
                {
                    int row = (y * width + bx) * 4;
                    for (int x = 0; x < bw; x++)
                    {
                        pixels[row] = ab;
                        pixels[row + 1] = ag;
                        pixels[row + 2] = ar;
                        pixels[row + 3] = aa;
                        row += 4;
                    }
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ToBitmap(pixels, width, height);
    }

    public static BitmapSource BoxBlur(
        BitmapSource source,
        Int32Rect region,
        int radius,
        int passes = 3,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (byte[] pixels, int width, int height) = ExtractRegion(source, region);
        radius = Math.Max(1, radius);
        var scratch = GC.AllocateUninitializedArray<byte>(pixels.Length);

        for (int pass = 0; pass < passes; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BlurHorizontal(
                pixels,
                scratch,
                width,
                height,
                radius,
                cancellationToken);
            BlurVertical(
                scratch,
                pixels,
                width,
                height,
                radius,
                cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ToBitmap(pixels, width, height);
    }

    private static void BlurHorizontal(
        byte[] src,
        byte[] dst,
        int width,
        int height,
        int radius,
        CancellationToken cancellationToken)
    {
        int window = radius * 2 + 1;
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int rowStart = y * width * 4;
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int x = -radius; x <= radius; x++)
                    sum += src[rowStart + Math.Clamp(x, 0, width - 1) * 4 + c];

                for (int x = 0; x < width; x++)
                {
                    dst[rowStart + x * 4 + c] = (byte)(sum / window);
                    int leaving = Math.Clamp(x - radius, 0, width - 1);
                    int entering = Math.Clamp(x + radius + 1, 0, width - 1);
                    sum += src[rowStart + entering * 4 + c] - src[rowStart + leaving * 4 + c];
                }
            }
        }
    }

    private static void BlurVertical(
        byte[] src,
        byte[] dst,
        int width,
        int height,
        int radius,
        CancellationToken cancellationToken)
    {
        int window = radius * 2 + 1;
        int stride = width * 4;
        for (int x = 0; x < width; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int colStart = x * 4;
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int y = -radius; y <= radius; y++)
                    sum += src[Math.Clamp(y, 0, height - 1) * stride + colStart + c];

                for (int y = 0; y < height; y++)
                {
                    dst[y * stride + colStart + c] = (byte)(sum / window);
                    int leaving = Math.Clamp(y - radius, 0, height - 1);
                    int entering = Math.Clamp(y + radius + 1, 0, height - 1);
                    sum += src[entering * stride + colStart + c] - src[leaving * stride + colStart + c];
                }
            }
        }
    }

    private static (byte[] Pixels, int Width, int Height) ExtractRegion(BitmapSource source, Int32Rect region)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
            throw new ArgumentException("The source bitmap is empty.", nameof(source));

        region.X = Math.Clamp(region.X, 0, source.PixelWidth - 1);
        region.Y = Math.Clamp(region.Y, 0, source.PixelHeight - 1);
        region.Width = Math.Clamp(region.Width, 1, source.PixelWidth - region.X);
        region.Height = Math.Clamp(region.Height, 1, source.PixelHeight - region.Y);

        var crop = new CroppedBitmap(source, region);
        var converted = new FormatConvertedBitmap(crop, PixelFormats.Bgra32, null, 0);
        int stride = checked(region.Width * 4);
        var pixels = GC.AllocateUninitializedArray<byte>(
            checked(stride * region.Height));
        converted.CopyPixels(pixels, stride, 0);
        return (pixels, region.Width, region.Height);
    }

    private static BitmapSource ToBitmap(byte[] pixels, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
