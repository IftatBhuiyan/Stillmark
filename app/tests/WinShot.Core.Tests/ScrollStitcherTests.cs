using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.Capture;
using Xunit;

namespace WinShot.Core.Tests;

public class ScrollStitcherTests
{
    private const int W = 120;
    private const int Tall = 900;
    private const int FrameH = 300;

    // 120px wide, 24 buckets -> 5px per bucket (matches ScrollStitcher's bucketing).
    private const int BucketW = 5;

    /// <summary>Collision-free per-(row, bucket) value. Avoids the luma aliasing a plain
    /// mod-256 gradient would cause, so distinct rows are always far apart in signature space.</summary>
    private static byte Cell(int y, int bucket)
    {
        uint h = (uint)(y * 2654435761u + bucket * 2246822519u + 12345u);
        h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
        return (byte)(h & 0xFF);
    }

    /// <summary>A tall source image where every row has a distinct pattern (so alignment is unambiguous).</summary>
    private static byte[] BuildSource()
    {
        var src = new byte[W * Tall * 4];
        for (int y = 0; y < Tall; y++)
            for (int x = 0; x < W; x++)
            {
                int p = (y * W + x) * 4;
                byte v = Cell(y, x / BucketW);
                src[p] = v; src[p + 1] = v; src[p + 2] = v; src[p + 3] = 255;
            }
        return src;
    }

    /// <summary>A frame = the rows [offset, offset+FrameH) of the source.</summary>
    private static BitmapSource FrameAt(byte[] src, int offset)
    {
        var frame = new byte[W * FrameH * 4];
        Buffer.BlockCopy(src, offset * W * 4, frame, 0, W * FrameH * 4);
        var bmp = BitmapSource.Create(W, FrameH, 96, 96, PixelFormats.Bgra32, null, frame, W * 4);
        bmp.Freeze();
        return bmp;
    }

    [Fact]
    public void StitchesVerticalScrollToFullHeight()
    {
        var src = BuildSource();
        var stitcher = new ScrollStitcher();

        for (int offset = 0; offset + FrameH <= Tall; offset += 40)
            stitcher.Append(FrameAt(src, offset));
        // last aligned offset is 600 (600+300=900); ensure the very bottom frame is included
        stitcher.Append(FrameAt(src, Tall - FrameH));

        Assert.Equal(ScrollAxis.Vertical, stitcher.Axis);
        Assert.Equal(W, stitcher.PixelWidth);
        Assert.InRange(stitcher.PixelHeight, Tall - 4, Tall + 4);
    }

    [Fact]
    public void NoScrollDoesNotGrow()
    {
        var src = BuildSource();
        var stitcher = new ScrollStitcher();
        stitcher.Append(FrameAt(src, 100));
        int before = stitcher.PixelHeight;
        // Same frame again — nothing new should be appended.
        stitcher.Append(FrameAt(src, 100));
        Assert.Equal(before, stitcher.PixelHeight);
    }

    [Theory]
    [InlineData(false, 20)]
    [InlineData(false, 50)]
    [InlineData(false, 150)]
    [InlineData(true, 20)]
    [InlineData(true, 50)]
    [InlineData(true, 150)]
    public void FixedTrailingControlAppearsOnlyOnceAndUnderlyingContentIsRestored(bool horizontal, int step)
    {
        byte[] source = BuildSource();
        var stitcher = new ScrollStitcher();
        for (int offset = 0; offset <= Tall - FrameH; offset += step)
        {
            var pixels = new byte[W * FrameH * 4];
            Buffer.BlockCopy(source, offset * W * 4, pixels, 0, pixels.Length);
            PaintControls(pixels, FrameH);
            Assert.True(stitcher.Append(MakeBitmap(pixels, FrameH, horizontal)));
        }

        byte[] expected = (byte[])source.Clone();
        PaintControls(expected, Tall);
        BitmapSource expectedBitmap = MakeBitmap(expected, Tall, horizontal);
        BitmapSource actual = stitcher.ToBitmap();
        Assert.Equal(expectedBitmap.PixelWidth, actual.PixelWidth);
        Assert.Equal(expectedBitmap.PixelHeight, actual.PixelHeight);
        byte[] expectedPixels = new byte[expected.Length];
        byte[] actualPixels = new byte[expected.Length];
        expectedBitmap.CopyPixels(expectedPixels, expectedBitmap.PixelWidth * 4, 0);
        actual.CopyPixels(actualPixels, actual.PixelWidth * 4, 0);
        Assert.Equal(expectedPixels, actualPixels);

        static void PaintControls(byte[] pixels, int height)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < W; x++)
                    if (y < 8 || (y >= height - 35 && y < height - 17 && x >= W - 28 && x < W - 10))
                    {
                        int p = (y * W + x) * 4;
                        pixels[p] = 255;
                        pixels[p + 1] = 0;
                        pixels[p + 2] = 30;
                    }
        }

        static BitmapSource MakeBitmap(byte[] pixels, int height, bool transpose)
        {
            byte[] result = pixels;
            if (transpose)
            {
                result = new byte[pixels.Length];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < W; x++)
                        Buffer.BlockCopy(pixels, (y * W + x) * 4, result, (x * height + y) * 4, 4);
            }
            int width = transpose ? height : W;
            var bitmap = BitmapSource.Create(width, transpose ? W : height, 96, 96,
                PixelFormats.Bgra32, null, result, width * 4);
            bitmap.Freeze();
            return bitmap;
        }
    }

    [Fact]
    public void StitchedContentMatchesSource()
    {
        var src = BuildSource();
        var stitcher = new ScrollStitcher();
        for (int offset = 0; offset + FrameH <= Tall; offset += 50)
            stitcher.Append(FrameAt(src, offset));
        stitcher.Append(FrameAt(src, Tall - FrameH));

        var result = stitcher.ToBitmap();
        int h = result.PixelHeight;
        var px = new byte[W * h * 4];
        result.CopyPixels(px, W * 4, 0);

        // Spot-check several rows against the source pattern.
        foreach (int y in new[] { 0, 150, 450, h - 1 })
        {
            byte expected = Cell(y, 0); // column 0 is bucket 0
            Assert.Equal(expected, px[y * W * 4]); // blue channel of column 0
        }
    }

    [Fact]
    public void RejectedFrameDoesNotMoveTheAlignmentAnchor()
    {
        byte[] source = BuildSource();
        var stitcher = new ScrollStitcher();
        Assert.True(stitcher.Append(FrameAt(source, 0)));
        Assert.False(stitcher.Append(FrameAt(source, 500)));
        Assert.True(stitcher.Append(FrameAt(source, 40)));
        Assert.Equal(FrameH + 40, stitcher.PixelHeight);
        byte[] result = new byte[W * stitcher.PixelHeight * 4];
        stitcher.ToBitmap().CopyPixels(result, W * 4, 0);
        Assert.Equal(source.AsSpan(0, result.Length).ToArray(), result);
    }

    [Fact]
    public void MemoryBudgetAppendsContiguousPartialResultAndStops()
    {
        var src = BuildSource();
        const int partialRows = 65;
        long budget = (long)W * (FrameH + partialRows) * 4;
        var stitcher = new ScrollStitcher(maxCanvasBytes: budget);

        Assert.True(stitcher.Append(FrameAt(src, 0)));
        Assert.True(stitcher.Append(FrameAt(src, 100)));

        Assert.True(stitcher.AtLimit);
        Assert.Equal(ScrollLimitReason.MemoryBudget, stitcher.LimitReason);
        Assert.Equal(FrameH + partialRows, stitcher.PixelHeight);
        Assert.Equal(budget, stitcher.CanvasBytes);
        Assert.False(stitcher.Append(FrameAt(src, 200)));

        var partial = stitcher.ToBitmap();
        var pixels = new byte[checked(partial.PixelWidth * partial.PixelHeight * 4)];
        partial.CopyPixels(pixels, partial.PixelWidth * 4, 0);
        Assert.Equal(Cell(FrameH, 0), pixels[FrameH * W * 4]);
        Assert.Equal(Cell(FrameH + partialRows - 1, 0), pixels[(FrameH + partialRows - 1) * W * 4]);
    }

    [Fact]
    public void FrameLargerThanBudgetIsRejectedBeforeManagedPixelAllocation()
    {
        var src = BuildSource();
        long frameBytes = (long)W * FrameH * 4;
        var stitcher = new ScrollStitcher(maxCanvasBytes: frameBytes - 1);

        Assert.False(stitcher.Append(FrameAt(src, 0)));
        Assert.True(stitcher.AtLimit);
        Assert.Equal(ScrollLimitReason.MemoryBudget, stitcher.LimitReason);
        Assert.False(stitcher.HasContent);
        Assert.Throws<InvalidOperationException>(() => stitcher.ToBitmap());
    }

    [Fact]
    public void MaximumLengthKeepsContiguousPartialResult()
    {
        var src = BuildSource();
        var stitcher = new ScrollStitcher(maxLength: FrameH + 25);

        stitcher.Append(FrameAt(src, 0));
        Assert.True(stitcher.Append(FrameAt(src, 100)));

        Assert.True(stitcher.AtLimit);
        Assert.Equal(ScrollLimitReason.MaximumLength, stitcher.LimitReason);
        Assert.Equal(FrameH + 25, stitcher.PixelHeight);

        var partial = stitcher.ToBitmap();
        var pixels = new byte[partial.PixelWidth * partial.PixelHeight * 4];
        partial.CopyPixels(pixels, partial.PixelWidth * 4, 0);
        Assert.Equal(Cell(FrameH, 0), pixels[FrameH * W * 4]);
        Assert.Equal(Cell(FrameH + 24, 0), pixels[(FrameH + 24) * W * 4]);
    }

    [Fact]
    public void PreviewIsBoundedWithoutChangingFullResult()
    {
        var src = BuildSource();
        var stitcher = new ScrollStitcher();
        stitcher.Append(FrameAt(src, 0));
        stitcher.Append(FrameAt(src, 100));

        var preview = stitcher.ToPreviewBitmap(60, 120);

        Assert.InRange(preview.PixelWidth, 1, 60);
        Assert.InRange(preview.PixelHeight, 1, 120);
        Assert.Equal(W, stitcher.PixelWidth);
        Assert.Equal(400, stitcher.PixelHeight);
    }

    [Fact]
    public void StitchesHorizontalScrollAndPreservesRightEdge()
    {
        const int sourceWidth = 900;
        const int frameWidth = 300;
        const int height = 120;
        var source = new byte[sourceWidth * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < sourceWidth; x++)
            {
                int pixel = (y * sourceWidth + x) * 4;
                byte value = Cell(x, y / BucketW);
                source[pixel] = value;
                source[pixel + 1] = value;
                source[pixel + 2] = value;
                source[pixel + 3] = 255;
            }
        }

        BitmapSource FrameAtX(int offset)
        {
            var frame = new byte[frameWidth * height * 4];
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(
                    source,
                    (y * sourceWidth + offset) * 4,
                    frame,
                    y * frameWidth * 4,
                    frameWidth * 4);
            }

            var bitmap = BitmapSource.Create(
                frameWidth,
                height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                frame,
                frameWidth * 4);
            bitmap.Freeze();
            return bitmap;
        }

        var stitcher = new ScrollStitcher();
        for (int offset = 0; offset + frameWidth <= sourceWidth; offset += 50)
            stitcher.Append(FrameAtX(offset));
        stitcher.Append(FrameAtX(sourceWidth - frameWidth));

        Assert.Equal(ScrollAxis.Horizontal, stitcher.Axis);
        Assert.InRange(stitcher.PixelWidth, sourceWidth - 4, sourceWidth + 4);
        Assert.Equal(height, stitcher.PixelHeight);

        var result = stitcher.ToBitmap();
        var resultPixels = new byte[result.PixelWidth * result.PixelHeight * 4];
        result.CopyPixels(resultPixels, result.PixelWidth * 4, 0);
        Assert.Equal(Cell(result.PixelWidth - 1, 0), resultPixels[(result.PixelWidth - 1) * 4]);
    }
}
