using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinShot.Licensing;

/// <summary>
/// Persists an activated license key on disk and reloads it. The stored record also captures the
/// machine fingerprint at activation time; on load we require it to match, so copying license.dat to
/// a different machine forces the key to be entered again there. This local check does not impose a
/// device or seat limit: the same valid offline key can still be entered on another machine. The
/// license key's signature is the real gate — storage integrity is secondary because a forged or
/// edited token fails signature verification anyway.
/// </summary>
public sealed class LicenseStorage
{
    private const string ProtectedPrefix = "protected:v1:";
    private const int MaximumPersistedBytes = 32 * 1024;
    private const int MaximumLicenseKeyCharacters = 8 * 1024;
    private const int MaximumProviderInstanceCharacters = 128;
    private static readonly byte[] ProtectionEntropy =
        Encoding.UTF8.GetBytes("screen-capture-license-storage-v1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;

    internal string FilePath => _filePath;

    public LicenseStorage(string? baseDir = null)
    {
        string dir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinShot");
        _filePath = Path.Combine(dir, "license.dat");
    }

    private sealed record StoredLicense(
        string Key,
        string Machine,
        string? ProviderInstanceId = null,
        long? ProviderStoreId = null,
        long? ProviderProductId = null,
        long? ProviderVariantId = null,
        long? ProviderConfirmedAt = null);

    internal sealed record Snapshot(
        string Key,
        string? ProviderInstanceId,
        long? ProviderStoreId,
        long? ProviderProductId,
        long? ProviderVariantId,
        DateTimeOffset? ProviderConfirmedAt);

    /// <summary>Returns the stored license key text if present and bound to this machine; else null.</summary>
    public string? Load()
    {
        return LoadSnapshot()?.Key;
    }

    internal Snapshot? LoadSnapshot()
    {
        try
        {
            if (!File.Exists(_filePath))
                return null;

            string? persisted = ReadBoundedFile();
            if (persisted is null)
                return null;
            bool isLegacyPlaintext = !persisted.StartsWith(ProtectedPrefix, StringComparison.Ordinal);
            string json = isLegacyPlaintext ? persisted : Unprotect(persisted);
            var stored = JsonSerializer.Deserialize<StoredLicense>(json, JsonOptions);
            if (stored is null
                || string.IsNullOrWhiteSpace(stored.Key)
                || stored.Key.Length > MaximumLicenseKeyCharacters
                || stored.ProviderInstanceId?.Length > MaximumProviderInstanceCharacters)
                return null;
            if (!string.Equals(stored.Machine, MachineFingerprint.Current, StringComparison.Ordinal))
                return null; // activated on a different machine

            DateTimeOffset? providerConfirmedAt = null;
            if (stored.ProviderConfirmedAt is { } unixSeconds)
            {
                try { providerConfirmedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds); }
                catch (ArgumentOutOfRangeException) { return null; }
            }
            if (stored.ProviderVariantId is <= 0)
                return null;
            if (stored.ProviderStoreId is <= 0 || stored.ProviderProductId is <= 0)
                return null;
            int providerIdentityFieldCount =
                (stored.ProviderStoreId is null ? 0 : 1)
                + (stored.ProviderProductId is null ? 0 : 1)
                + (stored.ProviderVariantId is null ? 0 : 1);
            bool legacyVariantOnly = stored.ProviderStoreId is null
                && stored.ProviderProductId is null
                && stored.ProviderVariantId is not null;
            if (providerIdentityFieldCount is not 0 and not 3 && !legacyVariantOnly)
                return null;
            if (isLegacyPlaintext)
            {
                // Backward-compatible, best-effort migration. Preserve the complete validated
                // provider record: dropping its instance/identity/confirmation fields can revoke
                // a legitimate cached entitlement after the next process start.
                try { PersistProtected(stored); }
                catch (Exception ex) when (ex is IOException
                    or UnauthorizedAccessException
                    or CryptographicException)
                {
                }
            }
            return new Snapshot(
                stored.Key,
                stored.ProviderInstanceId,
                stored.ProviderStoreId,
                stored.ProviderProductId,
                stored.ProviderVariantId,
                providerConfirmedAt);
        }
        catch (Exception ex) when (ex is IOException
            or JsonException
            or UnauthorizedAccessException
            or CryptographicException
            or FormatException)
        {
            return null;
        }
    }

    public void Save(string key)
    {
        SaveCore(
            key,
            providerInstanceId: null,
            providerStoreId: null,
            providerProductId: null,
            providerVariantId: null,
            providerConfirmedAt: null);
    }

    internal void SaveProviderActivation(
        string key,
        string providerInstanceId,
        long providerStoreId,
        long providerProductId,
        long providerVariantId,
        DateTimeOffset providerConfirmedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerInstanceId);
        if (providerStoreId <= 0)
            throw new ArgumentOutOfRangeException(nameof(providerStoreId));
        if (providerProductId <= 0)
            throw new ArgumentOutOfRangeException(nameof(providerProductId));
        if (providerVariantId <= 0)
            throw new ArgumentOutOfRangeException(nameof(providerVariantId));
        SaveCore(
            key,
            providerInstanceId.Trim(),
            providerStoreId,
            providerProductId,
            providerVariantId,
            providerConfirmedAt);
    }

    internal void UpdateProviderConfirmation(Snapshot snapshot, DateTimeOffset providerConfirmedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.ProviderInstanceId))
            throw new InvalidOperationException("No provider activation is stored.");
        SaveCore(
            snapshot.Key,
            snapshot.ProviderInstanceId,
            snapshot.ProviderStoreId,
            snapshot.ProviderProductId,
            snapshot.ProviderVariantId,
            providerConfirmedAt);
    }

    internal void ClearProviderConfirmation(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SaveCore(
            snapshot.Key,
            snapshot.ProviderInstanceId,
            snapshot.ProviderStoreId,
            snapshot.ProviderProductId,
            snapshot.ProviderVariantId,
            providerConfirmedAt: null);
    }

    private void SaveCore(
        string key,
        string? providerInstanceId,
        long? providerStoreId,
        long? providerProductId,
        long? providerVariantId,
        DateTimeOffset? providerConfirmedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > MaximumLicenseKeyCharacters)
            throw new ArgumentException("The license key is too long.", nameof(key));
        if (providerInstanceId?.Length > MaximumProviderInstanceCharacters)
            throw new ArgumentException("The provider instance identifier is too long.", nameof(providerInstanceId));
        if (providerStoreId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(providerStoreId));
        if (providerProductId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(providerProductId));
        if (providerVariantId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(providerVariantId));
        int providerIdentityFieldCount =
            (providerStoreId is null ? 0 : 1)
            + (providerProductId is null ? 0 : 1)
            + (providerVariantId is null ? 0 : 1);
        if (providerIdentityFieldCount is not 0 and not 3)
            throw new InvalidOperationException("Provider identity fields must be persisted together.");
        var stored = new StoredLicense(
            key,
            MachineFingerprint.Current,
            providerInstanceId,
            providerStoreId,
            providerProductId,
            providerVariantId,
            providerConfirmedAt?.ToUnixTimeSeconds());
        PersistProtected(stored);
    }

    private void PersistProtected(StoredLicense stored)
    {
        string directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        byte[] plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(stored, JsonOptions));
        byte[] protectedBytes = WindowsDataProtection.Protect(plaintext, ProtectionEntropy);
        string envelope = ProtectedPrefix + Convert.ToBase64String(protectedBytes);
        if (Encoding.UTF8.GetByteCount(envelope) > MaximumPersistedBytes)
            throw new InvalidOperationException("The protected license record exceeds its storage limit.");
        string temporaryPath = Path.Combine(directory, $"{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, envelope, Encoding.UTF8);
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_filePath))
                File.Delete(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Unprotect(string envelope)
    {
        byte[] protectedBytes = Convert.FromBase64String(envelope[ProtectedPrefix.Length..]);
        byte[] plaintext = WindowsDataProtection.Unprotect(protectedBytes, ProtectionEntropy);
        if (plaintext.Length > MaximumPersistedBytes)
            throw new FormatException("The protected license payload exceeds its storage limit.");
        return Encoding.UTF8.GetString(plaintext);
    }

    private string? ReadBoundedFile()
    {
        using var stream = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length > MaximumPersistedBytes)
            return null;

        using var content = new MemoryStream(capacity: checked((int)stream.Length));
        var buffer = new byte[4096];
        while (true)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            if (content.Length + read > MaximumPersistedBytes)
                return null;
            content.Write(buffer, 0, read);
        }
        string text = Encoding.UTF8.GetString(content.GetBuffer(), 0, checked((int)content.Length));
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }
}
