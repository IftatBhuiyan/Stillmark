using System.IO;
using System.Text.Json;

namespace WinShot.App;

internal enum CrashRecoveryDenialReason
{
    None,
    AttemptLimitReached,
    StatePersistenceUnavailable,
}

internal readonly record struct CrashRecoveryDecision(
    bool Allowed,
    int AttemptCount,
    CrashRecoveryDenialReason DenialReason);

/// <summary>
/// Bounds Windows Application Restart loops without weakening normal user-initiated launches.
/// </summary>
internal static class CrashRecoveryGuard
{
    internal const int MaxAutomaticStarts = 3;
    internal const int MaximumStateBytes = 4 * 1024;
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private sealed record RecoveryState(DateTimeOffset WindowStartedUtc, int AttemptCount);

    internal static string DefaultStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinShot",
        "recovery-state.json");

    internal static CrashRecoveryDecision Evaluate(
        bool recoveredStart,
        DateTimeOffset now,
        string? statePath = null)
    {
        string path = Path.GetFullPath(statePath ?? DefaultStatePath);
        if (!recoveredStart)
        {
            TryDelete(path);
            return new CrashRecoveryDecision(
                Allowed: true,
                AttemptCount: 0,
                CrashRecoveryDenialReason.None);
        }

        RecoveryState? prior = TryRead(path);
        bool sameWindow = prior is not null
            && prior.WindowStartedUtc <= now
            && now - prior.WindowStartedUtc <= Window;
        int attempts = sameWindow
            ? prior!.AttemptCount == int.MaxValue
                ? int.MaxValue
                : prior.AttemptCount + 1
            : 1;
        DateTimeOffset started = sameWindow ? prior!.WindowStartedUtc : now;

        if (!TryWrite(path, new RecoveryState(started, attempts)))
        {
            return new CrashRecoveryDecision(
                Allowed: false,
                AttemptCount: attempts,
                CrashRecoveryDenialReason.StatePersistenceUnavailable);
        }

        bool allowed = attempts <= MaxAutomaticStarts;
        return new CrashRecoveryDecision(
            Allowed: allowed,
            AttemptCount: attempts,
            allowed
                ? CrashRecoveryDenialReason.None
                : CrashRecoveryDenialReason.AttemptLimitReached);
    }

    private static RecoveryState? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            string? payload = ReadBoundedUtf8(path);
            if (payload is null)
                return null;
            var state = JsonSerializer.Deserialize<RecoveryState>(payload);
            return state is { AttemptCount: > 0 } ? state : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? ReadBoundedUtf8(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 1024,
            FileOptions.SequentialScan);
        if (stream.Length > MaximumStateBytes)
            return null;

        using var content = new MemoryStream(capacity: checked((int)stream.Length));
        var buffer = new byte[1024];
        while (true)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            if (content.Length + read > MaximumStateBytes)
                return null;
            content.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(
            content.GetBuffer(),
            0,
            checked((int)content.Length));
    }

    private static bool TryWrite(string path, RecoveryState state)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                return false;

            Directory.CreateDirectory(directory);
            string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(state);
                if (payload.Length is 0 or > MaximumStateBytes)
                    return false;
                using (var stream = new FileStream(
                    staging,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024,
                    FileOptions.WriteThrough))
                {
                    stream.Write(payload);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(staging, path, overwrite: true);
                return TryRead(path) == state;
            }
            finally
            {
                TryDelete(staging);
            }
        }
        catch (Exception ex) when (ex is
            IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return false;
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
}
