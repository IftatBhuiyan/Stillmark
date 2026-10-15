using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace WinShot.App;

internal enum HistoryThumbnailStatus
{
    Loaded,
    Missing,
    Rejected,
    Unreadable,
    Remote,
}

internal readonly record struct HistoryThumbnailResult(
    HistoryThumbnailStatus Status,
    BitmapSource? Image = null);

internal readonly record struct HistoryThumbnailDecodeSize(
    int DecodePixelWidth,
    int DecodePixelHeight);

internal readonly record struct HistoryThumbnailPathResolution(
    bool IsAccepted,
    string FullPath,
    HistoryThumbnailStatus RejectionStatus = HistoryThumbnailStatus.Rejected);

internal static class HistoryThumbnailPolicy
{
    internal const int MaxDecodeWidth = 392;
    internal const int MaxDecodeHeight = 244;
    internal const long MaxSourceFileBytes = 256L * 1024 * 1024;
    internal const long MaxSourcePixelCount = 250_000_000;
    internal const int MaxPathComponents = 64;

    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg" };

    internal static bool TryNormalizeLocalImagePath(string? candidate, out string fullPath)
    {
        HistoryThumbnailPathResolution resolution = ResolveForThumbnail(candidate);
        fullPath = resolution.IsAccepted ? resolution.FullPath : "";
        return resolution.IsAccepted;
    }

    /// <summary>
    /// Normalizes an image path for an explicit user-directed shell action. This performs syntax
    /// checks only; it deliberately does not check existence, drive type, or reparse metadata.
    /// </summary>
    internal static bool TryNormalizeImagePathForShellAction(
        string? candidate,
        out string fullPath)
    {
        fullPath = "";
        if (!TryNormalizeImagePathSyntax(candidate, out string normalized)
            || IsDeviceNamespace(normalized))
        {
            return false;
        }

        string? root = Path.GetPathRoot(normalized);
        if (string.IsNullOrEmpty(root))
            return false;
        if (IsUncPath(normalized))
        {
            if (!IsWellFormedUncRoot(root))
                return false;
        }
        else if (!IsDriveRoot(root))
        {
            return false;
        }

        fullPath = normalized;
        return true;
    }

    /// <summary>
    /// Resolves a path for automatic thumbnail loading. Remote UNC and mapped-drive paths are
    /// classified before FileInfo or the reparse-point walk can touch them.
    /// </summary>
    internal static HistoryThumbnailPathResolution ResolveForThumbnail(
        string? candidate,
        Func<string, DriveType>? getDriveType = null,
        Func<string, FileAttributes>? getAttributes = null)
    {
        if (!TryNormalizeImagePathSyntax(candidate, out string normalized))
            return new(false, "", HistoryThumbnailStatus.Rejected);

        if (IsUncPath(normalized))
        {
            return IsWellFormedUncRoot(Path.GetPathRoot(normalized))
                ? new(false, "", HistoryThumbnailStatus.Remote)
                : new(false, "", HistoryThumbnailStatus.Rejected);
        }

        string? root = Path.GetPathRoot(normalized);
        if (!IsDriveRoot(root))
            return new(false, "", HistoryThumbnailStatus.Rejected);

        try
        {
            DriveType driveType = (getDriveType ?? (static path => new DriveInfo(path).DriveType))(root!);
            if (driveType == DriveType.Network)
                return new(false, "", HistoryThumbnailStatus.Remote);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException)
        {
            return new(false, "", HistoryThumbnailStatus.Rejected);
        }

        if (!HasNoReparsePointComponents(
                normalized,
                getAttributes ?? File.GetAttributes))
        {
            return new(false, "", HistoryThumbnailStatus.Rejected);
        }

        return new(true, normalized);
    }

    private static bool TryNormalizeImagePathSyntax(
        string? candidate,
        out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(candidate)
            || !Path.IsPathFullyQualified(candidate))
        {
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(candidate);
            if (!SupportedExtensions.Contains(Path.GetExtension(normalized))
                || IsDeviceNamespace(normalized)
                || ContainsInvalidWindowsPathCharacters(normalized))
            {
                normalized = "";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            normalized = "";
            return false;
        }
    }

    private static bool ContainsInvalidWindowsPathCharacters(string path)
    {
        string? root = Path.GetPathRoot(path);
        bool hasAsciiDrivePrefix = path.Length >= 2
            && path[1] == ':'
            && IsAsciiDriveLetter(path[0]);
        for (int index = 0; index < path.Length; index++)
        {
            char character = path[index];
            if (character < ' '
                || character is '"' or '<' or '>' or '|' or '*' or '?'
                || (character == ':'
                    && !(hasAsciiDrivePrefix && index == 1)))
            {
                return true;
            }
        }

        return string.IsNullOrEmpty(root);
    }

    private static bool IsDeviceNamespace(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal)
        || path.StartsWith(@"\\.\", StringComparison.Ordinal)
        || path.StartsWith(@"\??\", StringComparison.Ordinal);

    private static bool IsUncPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal);

    private static bool IsDriveRoot(string? root) =>
        !string.IsNullOrEmpty(root)
        && root.Length == 3
        && root[1] == ':'
        && IsAsciiDriveLetter(root[0])
        && (root[2] == Path.DirectorySeparatorChar
            || root[2] == Path.AltDirectorySeparatorChar);

    private static bool IsAsciiDriveLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsWellFormedUncRoot(string? root)
    {
        if (string.IsNullOrEmpty(root) || !IsUncPath(root))
            return false;
        string[] components = root.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
        return components.Length == 2
            && components.All(component => component is not "." and not "..");
    }

    internal static bool HasNoReparsePointComponents(
        string fullPath,
        Func<string, FileAttributes> getAttributes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentNullException.ThrowIfNull(getAttributes);

        try
        {
            string normalized = Path.GetFullPath(fullPath);
            string? root = Path.GetPathRoot(normalized);
            if (string.IsNullOrEmpty(root))
                return false;

            string relative = Path.GetRelativePath(root, normalized);
            string[] components = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            if (components.Length == 0 || components.Length > MaxPathComponents)
                return false;

            string current = root;
            foreach (string component in components)
            {
                current = Path.Combine(current, component);
                FileAttributes attributes;
                try
                {
                    attributes = getAttributes(current);
                }
                catch (FileNotFoundException)
                {
                    // Once a component is absent, no deeper component can currently be traversed.
                    return true;
                }
                catch (DirectoryNotFoundException)
                {
                    return true;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static bool TryGetDecodeSize(
        int sourceWidth,
        int sourceHeight,
        out HistoryThumbnailDecodeSize size)
    {
        size = default;
        if (sourceWidth <= 0
            || sourceHeight <= 0
            || (long)sourceWidth * sourceHeight > MaxSourcePixelCount)
        {
            return false;
        }

        // Specify one WIC decode dimension so aspect ratio is preserved while both resulting
        // dimensions remain bounded by the visual thumbnail size at two device pixels per DIP.
        if ((long)sourceWidth * MaxDecodeHeight >= (long)sourceHeight * MaxDecodeWidth)
            size = new HistoryThumbnailDecodeSize(MaxDecodeWidth, 0);
        else
            size = new HistoryThumbnailDecodeSize(0, MaxDecodeHeight);
        return true;
    }
}

internal static class HistoryThumbnailLoader
{
    private const int MaximumConcurrentDecodes = 3;
    private static readonly SemaphoreSlim DecodeSlots =
        new(MaximumConcurrentDecodes, MaximumConcurrentDecodes);

    internal static async Task<HistoryThumbnailResult> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        return await LoadAsync(
            path,
            cancellationToken,
            static candidate => HistoryThumbnailPolicy.ResolveForThumbnail(candidate));
    }

    internal static async Task<HistoryThumbnailResult> LoadAsync(
        string path,
        CancellationToken cancellationToken,
        Func<string, HistoryThumbnailPathResolution> resolvePath)
    {
        ArgumentNullException.ThrowIfNull(resolvePath);
        await DecodeSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => LoadOnWorker(path, cancellationToken, resolvePath),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DecodeSlots.Release();
        }
    }

    private static HistoryThumbnailResult LoadOnWorker(
        string path,
        CancellationToken cancellationToken,
        Func<string, HistoryThumbnailPathResolution> resolvePath)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            HistoryThumbnailPathResolution resolution = resolvePath(path);
            if (!resolution.IsAccepted)
                return new HistoryThumbnailResult(resolution.RejectionStatus);

            string fullPath = resolution.FullPath;
            var file = new FileInfo(fullPath);
            if (!file.Exists)
                return new HistoryThumbnailResult(HistoryThumbnailStatus.Missing);
            if (file.Length <= 0 || file.Length > HistoryThumbnailPolicy.MaxSourceFileBytes)
                return new HistoryThumbnailResult(HistoryThumbnailStatus.Unreadable);

            using var fileStream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            using var stream = new CancellationCheckingStream(fileStream, cancellationToken);

            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            BitmapFrame frame = decoder.Frames[0];
            if (!HistoryThumbnailPolicy.TryGetDecodeSize(
                    frame.PixelWidth,
                    frame.PixelHeight,
                    out HistoryThumbnailDecodeSize decode))
            {
                return new HistoryThumbnailResult(HistoryThumbnailStatus.Unreadable);
            }

            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions =
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            image.DecodePixelWidth = decode.DecodePixelWidth;
            image.DecodePixelHeight = decode.DecodePixelHeight;
            image.EndInit();
            cancellationToken.ThrowIfCancellationRequested();
            image.Freeze();

            return new HistoryThumbnailResult(HistoryThumbnailStatus.Loaded, image);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or ArgumentException
                                   or InvalidOperationException
                                   or COMException)
        {
            return new HistoryThumbnailResult(HistoryThumbnailStatus.Unreadable);
        }
    }

    private sealed class CancellationCheckingStream : Stream
    {
        private readonly Stream _inner;
        private readonly CancellationToken _cancellationToken;

        internal CancellationCheckingStream(Stream inner, CancellationToken cancellationToken)
        {
            _inner = inner;
            _cancellationToken = cancellationToken;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _inner.Position = value;
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            return _inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            return _inner.Read(buffer);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            return _inner.Seek(offset, origin);
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
