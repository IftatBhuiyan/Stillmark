using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WinShot.Core.Update;

internal sealed record ParsedReleaseManifest(
    string Application,
    Version Version,
    string RuntimeIdentifier,
    string PackageKind,
    bool WorkingTreeDirty,
    bool BuildInputsDirty,
    int ApplicationMajor,
    ReleaseManifestEvidence Evidence);

/// <summary>
/// Reads the exact schema emitted by tools/release/Release.Common.ps1. Inventory entries are
/// validated as signed declarations only; this reader does not inspect an installer's contents.
/// </summary>
internal static class ReleaseManifestReader
{
    // Mirrored from packaging/release.json validationLimits. Keep these values in lockstep with
    // that reviewed release contract.
    internal const int MaximumFileCount = 2048;
    internal const int MaximumRelativePathLength = 512;
    internal const long MaximumFileBytes = 134_217_728;
    internal const long MaximumTotalBytes = 536_870_912;
    private const string ChecksumFileName = "SHA256SUMS";
    // packaging/release.json requiredPayloadFiles, except its unsigned-only marker. A signed
    // production manifest must contain every real payload name and must refuse that marker.
    private static readonly string[] RequiredPayloadFileNames =
    [
        "WinShot.exe",
        "WinShot.Startup.exe",
        "WinShot.dll",
        "WinShot.Core.dll",
        "WinShot.Capture.dll",
        "WinShot.Interop.dll",
        "WinShot.Licensing.dll",
        "WinShot.deps.json",
        "WinShot.runtimeconfig.json",
        "startup-build.json",
        "coreclr.dll",
        "clrjit.dll",
        "hostfxr.dll",
        "hostpolicy.dll",
        "System.Private.CoreLib.dll",
        "ScreenRecorderLib.dll",
        "commercial-config.json",
        "THIRD-PARTY-NOTICES.md",
    ];
    private const string UnsignedTestMarkerFileName = "UNSIGNED-TEST-ARTIFACT.txt";

    internal static bool TryRead(
        byte[] bytes,
        [NotNullWhen(true)] out ParsedReleaseManifest? manifest)
    {
        manifest = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 10,
                });
            if (!TryExactObject(
                    document.RootElement,
                    [
                        "schemaVersion",
                        "application",
                        "version",
                        "runtimeIdentifier",
                        "packageKind",
                        "selfContained",
                        "trimmed",
                        "singleFile",
                        "sourceProvenance",
                        "commercialConfiguration",
                        "files",
                    ],
                    out Dictionary<string, JsonElement>? root)
                || !TryCanonicalInt32(root["schemaVersion"], out int schemaVersion)
                || schemaVersion != 1
                || !TryIdentity(root["application"], 64, out string? application)
                || !TryIdentity(root["version"], 32, out string? versionText)
                || !TryExactVersion(versionText, out Version? version)
                || !TryIdentity(
                    root["runtimeIdentifier"],
                    32,
                    out string? runtimeIdentifier)
                || !TryIdentity(root["packageKind"], 32, out string? packageKind)
                || !TryBoolean(root["selfContained"], out bool selfContained)
                || !selfContained
                || !TryBoolean(root["trimmed"], out bool trimmed)
                || trimmed
                || !TryBoolean(root["singleFile"], out bool singleFile)
                || singleFile
                || !TryReadSourceProvenance(
                    root["sourceProvenance"],
                    out SourceProvenance? source)
                || !TryReadCommercialConfiguration(
                    root["commercialConfiguration"],
                    out CommercialConfiguration? commercial)
                || !TryReadFiles(
                    root["files"],
                    out int fileCount,
                    out long declaredPayloadBytes,
                    out Dictionary<string, string>? inventory)
                || !inventory.TryGetValue(
                    "commercial-config.json",
                    out string? commercialConfigurationSha256)
                || !commercialConfigurationSha256.Equals(
                    commercial.Sha256,
                    StringComparison.Ordinal))
            {
                return false;
            }

            manifest = new ParsedReleaseManifest(
                application,
                version,
                runtimeIdentifier,
                packageKind,
                source.WorkingTreeDirty,
                source.BuildInputsDirty,
                commercial.ApplicationMajor,
                new ReleaseManifestEvidence(
                    schemaVersion,
                    fileCount,
                    declaredPayloadBytes,
                    source.BuildInputsSha256,
                    source.PackagingToolsSha256,
                    commercial.Sha256));
            return true;
        }
        catch (Exception ex) when (ex is JsonException
            or InvalidOperationException
            or FormatException
            or OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadSourceProvenance(
        JsonElement element,
        [NotNullWhen(true)] out SourceProvenance? source)
    {
        source = null;
        if (!TryExactObject(
                element,
                [
                    "commitSha",
                    "workingTreeDirty",
                    "buildInputsDirty",
                    "buildInputsSha256",
                    "buildInputFileCount",
                    "packagingToolsSha256",
                    "packagingToolFileCount",
                    "pinnedSdkVersion",
                ],
                out Dictionary<string, JsonElement>? values)
            || !TryString(values["commitSha"], 40, out string? commitSha)
            || !IsLowerHex(commitSha, 40)
            || !TryBoolean(values["workingTreeDirty"], out bool workingTreeDirty)
            || !TryBoolean(values["buildInputsDirty"], out bool buildInputsDirty)
            || !TrySha256(values["buildInputsSha256"], out string? buildInputsSha256)
            || !TryPositiveInt32(values["buildInputFileCount"], out _)
            || !TrySha256(
                values["packagingToolsSha256"],
                out string? packagingToolsSha256)
            || !TryPositiveInt32(values["packagingToolFileCount"], out _)
            || !TryString(values["pinnedSdkVersion"], 32, out string? sdkVersion)
            || !TryExactThreePartVersion(sdkVersion))
        {
            return false;
        }

        source = new SourceProvenance(
            workingTreeDirty,
            buildInputsDirty,
            buildInputsSha256,
            packagingToolsSha256);
        return true;
    }

    private static bool TryReadCommercialConfiguration(
        JsonElement element,
        [NotNullWhen(true)] out CommercialConfiguration? configuration)
    {
        configuration = null;
        if (!TryExactObject(
                element,
                [
                    "sha256",
                    "contractRevision",
                    "applicationMajor",
                    "licenseMode",
                    "provider",
                    "checkoutApproved",
                    "buyerTermsApproved",
                    "primaryVariantId",
                ],
                out Dictionary<string, JsonElement>? values)
            || !TrySha256(values["sha256"], out string? sha256)
            || !TryPositiveInt32(values["contractRevision"], out _)
            || !TryPositiveInt32(values["applicationMajor"], out int applicationMajor)
            || !TryString(values["licenseMode"], 32, out string? licenseMode)
            || !TryBoolean(values["checkoutApproved"], out bool checkoutApproved)
            || !TryBoolean(values["buyerTermsApproved"], out bool buyerTermsApproved)
            || checkoutApproved != buyerTermsApproved
            || !TryNullablePositiveInt64(
                values["primaryVariantId"],
                out long? primaryVariantId))
        {
            return false;
        }

        if (!licenseMode.Equals("offline-signed", StringComparison.Ordinal)
            || !checkoutApproved
            || !buyerTermsApproved
            || primaryVariantId is not null
            || values["provider"].ValueKind != JsonValueKind.Null)
        {
            return false;
        }

        configuration = new CommercialConfiguration(applicationMajor, sha256);
        return true;
    }

    private static bool TryReadFiles(
        JsonElement element,
        out int fileCount,
        out long declaredPayloadBytes,
        [NotNullWhen(true)] out Dictionary<string, string>? inventory)
    {
        fileCount = default;
        declaredPayloadBytes = default;
        inventory = null;
        if (element.ValueKind != JsonValueKind.Array)
            return false;

        fileCount = element.GetArrayLength();
        if (fileCount is < 1 or > MaximumFileCount)
            return false;

        var ordinalPaths = new HashSet<string>(StringComparer.Ordinal);
        var windowsPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var collected = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (JsonElement entry in element.EnumerateArray())
            {
                if (!TryExactObject(
                        entry,
                        ["path", "bytes", "sha256"],
                        out Dictionary<string, JsonElement>? values)
                    || !TryString(
                        values["path"],
                        MaximumRelativePathLength,
                        out string? path)
                    || !IsCanonicalRelativePath(path)
                    || !ordinalPaths.Add(path)
                    || !windowsPaths.Add(path)
                    || !TryCanonicalInt64(values["bytes"], out long bytes)
                    || bytes is < 0 or > MaximumFileBytes
                    || !TrySha256(values["sha256"], out string? sha256))
                {
                    return false;
                }

                declaredPayloadBytes = checked(declaredPayloadBytes + bytes);
                if (declaredPayloadBytes > MaximumTotalBytes)
                    return false;
                collected.Add(path, sha256);
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        if (RequiredPayloadFileNames.Any(required => !collected.ContainsKey(required))
            || windowsPaths.Contains(UnsignedTestMarkerFileName))
            return false;

        inventory = collected;
        return true;
    }

    private static bool IsCanonicalRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumRelativePathLength
            || !value.IsNormalized(NormalizationForm.FormC)
            || value.StartsWith("/", StringComparison.Ordinal)
            || value.EndsWith("/", StringComparison.Ordinal)
            || value.Contains('\\')
            || value.Contains(':')
            || Path.IsPathRooted(value))
        {
            return false;
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        string[] segments = value.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length is < 1 or > 255
                || segment is "." or ".."
                || !segment.Equals(segment.Trim(), StringComparison.Ordinal)
                || segment.EndsWith(".", StringComparison.Ordinal)
                || segment.IndexOfAny(invalid) >= 0
                || IsReservedDeviceName(segment))
            {
                return false;
            }
        }

        string fileName = segments[^1];
        return !fileName.Equals(
                UpdateCandidateVerifier.ReleaseManifestFileName,
                StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals(ChecksumFileName, StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals(
                UnsignedTestMarkerFileName,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReservedDeviceName(string segment)
    {
        string stem = segment.Split('.')[0].TrimEnd(' ', '.');
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4
                && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && (stem[3] is >= '1' and <= '9'
                    or '\u00B9'
                    or '\u00B2'
                    or '\u00B3'));
    }

    private static bool TryExactObject(
        JsonElement element,
        string[] expectedNames,
        [NotNullWhen(true)] out Dictionary<string, JsonElement>? values)
    {
        values = null;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var expected = new HashSet<string>(expectedNames, StringComparer.Ordinal);
        var collected = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name)
                || !collected.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }

        if (collected.Count != expected.Count)
            return false;

        values = collected;
        return true;
    }

    private static bool TryIdentity(
        JsonElement element,
        int maximumLength,
        [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (!TryString(element, maximumLength, out string? candidate)
            || candidate.Any(character => character is < '!' or > '~'))
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static bool TryString(
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
            || !candidate.Equals(candidate.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static bool TrySha256(
        JsonElement element,
        [NotNullWhen(true)] out string? value)
    {
        value = null;
        return TryString(element, 64, out string? candidate)
            && IsUpperHex(candidate, 64)
            && Assign(candidate, out value);
    }

    private static bool Assign<T>(T value, out T assigned)
    {
        assigned = value;
        return true;
    }

    private static bool IsUpperHex(string value, int length) =>
        value.Length == length
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'A' and <= 'F');

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f');

    private static bool TryExactVersion(
        string value,
        [NotNullWhen(true)] out Version? version)
    {
        version = null;
        if (!Version.TryParse(value, out Version? parsed)
            || parsed.Major <= 0
            || parsed.Build < 0
            || parsed.Revision >= 0
            || !parsed.ToString(3).Equals(value, StringComparison.Ordinal))
        {
            return false;
        }

        version = parsed;
        return true;
    }

    private static bool TryExactThreePartVersion(string value) =>
        Version.TryParse(value, out Version? parsed)
        && parsed.Build >= 0
        && parsed.Revision < 0
        && parsed.ToString(3).Equals(value, StringComparison.Ordinal);

    private static bool TryBoolean(JsonElement element, out bool value)
    {
        value = default;
        if (element.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }

        return element.ValueKind == JsonValueKind.False;
    }

    private static bool TryPositiveInt32(JsonElement element, out int value) =>
        TryCanonicalInt32(element, out value) && value > 0;

    private static bool TryPositiveInt64(JsonElement element, out long value) =>
        TryCanonicalInt64(element, out value) && value > 0;

    private static bool TryCanonicalInt32(JsonElement element, out int value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value)
            && element.GetRawText().Equals(
                value.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
    }

    private static bool TryCanonicalInt64(JsonElement element, out long value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value)
            && element.GetRawText().Equals(
                value.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
    }

    private static bool TryNullablePositiveInt64(
        JsonElement element,
        out long? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Null)
            return true;
        if (!TryPositiveInt64(element, out long parsed))
            return false;
        value = parsed;
        return true;
    }

    private static bool TryCanonicalPositiveInt64(string value, out long parsed) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)
        && parsed > 0
        && parsed.ToString(CultureInfo.InvariantCulture).Equals(
            value,
            StringComparison.Ordinal);

    private static bool TryCanonicalPositiveInt32(string value, out int parsed) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)
        && parsed > 0
        && parsed.ToString(CultureInfo.InvariantCulture).Equals(
            value,
            StringComparison.Ordinal);

    private sealed record SourceProvenance(
        bool WorkingTreeDirty,
        bool BuildInputsDirty,
        string BuildInputsSha256,
        string PackagingToolsSha256);

    private sealed record CommercialConfiguration(
        int ApplicationMajor,
        string Sha256);

}
