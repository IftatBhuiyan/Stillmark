using System.IO;
using System.Windows.Media.Imaging;

namespace WinShot.App;

/// <summary>
/// A thread-safe captured image together with its reusable, alpha-preserving PNG representation.
/// The source must be frozen before the payload is created so background delivery can safely read it.
/// </summary>
public sealed class EncodedImagePayload
{
    private readonly byte[] _pngBuffer;
    private readonly int _pngLength;

    internal EncodedImagePayload(
        BitmapSource image,
        byte[] pngBuffer,
        int pngLength,
        bool takeOwnership)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(pngBuffer);
        if (!image.IsFrozen)
            throw new ArgumentException("The image must be frozen before it can enter a reusable payload.", nameof(image));
        if (pngLength < 8 || pngLength > pngBuffer.Length)
            throw new ArgumentOutOfRangeException(nameof(pngLength));
        if (!HasPngSignature(pngBuffer))
            throw new ArgumentException("The encoded bytes are not a PNG image.", nameof(pngBuffer));

        Image = image;
        _pngLength = pngLength;
        _pngBuffer = takeOwnership
            ? pngBuffer
            : pngBuffer.AsSpan(0, pngLength).ToArray();
    }

    public BitmapSource Image { get; }

    public int PngLength => _pngLength;

    public ReadOnlyMemory<byte> PngBytes => _pngBuffer.AsMemory(0, _pngLength);

    public Stream OpenPngStream() =>
        new MemoryStream(_pngBuffer, 0, _pngLength, writable: false, publiclyVisible: false);

    internal void CopyPngTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(_pngBuffer, 0, _pngLength);
    }

    internal ValueTask CopyPngToAsync(Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return destination.WriteAsync(PngBytes, cancellationToken);
    }

    private static bool HasPngSignature(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8
        && bytes[0] == 0x89
        && bytes[1] == 0x50
        && bytes[2] == 0x4E
        && bytes[3] == 0x47
        && bytes[4] == 0x0D
        && bytes[5] == 0x0A
        && bytes[6] == 0x1A
        && bytes[7] == 0x0A;
}
