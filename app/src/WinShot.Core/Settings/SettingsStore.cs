using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinShot.Core.Settings;

public enum SettingsSaveStatus
{
    Saved,
    ReadOnly,
    Failed,
}

public readonly record struct SettingsSaveResult(SettingsSaveStatus Status)
{
    public bool Succeeded => Status == SettingsSaveStatus.Saved;

    public static SettingsSaveResult Saved { get; } =
        new(SettingsSaveStatus.Saved);

    public static SettingsSaveResult ReadOnly { get; } =
        new(SettingsSaveStatus.ReadOnly);

    public static SettingsSaveResult Failed { get; } =
        new(SettingsSaveStatus.Failed);
}

public sealed class SettingsStore
{
    /// <summary>
    /// Maximum accepted size of settings.json. Settings are a small local profile; rejecting
    /// larger inputs before deserialization prevents an accidentally or maliciously replaced
    /// file from causing unbounded memory use during application startup.
    /// </summary>
    public const int MaximumFileSizeBytes = 1024 * 1024;

    private const int CopyBufferSizeBytes = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters =
        {
            new TolerantStringEnumConverter<ImageFormatKind>(),
            new TolerantStringEnumConverter<WindowBackgroundKind>(),
            new TolerantStringEnumConverter<AppearanceKind>(),
        },
    };

    public string FilePath { get; }
    public bool HasNewerSchema { get; private set; }

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinShot", "settings.json");
    }

    public AppSettings Load()
    {
        HasNewerSchema = false;
        try
        {
            if (File.Exists(FilePath))
            {
                using var stream = new FileStream(
                    FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBufferSizeBytes,
                    FileOptions.SequentialScan);

                if (stream.Length > MaximumFileSizeBytes)
                {
                    PreserveCorrupt(stream);
                    return AppSettings.CreateDefault();
                }

                try
                {
                    using MemoryStream payload = ReadBoundedPayload(stream);
                    int schemaVersion = ReadSchemaVersion(payload);
                    if (schemaVersion > AppSettings.CurrentSchemaVersion)
                    {
                        // Inspect the top-level schema before binding known enum/property shapes.
                        // A future version may validly add values that this binary cannot parse.
                        HasNewerSchema = true;
                        return AppSettings.CreateDefault();
                    }

                    payload.Position = 0;
                    var loaded = JsonSerializer.Deserialize<AppSettings>(payload, JsonOptions);
                    if (loaded is not null)
                    {
                        return AppSettingsNormalizer.Normalize(loaded);
                    }
                }
                catch (SettingsFileTooLargeException)
                {
                    // Length is checked before reading, but enforce the same limit while reading
                    // in case a filesystem permits the opened file to grow concurrently.
                    PreserveCorrupt(stream);
                }
                catch (JsonException)
                {
                    // Reuse the already-open, size-checked handle so the evidence is from the
                    // exact file that failed parsing rather than a path that may have changed.
                    PreserveCorrupt(stream);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return AppSettings.CreateDefault();
    }

    private static int ReadSchemaVersion(MemoryStream payload)
    {
        payload.Position = 0;
        using JsonDocument document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(
                nameof(AppSettings.SchemaVersion),
                out JsonElement schemaElement))
        {
            return 0;
        }

        if (schemaElement.ValueKind != JsonValueKind.Number
            || !schemaElement.TryGetInt32(out int schemaVersion))
        {
            throw new JsonException("SchemaVersion must be a 32-bit integer.");
        }

        return schemaVersion;
    }

    public SettingsSaveResult Save(AppSettings settings)
    {
        if (HasNewerSchema)
            return SettingsSaveResult.ReadOnly;

        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        string tmp = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            // Write to a temp file then atomically swap it in, so a crash mid-write can never
            // truncate settings.json (which would silently reset the user's whole config).
            using (var destination = new FileStream(
                tmp,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSizeBytes,
                FileOptions.WriteThrough))
            {
                var boundedDestination = new MaximumLengthWriteStream(destination, MaximumFileSizeBytes);
                JsonSerializer.Serialize(boundedDestination, settings, JsonOptions);
                boundedDestination.Flush();
                destination.Flush(flushToDisk: true);
            }

            if (File.Exists(FilePath))
                File.Replace(tmp, FilePath, null);
            else
                File.Move(tmp, FilePath);
            return SettingsSaveResult.Saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preserve the previous settings rather than falling back to a direct, non-atomic write.
            TryDelete(tmp);
            return SettingsSaveResult.Failed;
        }
    }

    private static MemoryStream ReadBoundedPayload(Stream source)
    {
        int initialCapacity = source.CanSeek
            ? (int)Math.Min(source.Length, MaximumFileSizeBytes)
            : 0;
        var payload = new MemoryStream(initialCapacity);
        CopyAtMost(source, payload, MaximumFileSizeBytes + 1);
        if (payload.Length > MaximumFileSizeBytes)
        {
            payload.Dispose();
            throw new SettingsFileTooLargeException();
        }

        payload.Position = 0;
        return payload;
    }

    private void PreserveCorrupt(FileStream source)
    {
        string evidencePath = FilePath + ".corrupt.json";
        string temporaryEvidencePath = evidencePath + ".tmp";

        try
        {
            source.Position = 0;
            using (var destination = new FileStream(
                temporaryEvidencePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSizeBytes,
                FileOptions.WriteThrough))
            {
                CopyAtMost(source, destination, MaximumFileSizeBytes);
                destination.Flush(flushToDisk: true);
            }

            if (File.Exists(evidencePath))
                File.Replace(temporaryEvidencePath, evidencePath, null);
            else
                File.Move(temporaryEvidencePath, evidencePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(temporaryEvidencePath);
        }
    }

    private static void CopyAtMost(Stream source, Stream destination, int maximumBytes)
    {
        var buffer = new byte[CopyBufferSizeBytes];
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class SettingsFileTooLargeException : IOException
    {
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
                throw new SettingsFileTooLargeException();
        }
    }
}

/// <summary>
/// Lets the normalizer repair an unknown enum member without discarding otherwise valid settings.
/// Structurally malformed JSON still raises <see cref="JsonException"/> and follows the corrupt
/// file recovery path.
/// </summary>
internal sealed class TolerantStringEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly TEnum InvalidValue =
        (TEnum)Enum.ToObject(typeof(TEnum), int.MinValue);

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? text = reader.GetString();
            return Enum.TryParse(text, ignoreCase: true, out TEnum parsed) ? parsed : InvalidValue;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int numeric))
            return (TEnum)Enum.ToObject(typeof(TEnum), numeric);

        throw new JsonException($"Expected a string or 32-bit integer for {typeof(TEnum).Name}.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

public sealed class SettingsManager
{
    private readonly SettingsStore _store;

    public AppSettings Current { get; private set; }
    public bool IsReadOnly => _store.HasNewerSchema;
    public event Action? Changed;

    public SettingsManager(SettingsStore store)
    {
        _store = store;
        Current = store.Load();
    }

    public SettingsSaveResult Update(AppSettings settings)
    {
        if (IsReadOnly)
            return SettingsSaveResult.ReadOnly;

        AppSettings normalized = AppSettingsNormalizer.Normalize(settings);
        SettingsSaveResult result = _store.Save(normalized);
        if (!result.Succeeded)
            return result;

        Current = normalized;
        Changed?.Invoke();
        return result;
    }

    /// <summary>Persists in-place mutations of Current (e.g. last-area memory) without raising Changed.</summary>
    public SettingsSaveResult SaveCurrent()
    {
        if (IsReadOnly)
            return SettingsSaveResult.ReadOnly;

        AppSettingsNormalizer.Normalize(Current);
        return _store.Save(Current);
    }
}
