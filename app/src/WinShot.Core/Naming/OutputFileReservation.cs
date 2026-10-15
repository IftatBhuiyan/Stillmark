namespace WinShot.Core.Naming;

/// <summary>
/// Owns one atomically-created output placeholder and its exclusive write stream. Disposing an
/// uncommitted lease removes only that placeholder; committing flushes, closes, and preserves it.
/// </summary>
public sealed class OutputFileReservation : IDisposable
{
    public const int DefaultMaxAttempts = 1_000;
    public const int MaximumAttempts = 10_000;
    public const int MaximumFileNameLength = 255;

    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;

    private readonly object _gate = new();
    private FileStream? _stream;
    private bool _committed;
    private bool _disposed;

    private OutputFileReservation(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    public string Path { get; }

    public FileStream Stream
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed || _stream is null, this);
                return _stream;
            }
        }
    }

    public bool IsCommitted
    {
        get
        {
            lock (_gate)
                return _committed;
        }
    }

    public static OutputFileReservation Create(
        string directory,
        string baseName,
        string extension,
        int maxAttempts = DefaultMaxAttempts)
    {
        string validatedDirectory = ValidateInputs(
            directory,
            baseName,
            extension,
            maxAttempts);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            string suffix = attempt == 1 ? "" : $" {attempt}";
            string fileName = baseName + suffix + extension;
            string candidate = System.IO.Path.Combine(validatedDirectory, fileName);

            try
            {
                var stream = new FileStream(
                    candidate,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    FileOptions.SequentialScan);
                return new OutputFileReservation(candidate, stream);
            }
            catch (IOException ex) when (IsNameCollision(ex, candidate))
            {
            }
        }

        throw new IOException(
            $"Unable to reserve a unique output file after {maxAttempts} attempts.");
    }

    /// <summary>
    /// Flushes and closes the owned stream, then transfers the placeholder to the caller as a
    /// preserved output. A committed reservation remains on disk when the lease is disposed.
    /// </summary>
    public void Commit()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed)
                return;

            FileStream stream = _stream
                ?? throw new ObjectDisposedException(nameof(OutputFileReservation));
            stream.Flush(flushToDisk: true);
            stream.Dispose();
            _stream = null;
            _committed = true;
        }
    }

    public void Dispose()
    {
        FileStream? stream;
        bool deletePlaceholder;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            stream = _stream;
            _stream = null;
            deletePlaceholder = !_committed;
        }

        Exception? closeFailure = null;
        try
        {
            stream?.Dispose();
        }
        catch (Exception ex)
        {
            closeFailure = ex;
        }

        Exception? deleteFailure = null;
        if (deletePlaceholder)
        {
            try
            {
                File.Delete(Path);
            }
            catch (Exception ex)
            {
                deleteFailure = ex;
            }
        }

        if (closeFailure is not null && deleteFailure is not null)
            throw new AggregateException(closeFailure, deleteFailure);
        if (closeFailure is not null)
            throw closeFailure;
        if (deleteFailure is not null)
            throw deleteFailure;
    }

    private static string ValidateInputs(
        string directory,
        string baseName,
        string extension,
        int maxAttempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        if (!string.Equals(directory, directory.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("The output directory cannot have surrounding whitespace.", nameof(directory));
        if (!string.Equals(baseName, baseName.Trim(), StringComparison.Ordinal) ||
            baseName.EndsWith('.') ||
            baseName is "." or "..")
            throw new ArgumentException("The output base name is not valid.", nameof(baseName));
        if (!extension.StartsWith('.') ||
            extension.Length < 2 ||
            extension.EndsWith('.') ||
            !string.Equals(extension, extension.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("The output extension must begin with a dot.", nameof(extension));
        if (maxAttempts < 1 || maxAttempts > MaximumAttempts)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        char[] invalid = System.IO.Path.GetInvalidFileNameChars();
        if (baseName.IndexOfAny(invalid) >= 0 ||
            extension.IndexOfAny(invalid) >= 0 ||
            IsReservedDeviceName(baseName))
            throw new ArgumentException("The output file name contains an invalid component.");

        string largestSuffix = maxAttempts == 1 ? "" : $" {maxAttempts}";
        if (baseName.Length + largestSuffix.Length + extension.Length > MaximumFileNameLength)
            throw new PathTooLongException(
                $"The output file name must be at most {MaximumFileNameLength} characters.");

        string fullDirectory = System.IO.Path.GetFullPath(directory);
        if (!Directory.Exists(fullDirectory))
            throw new DirectoryNotFoundException("The output directory does not exist.");
        return fullDirectory;
    }

    private static bool IsReservedDeviceName(string baseName)
    {
        string stem = baseName.Split('.')[0].TrimEnd(' ', '.');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;

        return stem.Length == 4 &&
               stem[3] is >= '1' and <= '9' &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNameCollision(IOException exception, string candidate)
    {
        int error = exception.HResult & 0xFFFF;
        return error is ErrorFileExists or ErrorAlreadyExists ||
               File.Exists(candidate) ||
               Directory.Exists(candidate);
    }
}
