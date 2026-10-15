using System.Text.Json;

namespace WinShot.Core.History;

public sealed record HistoryEntry(
    string Id,
    DateTime Timestamp,
    string FilePath,
    bool IsManagedCopy, // true when the file lives in our History folder and we own its lifetime
    int Width,
    int Height);

/// <summary>
/// Rolling record of recent captures. Captures the user didn't save to disk get a
/// managed PNG copy in %LOCALAPPDATA%\WinShot\History so history stays browsable.
/// </summary>
public sealed class HistoryService
{
    public const int MaxEntries = 40;
    public const int MaximumMetadataBytes = 1024 * 1024;

    private const int CopyBufferSizeBytes = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _jsonPath;
    private readonly List<HistoryEntry> _entries = new(); // newest first

    public string HistoryDirectory { get; }
    public IReadOnlyList<HistoryEntry> Entries => _entries;
    public event Action? Changed;

    public HistoryService(string? baseDirectory = null)
    {
        string baseDir = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinShot");
        baseDir = Path.GetFullPath(baseDir);
        HistoryDirectory = Path.Combine(baseDir, "History");
        _jsonPath = Path.Combine(baseDir, "history.json");
        Load();
    }

    /// <summary>Path for a managed copy of an unsaved capture.</summary>
    public string CreateManagedPath(DateTime timestamp, string id)
    {
        Directory.CreateDirectory(HistoryDirectory);
        return Path.Combine(HistoryDirectory, $"{timestamp:yyyyMMdd_HHmmss}_{id}.png");
    }

    public bool Add(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        List<HistoryEntry> previous = [.. _entries];
        bool fileWasAlreadyReferenced = previous.Any(existing =>
            string.Equals(existing.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase));
        if (_entries.Count >= MaxEntries)
        {
            // Keep the oldest image tracked if another application prevents its cleanup.
            // Capture delivery continues even when adding the optional History copy fails.
            HistoryEntry oldest = _entries[^1];
            if (!Save() || (!string.Equals(oldest.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase)
                && !DeleteManagedFile(oldest)))
            {
                if (!fileWasAlreadyReferenced) DeleteManagedFile(entry);
                return false;
            }
        }
        _entries.Insert(0, entry);
        while (_entries.Count > MaxEntries)
        {
            _entries.RemoveAt(_entries.Count - 1);
        }

        if (!Save())
        {
            _entries.Clear();
            _entries.AddRange(previous);
            if (!fileWasAlreadyReferenced)
                DeleteManagedFile(entry);
            return false;
        }

        Changed?.Invoke();
        return true;
    }

    public bool Remove(string id)
    {
        return Remove(id, out _);
    }

    /// <summary>Records a saved editor result, retaining ownership when Save As replaces a tracked file.</summary>
    public bool RecordSavedImage(string path, DateTime timestamp, int width, int height)
    {
        string fullPath = Path.GetFullPath(path);
        int existingIndex = _entries.FindIndex(entry =>
            string.Equals(entry.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existingIndex < 0)
            return Add(new HistoryEntry(Guid.NewGuid().ToString("N"), timestamp, fullPath, false, width, height));

        List<HistoryEntry> previous = [.. _entries];
        HistoryEntry updated = _entries[existingIndex] with
        {
            Timestamp = timestamp,
            Width = width,
            Height = height,
        };
        _entries.RemoveAt(existingIndex);
        _entries.Insert(0, updated);
        if (!Save())
        {
            _entries.Clear();
            _entries.AddRange(previous);
            return false;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Removes one entry, retaining its durable reference until its owned image is deleted.
    /// </summary>
    public bool Remove(string id, out bool cleanupFailed)
    {
        cleanupFailed = false;
        int index = _entries.FindIndex(e => e.Id == id);
        if (index < 0)
            return false;
        HistoryEntry removed = _entries[index];
        // Check metadata is writable while keeping the existing references durable. A crash or
        // failed final save may leave a missing-image entry, but never an untracked private image.
        if (!Save())
            return false;

        if (!DeleteManagedFile(removed))
        {
            cleanupFailed = true;
            return false;
        }

        _entries.RemoveAt(index);
        bool saved = Save();
        if (!saved)
            _entries.Insert(index, removed);
        Changed?.Invoke();
        return saved;
    }

    public bool Clear()
    {
        return Clear(out _);
    }

    /// <summary>
    /// Clears history. Managed files that cannot be deleted remain referenced in history so the
    /// user can retry after releasing the lock.
    /// </summary>
    public bool Clear(out bool cleanupFailed)
    {
        cleanupFailed = false;
        HistoryEntry[] previous = [.. _entries];
        if (!Save())
            return false;

        var undeleted = new List<HistoryEntry>();
        foreach (HistoryEntry entry in previous)
        {
            if (!DeleteManagedFile(entry))
                undeleted.Add(entry);
        }

        cleanupFailed = undeleted.Count > 0;
        _entries.Clear();
        _entries.AddRange(undeleted);
        bool saved = Save();
        if (!saved)
        {
            _entries.Clear();
            _entries.AddRange(previous);
        }

        Changed?.Invoke();
        return saved && !cleanupFailed;
    }

    private bool DeleteManagedFile(HistoryEntry entry)
    {
        if (!entry.IsManagedCopy || !IsOwnedManagedPath(entry.FilePath))
            return true; // never touch files the user saved themselves
        try
        {
            File.Delete(entry.FilePath);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private bool IsOwnedManagedPath(string path)
    {
        try
        {
            // Managed copies are created directly in HistoryDirectory. Do not trust the persisted
            // IsManagedCopy flag by itself: a damaged or edited history.json must never turn Clear
            // History into an arbitrary-file deletion primitive.
            string fullPath = Path.GetFullPath(path);
            string? parent = Path.GetDirectoryName(fullPath);
            return string.Equals(parent, HistoryDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_jsonPath))
            {
                using var source = new FileStream(
                    _jsonPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBufferSizeBytes,
                    FileOptions.SequentialScan);
                if (source.Length > MaximumMetadataBytes)
                    return;

                using var payload = new MemoryStream(
                    (int)Math.Min(source.Length, MaximumMetadataBytes));
                CopyAtMost(source, payload, MaximumMetadataBytes + 1);
                if (payload.Length > MaximumMetadataBytes)
                    return;

                payload.Position = 0;
                var loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(payload, JsonOptions);
                if (loaded is not null)
                    _entries.AddRange(loaded.Where(entry =>
                        entry is not null
                        && !string.IsNullOrWhiteSpace(entry.Id)
                        && !string.IsNullOrWhiteSpace(entry.FilePath)
                        && Path.IsPathFullyQualified(entry.FilePath)
                        && entry.Width > 0
                        && entry.Height > 0).Take(MaxEntries));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt history is not worth crashing over; start fresh.
        }
    }

    private bool Save()
    {
        string? tmp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_jsonPath)!);
            tmp = _jsonPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var destination = new FileStream(
                tmp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSizeBytes,
                FileOptions.WriteThrough))
            {
                var bounded = new MaximumLengthWriteStream(
                    destination,
                    MaximumMetadataBytes);
                JsonSerializer.Serialize(bounded, _entries, JsonOptions);
                bounded.Flush();
                destination.Flush(flushToDisk: true);
            }

            if (File.Exists(_jsonPath))
                File.Replace(tmp, _jsonPath, null);
            else
                File.Move(tmp, _jsonPath);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (tmp is not null)
            {
                try
                {
                    File.Delete(tmp);
                }
                catch (Exception cleanupException) when (
                    cleanupException is IOException or UnauthorizedAccessException)
                {
                }
            }
            return false;
        }
    }

    private static void CopyAtMost(Stream source, Stream destination, int maximumBytes)
    {
        byte[] buffer = new byte[CopyBufferSizeBytes];
        int remaining = maximumBytes;
        while (remaining > 0)
        {
            int bytesRead = source.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (bytesRead == 0)
                break;
            destination.Write(buffer, 0, bytesRead);
            remaining -= bytesRead;
        }
    }

    private sealed class MaximumLengthWriteStream(Stream destination, long maximumBytes) : Stream
    {
        private long _bytesWritten;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _bytesWritten;
        public override long Position
        {
            get => _bytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush() => destination.Flush();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            destination.Write(buffer, offset, count);
            _bytesWritten += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            destination.Write(buffer);
            _bytesWritten += buffer.Length;
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacity(1);
            destination.WriteByte(value);
            _bytesWritten++;
        }

        private void EnsureCapacity(int additionalBytes)
        {
            if (additionalBytes > maximumBytes - _bytesWritten)
                throw new IOException(
                    $"History metadata exceeded {MaximumMetadataBytes} bytes.");
        }
    }
}
