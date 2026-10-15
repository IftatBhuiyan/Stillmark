using System.IO;

namespace WinShot.Capture.Gif;

/// <summary>
/// Streaming animated-GIF encoder. Frames are mapped to a shared 6×6×6 web-safe
/// color cube with 4×4 ordered (Bayer) dithering — fast, deterministic, and good
/// enough for UI/screen content — then LZW-compressed per the GIF89a spec.
/// </summary>
public sealed class GifEncoder
{
    private readonly Stream _out;
    private readonly int _width;
    private readonly int _height;
    private bool _headerWritten;

    // 4×4 Bayer matrix, values 0..15.
    private static readonly int[,] Bayer =
    {
        { 0, 8, 2, 10 },
        { 12, 4, 14, 6 },
        { 3, 11, 1, 9 },
        { 15, 7, 13, 5 },
    };

    public GifEncoder(Stream output, int width, int height)
    {
        _out = output;
        _width = width;
        _height = height;
    }

    /// <summary>Adds a frame. <paramref name="bgra"/> is top-down BGRA32; delay is in centiseconds (1/100 s).</summary>
    public void AddFrame(byte[] bgra, int stride, int delayCentiseconds)
    {
        if (!_headerWritten)
        {
            WriteHeader();
            _headerWritten = true;
        }
        WriteGraphicControl(Math.Max(2, delayCentiseconds));
        WriteImage(bgra, stride);
    }

    public void Finish()
    {
        if (!_headerWritten)
            WriteHeader(); // an empty GIF is still valid
        _out.WriteByte(0x3B); // trailer
    }

    // ---- Structure ----

    private void WriteHeader()
    {
        // "GIF89a"
        _out.Write(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, 0, 6);

        // Logical Screen Descriptor: global color table, 256 entries (8-bit), color resolution 8.
        WriteUShort(_width);
        WriteUShort(_height);
        _out.WriteByte(0xF7); // GCT flag=1, color res=111, sort=0, GCT size=111 (2^8 = 256)
        _out.WriteByte(0);    // background color index
        _out.WriteByte(0);    // pixel aspect ratio

        WriteGlobalColorTable();

        // NETSCAPE2.0 application extension → loop forever.
        _out.WriteByte(0x21);
        _out.WriteByte(0xFF);
        _out.WriteByte(0x0B);
        _out.Write(new byte[] { 0x4E, 0x45, 0x54, 0x53, 0x43, 0x41, 0x50, 0x45, 0x32, 0x2E, 0x30 }, 0, 11); // "NETSCAPE2.0"
        _out.WriteByte(0x03);
        _out.WriteByte(0x01);
        WriteUShort(0); // loop count 0 = forever
        _out.WriteByte(0x00);
    }

    private void WriteGlobalColorTable()
    {
        // 216 web-safe cube entries, then padded to 256 with fine grays.
        var table = new byte[256 * 3];
        int i = 0;
        for (int r = 0; r < 6; r++)
            for (int g = 0; g < 6; g++)
                for (int b = 0; b < 6; b++)
                {
                    table[i * 3] = (byte)(r * 51);
                    table[i * 3 + 1] = (byte)(g * 51);
                    table[i * 3 + 2] = (byte)(b * 51);
                    i++;
                }
        for (; i < 256; i++)
        {
            byte gray = (byte)Math.Min(255, (i - 216) * 255 / 39);
            table[i * 3] = gray;
            table[i * 3 + 1] = gray;
            table[i * 3 + 2] = gray;
        }
        _out.Write(table, 0, table.Length);
    }

    private void WriteGraphicControl(int delayCentiseconds)
    {
        _out.WriteByte(0x21); // extension introducer
        _out.WriteByte(0xF9); // graphic control label
        _out.WriteByte(0x04); // block size
        _out.WriteByte(0x00); // no transparency, disposal = 0
        WriteUShort(delayCentiseconds);
        _out.WriteByte(0x00); // transparent color index (unused)
        _out.WriteByte(0x00); // block terminator
    }

    private void WriteImage(byte[] bgra, int stride)
    {
        _out.WriteByte(0x2C); // image separator
        WriteUShort(0);
        WriteUShort(0);
        WriteUShort(_width);
        WriteUShort(_height);
        _out.WriteByte(0x00); // no local color table, not interlaced

        var indices = Quantize(bgra, stride);
        LzwCompress(indices);
    }

    /// <summary>Maps pixels to color-cube indices [0,215] with ordered dithering.</summary>
    private byte[] Quantize(byte[] bgra, int stride)
    {
        var indices = new byte[_width * _height];
        for (int y = 0; y < _height; y++)
        {
            int row = y * stride;
            int outRow = y * _width;
            for (int x = 0; x < _width; x++)
            {
                int p = row + x * 4;
                // Dither offset centered on 0, amplitude ≈ half a cube step (51).
                int d = (Bayer[y & 3, x & 3] - 8) * 51 / 16;
                int b = Level(bgra[p] + d);
                int g = Level(bgra[p + 1] + d);
                int r = Level(bgra[p + 2] + d);
                indices[outRow + x] = (byte)(r * 36 + g * 6 + b);
            }
        }
        return indices;
    }

    private static int Level(int channel)
    {
        int v = (channel + 25) / 51; // round to nearest of 0..5
        return v < 0 ? 0 : v > 5 ? 5 : v;
    }

    // ---- LZW (GIF variable-length, LSB-first) ----

    private void LzwCompress(byte[] indices)
    {
        const int minCodeSize = 8; // 256-color table
        _out.WriteByte(minCodeSize);

        int clearCode = 1 << minCodeSize; // 256
        int eoiCode = clearCode + 1;      // 257
        int codeSize = minCodeSize + 1;   // 9
        int nextCode = eoiCode + 1;

        var dict = new Dictionary<(int prefix, byte next), int>();
        void ResetDict()
        {
            dict.Clear();
            codeSize = minCodeSize + 1;
            nextCode = eoiCode + 1;
        }

        var packer = new BitPacker(_out);
        packer.Write(clearCode, codeSize);
        ResetDict();

        int current = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            byte k = indices[i];
            var key = (current, k);
            if (dict.TryGetValue(key, out int code))
            {
                current = code;
            }
            else
            {
                packer.Write(current, codeSize);
                dict[key] = nextCode++;
                if (nextCode > (1 << codeSize) && codeSize < 12)
                    codeSize++;
                if (nextCode >= 4096)
                {
                    packer.Write(clearCode, codeSize);
                    ResetDict();
                }
                current = k;
            }
        }
        packer.Write(current, codeSize);
        packer.Write(eoiCode, codeSize);
        packer.Flush();
    }

    private void WriteUShort(int value)
    {
        _out.WriteByte((byte)(value & 0xFF));
        _out.WriteByte((byte)((value >> 8) & 0xFF));
    }

    /// <summary>Packs variable-width codes LSB-first into GIF ≤255-byte sub-blocks.</summary>
    private sealed class BitPacker
    {
        private readonly Stream _out;
        private readonly byte[] _block = new byte[255];
        private int _blockLen;
        private int _bitBuffer;
        private int _bitCount;

        public BitPacker(Stream output) => _out = output;

        public void Write(int code, int codeSize)
        {
            _bitBuffer |= code << _bitCount;
            _bitCount += codeSize;
            while (_bitCount >= 8)
            {
                _block[_blockLen++] = (byte)(_bitBuffer & 0xFF);
                _bitBuffer >>= 8;
                _bitCount -= 8;
                if (_blockLen == 255)
                    FlushBlock();
            }
        }

        public void Flush()
        {
            if (_bitCount > 0)
            {
                _block[_blockLen++] = (byte)(_bitBuffer & 0xFF);
                _bitBuffer = 0;
                _bitCount = 0;
                if (_blockLen == 255)
                    FlushBlock();
            }
            FlushBlock();
            _out.WriteByte(0x00); // block terminator
        }

        private void FlushBlock()
        {
            if (_blockLen == 0)
                return;
            _out.WriteByte((byte)_blockLen);
            _out.Write(_block, 0, _blockLen);
            _blockLen = 0;
        }
    }
}
