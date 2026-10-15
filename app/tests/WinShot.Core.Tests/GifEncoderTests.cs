using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.Capture.Gif;
using Xunit;

namespace WinShot.Core.Tests;

public class GifEncoderTests
{
    private const int W = 8;
    private const int H = 6;

    // Solid colors on exact 6×6×6 cube nodes (multiples of 51) so dithering is a no-op.
    private static byte[] Solid(byte r, byte g, byte b)
    {
        var px = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            px[i * 4] = b;
            px[i * 4 + 1] = g;
            px[i * 4 + 2] = r;
            px[i * 4 + 3] = 255;
        }
        return px;
    }

    [Fact]
    public void EncodesFramesDecodableByWpfWithCorrectColors()
    {
        var colors = new (byte r, byte g, byte b)[]
        {
            (255, 0, 0), (0, 255, 0), (0, 0, 255), (51, 102, 153), (255, 255, 255), (0, 0, 0),
        };

        using var ms = new MemoryStream();
        var enc = new GifEncoder(ms, W, H);
        foreach (var c in colors)
            enc.AddFrame(Solid(c.r, c.g, c.b), W * 4, delayCentiseconds: 8);
        enc.Finish();

        ms.Position = 0;
        var decoder = new GifBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Equal(colors.Length, decoder.Frames.Count);

        for (int f = 0; f < colors.Length; f++)
        {
            var frame = decoder.Frames[f];
            Assert.Equal(W, frame.PixelWidth);
            Assert.Equal(H, frame.PixelHeight);

            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var px = new byte[W * H * 4];
            bgra.CopyPixels(px, W * 4, 0);

            var (r, g, b) = colors[f];
            // Check a handful of pixels across the frame.
            foreach (int i in new[] { 0, W * H / 2, W * H - 1 })
            {
                Assert.Equal(b, px[i * 4]);
                Assert.Equal(g, px[i * 4 + 1]);
                Assert.Equal(r, px[i * 4 + 2]);
            }
        }
    }

    [Fact]
    public void EncodesAGradientWithoutCorruptingTheStream()
    {
        // A varied frame exercises the LZW dictionary growth / clear-code path.
        var px = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                int p = (y * 64 + x) * 4;
                px[p] = (byte)(x * 4);
                px[p + 1] = (byte)(y * 4);
                px[p + 2] = (byte)((x + y) * 2);
                px[p + 3] = 255;
            }

        using var ms = new MemoryStream();
        var enc = new GifEncoder(ms, 64, 64);
        enc.AddFrame(px, 64 * 4, 8);
        enc.Finish();

        ms.Position = 0;
        var decoder = new GifBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Single(decoder.Frames);
        Assert.Equal(64, decoder.Frames[0].PixelWidth);
        Assert.Equal(64, decoder.Frames[0].PixelHeight);
    }
}
