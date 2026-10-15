using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinShot.Core.Update;

/// <summary>
/// The signed release metadata that Velopack's feed does not carry: publication time and the
/// package identity that the application is willing to download. The JSON and detached signature
/// are immutable per version and are verified before a package is made available to the UI.
/// </summary>
public sealed record VelopackReleaseCatalogEntry(
    int SchemaVersion,
    string Application,
    string PackageId,
    string Channel,
    string RuntimeIdentifier,
    Version Version,
    DateTimeOffset ReleasedAtUtc,
    string PackageFileName,
    long PackageBytes,
    string PackageSha256);

public sealed record VelopackReleaseExpectations(
    string Application,
    string PackageId,
    string Channel,
    string RuntimeIdentifier,
    Version Version,
    string PackageFileName);

/// <summary>
/// Fail-closed parser and detached RSA-PSS/SHA-256 verifier for a Velopack release catalog.
/// </summary>
public static class VelopackReleaseCatalogVerifier
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumCatalogBytes = 16 * 1024;
    public const int MaximumSignatureBytes = 1024;
    public const long MaximumPackageBytes = 512L * 1024 * 1024;

    public static bool TryVerify(
        ReadOnlySpan<byte> catalogBytes,
        ReadOnlySpan<byte> signatureBytes,
        string? publicKeyPem,
        string? expectedPublicKeySha256,
        [NotNullWhen(true)] out VelopackReleaseCatalogEntry? entry)
    {
        entry = null;
        if (catalogBytes.Length is <= 0 or > MaximumCatalogBytes
            || signatureBytes.Length is <= 0 or > MaximumSignatureBytes
            || HasUtf8Bom(catalogBytes)
            || string.IsNullOrWhiteSpace(publicKeyPem)
            || !IsSha256(expectedPublicKeySha256))
            return false;

        try
        {
            using RSA rsa = RSA.Create();
            if (publicKeyPem.Length > 16 * 1024
                || publicKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
                return false;
            rsa.ImportFromPem(publicKeyPem);
            if (rsa.KeySize < 3072 || signatureBytes.Length != rsa.KeySize / 8)
                return false;

            string actualFingerprint = Convert.ToHexString(
                SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
            if (!actualFingerprint.Equals(expectedPublicKeySha256, StringComparison.Ordinal))
                return false;
            if (!rsa.VerifyData(
                    catalogBytes,
                    signatureBytes,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss))
                return false;

            return TryParse(catalogBytes, out entry);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    public static bool Matches(
        VelopackReleaseCatalogEntry? entry,
        VelopackReleaseExpectations? expectations)
    {
        if (entry is null
            || expectations is null
            || entry.Application is null
            || entry.PackageId is null
            || entry.Channel is null
            || entry.RuntimeIdentifier is null
            || entry.Version is null
            || entry.PackageFileName is null
            || expectations.Application is null
            || expectations.PackageId is null
            || expectations.Channel is null
            || expectations.RuntimeIdentifier is null
            || expectations.Version is null
            || expectations.PackageFileName is null)
            return false;
        return entry.SchemaVersion == CurrentSchemaVersion
            && entry.Application.Equals(expectations.Application, StringComparison.Ordinal)
            && entry.PackageId.Equals(expectations.PackageId, StringComparison.Ordinal)
            && entry.Channel.Equals(expectations.Channel, StringComparison.Ordinal)
            && entry.RuntimeIdentifier.Equals(
                expectations.RuntimeIdentifier,
                StringComparison.Ordinal)
            && entry.Version == expectations.Version
            && entry.PackageFileName.Equals(
                expectations.PackageFileName,
                StringComparison.Ordinal);
    }

    public static bool VerifyPackage(
        string? packagePath,
        VelopackReleaseCatalogEntry? entry)
    {
        if (entry is null
            || string.IsNullOrWhiteSpace(packagePath)
            || entry.PackageBytes is <= 0 or > MaximumPackageBytes
            || !IsSha256(entry.PackageSha256)
            || !IsSafePackageFileName(entry.PackageFileName))
            return false;

        try
        {
            string fullPath = Path.GetFullPath(packagePath);
            if (!Path.GetFileName(fullPath).Equals(
                    entry.PackageFileName,
                    StringComparison.Ordinal))
                return false;
            FileInfo info = new(fullPath);
            if (!info.Exists || info.Length != entry.PackageBytes)
                return false;
            using FileStream stream = new(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan);
            string actualHash = Convert.ToHexString(SHA256.HashData(stream));
            return actualHash.Equals(entry.PackageSha256, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException
            or CryptographicException)
        {
            return false;
        }
    }

    public static string GetCatalogFileName(string packageId, Version version)
    {
        if (!IsSafeIdentity(packageId, 128) || version is null || !TryExactVersion(
                version.ToString(3), out Version? parsed))
            throw new ArgumentException("Package identity or version is unsafe.");
        return $"{packageId}-{parsed}-release-catalog.json";
    }

    public static bool IsSafePackageFileName(string? value)
    {
        if (!IsSafeIdentity(value, 256)
            || value is null
            || !Path.GetFileName(value).Equals(value, StringComparison.Ordinal)
            || value.Contains(':')
            || value.EndsWith(".", StringComparison.Ordinal)
            || value.EndsWith(" ", StringComparison.Ordinal)
            || value.Contains("..", StringComparison.Ordinal)
            || !value.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            return false;

        string stem = Path.GetFileNameWithoutExtension(value).Split('.')[0]
            .TrimEnd(' ', '.');
        string[] reserved =
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ];
        return !reserved.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryParse(
        ReadOnlySpan<byte> bytes,
        [NotNullWhen(true)] out VelopackReleaseCatalogEntry? entry)
    {
        entry = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            string[] names =
            [
                "schemaVersion", "application", "packageId", "channel", "runtimeIdentifier",
                "version", "releasedAtUtc", "packageFileName", "packageBytes", "packageSha256",
            ];
            var expected = new HashSet<string>(names, StringComparer.Ordinal);
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!expected.Contains(property.Name) || !values.TryAdd(property.Name, property.Value))
                    return false;
            }
            if (values.Count != expected.Count
                || !TryInt32(values["schemaVersion"], out int schemaVersion)
                || schemaVersion != CurrentSchemaVersion
                || !TryDisplayName(values["application"], 64, out string? application)
                || !TryBoundedString(values["packageId"], 128, out string? packageId)
                || !TryBoundedString(values["channel"], 64, out string? channel)
                || !TryBoundedString(values["runtimeIdentifier"], 32, out string? runtime)
                || !TryBoundedString(values["version"], 32, out string? versionText)
                || !TryExactVersion(versionText, out Version? version)
                || !TryInt64(values["releasedAtUtc"], out long releasedSeconds)
                || releasedSeconds <= 0
                || !TryBoundedString(values["packageFileName"], 256, out string? packageFileName)
                || !IsSafePackageFileName(packageFileName)
                || !TryInt64(values["packageBytes"], out long packageBytes)
                || packageBytes is <= 0 or > MaximumPackageBytes
                || !TrySha256(values["packageSha256"], out string? packageSha256))
                return false;

            DateTimeOffset releasedAtUtc = DateTimeOffset.FromUnixTimeSeconds(releasedSeconds);
            entry = new VelopackReleaseCatalogEntry(
                schemaVersion,
                application!,
                packageId!,
                channel!,
                runtime!,
                version!,
                releasedAtUtc,
                packageFileName!,
                packageBytes,
                packageSha256!);
            return true;
        }
        catch (Exception ex) when (ex is FormatException
            or JsonException
            or ArgumentException
            or OverflowException)
        {
            return false;
        }
    }

    private static bool TryBoundedString(
        JsonElement element,
        int maximumLength,
        [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        string? candidate = element.GetString();
        if (!IsSafeIdentity(candidate, maximumLength))
            return false;
        value = candidate!;
        return true;
    }

    private static bool TryDisplayName(
        JsonElement element,
        int maximumLength,
        [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        string? candidate = element.GetString();
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Length > maximumLength
            || !candidate.Equals(candidate.Trim(), StringComparison.Ordinal)
            || !candidate.All(character => character is >= ' ' and <= '~'))
            return false;
        value = candidate;
        return true;
    }

    private static bool IsSafeIdentity(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && value.Equals(value.Trim(), StringComparison.Ordinal)
        && value.All(character => character is >= '!' and <= '~');

    private static bool TryExactVersion(
        string? value,
        [NotNullWhen(true)] out Version? version)
    {
        version = null;
        if (value is null
            || !Version.TryParse(value, out Version? parsed)
            || parsed.Major <= 0
            || parsed.Build < 0
            || parsed.Revision >= 0
            || !parsed.ToString(3).Equals(value, StringComparison.Ordinal))
            return false;
        version = parsed;
        return true;
    }

    private static bool TrySha256(
        JsonElement element,
        [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        string? candidate = element.GetString();
        if (!IsSha256(candidate))
            return false;
        value = candidate!;
        return true;
    }

    private static bool IsSha256(string? value) =>
        value is not null
        && value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool TryInt32(JsonElement element, out int value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    private static bool TryInt64(JsonElement element, out long value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    private static bool HasUtf8Bom(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
}
