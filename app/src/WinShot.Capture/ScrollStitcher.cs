using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinShot.Capture;

public enum ScrollAxis
{
    Unknown,
    Vertical,
    Horizontal,
}

public enum ScrollLimitReason
{
    None,
    MaximumLength,
    MemoryBudget,
}

/// <summary>
/// Stitches a sequence of live captures of a scrolling region into one long image.
/// Each new frame is aligned to the previous one by comparing row (or column)
/// signatures; the trailing overlap is refreshed and newly revealed pixels appended. Heuristic by nature —
/// works best when the region is captured frequently so scroll steps stay small.
/// </summary>
public sealed class ScrollStitcher
{
    private const int Buckets = 24;         // horizontal buckets per row signature
    private const int RowMatchTolerance = 14; // avg per-bucket abs diff to count a row as "matching"
    private const double MinOverlapFraction = 0.35;
    private const double MinMatchFraction = 0.7;
    private const double NoScrollMargin = 0.05; // best d must beat the no-scroll baseline by this much
    private const int MaxAlignmentSamples = 64;
    public const long DefaultMaxCanvasBytes = 128L * 1024 * 1024;

    private readonly int _maxLength;
    private readonly long _maxCanvasBytes;

    private byte[]? _firstFrame;
    private readonly List<byte[]> _strips = [];
    private readonly List<int> _stripLengths = [];
    private int _length;       // current stitched height (vertical) or width (horizontal)
    private int _frameW, _frameH;
    private byte[]? _prevFrame;
    private byte[][]? _prevRowSig;
    private byte[][]? _prevColSig;

    public ScrollAxis Axis { get; private set; } = ScrollAxis.Unknown;
    public bool AtLimit { get; private set; }
    public ScrollLimitReason LimitReason { get; private set; }
    public bool HasContent => _firstFrame is not null;

    public int PixelWidth => Axis == ScrollAxis.Horizontal ? _length : _frameW;
    public int PixelHeight => Axis == ScrollAxis.Horizontal ? _frameH : _length;
    public long CanvasBytes => HasContent ? checked((long)PixelWidth * PixelHeight * 4) : 0;
    public long MaxCanvasBytes => _maxCanvasBytes;

    public ScrollStitcher(int maxLength = 20000, long maxCanvasBytes = DefaultMaxCanvasBytes)
    {
        if (maxLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLength));
        if (maxCanvasBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCanvasBytes));

        _maxLength = maxLength;
        _maxCanvasBytes = maxCanvasBytes;
    }

    private void SetLimit(ScrollLimitReason reason)
    {
        AtLimit = true;
        LimitReason = reason;
    }

    /// <summary>Feeds a captured frame. Returns true if new content was appended.</summary>
    public bool Append(BitmapSource frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var converted = frame.Format == PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        long frameBytes = checked((long)w * h * 4);
        if (frameBytes > _maxCanvasBytes || frameBytes > Array.MaxLength)
        {
            SetLimit(ScrollLimitReason.MemoryBudget);
            return false;
        }

        if (_firstFrame is not null && (w != _frameW || h != _frameH || AtLimit))
            return false;

        var pixels = new byte[(int)frameBytes];
        converted.CopyPixels(pixels, checked(w * 4), 0);

        if (_firstFrame is null)
        {
            InitFrom(pixels, w, h);
            return true;
        }

        bool appended = Axis switch
        {
            ScrollAxis.Horizontal => TryAppendHorizontal(pixels),
            ScrollAxis.Vertical => TryAppendVertical(pixels),
            _ => TryDetectAndAppend(pixels),
        };
        // A rejected (for example, mid-animation) frame is not the canvas's last
        // viewport. Keep alignment anchored to the last frame actually stitched.
        if (appended)
        {
            _prevFrame = pixels;
            _prevRowSig = null;
            _prevColSig = null;
        }
        return appended;
    }

    private void InitFrom(byte[] pixels, int w, int h)
    {
        _frameW = w;
        _frameH = h;
        _prevFrame = pixels;
        // Canvas starts as the first frame; grows along the scroll axis.
        _length = h; // provisional (vertical); reset when axis is known
        // The canvas overlap can be refreshed; alignment must retain the original frame.
        _firstFrame = (byte[])pixels.Clone();
    }

    private bool TryDetectAndAppend(byte[] cur)
    {
        int vy = DetectVertical(_prevFrame!, cur);
        int hx = DetectHorizontal(_prevFrame!, cur);
        if (vy > 0 && vy >= hx)
        {
            Axis = ScrollAxis.Vertical;
            _length = _frameH;
            AppendVerticalRows(cur, vy);
            return true;
        }
        if (hx > 0)
        {
            Axis = ScrollAxis.Horizontal;
            // Re-lay the canvas as horizontal: first frame becomes the left strip.
            _length = _frameW;
            AppendHorizontalCols(cur, hx);
            return true;
        }
        return false;
    }

    private bool TryAppendVertical(byte[] cur)
    {
        int d = DetectVertical(_prevFrame!, cur);
        if (d <= 0)
            return false;
        AppendVerticalRows(cur, d);
        return true;
    }

    private bool TryAppendHorizontal(byte[] cur)
    {
        int d = DetectHorizontal(_prevFrame!, cur);
        if (d <= 0)
            return false;
        AppendHorizontalCols(cur, d);
        return true;
    }

    // ---- Vertical ----

    private byte[][] RowSignatures(byte[] frame)
    {
        var sig = new byte[_frameH][];
        int bucketW = Math.Max(1, _frameW / Buckets);
        for (int y = 0; y < _frameH; y++)
        {
            var s = new byte[Buckets];
            int rowStart = y * _frameW * 4;
            for (int b = 0; b < Buckets; b++)
            {
                int x0 = b * bucketW, x1 = Math.Min(_frameW, x0 + bucketW);
                long sum = 0;
                for (int x = x0; x < x1; x++)
                {
                    int p = rowStart + x * 4;
                    sum += (frame[p] * 299 + frame[p + 1] * 587 + frame[p + 2] * 114) / 1000; // luma
                }
                s[b] = (byte)(sum / Math.Max(1, x1 - x0));
            }
            sig[y] = s;
        }
        return sig;
    }

    private int DetectVertical(byte[] prev, byte[] cur)
    {
        var prevSig = _prevRowSig ??= RowSignatures(prev);
        var curSig = RowSignatures(cur);
        _prevRowSig = prevSig;

        int minOverlap = (int)(_frameH * MinOverlapFraction);

        // Baseline: how well do the frames align with NO scroll (d = 0)? If they already
        // align well, nothing scrolled — any d > 0 that "matches" is spurious (repetitive
        // rows, e.g. code with blank lines). A real scroll aligns poorly at d = 0.
        int zeroMatches = 0;
        for (int y = 0; y < _frameH; y++)
            if (RowsMatch(prevSig[y], curSig[y]))
                zeroMatches++;
        double zeroFrac = (double)zeroMatches / _frameH;

        int bestD = 0;
        double bestSampledFrac = 0;
        // Scrolled down by d: prev[y+d] ~ cur[y], new content = cur bottom d rows.
        for (int d = 1; d <= _frameH - minOverlap; d++)
        {
            int overlap = _frameH - d;
            double frac = MatchFraction(prevSig, curSig, d, overlap, MaxAlignmentSamples);
            if (frac > bestSampledFrac)
            {
                bestSampledFrac = frac;
                bestD = d;
            }
        }

        double bestFrac = bestD > 0
            ? MatchFraction(prevSig, curSig, bestD, _frameH - bestD, int.MaxValue)
            : 0;
        // Require a clean alignment that clearly beats the no-scroll baseline.
        if (bestD > 0 && bestFrac >= MinMatchFraction && bestFrac > zeroFrac + NoScrollMargin)
            return bestD;
        return 0;
    }

    private static bool RowsMatch(byte[] a, byte[] b)
    {
        int sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += Math.Abs(a[i] - b[i]);
        return sum / a.Length <= RowMatchTolerance;
    }

    private void AppendVerticalRows(byte[] cur, int d)
    {
        int appendedRows = GetAllowedAppendLength(d, checked(_frameW * 4));
        if (appendedRows <= 0)
            return;

        RefreshTrailingOverlap(cur, d, horizontal: false);
        int stride = _frameW * 4;
        var strip = new byte[checked(appendedRows * stride)];
        // Start at the beginning of the newly revealed area. If the budget accepts only part of
        // this displacement, retain the contiguous prefix rather than skipping to the frame end.
        Buffer.BlockCopy(cur, (_frameH - d) * stride, strip, 0, strip.Length);
        _strips.Add(strip);
        _stripLengths.Add(appendedRows);
        _length += appendedRows;
        if (_length >= _maxLength)
            SetLimit(ScrollLimitReason.MaximumLength);
        else if (CanvasBytes >= _maxCanvasBytes)
            SetLimit(ScrollLimitReason.MemoryBudget);
    }

    // Join inside the overlap instead of at the viewport edge. The next frame exposes
    // real document pixels behind trailing floating controls, so replace that old tail.
    // Leading headers stay in the first frame and trailing controls stay in the last.
    // This cannot reconstruct content that is never exposed in any captured frame.
    private void RefreshTrailingOverlap(byte[] current, int displacement, bool horizontal)
    {
        int extent = horizontal ? _frameW : _frameH;
        int seam = (extent - displacement) / 2;
        int currentOrigin = _length - extent + displacement;
        int replaceStart = currentOrigin + seam;
        int segmentStart = 0;
        for (int segment = -1; segment < _strips.Count; segment++)
        {
            byte[] destination = segment < 0 ? _firstFrame! : _strips[segment];
            int segmentLength = segment < 0 ? extent : _stripLengths[segment];
            int start = Math.Max(replaceStart, segmentStart);
            int end = Math.Min(_length, segmentStart + segmentLength);
            if (start < end)
            {
                int sourceUnit = start - currentOrigin;
                int targetUnit = start - segmentStart;
                if (horizontal)
                {
                    for (int y = 0; y < _frameH; y++)
                        Buffer.BlockCopy(current, (y * _frameW + sourceUnit) * 4,
                            destination, (y * segmentLength + targetUnit) * 4, (end - start) * 4);
                }
                else
                {
                    int stride = _frameW * 4;
                    Buffer.BlockCopy(current, sourceUnit * stride,
                        destination, targetUnit * stride, (end - start) * stride);
                }
            }
            segmentStart += segmentLength;
        }
    }

    // ---- Horizontal ----

    private byte[][] ColSignatures(byte[] frame)
    {
        var sig = new byte[_frameW][];
        int bucketH = Math.Max(1, _frameH / Buckets);
        int stride = _frameW * 4;
        for (int x = 0; x < _frameW; x++)
        {
            var s = new byte[Buckets];
            for (int b = 0; b < Buckets; b++)
            {
                int y0 = b * bucketH, y1 = Math.Min(_frameH, y0 + bucketH);
                long sum = 0;
                for (int y = y0; y < y1; y++)
                {
                    int p = y * stride + x * 4;
                    sum += (frame[p] * 299 + frame[p + 1] * 587 + frame[p + 2] * 114) / 1000;
                }
                s[b] = (byte)(sum / Math.Max(1, y1 - y0));
            }
            sig[x] = s;
        }
        return sig;
    }

    private int DetectHorizontal(byte[] prev, byte[] cur)
    {
        var prevSig = _prevColSig ??= ColSignatures(prev);
        var curSig = ColSignatures(cur);
        _prevColSig = prevSig;

        int minOverlap = (int)(_frameW * MinOverlapFraction);

        int zeroMatches = 0;
        for (int x = 0; x < _frameW; x++)
            if (RowsMatch(prevSig[x], curSig[x]))
                zeroMatches++;
        double zeroFrac = (double)zeroMatches / _frameW;

        int bestD = 0;
        double bestSampledFrac = 0;
        for (int d = 1; d <= _frameW - minOverlap; d++)
        {
            int overlap = _frameW - d;
            double frac = MatchFraction(prevSig, curSig, d, overlap, MaxAlignmentSamples);
            if (frac > bestSampledFrac)
            {
                bestSampledFrac = frac;
                bestD = d;
            }
        }

        double bestFrac = bestD > 0
            ? MatchFraction(prevSig, curSig, bestD, _frameW - bestD, int.MaxValue)
            : 0;
        if (bestD > 0 && bestFrac >= MinMatchFraction && bestFrac > zeroFrac + NoScrollMargin)
            return bestD;
        return 0;
    }

    private static double MatchFraction(
        byte[][] previous,
        byte[][] current,
        int displacement,
        int overlap,
        int maxSamples)
    {
        int sampleCount = Math.Min(overlap, maxSamples);
        if (sampleCount <= 0)
            return 0;

        int matches = 0;
        for (int sample = 0; sample < sampleCount; sample++)
        {
            int offset = sampleCount == 1
                ? 0
                : (int)((long)sample * (overlap - 1) / (sampleCount - 1));
            if (RowsMatch(previous[offset + displacement], current[offset]))
                matches++;
        }

        return (double)matches / sampleCount;
    }

    private void AppendHorizontalCols(byte[] cur, int d)
    {
        int bytesPerColumn = checked(_frameH * 4);
        int appendedColumns = GetAllowedAppendLength(d, bytesPerColumn);
        if (appendedColumns <= 0)
            return;

        RefreshTrailingOverlap(cur, d, horizontal: true);
        int stripStride = checked(appendedColumns * 4);
        var strip = new byte[checked(stripStride * _frameH)];
        int curStride = _frameW * 4;
        for (int y = 0; y < _frameH; y++)
        {
            Buffer.BlockCopy(
                cur,
                y * curStride + (_frameW - d) * 4,
                strip,
                y * stripStride,
                stripStride);
        }

        _strips.Add(strip);
        _stripLengths.Add(appendedColumns);
        _length += appendedColumns;
        if (_length >= _maxLength)
            SetLimit(ScrollLimitReason.MaximumLength);
        else if (CanvasBytes >= _maxCanvasBytes)
            SetLimit(ScrollLimitReason.MemoryBudget);
    }

    /// <summary>
    /// Creates a bounded nearest-neighbor preview directly from strip storage. This avoids
    /// materializing the full-resolution result during a live capture.
    /// </summary>
    public BitmapSource ToPreviewBitmap(int maxPixelWidth, int maxPixelHeight)
    {
        if (_firstFrame is null)
            throw new InvalidOperationException("No scrolling-capture frame is available.");
        if (maxPixelWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPixelWidth));
        if (maxPixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPixelHeight));

        int sourceWidth = PixelWidth;
        int sourceHeight = PixelHeight;
        double scale = Math.Min(
            1,
            Math.Min((double)maxPixelWidth / sourceWidth, (double)maxPixelHeight / sourceHeight));
        int previewWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        int previewHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        var preview = new byte[checked(previewWidth * previewHeight * 4)];

        if (Axis == ScrollAxis.Horizontal)
            ComposeHorizontalPreview(preview, previewWidth, previewHeight, sourceWidth, sourceHeight);
        else
            ComposeVerticalPreview(preview, previewWidth, previewHeight, sourceWidth, sourceHeight);

        var bitmap = BitmapSource.Create(
            previewWidth,
            previewHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            preview,
            checked(previewWidth * 4));
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The stitched result so far.</summary>
    public BitmapSource ToBitmap()
    {
        if (_firstFrame is null)
            throw new InvalidOperationException("No scrolling-capture frame is available.");

        int w = PixelWidth, h = PixelHeight;
        long byteCount = checked((long)w * h * 4);
        if (byteCount > _maxCanvasBytes || byteCount > Array.MaxLength)
            throw new InvalidOperationException("The scrolling-capture result exceeds its memory budget.");

        var pixels = new byte[(int)byteCount];
        if (Axis == ScrollAxis.Horizontal)
            ComposeHorizontal(pixels, w);
        else
            ComposeVertical(pixels);

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, checked(w * 4));
        bmp.Freeze();
        return bmp;
    }

    private int GetAllowedAppendLength(int requestedLength, int bytesPerUnit)
    {
        int lengthAllowance = Math.Max(0, _maxLength - _length);
        long availableBytes = Math.Max(0, _maxCanvasBytes - CanvasBytes);
        int memoryAllowance = (int)Math.Min(int.MaxValue, availableBytes / bytesPerUnit);
        int allowed = Math.Min(requestedLength, Math.Min(lengthAllowance, memoryAllowance));

        if (allowed < requestedLength)
        {
            SetLimit(memoryAllowance <= lengthAllowance
                ? ScrollLimitReason.MemoryBudget
                : ScrollLimitReason.MaximumLength);
        }

        return allowed;
    }

    private void ComposeVertical(byte[] destination)
    {
        Buffer.BlockCopy(_firstFrame!, 0, destination, 0, _firstFrame!.Length);
        int offset = _firstFrame.Length;
        foreach (byte[] strip in _strips)
        {
            Buffer.BlockCopy(strip, 0, destination, offset, strip.Length);
            offset += strip.Length;
        }
    }

    private void ComposeHorizontal(byte[] destination, int outputWidth)
    {
        int outputStride = checked(outputWidth * 4);
        int firstStride = checked(_frameW * 4);

        for (int y = 0; y < _frameH; y++)
        {
            int destinationOffset = y * outputStride;
            Buffer.BlockCopy(_firstFrame!, y * firstStride, destination, destinationOffset, firstStride);
            destinationOffset += firstStride;

            for (int index = 0; index < _strips.Count; index++)
            {
                int stripStride = checked(_stripLengths[index] * 4);
                Buffer.BlockCopy(
                    _strips[index],
                    y * stripStride,
                    destination,
                    destinationOffset,
                    stripStride);
                destinationOffset += stripStride;
            }
        }
    }

    private void ComposeVerticalPreview(
        byte[] destination,
        int previewWidth,
        int previewHeight,
        int sourceWidth,
        int sourceHeight)
    {
        int destinationStride = previewWidth * 4;
        int sourceStride = _frameW * 4;

        for (int y = 0; y < previewHeight; y++)
        {
            int sourceY = ScaleCoordinate(y, previewHeight, sourceHeight);
            byte[] source = _firstFrame!;
            int localY = sourceY;
            if (sourceY >= _frameH)
            {
                int remaining = sourceY - _frameH;
                for (int index = 0; index < _strips.Count; index++)
                {
                    int stripLength = _stripLengths[index];
                    if (remaining < stripLength)
                    {
                        source = _strips[index];
                        localY = remaining;
                        break;
                    }

                    remaining -= stripLength;
                }
            }

            int sourceRow = localY * sourceStride;
            int destinationRow = y * destinationStride;
            for (int x = 0; x < previewWidth; x++)
            {
                int sourceX = ScaleCoordinate(x, previewWidth, sourceWidth);
                int sourcePixel = sourceRow + sourceX * 4;
                int destinationPixel = destinationRow + x * 4;
                destination[destinationPixel] = source[sourcePixel];
                destination[destinationPixel + 1] = source[sourcePixel + 1];
                destination[destinationPixel + 2] = source[sourcePixel + 2];
                destination[destinationPixel + 3] = source[sourcePixel + 3];
            }
        }
    }

    private void ComposeHorizontalPreview(
        byte[] destination,
        int previewWidth,
        int previewHeight,
        int sourceWidth,
        int sourceHeight)
    {
        var columnSources = new byte[previewWidth][];
        var columnOffsets = new int[previewWidth];
        var columnStrides = new int[previewWidth];

        for (int x = 0; x < previewWidth; x++)
        {
            int sourceX = ScaleCoordinate(x, previewWidth, sourceWidth);
            if (sourceX < _frameW)
            {
                columnSources[x] = _firstFrame!;
                columnOffsets[x] = sourceX * 4;
                columnStrides[x] = _frameW * 4;
                continue;
            }

            int remaining = sourceX - _frameW;
            for (int index = 0; index < _strips.Count; index++)
            {
                int stripLength = _stripLengths[index];
                if (remaining < stripLength)
                {
                    columnSources[x] = _strips[index];
                    columnOffsets[x] = remaining * 4;
                    columnStrides[x] = stripLength * 4;
                    break;
                }

                remaining -= stripLength;
            }
        }

        int destinationStride = previewWidth * 4;
        for (int y = 0; y < previewHeight; y++)
        {
            int sourceY = ScaleCoordinate(y, previewHeight, sourceHeight);
            int destinationRow = y * destinationStride;
            for (int x = 0; x < previewWidth; x++)
            {
                byte[] source = columnSources[x];
                int sourcePixel = sourceY * columnStrides[x] + columnOffsets[x];
                int destinationPixel = destinationRow + x * 4;
                destination[destinationPixel] = source[sourcePixel];
                destination[destinationPixel + 1] = source[sourcePixel + 1];
                destination[destinationPixel + 2] = source[sourcePixel + 2];
                destination[destinationPixel + 3] = source[sourcePixel + 3];
            }
        }
    }

    private static int ScaleCoordinate(int coordinate, int targetLength, int sourceLength)
    {
        return targetLength <= 1
            ? 0
            : (int)((long)coordinate * (sourceLength - 1) / (targetLength - 1));
    }
}
