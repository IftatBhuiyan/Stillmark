using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinShot.Licensing.Accounts;

public sealed class AccountSession
{
    public AccountSession(
        string accessToken,
        string refreshToken,
        DateTimeOffset expiresAtUtc,
        string? email,
        DateTimeOffset lastRefreshedAtUtc)
    {
        AccessToken = ValidateToken(accessToken, nameof(accessToken));
        RefreshToken = ValidateToken(refreshToken, nameof(refreshToken));
        if (expiresAtUtc <= DateTimeOffset.UnixEpoch)
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));
        if (lastRefreshedAtUtc <= DateTimeOffset.UnixEpoch)
            throw new ArgumentOutOfRangeException(nameof(lastRefreshedAtUtc));
        if (email?.Length > 320)
            throw new ArgumentException("The account email is too long.", nameof(email));
        ExpiresAtUtc = expiresAtUtc.ToUniversalTime();
        Email = email;
        LastRefreshedAtUtc = lastRefreshedAtUtc.ToUniversalTime();
    }

    public string AccessToken { get; }
    public string RefreshToken { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public string? Email { get; }
    public DateTimeOffset LastRefreshedAtUtc { get; }

    public AccountSessionInfo ToInfo() => new(Email, ExpiresAtUtc);

    public override string ToString() => "[account session]";

    private static string ValidateToken(string token, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        string normalized = token.Trim();
        if (normalized.Length > 32 * 1024
            || normalized.Any(c => c is '\r' or '\n' or <= '\u001F'))
            throw new ArgumentException("The account session token is invalid.", parameterName);
        return normalized;
    }
}

public interface IAccountSessionStore
{
    AccountSession? Load();
    void Save(AccountSession session);
    void Clear();
}

public sealed class DesktopAccountSessionStore : IAccountSessionStore
{
    private const int MaximumPersistedBytes = 64 * 1024;
    private const string ProtectedPrefix = "RLS1:";
    private static readonly byte[] ProtectionEntropy =
        Encoding.UTF8.GetBytes("relic-screenshot-account-session-v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _filePath;

    public DesktopAccountSessionStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Stillmark",
            "account-session.dat");
    }

    public AccountSession? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return null;
            string envelope = ReadBounded();
            if (!envelope.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
                return null;
            byte[] protectedBytes = Convert.FromBase64String(envelope[ProtectedPrefix.Length..]);
            byte[] plaintext = WindowsDataProtection.Unprotect(protectedBytes, ProtectionEntropy);
            if (plaintext.Length == 0 || plaintext.Length > MaximumPersistedBytes)
                return null;
            StoredSession? stored = JsonSerializer.Deserialize<StoredSession>(plaintext, JsonOptions);
            if (stored is null)
                return null;
            return new AccountSession(
                stored.AccessToken,
                stored.RefreshToken,
                DateTimeOffset.FromUnixTimeSeconds(stored.ExpiresAt),
                stored.Email,
                DateTimeOffset.FromUnixTimeSeconds(stored.LastRefreshedAt));
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or CryptographicException
            or FormatException
            or JsonException
            or ArgumentException
            or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public void Save(AccountSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var stored = new StoredSession(
            session.AccessToken,
            session.RefreshToken,
            session.ExpiresAtUtc.ToUnixTimeSeconds(),
            session.Email,
            session.LastRefreshedAtUtc.ToUnixTimeSeconds());
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(stored, JsonOptions);
        byte[] protectedBytes = WindowsDataProtection.Protect(plaintext, ProtectionEntropy);
        string envelope = ProtectedPrefix + Convert.ToBase64String(protectedBytes);
        if (Encoding.UTF8.GetByteCount(envelope) > MaximumPersistedBytes)
            throw new InvalidOperationException("The protected account session exceeds its storage limit.");

        string? directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("The account session path has no directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $"{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(envelope);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_filePath))
                File.Delete(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string ReadBounded()
    {
        using var stream = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            4096,
            FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > MaximumPersistedBytes)
            return string.Empty;
        using var content = new MemoryStream(checked((int)stream.Length));
        stream.CopyTo(content);
        return Encoding.UTF8.GetString(content.ToArray());
    }

    private sealed record StoredSession(
        string AccessToken,
        string RefreshToken,
        long ExpiresAt,
        string? Email,
        long LastRefreshedAt);
}
