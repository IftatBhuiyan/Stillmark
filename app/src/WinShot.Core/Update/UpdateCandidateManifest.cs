using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32.SafeHandles;

namespace WinShot.Core.Update;

internal sealed record UpdateCandidateExpectations(
    string Application,
    string PackageId,
    string Channel,
    string RuntimeIdentifier,
    string StagingDirectory,
    string ReleasePublicKeySha256,
    Version CurrentVersion,
    int EntitledMajor,
    DateTimeOffset? UpdatesUntilUtc = null,
    DateTimeOffset? CurrentReleasedAtUtc = null);

internal enum UpdateCandidateVerificationStatus
{
    Verified,
    InvalidInput,
    StagingRootInvalid,
    DescriptorMissing,
    SignatureMissing,
    DescriptorTooLarge,
    SignatureInvalid,
    PublicKeyInvalid,
    ManifestInvalid,
    IdentityMismatch,
    PreflightRejected,
    AuthenticityRejected,
    PayloadMissing,
    PayloadUnexpected,
    PayloadChanged,
    UnsafeFilesystemEntry,
    IoFailure,
}

internal enum UpdateCandidateAuthenticityStatus
{
    Trusted,
    Untrusted,
    PublisherMismatch,
    TimestampInvalid,
    VerificationUnavailable,
}

/// <summary>
/// Installer adapter boundary for Windows Authenticode, exact publisher, and RFC 3161 timestamp
/// verification. The handle is borrowed and identifies the already opened, digest-verified file;
/// adapters must not close it or reopen the display name. A production implementation must pass
/// this handle as WINTRUST_FILE_INFO.hFile. Certificate extraction alone is not sufficient.
/// </summary>
internal interface IUpdateCandidateAuthenticityVerifier
{
    ValueTask<UpdateCandidateAuthenticityStatus> VerifyAsync(
        SafeFileHandle packageHandle,
        string packageDisplayName,
        CancellationToken cancellationToken);
}

internal sealed class UpdateCandidateVerificationResult
{
    internal UpdateCandidateVerificationResult(
        UpdateCandidateVerificationStatus status,
        VerifiedUpdateCandidate? candidate = null)
    {
        Status = status;
        Candidate = candidate;
    }

    public UpdateCandidateVerificationStatus Status { get; }

    public VerifiedUpdateCandidate? Candidate { get; }

    public bool IsVerified =>
        Status == UpdateCandidateVerificationStatus.Verified && Candidate is not null;
}

/// <summary>
/// Validated metadata declared by the signed release manifest. This evidence binds the manifest
/// schema and inventory summary to the update capability; it does not claim that files embedded
/// inside an installer executable were extracted or individually inspected.
/// </summary>
public sealed class ReleaseManifestEvidence
{
    internal ReleaseManifestEvidence(
        int schemaVersion,
        int declaredFileCount,
        long declaredPayloadBytes,
        string buildInputsSha256,
        string packagingToolsSha256,
        string commercialConfigurationSha256)
    {
        SchemaVersion = schemaVersion;
        DeclaredFileCount = declaredFileCount;
        DeclaredPayloadBytes = declaredPayloadBytes;
        BuildInputsSha256 = buildInputsSha256;
        PackagingToolsSha256 = packagingToolsSha256;
        CommercialConfigurationSha256 = commercialConfigurationSha256;
    }

    public int SchemaVersion { get; }

    public int DeclaredFileCount { get; }

    public long DeclaredPayloadBytes { get; }

    public string BuildInputsSha256 { get; }

    public string PackagingToolsSha256 { get; }

    public string CommercialConfigurationSha256 { get; }
}

/// <summary>
/// Opaque evidence that one exact staged candidate passed descriptor-signature, identity,
/// entitlement, staging-inventory, size, and digest verification. The constructor is intentionally
/// internal so UI or installer adapters cannot manufacture trusted-candidate booleans.
/// </summary>
public sealed class VerifiedUpdateCandidate : IDisposable
{
    private int _consumed;
    private readonly long _verifiedTimestamp;
    private readonly TimeSpan _capabilityLifetime;
    private readonly Action? _ownershipAcquiredHook;
    private FileStream? _packageLease;
    private FileStream? _releaseManifestLease;

    internal VerifiedUpdateCandidate(
        Version version,
        string packageId,
        string channel,
        string runtimeIdentifier,
        string packagePath,
        string packageSha256,
        UpdatePackageFileIdentity packageFileIdentity,
        string releaseManifestSha256,
        ReleaseManifestEvidence releaseManifest,
        DateTimeOffset verifiedAtUtc,
        DateTimeOffset expiresAtUtc,
        long verifiedTimestamp,
        TimeSpan capabilityLifetime,
        FileStream packageLease,
        FileStream releaseManifestLease,
        Action? ownershipAcquiredHook = null,
        DateTimeOffset? releasedAtUtc = null)
    {
        Version = version;
        ReleasedAtUtc = releasedAtUtc;
        PackageId = packageId;
        Channel = channel;
        RuntimeIdentifier = runtimeIdentifier;
        PackagePath = packagePath;
        PackageSha256 = packageSha256;
        PackageFileIdentity = packageFileIdentity;
        ReleaseManifestSha256 = releaseManifestSha256;
        ReleaseManifest = releaseManifest;
        VerifiedAtUtc = verifiedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        _verifiedTimestamp = verifiedTimestamp;
        _capabilityLifetime = capabilityLifetime;
        _packageLease = packageLease;
        _releaseManifestLease = releaseManifestLease;
        _ownershipAcquiredHook = ownershipAcquiredHook;
    }

    public Version Version { get; }

    public DateTimeOffset? ReleasedAtUtc { get; }

    public string PackageId { get; }

    public string Channel { get; }

    public string RuntimeIdentifier { get; }

    public string PackageSha256 { get; }

    public UpdatePackageFileIdentity PackageFileIdentity { get; }

    public string ReleaseManifestSha256 { get; }

    public ReleaseManifestEvidence ReleaseManifest { get; }

    public DateTimeOffset VerifiedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    internal bool TryCreateHandoffAuthorization(
        out UpdateHandoffAuthorization? authorization)
    {
        authorization = null;
        if (Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
            return false;
        _ownershipAcquiredHook?.Invoke();
        if (Stopwatch.GetElapsedTime(_verifiedTimestamp) > _capabilityLifetime)
        {
            Dispose();
            return false;
        }

        FileStream? packageLease = Interlocked.Exchange(ref _packageLease, null);
        FileStream? manifestLease = Interlocked.Exchange(ref _releaseManifestLease, null);
        if (packageLease is null || manifestLease is null)
        {
            packageLease?.Dispose();
            manifestLease?.Dispose();
            return false;
        }
        if (Stopwatch.GetElapsedTime(_verifiedTimestamp) > _capabilityLifetime)
        {
            packageLease.Dispose();
            manifestLease.Dispose();
            return false;
        }

        authorization = new UpdateHandoffAuthorization(
            Version,
            PackageId,
            Channel,
            RuntimeIdentifier,
            PackagePath,
            PackageSha256,
            PackageFileIdentity,
            ReleaseManifestSha256,
            ReleaseManifest,
            ExpiresAtUtc,
            _verifiedTimestamp,
            _capabilityLifetime,
            packageLease,
            manifestLease);
        return true;
    }

    internal string PackagePath { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _packageLease, null)?.Dispose();
        Interlocked.Exchange(ref _releaseManifestLease, null)?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Single-use handoff lease bound to the exact verified package path and digest. The held file
/// handles deny write/delete sharing until lease-protected process creation completes or fails.
/// </summary>
public sealed class UpdateHandoffAuthorization : IDisposable
{
    private int _consumed;
    private readonly long _verifiedTimestamp;
    private readonly TimeSpan _capabilityLifetime;
    private readonly Action? _ownershipAcquiredHook;
    private FileStream? _packageLease;
    private FileStream? _releaseManifestLease;

    internal UpdateHandoffAuthorization(
        Version version,
        string packageId,
        string channel,
        string runtimeIdentifier,
        string packagePath,
        string packageSha256,
        UpdatePackageFileIdentity packageFileIdentity,
        string releaseManifestSha256,
        ReleaseManifestEvidence releaseManifest,
        DateTimeOffset expiresAtUtc,
        long verifiedTimestamp,
        TimeSpan capabilityLifetime,
        FileStream packageLease,
        FileStream releaseManifestLease,
        Action? ownershipAcquiredHook = null)
    {
        Version = version;
        PackageId = packageId;
        Channel = channel;
        RuntimeIdentifier = runtimeIdentifier;
        _packagePath = packagePath;
        PackageSha256 = packageSha256;
        PackageFileIdentity = packageFileIdentity;
        ReleaseManifestSha256 = releaseManifestSha256;
        ReleaseManifest = releaseManifest;
        ExpiresAtUtc = expiresAtUtc;
        _verifiedTimestamp = verifiedTimestamp;
        _capabilityLifetime = capabilityLifetime;
        _packageLease = packageLease;
        _releaseManifestLease = releaseManifestLease;
        _ownershipAcquiredHook = ownershipAcquiredHook;
    }

    private readonly string _packagePath;

    public Version Version { get; }

    public string PackageId { get; }

    public string Channel { get; }

    public string RuntimeIdentifier { get; }

    public string PackageSha256 { get; }

    public UpdatePackageFileIdentity PackageFileIdentity { get; }

    public string ReleaseManifestSha256 { get; }

    public ReleaseManifestEvidence ReleaseManifest { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    internal bool TryAcquireExecutionLease(
        [NotNullWhen(true)] out UpdateExecutionLease? executionLease)
    {
        executionLease = null;
        if (Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
            return false;
        _ownershipAcquiredHook?.Invoke();
        if (Stopwatch.GetElapsedTime(_verifiedTimestamp) > _capabilityLifetime)
        {
            Dispose();
            return false;
        }

        FileStream? packageLease = Interlocked.Exchange(ref _packageLease, null);
        FileStream? manifestLease = Interlocked.Exchange(ref _releaseManifestLease, null);
        if (packageLease is null || manifestLease is null)
        {
            packageLease?.Dispose();
            manifestLease?.Dispose();
            return false;
        }
        if (Stopwatch.GetElapsedTime(_verifiedTimestamp) > _capabilityLifetime)
        {
            packageLease.Dispose();
            manifestLease.Dispose();
            return false;
        }

        executionLease = new UpdateExecutionLease(
            _packagePath,
            PackageSha256,
            PackageFileIdentity,
            ReleaseManifest,
            packageLease,
            manifestLease,
            _verifiedTimestamp,
            _capabilityLifetime);
        return true;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _packageLease, null)?.Dispose();
        Interlocked.Exchange(ref _releaseManifestLease, null)?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Owns the verified file handles while an approved launcher accepts the exact package. The
/// borrowed PackageHandle is the authority for file identity. PackagePath is display/lookup
/// metadata only: invoking path-only Process.Start is not an authorized update handoff. Dispose
/// only after an approved no-shell launch boundary has retained the handle, immediately
/// revalidated its path and identity, and completed or rejected path-based process creation.
/// </summary>
public sealed class UpdateExecutionLease : IDisposable
{
    private int _launchConsumed;
    private readonly long _verifiedTimestamp;
    private readonly TimeSpan _capabilityLifetime;
    private FileStream? _packageLease;
    private FileStream? _releaseManifestLease;

    internal UpdateExecutionLease(
        string packagePath,
        string packageSha256,
        UpdatePackageFileIdentity packageFileIdentity,
        ReleaseManifestEvidence releaseManifest,
        FileStream packageLease,
        FileStream releaseManifestLease,
        long? verifiedTimestamp = null,
        TimeSpan? capabilityLifetime = null)
    {
        PackagePath = packagePath;
        PackageSha256 = packageSha256;
        PackageFileIdentity = packageFileIdentity;
        ReleaseManifest = releaseManifest;
        _verifiedTimestamp = verifiedTimestamp ?? Stopwatch.GetTimestamp();
        _capabilityLifetime = capabilityLifetime ?? TimeSpan.FromMinutes(10);
        if (_capabilityLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(capabilityLifetime));
        _packageLease = packageLease;
        _releaseManifestLease = releaseManifestLease;
    }

    public string PackagePath { get; }

    public string PackageSha256 { get; }

    public UpdatePackageFileIdentity PackageFileIdentity { get; }

    /// <summary>
    /// Borrowed handle for the exact digest-verified package. The execution lease retains
    /// ownership; callers must not close or dispose this handle.
    /// </summary>
    public SafeFileHandle PackageHandle
    {
        get
        {
            FileStream? packageLease = Volatile.Read(ref _packageLease);
            if (packageLease is null)
                throw new ObjectDisposedException(nameof(UpdateExecutionLease));
            return packageLease.SafeFileHandle;
        }
    }

    public ReleaseManifestEvidence ReleaseManifest { get; }

    internal bool TryAcquireLaunchHandle(
        [NotNullWhen(true)] out SafeFileHandle? packageHandle)
    {
        packageHandle = null;
        if (Interlocked.CompareExchange(ref _launchConsumed, 1, 0) != 0)
            return false;
        if (Stopwatch.GetElapsedTime(_verifiedTimestamp) > _capabilityLifetime)
        {
            Dispose();
            return false;
        }

        FileStream? packageLease = Volatile.Read(ref _packageLease);
        if (packageLease is null)
        {
            Dispose();
            return false;
        }

        SafeFileHandle? candidate = null;
        bool referenceAdded = false;
        try
        {
            candidate = packageLease.SafeFileHandle;
            candidate.DangerousAddRef(ref referenceAdded);
            if (!referenceAdded || candidate.IsInvalid)
                return false;
            if (Stopwatch.GetElapsedTime(_verifiedTimestamp) > _capabilityLifetime)
                return false;

            packageHandle = candidate;
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            if (packageHandle is null)
            {
                if (referenceAdded)
                    candidate!.DangerousRelease();
                Dispose();
            }
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _packageLease, null)?.Dispose();
        Interlocked.Exchange(ref _releaseManifestLease, null)?.Dispose();
        GC.SuppressFinalize(this);
    }
}
