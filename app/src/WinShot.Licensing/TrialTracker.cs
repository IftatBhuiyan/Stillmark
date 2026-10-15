using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace WinShot.Licensing;

/// <summary>
/// Tracks the free trial window across two independent stores (a file and an HKCU registry value),
/// each integrity-protected with a machine-bound HMAC so casual hand-edits are ignored.
///
/// Clock-rollback resistance: we remember the latest time we've ever seen and use
/// <c>max(now, lastSeen)</c> as the effective clock, so winding the system clock backwards cannot
/// extend the trial. We deliberately do NOT hard-punish anomalies (which would risk false positives
/// from NTP / timezone changes on legitimate machines); we just refuse to let time run backwards.
///
/// This is casual-piracy resistance, not DRM: deleting both stores restarts the trial. Preventing
/// that requires server-side activation (a documented future upgrade).
/// </summary>
public sealed class TrialTracker
{
    public const int TrialDays = 14;
    internal const int MaximumStateBytes = 4 * 1024;
    private const int SecondsPerDay = 86400;
    private const string RegPath = @"Software\WinShot";
    private const string RegValueName = "Tk";

    private readonly string _filePath;
    private readonly byte[] _hmacKey;
    private readonly Func<DateTimeOffset> _now;
    private readonly bool _useRegistry;

    internal string StateFilePath => _filePath;

    /// <param name="useRegistry">
    /// When true (production) a redundant copy is also kept in HKCU so deleting the file alone does
    /// not reset the trial. Tests pass false to stay isolated from shared machine state.
    /// </param>
    public TrialTracker(string? baseDir = null, Func<DateTimeOffset>? now = null, bool useRegistry = true)
    {
        string dir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinShot");
        _filePath = Path.Combine(dir, "trial.dat");
        _hmacKey = SHA256.HashData(Encoding.UTF8.GetBytes("winshot-trial-v1|" + MachineFingerprint.Current));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _useRegistry = useRegistry;
    }

    /// <summary>Days remaining in the trial (0 once elapsed). Advances the rollback guard as a side effect.</summary>
    public int DaysLeft()
    {
        TimeSpan remaining = TimeRemaining();
        return remaining <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(remaining.TotalDays);
    }

    /// <summary>Precise time remaining in the trial, clamped at zero. Advances the rollback guard.</summary>
    public TimeSpan TimeRemaining()
    {
        long now = _now().ToUnixTimeSeconds();
        var records = ReadAll();

        long firstRun = records.Count > 0 ? Math.Min(records.Min(r => r.FirstRun), now) : now;
        long lastSeen = records.Count > 0 ? Math.Max(records.Max(r => r.LastSeen), firstRun) : now;
        long effectiveNow = Math.Max(now, lastSeen); // never let the clock run backwards

        Persist(firstRun, effectiveNow);

        long trialEndsAt = firstRun + (long)TrialDays * SecondsPerDay;
        long remainingSeconds = Math.Max(0, trialEndsAt - effectiveNow);
        return TimeSpan.FromSeconds(remainingSeconds);
    }

    private readonly record struct Record(long FirstRun, long LastSeen);

    private List<Record> ReadAll()
    {
        var results = new List<Record>();
        if (_useRegistry && TryDecode(ReadRegistry(), out var reg)) results.Add(reg);
        if (TryDecode(ReadFile(), out var file)) results.Add(file);
        return results;
    }

    private void Persist(long firstRun, long lastSeen)
    {
        string encoded = Encode(new Record(firstRun, lastSeen));
        if (_useRegistry)
            WriteRegistry(encoded);
        WriteFile(encoded);
    }

    // ---- record encoding (payload.hmac, both base64url) ----

    private string Encode(Record record)
    {
        byte[] payload = Encoding.ASCII.GetBytes($"{record.FirstRun}|{record.LastSeen}");
        byte[] mac = HMACSHA256.HashData(_hmacKey, payload);
        return Base64Url.Encode(payload) + "." + Base64Url.Encode(mac);
    }

    private bool TryDecode(string? text, out Record record)
    {
        record = default;
        if (string.IsNullOrWhiteSpace(text)
            || Encoding.UTF8.GetByteCount(text) > MaximumStateBytes)
            return false;
        int dot = text.IndexOf('.');
        if (dot <= 0)
            return false;
        try
        {
            byte[] payload = Base64Url.Decode(text[..dot]);
            byte[] mac = Base64Url.Decode(text[(dot + 1)..]);
            byte[] expected = HMACSHA256.HashData(_hmacKey, payload);
            if (!CryptographicOperations.FixedTimeEquals(mac, expected))
                return false;
            string[] parts = Encoding.ASCII.GetString(payload).Split('|');
            if (parts.Length != 2)
                return false;
            record = new Record(long.Parse(parts[0]), long.Parse(parts[1]));
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return false;
        }
    }

    // ---- stores ----

    private static string? ReadRegistry()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegPath, writable: false);
            return key?.GetValue(RegValueName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static void WriteRegistry(string value)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegPath, writable: true);
            key.SetValue(RegValueName, value, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }

    private string? ReadFile()
    {
        try
        {
            if (!File.Exists(_filePath))
                return null;

            using var stream = new FileStream(
                _filePath,
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
            return Encoding.UTF8.GetString(content.GetBuffer(), 0, checked((int)content.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteFile(string value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
