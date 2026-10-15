using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.Core.Naming;
using WinShot.Core.Settings;

namespace WinShot.App;

public static class ImageSaveService
{
    private const int MaximumNameAttempts = 1_000;
    private const int MaximumStagingAttempts = 10;

    internal delegate ValueTask PngStagingWriter(
        Stream destination,
        CancellationToken cancellationToken);

    /// <summary>
    /// Owns one completely written same-directory staging file. Commit performs only the final
    /// namespace operation; disposing an uncommitted write removes only WinShot's staging file.
    /// </summary>
    public sealed class PreparedImageWrite : IDisposable
    {
        private readonly object _sync = new();
        private readonly string _directory;
        private readonly string? _baseName;
        private readonly string _extension;
        private readonly string? _exactPath;
        private readonly bool _replaceExisting;
        private string? _stagingPath;
        private bool _committed;

        internal PreparedImageWrite(
            string stagingPath,
            string directory,
            string baseName,
            string extension)
        {
            _stagingPath = stagingPath;
            _directory = directory;
            _baseName = baseName;
            _extension = extension;
        }

        internal PreparedImageWrite(
            string stagingPath,
            string exactPath,
            bool replaceExisting)
        {
            _stagingPath = stagingPath;
            _directory = Path.GetDirectoryName(exactPath)!;
            _baseName = null;
            _extension = Path.GetExtension(exactPath);
            _exactPath = exactPath;
            _replaceExisting = replaceExisting;
        }

        public string Commit()
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_stagingPath is null && !_committed, this);
                if (_committed)
                    throw new InvalidOperationException("The prepared image write was already committed.");

                string stagingPath = _stagingPath!;
                string path = _exactPath is null
                    ? CommitUnique(stagingPath, _directory, _baseName!, _extension)
                    : CommitExact(stagingPath, _exactPath, _replaceExisting);
                _stagingPath = null;
                _committed = true;
                return path;
            }
        }

        public void Dispose()
        {
            string? stagingPath;
            lock (_sync)
            {
                stagingPath = _stagingPath;
                _stagingPath = null;
            }

            if (stagingPath is not null)
                DeleteOwnedStagingFile(stagingPath);
        }
    }

    /// <summary>Saves the image per settings (folder, name template, format) and returns the file path.</summary>
    public static string Save(BitmapSource image, AppSettings settings, DateTime timestamp)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(settings);
        AppSettings settingsSnapshot = settings.Clone();
        return SaveBitmapCore(image, settingsSnapshot, timestamp, CancellationToken.None);
    }

    private static string SaveBitmapCore(
        BitmapSource image,
        AppSettings settings,
        DateTime timestamp,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(settings.SaveDirectory);
        string baseName = FileNameTemplate.Expand(settings.FileNameTemplate, timestamp);

        BitmapEncoder encoder;
        string extension;
        if (settings.ImageFormat == ImageFormatKind.Jpg)
        {
            encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(settings.JpgQuality, 1, 100) };
            extension = ".jpg";
            if (image.Format == PixelFormats.Pbgra32 || image.Format == PixelFormats.Bgra32)
                image = ImageBeautifier.FlattenToWhite(image); // JPEG has no alpha; avoid black halos
        }
        else
        {
            encoder = new PngBitmapEncoder();
            extension = ".png";
        }

        encoder.Frames.Add(BitmapFrame.Create(image));
        (string stagingPath, FileStream stagingStream) =
            CreateOwnedStagingFile(settings.SaveDirectory, useAsync: false);
        bool committed = false;

        try
        {
            using (stagingStream)
            {
                encoder.Save(stagingStream);
                cancellationToken.ThrowIfCancellationRequested();
                stagingStream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            string path = CommitUnique(stagingPath, settings.SaveDirectory, baseName, extension);
            committed = true;
            return path;
        }
        finally
        {
            if (!committed)
                DeleteOwnedStagingFile(stagingPath);
        }
    }

    /// <summary>
    /// Saves a reusable payload without re-encoding when PNG is selected. JPEG retains the
    /// established flattening and quality behavior because it necessarily uses a different codec.
    /// </summary>
    public static string Save(
        EncodedImagePayload payload,
        AppSettings settings,
        DateTime timestamp)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(settings);
        AppSettings settingsSnapshot = settings.Clone();

        if (settingsSnapshot.ImageFormat == ImageFormatKind.Jpg)
            return SaveBitmapCore(
                payload.Image,
                settingsSnapshot,
                timestamp,
                CancellationToken.None);

        Directory.CreateDirectory(settingsSnapshot.SaveDirectory);
        string baseName = FileNameTemplate.Expand(settingsSnapshot.FileNameTemplate, timestamp);
        return SavePngCopyCoreAsync(
                payload,
                settingsSnapshot.SaveDirectory,
                baseName,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Persists a reusable payload away from the caller's dispatcher. PNG bytes are staged in the
    /// destination directory and atomically renamed; cancellation removes only that owned staging
    /// file. JPEG encoding runs on a worker while preserving the existing public save behavior.
    /// </summary>
    public static Task<string> SaveAsync(
        EncodedImagePayload payload,
        AppSettings settings,
        DateTime timestamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        AppSettings settingsSnapshot = settings.Clone();

        if (settingsSnapshot.ImageFormat == ImageFormatKind.Jpg)
        {
            return Task.Run(
                () => SaveBitmapCore(
                    payload.Image,
                    settingsSnapshot,
                    timestamp,
                    cancellationToken),
                cancellationToken);
        }

        string baseName = FileNameTemplate.Expand(settingsSnapshot.FileNameTemplate, timestamp);
        return QueuePngCopyAsync(
            payload,
            settingsSnapshot.SaveDirectory,
            baseName,
            cancellationToken);
    }

    /// <summary>
    /// Fully stages a settings-based save without publishing a final path. The caller must commit
    /// only while its source revision and lifetime still own delivery.
    /// </summary>
    public static Task<PreparedImageWrite> PrepareAsync(
        EncodedImagePayload payload,
        AppSettings settings,
        DateTime timestamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        AppSettings settingsSnapshot = settings.Clone();
        string baseName = FileNameTemplate.Expand(
            settingsSnapshot.FileNameTemplate,
            timestamp);

        if (settingsSnapshot.ImageFormat == ImageFormatKind.Jpg)
        {
            return Task.Run(
                () => PrepareBitmapCore(
                    payload.Image,
                    settingsSnapshot.SaveDirectory,
                    baseName,
                    ".jpg",
                    Math.Clamp(settingsSnapshot.JpgQuality, 1, 100),
                    exactPath: null,
                    replaceExisting: false,
                    cancellationToken),
                cancellationToken);
        }

        return QueuePreparePngCopyAsync(
            payload,
            settingsSnapshot.SaveDirectory,
            baseName,
            exactPath: null,
            replaceExisting: false,
            payload.CopyPngToAsync,
            cancellationToken);
    }

    /// <summary>
    /// Stages PNG bytes beside an exact Save As destination. A new destination is create-only;
    /// an existing destination is replaced atomically only after the caller commits.
    /// </summary>
    public static Task<PreparedImageWrite> PrepareExactPngAsync(
        EncodedImagePayload payload,
        string destinationPath,
        bool replaceExisting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return PrepareExactPngAsync(
            payload,
            destinationPath,
            replaceExisting,
            payload.CopyPngToAsync,
            cancellationToken);
    }

    internal static Task<PreparedImageWrite> PrepareExactPngAsync(
        EncodedImagePayload payload,
        string destinationPath,
        bool replaceExisting,
        PngStagingWriter stagingWriter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(stagingWriter);
        string exactPath = ValidateExactDestination(destinationPath, ".png");
        cancellationToken.ThrowIfCancellationRequested();
        return QueuePreparePngCopyAsync(
            payload,
            Path.GetDirectoryName(exactPath)!,
            baseName: null,
            exactPath,
            replaceExisting,
            stagingWriter,
            cancellationToken);
    }

    /// <summary>
    /// Stages a JPEG beside an exact Save As destination on a worker. Alpha is flattened to white
    /// and the established bounded quality semantics are preserved.
    /// </summary>
    public static Task<PreparedImageWrite> PrepareExactJpegAsync(
        BitmapSource image,
        string destinationPath,
        int quality,
        bool replaceExisting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.IsFrozen)
            throw new ArgumentException("Async image staging requires a frozen source.", nameof(image));
        string exactPath = ValidateExactDestination(destinationPath, ".jpg", ".jpeg");
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(
            () => PrepareBitmapCore(
                image,
                Path.GetDirectoryName(exactPath)!,
                baseName: null,
                ".jpg",
                Math.Clamp(quality, 1, 100),
                exactPath,
                replaceExisting,
                cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Writes the payload's existing PNG bytes to a collision-safe final path without re-encoding.
    /// Directory creation and I/O run on a worker; cancellation removes only WinShot's GUID-named
    /// staging file.
    /// </summary>
    public static Task<string> SavePngCopyAsync(
        EncodedImagePayload payload,
        string directory,
        string baseName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        cancellationToken.ThrowIfCancellationRequested();

        string fullDirectory = Path.GetFullPath(directory);
        string safeBaseName = FileNameTemplate.Expand(baseName, DateTime.Now);
        return QueuePngCopyAsync(payload, fullDirectory, safeBaseName, cancellationToken);
    }

    /// <summary>
    /// Fully stages a collision-safe PNG copy on a worker without publishing its final name.
    /// </summary>
    public static Task<PreparedImageWrite> PreparePngCopyAsync(
        EncodedImagePayload payload,
        string directory,
        string baseName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return PreparePngCopyAsync(
            payload,
            directory,
            baseName,
            payload.CopyPngToAsync,
            cancellationToken);
    }

    internal static Task<PreparedImageWrite> PreparePngCopyAsync(
        EncodedImagePayload payload,
        string directory,
        string baseName,
        PngStagingWriter stagingWriter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        ArgumentNullException.ThrowIfNull(stagingWriter);
        cancellationToken.ThrowIfCancellationRequested();

        string fullDirectory = Path.GetFullPath(directory);
        string safeBaseName = FileNameTemplate.Expand(baseName, DateTime.Now);
        return QueuePreparePngCopyAsync(
            payload,
            fullDirectory,
            safeBaseName,
            exactPath: null,
            replaceExisting: false,
            stagingWriter,
            cancellationToken);
    }

    private static Task<string> QueuePngCopyAsync(
        EncodedImagePayload payload,
        string directory,
        string baseName,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => SavePngCopyCoreAsync(payload, directory, baseName, cancellationToken),
            cancellationToken);
    }

    private static Task<PreparedImageWrite> QueuePreparePngCopyAsync(
        EncodedImagePayload payload,
        string directory,
        string? baseName,
        string? exactPath,
        bool replaceExisting,
        PngStagingWriter stagingWriter,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => PreparePngCopyCoreAsync(
                payload,
                directory,
                baseName,
                exactPath,
                replaceExisting,
                stagingWriter,
                cancellationToken),
            cancellationToken);
    }

    private static async Task<string> SavePngCopyCoreAsync(
        EncodedImagePayload payload,
        string directory,
        string baseName,
        CancellationToken cancellationToken)
    {
        using PreparedImageWrite prepared = await PreparePngCopyCoreAsync(
                payload,
                directory,
                baseName,
                exactPath: null,
                replaceExisting: false,
                payload.CopyPngToAsync,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return prepared.Commit();
    }

    private static async Task<PreparedImageWrite> PreparePngCopyCoreAsync(
        EncodedImagePayload payload,
        string directory,
        string? baseName,
        string? exactPath,
        bool replaceExisting,
        PngStagingWriter stagingWriter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        (string stagingPath, FileStream stagingStream) =
            CreateOwnedStagingFile(fullDirectory, useAsync: true);
        bool handedOff = false;

        try
        {
            await using (stagingStream)
            {
                await stagingWriter(stagingStream, cancellationToken).ConfigureAwait(false);
                if (stagingStream.Length != payload.PngLength)
                {
                    throw new IOException(
                        "The owned PNG staging write did not produce the complete payload.");
                }
                await stagingStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                stagingStream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            PreparedImageWrite prepared = exactPath is null
                ? new PreparedImageWrite(stagingPath, fullDirectory, baseName!, ".png")
                : new PreparedImageWrite(stagingPath, exactPath, replaceExisting);
            handedOff = true;
            return prepared;
        }
        finally
        {
            if (!handedOff)
                DeleteOwnedStagingFile(stagingPath);
        }
    }

    private static PreparedImageWrite PrepareBitmapCore(
        BitmapSource image,
        string directory,
        string? baseName,
        string extension,
        int jpegQuality,
        string? exactPath,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        BitmapSource source = image;
        if (source.Format == PixelFormats.Pbgra32 || source.Format == PixelFormats.Bgra32)
            source = ImageBeautifier.FlattenToWhite(source);

        var encoder = new JpegBitmapEncoder
        {
            QualityLevel = Math.Clamp(jpegQuality, 1, 100),
        };
        encoder.Frames.Add(BitmapFrame.Create(source));
        (string stagingPath, FileStream stagingStream) =
            CreateOwnedStagingFile(fullDirectory, useAsync: false);
        bool handedOff = false;

        try
        {
            using (stagingStream)
            {
                encoder.Save(stagingStream);
                cancellationToken.ThrowIfCancellationRequested();
                stagingStream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            PreparedImageWrite prepared = exactPath is null
                ? new PreparedImageWrite(stagingPath, fullDirectory, baseName!, extension)
                : new PreparedImageWrite(stagingPath, exactPath, replaceExisting);
            handedOff = true;
            return prepared;
        }
        finally
        {
            if (!handedOff)
                DeleteOwnedStagingFile(stagingPath);
        }
    }

    private static (string Path, FileStream Stream) CreateOwnedStagingFile(
        string directory,
        bool useAsync)
    {
        for (int attempt = 0; attempt < MaximumStagingAttempts; attempt++)
        {
            string path = Path.Combine(directory, $".winshot-{Guid.NewGuid():N}.partial");
            try
            {
                var stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    options: useAsync
                        ? FileOptions.Asynchronous | FileOptions.SequentialScan
                        : FileOptions.SequentialScan);
                return (path, stream);
            }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path))
            {
            }
        }

        throw new IOException(
            $"Unable to create an owned image staging file after {MaximumStagingAttempts} attempts.");
    }

    private static string CommitUnique(
        string stagingPath,
        string directory,
        string baseName,
        string extension)
    {
        for (int suffix = 1; suffix <= MaximumNameAttempts; suffix++)
        {
            string fileName = suffix == 1
                ? baseName + extension
                : $"{baseName} {suffix}{extension}";
            string candidate = Path.Combine(directory, fileName);

            try
            {
                File.Move(stagingPath, candidate);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate))
            {
                // Another capture won the name between allocation and commit. Try the next suffix.
            }
        }

        throw new IOException(
            $"Unable to commit a unique image file after {MaximumNameAttempts} attempts.");
    }

    private static string CommitExact(
        string stagingPath,
        string exactPath,
        bool replaceExisting)
    {
        if (!replaceExisting)
        {
            File.Move(stagingPath, exactPath);
            return exactPath;
        }

        if (File.Exists(exactPath))
        {
            File.Replace(stagingPath, exactPath, destinationBackupFileName: null);
            return exactPath;
        }

        File.Move(stagingPath, exactPath);
        return exactPath;
    }

    private static string ValidateExactDestination(
        string destinationPath,
        params string[] allowedExtensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string exactPath = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(exactPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("The destination must include a parent directory.", nameof(destinationPath));

        string extension = Path.GetExtension(exactPath);
        if (!allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The destination extension must be {string.Join(" or ", allowedExtensions)}.",
                nameof(destinationPath));
        }

        return exactPath;
    }

    private static void DeleteOwnedStagingFile(string stagingPath)
    {
        try
        {
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
