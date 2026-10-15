using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinShot.Core.Update;

/// <summary>
/// Authenticates and validates one installer-owned staged update candidate without contacting a
/// network service. Production Authenticode, timestamp, publisher, and installer binding remain
/// additional mandatory release gates.
/// </summary>
internal static class UpdateCandidateVerifier
{
    public const string DescriptorFileName = "update-candidate.json";
    public const string SignatureFileName = "update-candidate.sig";
    public const string ReleaseManifestFileName = "release-manifest.json";
    public const int MaximumDescriptorBytes = 32 * 1024;
    public const int MaximumSignatureBytes = 1024;
    public const long MaximumPackageBytes = 512L * 1024 * 1024;
    public const long MaximumReleaseManifestBytes = 16L * 1024 * 1024;
    public static readonly TimeSpan CapabilityLifetime = TimeSpan.FromMinutes(10);

    public static async Task<UpdateCandidateVerificationResult> VerifyAsync(
        string? releasePublicKeyPem,
        UpdateCandidateExpectations? expectations,
        IUpdateCandidateAuthenticityVerifier? authenticityVerifier,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryValidateExpectations(expectations)
            || string.IsNullOrWhiteSpace(releasePublicKeyPem)
            || authenticityVerifier is null)
        {
            return Result(UpdateCandidateVerificationStatus.InvalidInput);
        }

        string root;
        try
        {
            root = Path.GetFullPath(expectations!.StagingDirectory).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (root.Length == 0
                || !Directory.Exists(root)
                || !IsSafeOwnedStagingRoot(root))
            {
                return Result(UpdateCandidateVerificationStatus.StagingRootInvalid);
            }
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return Result(UpdateCandidateVerificationStatus.StagingRootInvalid);
        }

        string descriptorPath = Path.Combine(root, DescriptorFileName);
        string signaturePath = Path.Combine(root, SignatureFileName);
        string releaseManifestPath = Path.Combine(root, ReleaseManifestFileName);
        if (!File.Exists(descriptorPath))
            return Result(UpdateCandidateVerificationStatus.DescriptorMissing);
        if (!File.Exists(signaturePath))
            return Result(UpdateCandidateVerificationStatus.SignatureMissing);

        try
        {
            if (IsReparsePoint(descriptorPath)
                || IsReparsePoint(signaturePath)
                || (File.Exists(releaseManifestPath) && IsReparsePoint(releaseManifestPath)))
            {
                return Result(UpdateCandidateVerificationStatus.UnsafeFilesystemEntry);
            }

            var descriptorInfo = new FileInfo(descriptorPath);
            if (descriptorInfo.Length is <= 0 or > MaximumDescriptorBytes)
                return Result(UpdateCandidateVerificationStatus.DescriptorTooLarge);

            var signatureInfo = new FileInfo(signaturePath);
            if (signatureInfo.Length is <= 0 or > MaximumSignatureBytes)
                return Result(UpdateCandidateVerificationStatus.SignatureInvalid);

            byte[] descriptorBytes = await File.ReadAllBytesAsync(
                descriptorPath,
                cancellationToken).ConfigureAwait(false);
            byte[] signatureBytes = await File.ReadAllBytesAsync(
                signaturePath,
                cancellationToken).ConfigureAwait(false);
            if (descriptorBytes.Length is <= 0 or > MaximumDescriptorBytes)
                return Result(UpdateCandidateVerificationStatus.DescriptorTooLarge);
            if (signatureBytes.Length is <= 0 or > MaximumSignatureBytes)
                return Result(UpdateCandidateVerificationStatus.SignatureInvalid);
            if (HasUtf8Bom(descriptorBytes))
                return Result(UpdateCandidateVerificationStatus.ManifestInvalid);

            UpdateCandidateVerificationStatus signatureStatus = VerifySignature(
                descriptorBytes,
                signatureBytes,
                releasePublicKeyPem,
                expectations.ReleasePublicKeySha256);
            if (signatureStatus != UpdateCandidateVerificationStatus.Verified)
                return Result(signatureStatus);

            if (!TryParseDescriptor(
                    descriptorBytes,
                    out ParsedUpdateCandidate? parsed))
            {
                return Result(UpdateCandidateVerificationStatus.ManifestInvalid);
            }

            if (!MatchesExpectations(parsed, expectations!))
                return Result(UpdateCandidateVerificationStatus.IdentityMismatch);

            UpdatePreflightDecision preflight = UpdatePreflightPolicy.Evaluate(
                new UpdatePreflightInput(
                    expectations!.CurrentVersion,
                    parsed.Version,
                    expectations.EntitledMajor,
                    expectations.UpdatesUntilUtc,
                    expectations.CurrentReleasedAtUtc,
                    parsed.ReleasedAtUtc));
            if (!preflight.CanStage)
                return Result(UpdateCandidateVerificationStatus.PreflightRejected);

            string packagePath = Path.Combine(root, parsed.PackageFileName);
            if (!File.Exists(packagePath) || !File.Exists(releaseManifestPath))
                return Result(UpdateCandidateVerificationStatus.PayloadMissing);
            if (IsReparsePoint(packagePath) || IsReparsePoint(releaseManifestPath))
                return Result(UpdateCandidateVerificationStatus.UnsafeFilesystemEntry);

            UpdateCandidateVerificationStatus inventoryStatus = VerifyExactInventory(
                root,
                parsed.PackageFileName);
            if (inventoryStatus != UpdateCandidateVerificationStatus.Verified)
                return Result(inventoryStatus);

            FileStream? packageLease = null;
            FileStream? manifestLease = null;
            try
            {
                VerifiedFileOpenResult packageResult = await OpenAndVerifyFileAsync(
                    packagePath,
                    parsed.PackageBytes,
                    parsed.PackageSha256,
                    MaximumPackageBytes,
                    cancellationToken).ConfigureAwait(false);
                packageLease = packageResult.Lease;
                if (packageResult.Status != UpdateCandidateVerificationStatus.Verified
                    || packageLease is null
                    || packageResult.Identity is null)
                {
                    return Result(packageResult.Status);
                }

                VerifiedFileOpenResult manifestResult = await OpenAndVerifyFileAsync(
                    releaseManifestPath,
                    parsed.ReleaseManifestBytes,
                    parsed.ReleaseManifestSha256,
                    MaximumReleaseManifestBytes,
                    cancellationToken).ConfigureAwait(false);
                manifestLease = manifestResult.Lease;
                if (manifestResult.Status != UpdateCandidateVerificationStatus.Verified
                    || manifestLease is null)
                {
                    return Result(manifestResult.Status);
                }

                ReleaseManifestBindingResult binding =
                    await VerifyReleaseManifestBindingAsync(
                        manifestLease,
                        parsed,
                        cancellationToken).ConfigureAwait(false);
                if (binding.Status != UpdateCandidateVerificationStatus.Verified
                    || binding.Manifest is null)
                {
                    return Result(binding.Status);
                }

                UpdateCandidateAuthenticityStatus authenticity;
                SafeFileHandle packageHandle = packageLease.SafeFileHandle;
                bool packageHandleRefAdded = false;
                try
                {
                    packageHandle.DangerousAddRef(ref packageHandleRefAdded);
                    authenticity = await authenticityVerifier.VerifyAsync(
                        packageHandle,
                        parsed.PackageFileName,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is Win32Exception
                    or InvalidOperationException
                    or NotSupportedException
                    or CryptographicException)
                {
                    return Result(UpdateCandidateVerificationStatus.AuthenticityRejected);
                }
                finally
                {
                    if (packageHandleRefAdded)
                        packageHandle.DangerousRelease();
                }
                if (packageHandle.IsClosed || packageHandle.IsInvalid)
                    return Result(UpdateCandidateVerificationStatus.AuthenticityRejected);
                if (authenticity != UpdateCandidateAuthenticityStatus.Trusted)
                    return Result(UpdateCandidateVerificationStatus.AuthenticityRejected);
                UpdateCandidateVerificationStatus pathIdentityStatus =
                    RevalidatePackagePathIdentity(
                        packagePath,
                        packageResult.Identity);
                if (pathIdentityStatus != UpdateCandidateVerificationStatus.Verified)
                    return Result(pathIdentityStatus);

                DateTimeOffset verifiedAtUtc = DateTimeOffset.UtcNow;
                long verifiedTimestamp = Stopwatch.GetTimestamp();
                var candidate = new VerifiedUpdateCandidate(
                    parsed.Version,
                    parsed.PackageId,
                    parsed.Channel,
                    parsed.RuntimeIdentifier,
                    packagePath,
                    parsed.PackageSha256,
                    packageResult.Identity,
                    parsed.ReleaseManifestSha256,
                    binding.Manifest.Evidence,
                    verifiedAtUtc,
                    verifiedAtUtc.Add(CapabilityLifetime),
                    verifiedTimestamp,
                    CapabilityLifetime,
                    packageLease,
                    manifestLease,
                    releasedAtUtc: parsed.ReleasedAtUtc);
                packageLease = null;
                manifestLease = null;
                return Result(UpdateCandidateVerificationStatus.Verified, candidate);
            }
            finally
            {
                packageLease?.Dispose();
                manifestLease?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or CryptographicException)
        {
            return Result(UpdateCandidateVerificationStatus.IoFailure);
        }
    }

    private static UpdateCandidateVerificationStatus VerifySignature(
        byte[] descriptorBytes,
        byte[] signatureBytes,
        string publicKeyPem,
        string expectedPublicKeySha256)
    {
        if (publicKeyPem.Length > 16 * 1024
            || publicKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            return UpdateCandidateVerificationStatus.PublicKeyInvalid;
        }

        try
        {
            using RSA rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            if (rsa.KeySize < 3072 || signatureBytes.Length != rsa.KeySize / 8)
                return UpdateCandidateVerificationStatus.PublicKeyInvalid;
            string actualPublicKeySha256 = Convert.ToHexString(
                SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
            if (!actualPublicKeySha256.Equals(
                    expectedPublicKeySha256,
                    StringComparison.Ordinal))
            {
                return UpdateCandidateVerificationStatus.PublicKeyInvalid;
            }

            return rsa.VerifyData(
                descriptorBytes,
                signatureBytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss)
                ? UpdateCandidateVerificationStatus.Verified
                : UpdateCandidateVerificationStatus.SignatureInvalid;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return UpdateCandidateVerificationStatus.PublicKeyInvalid;
        }
    }

    private static bool TryParseDescriptor(
        byte[] descriptorBytes,
        [NotNullWhen(true)] out ParsedUpdateCandidate? parsed)
    {
        parsed = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                descriptorBytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });
            bool datedDescriptor = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schemaVersion", out var schema)
                && TryInt32(schema, out int descriptorSchema)
                && descriptorSchema == 2;
            string[] expectedNames =
            [
                "schemaVersion", "application", "packageId", "channel", "version", "paidMajor",
                "runtimeIdentifier", "packageKind", "packageFileName", "packageBytes",
                "packageSha256", "releaseManifestBytes", "releaseManifestSha256",
                .. datedDescriptor ? new[] { "releasedAtUtc" } : Array.Empty<string>(),
            ];
            if (!TryExactObject(
                    document.RootElement,
                    expectedNames,
                    out Dictionary<string, JsonElement>? values)
                || !TryInt32(values["schemaVersion"], out int schemaVersion)
                || schemaVersion is not (1 or 2)
                || !TryBoundedString(values["application"], 64, out string? application)
                || !TryBoundedString(values["packageId"], 128, out string? packageId)
                || !TryBoundedString(values["channel"], 64, out string? channel)
                || !TryBoundedString(values["version"], 32, out string? versionText)
                || !TryExactVersion(versionText, out Version? version)
                || !TryInt32(values["paidMajor"], out int paidMajor)
                || paidMajor <= 0
                || paidMajor != version.Major
                || !TryBoundedString(
                    values["runtimeIdentifier"],
                    32,
                    out string? runtimeIdentifier)
                || !TryBoundedString(values["packageKind"], 32, out string? packageKind)
                || !packageKind.Equals("SIGNED_PRODUCTION", StringComparison.Ordinal)
                || !TryBoundedString(
                    values["packageFileName"],
                    128,
                    out string? packageFileName)
                || !IsSafePackageFileName(packageFileName)
                || !TryInt64(values["packageBytes"], out long packageBytes)
                || packageBytes is <= 0 or > MaximumPackageBytes
                || !TrySha256(values["packageSha256"], out string? packageSha256)
                || !TryInt64(
                    values["releaseManifestBytes"],
                    out long releaseManifestBytes)
                || releaseManifestBytes is <= 0 or > MaximumReleaseManifestBytes
                || !TrySha256(
                    values["releaseManifestSha256"],
                    out string? releaseManifestSha256))
            {
                return false;
            }

            DateTimeOffset? releasedAtUtc = null;
            if (datedDescriptor)
            {
                if (!TryInt64(values["releasedAtUtc"], out long seconds)
                    || seconds is <= 0 or > 253_402_300_799)
                    return false;
                releasedAtUtc = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }

            parsed = new ParsedUpdateCandidate(
                application,
                packageId,
                channel,
                version,
                paidMajor,
                runtimeIdentifier,
                packageFileName,
                packageBytes,
                packageSha256,
                releaseManifestBytes,
                releaseManifestSha256,
                releasedAtUtc);
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

    private static UpdateCandidateVerificationStatus VerifyExactInventory(
        string root,
        string packageFileName)
    {
        var expected = new HashSet<string>(
            [
                DescriptorFileName,
                SignatureFileName,
                ReleaseManifestFileName,
                packageFileName,
            ],
            StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string entry in Directory.EnumerateFileSystemEntries(
                     root,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            if (IsReparsePoint(entry))
                return UpdateCandidateVerificationStatus.UnsafeFilesystemEntry;
            if (Directory.Exists(entry))
                return UpdateCandidateVerificationStatus.PayloadUnexpected;

            string name = Path.GetFileName(entry);
            if (!expected.Contains(name) || !seen.Add(name))
                return UpdateCandidateVerificationStatus.PayloadUnexpected;
        }

        return seen.SetEquals(expected)
            ? UpdateCandidateVerificationStatus.Verified
            : UpdateCandidateVerificationStatus.PayloadMissing;
    }

    private static async Task<VerifiedFileOpenResult> OpenAndVerifyFileAsync(
        string path,
        long expectedBytes,
        string expectedSha256,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var before = new FileInfo(path);
        if (before.Length != expectedBytes || before.Length > maximumBytes)
            return new VerifiedFileOpenResult(
                UpdateCandidateVerificationStatus.PayloadChanged,
                Lease: null,
                Identity: null);

        OpenedUpdateFile opened = UpdatePackageFileHandle.OpenReadNoReparse(
            path,
            bufferSize: 128 * 1024);
        if (opened.IsUnsafeFilesystemEntry)
        {
            return new VerifiedFileOpenResult(
                UpdateCandidateVerificationStatus.UnsafeFilesystemEntry,
                Lease: null,
                Identity: null);
        }

        FileStream stream = opened.Stream
            ?? throw new IOException("The staged update payload did not open.");
        try
        {
            if (opened.Identity is null
                || opened.Identity.FileLength != expectedBytes
                || stream.Length != expectedBytes)
            {
                return new VerifiedFileOpenResult(
                    UpdateCandidateVerificationStatus.PayloadChanged,
                    Lease: null,
                    Identity: null);
            }

            byte[] hash = await SHA256.HashDataAsync(
                stream,
                cancellationToken).ConfigureAwait(false);
            if (!Convert.ToHexString(hash).Equals(expectedSha256, StringComparison.Ordinal))
            {
                return new VerifiedFileOpenResult(
                    UpdateCandidateVerificationStatus.PayloadChanged,
                    Lease: null,
                    Identity: null);
            }

            var after = new FileInfo(path);
            if (after.Length != expectedBytes
                || after.LastWriteTimeUtc != before.LastWriteTimeUtc)
            {
                return new VerifiedFileOpenResult(
                    UpdateCandidateVerificationStatus.PayloadChanged,
                    Lease: null,
                    Identity: null);
            }

            stream.Position = 0;
            var result = new VerifiedFileOpenResult(
                UpdateCandidateVerificationStatus.Verified,
                stream,
                opened.Identity);
            stream = null!;
            return result;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static UpdateCandidateVerificationStatus RevalidatePackagePathIdentity(
        string packagePath,
        UpdatePackageFileIdentity expectedIdentity)
    {
        try
        {
            OpenedUpdateFile reopened = UpdatePackageFileHandle.OpenReadNoReparse(
                packagePath,
                bufferSize: 4096);
            using FileStream? stream = reopened.Stream;
            if (reopened.IsUnsafeFilesystemEntry)
                return UpdateCandidateVerificationStatus.UnsafeFilesystemEntry;
            if (reopened.Identity is null
                || !UpdatePackageFileHandle.IsSameFile(
                    expectedIdentity,
                    reopened.Identity))
            {
                return UpdateCandidateVerificationStatus.PayloadChanged;
            }

            return UpdateCandidateVerificationStatus.Verified;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UpdateCandidateVerificationStatus.PayloadChanged;
        }
    }

    private static async Task<ReleaseManifestBindingResult>
        VerifyReleaseManifestBindingAsync(
            FileStream stream,
            ParsedUpdateCandidate parsed,
            CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        if (bytes.Length == 0
            || bytes.LongLength > MaximumReleaseManifestBytes
            || HasUtf8Bom(bytes))
        {
            return new ReleaseManifestBindingResult(
                UpdateCandidateVerificationStatus.ManifestInvalid,
                Manifest: null);
        }

        if (!ReleaseManifestReader.TryRead(
                bytes,
                out ParsedReleaseManifest? manifest))
        {
            return new ReleaseManifestBindingResult(
                UpdateCandidateVerificationStatus.ManifestInvalid,
                Manifest: null);
        }

        if (!manifest.PackageKind.Equals("SIGNED_PRODUCTION", StringComparison.Ordinal)
            || manifest.WorkingTreeDirty
            || manifest.BuildInputsDirty)
        {
            return new ReleaseManifestBindingResult(
                UpdateCandidateVerificationStatus.ManifestInvalid,
                Manifest: null);
        }

        UpdateCandidateVerificationStatus status =
            manifest.Application.Equals(parsed.Application, StringComparison.Ordinal)
            && manifest.Version == parsed.Version
            && manifest.RuntimeIdentifier.Equals(
                parsed.RuntimeIdentifier,
                StringComparison.Ordinal)
            && manifest.ApplicationMajor == parsed.PaidMajor
                ? UpdateCandidateVerificationStatus.Verified
                : UpdateCandidateVerificationStatus.IdentityMismatch;
        return new ReleaseManifestBindingResult(
            status,
            status == UpdateCandidateVerificationStatus.Verified ? manifest : null);
    }

    private static bool TryValidateExpectations(
        [NotNullWhen(true)] UpdateCandidateExpectations? expectations)
    {
        return expectations is not null
            && IsSafeIdentity(expectations.Application, 64)
            && IsSafeIdentity(expectations.PackageId, 128)
            && IsSafeIdentity(expectations.Channel, 64)
            && IsSafeIdentity(expectations.RuntimeIdentifier, 32)
            && !string.IsNullOrWhiteSpace(expectations.StagingDirectory)
            && IsSha256(expectations.ReleasePublicKeySha256)
            && expectations.CurrentVersion is not null
            && expectations.CurrentVersion.Major > 0
            && expectations.EntitledMajor > 0;
    }

    private static bool MatchesExpectations(
        ParsedUpdateCandidate parsed,
        UpdateCandidateExpectations expectations)
    {
        return parsed.Application.Equals(expectations.Application, StringComparison.Ordinal)
            && parsed.PackageId.Equals(expectations.PackageId, StringComparison.Ordinal)
            && parsed.Channel.Equals(expectations.Channel, StringComparison.Ordinal)
            && parsed.RuntimeIdentifier.Equals(
                expectations.RuntimeIdentifier,
                StringComparison.Ordinal)
            && parsed.PaidMajor == parsed.Version.Major;
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

    private static bool IsSafeIdentity(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || !value.Equals(value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        return value.All(character => character is >= '!' and <= '~');
    }

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

    private static bool IsSafePackageFileName(string value)
    {
        if (!IsSafeIdentity(value, 128)
            || !Path.GetFileName(value).Equals(value, StringComparison.Ordinal)
            || value.Contains(":", StringComparison.Ordinal)
            || value.EndsWith(".", StringComparison.Ordinal)
            || value.EndsWith(" ", StringComparison.Ordinal)
            || value.Equals(DescriptorFileName, StringComparison.OrdinalIgnoreCase)
            || value.Equals(SignatureFileName, StringComparison.OrdinalIgnoreCase)
            || value.Equals(ReleaseManifestFileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(value);
        string deviceStem = stem.Split('.')[0].TrimEnd(' ', '.');
        string[] reserved =
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ];
        if (reserved.Contains(deviceStem, StringComparer.OrdinalIgnoreCase))
            return false;

        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TrySha256(
        JsonElement element,
        [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.String)
            return false;

        string? candidate = element.GetString();
        if (candidate is null
            || candidate.Length != 64
            || candidate.Any(character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'A' and <= 'F')))
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static bool IsSha256(string? value) =>
        value is not null
        && value.Length == 64
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'A' and <= 'F');

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

    private static bool HasUtf8Bom(byte[] value) =>
        value.Length >= 3
        && value[0] == 0xEF
        && value[1] == 0xBB
        && value[2] == 0xBF;

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsSafeOwnedStagingRoot(string root)
    {
        string? volumeRoot = Path.GetPathRoot(root);
        if (string.IsNullOrEmpty(volumeRoot)
            || root.Equals(
                volumeRoot.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(@"\\", StringComparison.Ordinal)
            || root.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return false;
        }

        for (DirectoryInfo? current = new(root);
             current is not null && current.FullName.Length >= volumeRoot.Length;
             current = current.Parent)
        {
            if (!current.Exists
                || (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static UpdateCandidateVerificationResult Result(
        UpdateCandidateVerificationStatus status,
        VerifiedUpdateCandidate? candidate = null) =>
        new(status, candidate);

    private sealed record ParsedUpdateCandidate(
        string Application,
        string PackageId,
        string Channel,
        Version Version,
        int PaidMajor,
        string RuntimeIdentifier,
        string PackageFileName,
        long PackageBytes,
        string PackageSha256,
        long ReleaseManifestBytes,
        string ReleaseManifestSha256,
        DateTimeOffset? ReleasedAtUtc);

    private sealed record VerifiedFileOpenResult(
        UpdateCandidateVerificationStatus Status,
        FileStream? Lease,
        UpdatePackageFileIdentity? Identity);

    private sealed record ReleaseManifestBindingResult(
        UpdateCandidateVerificationStatus Status,
        ParsedReleaseManifest? Manifest);
}
