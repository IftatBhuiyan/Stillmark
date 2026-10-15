using System.IO;
using System.Windows.Media.Imaging;

namespace WinShot.App;

/// <summary>Encodes frozen captures away from the WPF dispatcher into bounded reusable payloads.</summary>
public static class StillImageEncodingService
{
    public const int DefaultMaximumPngBytes = 512 * 1024 * 1024;

    private static readonly SemaphoreSlim EncodeLane = new(initialCount: 1, maxCount: 1);
    private static int _activeEncoderCount;
    private static int _peakEncoderCount;

    internal static int ActiveEncoderCount => Volatile.Read(ref _activeEncoderCount);
    internal static int PeakEncoderCount => Volatile.Read(ref _peakEncoderCount);

    public static Task<EncodedImagePayload> EncodePngAsync(
        BitmapSource image,
        CancellationToken cancellationToken = default,
        int maximumPngBytes = DefaultMaximumPngBytes)
    {
        ValidateInput(image, maximumPngBytes);
        if (!image.IsFrozen)
        {
            throw new ArgumentException(
                "Async PNG encoding requires a frozen BitmapSource.",
                nameof(image));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return EncodePngQueuedAsync(image, maximumPngBytes, cancellationToken);
    }

    internal static EncodedImagePayload EncodePng(
        BitmapSource image,
        int maximumPngBytes = DefaultMaximumPngBytes)
    {
        ValidateInput(image, maximumPngBytes);

        BitmapSource threadSafeImage = image;
        if (!threadSafeImage.IsFrozen && threadSafeImage.CanFreeze)
        {
            threadSafeImage = threadSafeImage.Clone();
            threadSafeImage.Freeze();
        }

        return EncodePngCore(threadSafeImage, maximumPngBytes, CancellationToken.None);
    }

    private static async Task<EncodedImagePayload> EncodePngQueuedAsync(
        BitmapSource image,
        int maximumPngBytes,
        CancellationToken cancellationToken)
    {
        await EncodeLane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                    () =>
                    {
                        int active = Interlocked.Increment(ref _activeEncoderCount);
                        UpdatePeakEncoderCount(active);
                        try
                        {
                            return EncodePngCore(image, maximumPngBytes, cancellationToken);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _activeEncoderCount);
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            EncodeLane.Release();
        }
    }

    private static void UpdatePeakEncoderCount(int active)
    {
        int observed = Volatile.Read(ref _peakEncoderCount);
        while (active > observed)
        {
            int prior = Interlocked.CompareExchange(ref _peakEncoderCount, active, observed);
            if (prior == observed)
                return;
            observed = prior;
        }
    }

    private static EncodedImagePayload EncodePngCore(
        BitmapSource image,
        int maximumPngBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new BoundedImageEncodingStream(maximumPngBytes, cancellationToken);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        try
        {
            encoder.Save(stream);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (stream.LimitExceeded)
        {
            // WIC translates managed stream-write failures into a codec exception. Restore the
            // useful bounded-payload contract for callers.
            throw new ImagePayloadTooLargeException(maximumPngBytes, exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] buffer = stream.DetachBuffer(out int length);
        return new EncodedImagePayload(image, buffer, length, takeOwnership: true);
    }

    private static void ValidateInput(BitmapSource image, int maximumPngBytes)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
            throw new ArgumentException("The image must contain at least one pixel.", nameof(image));
        if (maximumPngBytes < 8)
            throw new ArgumentOutOfRangeException(
                nameof(maximumPngBytes),
                "The PNG byte limit must be at least eight bytes.");
        if (maximumPngBytes > Array.MaxLength)
            throw new ArgumentOutOfRangeException(
                nameof(maximumPngBytes),
                $"The PNG byte limit cannot exceed {Array.MaxLength} bytes.");
    }

    private sealed class BoundedImageEncodingStream : MemoryStream
    {
        private const int InitialCapacity = 4 * 1024;

        private readonly int _maximumBytes;
        private readonly CancellationToken _cancellationToken;
        private bool _detached;

        public bool LimitExceeded { get; private set; }

        public BoundedImageEncodingStream(int maximumBytes, CancellationToken cancellationToken)
            : base(Math.Min(InitialCapacity, maximumBytes))
        {
            _maximumBytes = maximumBytes;
            _cancellationToken = cancellationToken;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (buffer.Length - offset < count)
                throw new ArgumentException("Offset and count exceed the source buffer.");

            PrepareWrite(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            PrepareWrite(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            PrepareWrite(1);
            base.WriteByte(value);
        }

        public override void SetLength(long value)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            EnsureWithinLimit(value);
            EnsureBoundedCapacity(value);
            base.SetLength(value);
        }

        public byte[] DetachBuffer(out int length)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_detached)
                throw new InvalidOperationException("The encoding buffer has already been detached.");

            length = checked((int)Length);
            _detached = true;
            return GetBuffer();
        }

        private void PrepareWrite(int count)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            long requiredLength;
            try
            {
                requiredLength = checked(Position + count);
            }
            catch (OverflowException exception)
            {
                throw new ImagePayloadTooLargeException(_maximumBytes, exception);
            }

            EnsureWithinLimit(requiredLength);
            EnsureBoundedCapacity(requiredLength);
        }

        private void EnsureWithinLimit(long requiredLength)
        {
            if (requiredLength > _maximumBytes)
            {
                LimitExceeded = true;
                throw new ImagePayloadTooLargeException(_maximumBytes);
            }
        }

        private void EnsureBoundedCapacity(long requiredLength)
        {
            if (requiredLength <= Capacity)
                return;

            int doubled = Capacity <= _maximumBytes / 2
                ? Capacity * 2
                : _maximumBytes;
            Capacity = Math.Max(checked((int)requiredLength), doubled);
        }
    }
}

public sealed class ImagePayloadTooLargeException : IOException
{
    public ImagePayloadTooLargeException(int maximumBytes)
        : base($"The encoded PNG exceeded the configured {maximumBytes}-byte limit.")
    {
        MaximumBytes = maximumBytes;
    }

    internal ImagePayloadTooLargeException(int maximumBytes, Exception innerException)
        : base($"The encoded PNG exceeded the configured {maximumBytes}-byte limit.", innerException)
    {
        MaximumBytes = maximumBytes;
    }

    public int MaximumBytes { get; }
}
