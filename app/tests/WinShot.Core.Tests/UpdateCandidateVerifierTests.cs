using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using WinShot.Core.Update;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class UpdateCandidateVerifierTests : IDisposable
{
    private const string Application = "WinShot";
    private const string PackageId = "example.WinShot";
    private const string Channel = "stable-v1";
    private const string RuntimeIdentifier = "win-x64";
    private const string PackageFileName = "WinShot-1.0.1-setup.exe";
    private const string BuildInputsSha256 =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string PackagingToolsSha256 =
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string CommercialConfigurationSha256 =
        "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
    private static readonly string[] RequiredManifestPayloadPaths =
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

    private static readonly Lazy<RSAParameters> SigningKey = new(CreateSigningKey);
    private readonly List<string> _temporaryDirectories = [];

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task DatedLicense_UsesReleaseDateFromSignedDescriptor(
        int cutoffOffsetSeconds, bool covered)
    {
        var cutoff = new DateTimeOffset(2027, 9, 11, 12, 0, 0, TimeSpan.Zero);
        CandidateFixture fixture = await CreateFixtureAsync(descriptorMutation: descriptor =>
        {
            descriptor["schemaVersion"] = 2;
            descriptor["releasedAtUtc"] = cutoff.AddSeconds(cutoffOffsetSeconds).ToUnixTimeSeconds();
        });
        var result = await UpdateCandidateVerifier.VerifyAsync(fixture.PublicKeyPem,
            Expectations(fixture) with
            {
                UpdatesUntilUtc = cutoff,
                CurrentReleasedAtUtc = cutoff.AddMonths(-6),
            }, TrustingAuthenticityVerifier.Instance);
        using var candidate = result.Candidate;
        Assert.Equal(covered ? UpdateCandidateVerificationStatus.Verified
            : UpdateCandidateVerificationStatus.PreflightRejected, result.Status);
    }

    [Fact]
    public async Task DatedLicense_CannotUseUndatedLegacyDescriptor()
    {
        CandidateFixture fixture = await CreateFixtureAsync();
        var result = await UpdateCandidateVerifier.VerifyAsync(fixture.PublicKeyPem,
            Expectations(fixture) with
            {
                UpdatesUntilUtc = DateTimeOffset.UtcNow,
                CurrentReleasedAtUtc = DateTimeOffset.UtcNow.AddMonths(-1),
            }, TrustingAuthenticityVerifier.Instance);
        Assert.Equal(UpdateCandidateVerificationStatus.PreflightRejected, result.Status);
    }

    [Fact]
    public void FileIdentityComparison_RejectsZeroFileIdentifiers()
    {
        var first = new UpdatePackageFileIdentity(
            volumeSerialNumber: 42,
            fileIdLow: 0,
            fileIdHigh: 0,
            fileLength: 128);
        var second = new UpdatePackageFileIdentity(
            volumeSerialNumber: 42,
            fileIdLow: 0,
            fileIdHigh: 0,
            fileLength: 128);

        Assert.False(UpdatePackageFileHandle.HasUsableFileId(
            first.FileIdLow,
            first.FileIdHigh));
        Assert.False(UpdatePackageFileHandle.IsSameFile(first, second));
    }

    [Fact]
    public void FileIdentityComparison_RequiresEveryIdentityComponent()
    {
        var expected = new UpdatePackageFileIdentity(
            volumeSerialNumber: 42,
            fileIdLow: 7,
            fileIdHigh: 11,
            fileLength: 128);

        Assert.True(
            UpdatePackageFileHandle.IsSameFile(
                expected,
                new UpdatePackageFileIdentity(42, 7, 11, 128)));
        Assert.False(
            UpdatePackageFileHandle.IsSameFile(
                expected,
                new UpdatePackageFileIdentity(43, 7, 11, 128)));
        Assert.False(
            UpdatePackageFileHandle.IsSameFile(
                expected,
                new UpdatePackageFileIdentity(42, 8, 11, 128)));
        Assert.False(
            UpdatePackageFileHandle.IsSameFile(
                expected,
                new UpdatePackageFileIdentity(42, 7, 12, 128)));
        Assert.False(
            UpdatePackageFileHandle.IsSameFile(
                expected,
                new UpdatePackageFileIdentity(42, 7, 11, 129)));
    }

    [Fact]
    public async Task ValidCandidate_ProducesOpaqueSingleUseHandoffCapability()
    {
        CandidateFixture fixture = await CreateFixtureAsync();

        UpdateCandidateVerificationResult verification =
            await UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture),
                TrustingAuthenticityVerifier.Instance);

        Assert.True(verification.IsVerified);
        VerifiedUpdateCandidate candidate = Assert.IsType<VerifiedUpdateCandidate>(
            verification.Candidate);
        Assert.Equal(new Version(1, 0, 1), candidate.Version);
        Assert.Equal(PackageId, candidate.PackageId);
        Assert.Equal(Channel, candidate.Channel);
        Assert.Equal(RuntimeIdentifier, candidate.RuntimeIdentifier);
        Assert.Equal(1, candidate.ReleaseManifest.SchemaVersion);
        Assert.Equal(18, candidate.ReleaseManifest.DeclaredFileCount);
        Assert.Equal(18, candidate.ReleaseManifest.DeclaredPayloadBytes);
        Assert.Equal(
            BuildInputsSha256,
            candidate.ReleaseManifest.BuildInputsSha256);
        Assert.Equal(
            PackagingToolsSha256,
            candidate.ReleaseManifest.PackagingToolsSha256);
        Assert.Equal(
            CommercialConfigurationSha256,
            candidate.ReleaseManifest.CommercialConfigurationSha256);
        Assert.Equal(candidate.PackageSha256, Convert.ToHexString(
            SHA256.HashData(
                await File.ReadAllBytesAsync(
                    Path.Combine(fixture.Directory, PackageFileName)))));
        Assert.False(candidate.PackageFileIdentity.FileIdLow == 0
            && candidate.PackageFileIdentity.FileIdHigh == 0
            && candidate.PackageFileIdentity.VolumeSerialNumber == 0);

        Assert.True(
            candidate.TryCreateHandoffAuthorization(
                out UpdateHandoffAuthorization? first));
        using UpdateHandoffAuthorization authorization =
            Assert.IsType<UpdateHandoffAuthorization>(first);
        Assert.Same(candidate.ReleaseManifest, authorization.ReleaseManifest);
        Assert.True(
            authorization.TryAcquireExecutionLease(
                out UpdateExecutionLease? executionLease));
        using (executionLease)
        {
            Assert.Same(candidate.ReleaseManifest, executionLease.ReleaseManifest);
            Assert.Same(
                candidate.PackageFileIdentity,
                executionLease.PackageFileIdentity);
            Assert.Same(
                executionLease.PackageHandle,
                executionLease.PackageHandle);
            Assert.False(executionLease.PackageHandle.IsClosed);
            Assert.Equal(
                Path.Combine(fixture.Directory, PackageFileName),
                executionLease.PackagePath);
            Assert.Throws<IOException>(
                () => File.AppendAllText(executionLease.PackagePath, "tampered"));
        }
        Assert.False(authorization.TryAcquireExecutionLease(out _));

        Assert.False(
            candidate.TryCreateHandoffAuthorization(
                out UpdateHandoffAuthorization? reused));
        Assert.Null(reused);
    }

    [Fact]
    public void IdleRestart_WithoutVerifiedCandidate_IsDenied()
    {
        UpdateReadinessDecision readiness = UpdateCoordinationPolicy.Evaluate(default);

        ProcessExitDecision decision = UpdateRestartPolicy.Decide(
            ProcessExitIntent.UpdateRestart,
            readiness);

        Assert.False(decision.AllowCoordinatedShutdown);
        Assert.False(decision.UnregisterApplicationRestart);
        Assert.False(decision.InvokeUpdaterHandoff);
        Assert.Equal(UpdateDeferralReason.CandidateUnavailable, decision.DeferralReasons);
    }

    [Fact]
    public async Task ConcurrentCapabilityConsumption_AllowsExactlyOneHandoff()
    {
        CandidateFixture fixture = await CreateFixtureAsync();
        UpdateCandidateVerificationResult verification =
            await UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture),
                TrustingAuthenticityVerifier.Instance);
        using VerifiedUpdateCandidate candidate =
            Assert.IsType<VerifiedUpdateCandidate>(verification.Candidate);
        (bool Acquired, UpdateHandoffAuthorization? Authorization)[] decisions =
            await Task.WhenAll(
            Enumerable.Range(0, 16).Select(
                _ => Task.Run(
                    () =>
                    {
                        bool acquired = candidate.TryCreateHandoffAuthorization(
                            out UpdateHandoffAuthorization? authorization);
                        return (acquired, authorization);
                    })));

        (bool Acquired, UpdateHandoffAuthorization? Authorization) winner =
            Assert.Single(
            decisions,
            decision => decision.Acquired);
        Assert.NotNull(winner.Authorization);
        foreach ((bool _, UpdateHandoffAuthorization? authorization) in decisions)
            authorization?.Dispose();
        Assert.Equal(15, decisions.Count(decision => !decision.Acquired));
    }

    [Fact]
    public async Task ExecutionLeaseAcquisition_RacingDispose_NeverReturnsAnUnlockedPackage()
    {
        for (int iteration = 0; iteration < 24; iteration++)
        {
            CandidateFixture fixture = await CreateFixtureAsync();
            UpdateCandidateVerificationResult verification =
                await UpdateCandidateVerifier.VerifyAsync(
                    fixture.PublicKeyPem,
                    Expectations(fixture),
                    TrustingAuthenticityVerifier.Instance);
            using VerifiedUpdateCandidate candidate =
                Assert.IsType<VerifiedUpdateCandidate>(verification.Candidate);
            Assert.True(
                candidate.TryCreateHandoffAuthorization(
                    out UpdateHandoffAuthorization? candidateAuthorization));
            UpdateHandoffAuthorization authorization =
                Assert.IsType<UpdateHandoffAuthorization>(
                    candidateAuthorization);
            using var start = new ManualResetEventSlim(false);
            UpdateExecutionLease? executionLease = null;

            Task<bool> acquire = Task.Run(
                () =>
                {
                    start.Wait();
                    return authorization.TryAcquireExecutionLease(
                        out executionLease);
                });
            Task dispose = Task.Run(
                () =>
                {
                    start.Wait();
                    authorization.Dispose();
                });
            start.Set();
            bool acquired = await acquire;
            await dispose;

            if (acquired)
            {
                using (executionLease)
                {
                    Assert.NotNull(executionLease);
                    Assert.Throws<IOException>(
                        () => File.AppendAllText(
                            executionLease.PackagePath,
                            "tampered"));
                }
            }
            else
            {
                Assert.Null(executionLease);
            }
        }
    }

    [Fact]
    public async Task ExpiredCapability_CannotAuthorizeHandoff()
    {
        CandidateFixture fixture = await CreateFixtureAsync();
        UpdateCandidateVerificationResult verification =
            await UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture),
                TrustingAuthenticityVerifier.Instance);
        VerifiedUpdateCandidate verified = Assert.IsType<VerifiedUpdateCandidate>(
            verification.Candidate);
        verified.Dispose();

        long expiredTimestamp = Stopwatch.GetTimestamp()
            - (long)(Stopwatch.Frequency
                * (UpdateCandidateVerifier.CapabilityLifetime.TotalSeconds + 1));
        using var packageLease = OpenLease(
            Path.Combine(fixture.Directory, PackageFileName));
        using var manifestLease = OpenLease(
            Path.Combine(
                fixture.Directory,
                UpdateCandidateVerifier.ReleaseManifestFileName));
        using var candidate = new VerifiedUpdateCandidate(
            verified.Version,
            verified.PackageId,
            verified.Channel,
            verified.RuntimeIdentifier,
            Path.Combine(fixture.Directory, PackageFileName),
            verified.PackageSha256,
            verified.PackageFileIdentity,
            verified.ReleaseManifestSha256,
            verified.ReleaseManifest,
            DateTimeOffset.UtcNow.Subtract(
                UpdateCandidateVerifier.CapabilityLifetime).AddSeconds(-1),
            DateTimeOffset.UtcNow.AddSeconds(-1),
            expiredTimestamp,
            UpdateCandidateVerifier.CapabilityLifetime,
            packageLease,
            manifestLease);

        Assert.False(
            candidate.TryCreateHandoffAuthorization(
                out UpdateHandoffAuthorization? authorization));
        Assert.Null(authorization);
    }

    [Fact]
    public async Task CandidateExpiringAfterOwnershipAcquisition_CannotCreateHandoff()
    {
        CandidateFixture fixture = await CreateFixtureAsync();
        UpdateCandidateVerificationResult verification = await VerifyFixtureAsync(fixture);
        VerifiedUpdateCandidate verified = Assert.IsType<VerifiedUpdateCandidate>(
            verification.Candidate);
        verified.Dispose();
        using var packageLease = OpenLease(
            Path.Combine(fixture.Directory, PackageFileName));
        using var manifestLease = OpenLease(
            Path.Combine(
                fixture.Directory,
                UpdateCandidateVerifier.ReleaseManifestFileName));
        using var ownershipAcquired = new ManualResetEventSlim(false);
        using var releaseOwnership = new ManualResetEventSlim(false);
        long verifiedTimestamp = Stopwatch.GetTimestamp();
        TimeSpan lifetime = TimeSpan.FromMilliseconds(20);
        using var candidate = new VerifiedUpdateCandidate(
            verified.Version,
            verified.PackageId,
            verified.Channel,
            verified.RuntimeIdentifier,
            Path.Combine(fixture.Directory, PackageFileName),
            verified.PackageSha256,
            verified.PackageFileIdentity,
            verified.ReleaseManifestSha256,
            verified.ReleaseManifest,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.Add(lifetime),
            verifiedTimestamp,
            lifetime,
            packageLease,
            manifestLease,
            () =>
            {
                ownershipAcquired.Set();
                releaseOwnership.Wait();
            });

        UpdateHandoffAuthorization? authorization = null;
        Task<bool> decide = Task.Factory.StartNew(
            () => candidate.TryCreateHandoffAuthorization(
                out authorization),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(ownershipAcquired.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(
            SpinWait.SpinUntil(
                () => Stopwatch.GetElapsedTime(verifiedTimestamp) > lifetime,
                TimeSpan.FromSeconds(2)));
        releaseOwnership.Set();

        Assert.False(await decide);
        Assert.Null(authorization);
    }

    [Fact]
    public async Task AuthorizationExpiringAfterOwnershipAcquisition_CannotCreateExecutionLease()
    {
        CandidateFixture fixture = await CreateFixtureAsync();
        UpdateCandidateVerificationResult verification = await VerifyFixtureAsync(fixture);
        VerifiedUpdateCandidate verified = Assert.IsType<VerifiedUpdateCandidate>(
            verification.Candidate);
        verified.Dispose();
        using var packageLease = OpenLease(
            Path.Combine(fixture.Directory, PackageFileName));
        using var manifestLease = OpenLease(
            Path.Combine(
                fixture.Directory,
                UpdateCandidateVerifier.ReleaseManifestFileName));
        using var ownershipAcquired = new ManualResetEventSlim(false);
        using var releaseOwnership = new ManualResetEventSlim(false);
        long verifiedTimestamp = Stopwatch.GetTimestamp();
        TimeSpan lifetime = TimeSpan.FromMilliseconds(20);
        using var authorization = new UpdateHandoffAuthorization(
            verified.Version,
            verified.PackageId,
            verified.Channel,
            verified.RuntimeIdentifier,
            Path.Combine(fixture.Directory, PackageFileName),
            verified.PackageSha256,
            verified.PackageFileIdentity,
            verified.ReleaseManifestSha256,
            verified.ReleaseManifest,
            DateTimeOffset.UtcNow.Add(lifetime),
            verifiedTimestamp,
            lifetime,
            packageLease,
            manifestLease,
            () =>
            {
                ownershipAcquired.Set();
                releaseOwnership.Wait();
            });

        UpdateExecutionLease? executionLease = null;
        Task<bool> acquire = Task.Factory.StartNew(
            () => authorization.TryAcquireExecutionLease(out executionLease),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(ownershipAcquired.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(
            SpinWait.SpinUntil(
                () => Stopwatch.GetElapsedTime(verifiedTimestamp) > lifetime,
                TimeSpan.FromSeconds(2)));
        releaseOwnership.Set();

        Assert.False(await acquire);
        Assert.Null(executionLease);
    }

    [Fact]
    public async Task DescriptorMutationAfterSigning_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync();
        string descriptorPath = Path.Combine(
            fixture.Directory,
            UpdateCandidateVerifier.DescriptorFileName);
        byte[] descriptor = await File.ReadAllBytesAsync(descriptorPath);
        descriptor[^2] ^= 1;
        await File.WriteAllBytesAsync(descriptorPath, descriptor);

        UpdateCandidateVerificationResult result =
            await UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture),
                TrustingAuthenticityVerifier.Instance);

        Assert.Equal(UpdateCandidateVerificationStatus.SignatureInvalid, result.Status);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public async Task SignedUnsignedTestKind_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            descriptorMutation: descriptor => descriptor["packageKind"] = "UNSIGNED_TEST");

        UpdateCandidateVerificationResult result =
            await UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture),
                TrustingAuthenticityVerifier.Instance);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Theory]
    [InlineData("application", "Other")]
    [InlineData("packageId", "other.Package")]
    [InlineData("channel", "beta-v1")]
    [InlineData("runtimeIdentifier", "win-arm64")]
    public async Task WrongIdentity_IsRejected(string propertyName, string value)
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            descriptorMutation: descriptor => descriptor[propertyName] = value);

        UpdateCandidateVerificationResult result =
            await UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture),
                TrustingAuthenticityVerifier.Instance);

        Assert.Equal(UpdateCandidateVerificationStatus.IdentityMismatch, result.Status);
    }

    [Fact]
    public async Task NotNewerCandidate_IsRejectedByPreflight()
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            descriptorMutation: descriptor => descriptor["version"] = "1.0.0");

        UpdateCandidateVerificationResult result =
            await UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture),
                TrustingAuthenticityVerifier.Instance);

        Assert.Equal(UpdateCandidateVerificationStatus.PreflightRejected, result.Status);
    }

    [Fact]
    public async Task PackageMutation_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync();
        await File.AppendAllTextAsync(
            Path.Combine(fixture.Directory, PackageFileName),
            "tampered");

        UpdateCandidateVerificationResult result =
            await UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture),
                TrustingAuthenticityVerifier.Instance);

        Assert.Equal(UpdateCandidateVerificationStatus.PayloadChanged, result.Status);
    }

    [Fact]
    public async Task ReleaseManifestMutation_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync();
        await File.AppendAllTextAsync(
            Path.Combine(
                fixture.Directory,
                UpdateCandidateVerifier.ReleaseManifestFileName),
            "tampered");

        UpdateCandidateVerificationResult result =
            await UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture),
                TrustingAuthenticityVerifier.Instance);

        Assert.Equal(UpdateCandidateVerificationStatus.PayloadChanged, result.Status);
    }

    [Fact]
    public async Task DuplicateSignedProperty_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync();
        string descriptorPath = Path.Combine(
            fixture.Directory,
            UpdateCandidateVerifier.DescriptorFileName);
        string descriptor = await File.ReadAllTextAsync(descriptorPath);
        descriptor = descriptor.Replace(
            "\"schemaVersion\":1",
            "\"schemaVersion\":1,\"schemaVersion\":1",
            StringComparison.Ordinal);
        await WriteSignedDescriptorAsync(
            descriptorPath,
            Path.Combine(fixture.Directory, UpdateCandidateVerifier.SignatureFileName),
            Encoding.UTF8.GetBytes(descriptor));

        UpdateCandidateVerificationResult result =
            await UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture),
                TrustingAuthenticityVerifier.Instance);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Fact]
    public async Task UnexpectedStagedFile_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Directory, "untrusted.exe"),
            "unexpected");

        UpdateCandidateVerificationResult result =
            await UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture),
                TrustingAuthenticityVerifier.Instance);

        Assert.Equal(UpdateCandidateVerificationStatus.PayloadUnexpected, result.Status);
    }

    [Theory]
    [InlineData("../WinShot.exe")]
    [InlineData("C:WinShot.exe")]
    [InlineData("CON.exe")]
    [InlineData("WinShot.exe.")]
    [InlineData("nested/WinShot.exe")]
    public async Task UnsafePackageFileName_IsRejected(string packageFileName)
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            descriptorMutation: descriptor =>
                descriptor["packageFileName"] = packageFileName);

        UpdateCandidateVerificationResult result =
            await UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture),
                TrustingAuthenticityVerifier.Instance);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Fact]
    public async Task NuGetPackageWithoutPeAuthenticodeSemantics_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            descriptorMutation: descriptor =>
                descriptor["packageFileName"] = "WinShot-1.0.1-full.nupkg");

        UpdateCandidateVerificationResult result =
            await UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture),
                TrustingAuthenticityVerifier.Instance);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Fact]
    public async Task WeakPublicKey_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync();
        using RSA weak = RSA.Create(2048);

        UpdateCandidateVerificationResult result =
            await UpdateCandidateVerifier.VerifyAsync(
                weak.ExportSubjectPublicKeyInfoPem(),
                Expectations(fixture),
                TrustingAuthenticityVerifier.Instance);

        Assert.Equal(UpdateCandidateVerificationStatus.PublicKeyInvalid, result.Status);
    }

    [Fact]
    public async Task PrivateKeyPem_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync();
        using RSA signer = CreateRsa();

        UpdateCandidateVerificationResult result =
            await UpdateCandidateVerifier.VerifyAsync(
                signer.ExportPkcs8PrivateKeyPem(),
                Expectations(fixture),
                TrustingAuthenticityVerifier.Instance);

        Assert.Equal(UpdateCandidateVerificationStatus.PublicKeyInvalid, result.Status);
    }

    [Fact]
    public async Task UnexpectedReleaseKeyFingerprint_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync();

        UpdateCandidateVerificationResult result =
            await UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture) with
                {
                    ReleasePublicKeySha256 = new string('0', 64),
                },
                TrustingAuthenticityVerifier.Instance);

        Assert.Equal(UpdateCandidateVerificationStatus.PublicKeyInvalid, result.Status);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public async Task FailedPlatformAuthenticity_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync();

        UpdateCandidateVerificationResult result =
            await UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture),
                RejectingAuthenticityVerifier.Instance);

        Assert.Equal(UpdateCandidateVerificationStatus.AuthenticityRejected, result.Status);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public async Task AuthenticityAdapter_CannotMutatePackageUnderVerification()
    {
        CandidateFixture fixture = await CreateFixtureAsync();
        var verifier = new MutatingAuthenticityVerifier(
            Path.Combine(fixture.Directory, PackageFileName));

        UpdateCandidateVerificationResult result =
            await UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture),
                verifier);

        Assert.True(verifier.MutationWasBlocked);
        Assert.True(result.IsVerified);
        result.Candidate?.Dispose();
    }

    [Fact]
    public async Task AuthenticityHandle_PathReplacementAndSymlinkSwapCannotChangeVerifiedFile()
    {
        CandidateFixture fixture = await CreateFixtureAsync();
        string packagePath = Path.Combine(fixture.Directory, PackageFileName);
        string replacementDirectory = Path.Combine(
            Path.GetTempPath(),
            $"winshot-update-replacement-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(replacementDirectory);
        _temporaryDirectories.Add(replacementDirectory);
        string replacementPath = Path.Combine(replacementDirectory, "replacement.tmp");
        byte[] expected = await File.ReadAllBytesAsync(packagePath);
        await File.WriteAllTextAsync(replacementPath, "different payload");
        var verifier = new ReplacingAuthenticityVerifier(
            packagePath,
            replacementPath,
            expected);

        UpdateCandidateVerificationResult result =
            await UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture),
                verifier);

        Assert.Equal(expected, verifier.BytesBeforeSwap);
        Assert.Equal(expected, verifier.BytesAfterSwap);
        Assert.True(verifier.ReplacementWasBlocked || !result.IsVerified);
        Assert.True(verifier.SymlinkSwapWasBlocked || !result.IsVerified);
        if (result.IsVerified)
        {
            using VerifiedUpdateCandidate candidate =
                Assert.IsType<VerifiedUpdateCandidate>(result.Candidate);
            Assert.True(
                candidate.TryCreateHandoffAuthorization(
                    out UpdateHandoffAuthorization? candidateAuthorization));
            using UpdateHandoffAuthorization authorization =
                Assert.IsType<UpdateHandoffAuthorization>(
                    candidateAuthorization);
            Assert.True(authorization.TryAcquireExecutionLease(
                out UpdateExecutionLease? executionLease));
            using (executionLease)
                Assert.Equal(expected, ReadHandle(executionLease.PackageHandle));
        }
        else
        {
            Assert.Null(result.Candidate);
            Assert.True(
                result.Status is UpdateCandidateVerificationStatus.PayloadChanged
                    or UpdateCandidateVerificationStatus.UnsafeFilesystemEntry);
        }
    }

    [Fact]
    public async Task ExpectedAuthenticityAdapterException_FailsClosed()
    {
        CandidateFixture fixture = await CreateFixtureAsync();

        UpdateCandidateVerificationResult result =
            await UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture),
                ThrowingAuthenticityVerifier.Instance);

        Assert.Equal(UpdateCandidateVerificationStatus.AuthenticityRejected, result.Status);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public async Task ReleaseManifestIdentityMismatch_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestVersion: "1.0.2");

        UpdateCandidateVerificationResult result =
            await UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture),
                TrustingAuthenticityVerifier.Instance);

        Assert.Equal(UpdateCandidateVerificationStatus.IdentityMismatch, result.Status);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("sourceProvenance")]
    [InlineData("commercialConfiguration")]
    [InlineData("file")]
    public async Task ReleaseManifestUnexpectedProperty_IsRejected(string scope)
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestMutation: manifest =>
            {
                Dictionary<string, object?> target = scope switch
                {
                    "root" => manifest,
                    "sourceProvenance" => ObjectProperty(
                        manifest,
                        "sourceProvenance"),
                    "commercialConfiguration" => ObjectProperty(
                        manifest,
                        "commercialConfiguration"),
                    "file" => FilesProperty(manifest)[0],
                    _ => throw new InvalidOperationException(),
                };
                target["unexpected"] = true;
            });

        UpdateCandidateVerificationResult result =
            await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("sourceProvenance")]
    [InlineData("commercialConfiguration")]
    [InlineData("files")]
    public async Task ReleaseManifestMissingRequiredProperty_IsRejected(string propertyName)
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestMutation: manifest => manifest.Remove(propertyName));

        UpdateCandidateVerificationResult result =
            await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Theory]
    [InlineData("sourceProvenance", "buildInputsSha256")]
    [InlineData("commercialConfiguration", "sha256")]
    [InlineData("file", "sha256")]
    public async Task ReleaseManifestMissingNestedProperty_IsRejected(
        string scope,
        string propertyName)
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestMutation: manifest =>
            {
                Dictionary<string, object?> target = scope switch
                {
                    "sourceProvenance" => ObjectProperty(
                        manifest,
                        "sourceProvenance"),
                    "commercialConfiguration" => ObjectProperty(
                        manifest,
                        "commercialConfiguration"),
                    "file" => FilesProperty(manifest)[0],
                    _ => throw new InvalidOperationException(),
                };
                target.Remove(propertyName);
            });

        UpdateCandidateVerificationResult result =
            await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Theory]
    [InlineData("buildInputsSha256")]
    [InlineData("packagingToolsSha256")]
    [InlineData("commercialConfigurationSha256")]
    public async Task ReleaseManifestMalformedRequiredDigest_IsRejected(string digest)
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestMutation: manifest =>
            {
                Dictionary<string, object?> target =
                    digest.Equals(
                        "commercialConfigurationSha256",
                        StringComparison.Ordinal)
                        ? ObjectProperty(manifest, "commercialConfiguration")
                        : ObjectProperty(manifest, "sourceProvenance");
                string property = digest.Equals(
                    "commercialConfigurationSha256",
                    StringComparison.Ordinal)
                    ? "sha256"
                    : digest;
                target[property] = new string('a', 64);
            });

        UpdateCandidateVerificationResult result =
            await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Theory]
    [InlineData("../WinShot.exe")]
    [InlineData("/WinShot.exe")]
    [InlineData("C:/WinShot.exe")]
    [InlineData("nested\\WinShot.exe")]
    [InlineData("nested//WinShot.exe")]
    [InlineData("nested/./WinShot.exe")]
    [InlineData("nested/NUL.txt")]
    [InlineData("nested/WinShot.exe.")]
    [InlineData("nested/COM\u00B9.txt")]
    [InlineData("nested/release-manifest.json")]
    [InlineData("nested/SHA256SUMS")]
    [InlineData("e\u0301/WinShot.exe")]
    public async Task ReleaseManifestUnsafeOrNonCanonicalPath_IsRejected(string path)
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestMutation: manifest =>
                FilesProperty(manifest)[0]["path"] = path);

        UpdateCandidateVerificationResult result =
            await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Fact]
    public async Task ReleaseManifestCaseInsensitivePathCollision_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestMutation: manifest =>
                FilesProperty(manifest)[1]["path"] = "winshot.EXE");

        UpdateCandidateVerificationResult result =
            await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Fact]
    public async Task ReleaseManifestExactDuplicatePath_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestMutation: manifest =>
                FilesProperty(manifest)[1]["path"] = "WinShot.exe");

        UpdateCandidateVerificationResult result =
            await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Fact]
    public async Task ReleaseManifestEmptyFilesArray_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestMutation: manifest => FilesProperty(manifest).Clear());

        UpdateCandidateVerificationResult result =
            await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Fact]
    public async Task ReleaseManifestOversizedFilesArray_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestMutation: manifest =>
            {
                List<Dictionary<string, object?>> files = FilesProperty(manifest);
                files.Clear();
                for (int index = 0;
                     index <= ReleaseManifestReader.MaximumFileCount;
                     index++)
                {
                    files.Add(
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["path"] = $"payload/file-{index:D4}.dll",
                            ["bytes"] = 1,
                            ["sha256"] = new string('D', 64),
                        });
                }
            });

        UpdateCandidateVerificationResult result =
            await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Theory]
    [InlineData("negativeBytes")]
    [InlineData("lowercaseSha256")]
    public async Task ReleaseManifestMalformedFileEvidence_IsRejected(string mutation)
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestMutation: manifest =>
            {
                Dictionary<string, object?> file = FilesProperty(manifest)[0];
                if (mutation.Equals("negativeBytes", StringComparison.Ordinal))
                    file["bytes"] = -1;
                else
                    file["sha256"] = new string('d', 64);
            });

        UpdateCandidateVerificationResult result =
            await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Fact]
    public async Task ReleaseManifestNonCanonicalNumericFileSize_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestBytesMutation: bytes =>
                Encoding.UTF8.GetBytes(
                    Encoding.UTF8.GetString(bytes).Replace(
                        "\"bytes\":1",
                        "\"bytes\":1e0",
                        StringComparison.Ordinal)));

        UpdateCandidateVerificationResult result =
            await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Fact]
    public async Task ReleaseManifestDuplicateRootProperty_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestBytesMutation: bytes =>
                Encoding.UTF8.GetBytes(
                    Encoding.UTF8.GetString(bytes).Replace(
                        "\"schemaVersion\":1",
                        "\"schemaVersion\":1,\"schemaVersion\":1",
                        StringComparison.Ordinal)));

        UpdateCandidateVerificationResult result =
            await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Fact]
    public async Task ReleaseManifestOfflineCommercialMode_IsRejectedForProduction()
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestMutation: manifest =>
            {
                Dictionary<string, object?> commercial = ObjectProperty(
                    manifest,
                    "commercialConfiguration");
                commercial["licenseMode"] = "offline-signed";
                commercial["provider"] = null;
                commercial["checkoutApproved"] = false;
                commercial["buyerTermsApproved"] = false;
                commercial["primaryVariantId"] = null;
            });

        UpdateCandidateVerificationResult result = await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Fact]
    public async Task ReleaseManifestUnapprovedCheckout_IsRejectedForProduction()
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestMutation: manifest =>
            {
                Dictionary<string, object?> commercial = ObjectProperty(
                    manifest,
                    "commercialConfiguration");
                commercial["checkoutApproved"] = false;
                commercial["buyerTermsApproved"] = false;
                commercial["primaryVariantId"] = null;
            });

        UpdateCandidateVerificationResult result = await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Fact]
    public async Task ReleaseManifestMissingProductionPayload_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestMutation: manifest =>
                FilesProperty(manifest).RemoveAll(
                    file => file["path"]?.ToString() == "WinShot.Core.dll"));

        UpdateCandidateVerificationResult result = await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Fact]
    public async Task ReleaseManifestProductionPayloadNameCasing_IsExact()
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestMutation: manifest =>
            {
                Dictionary<string, object?> core = Assert.Single(
                    FilesProperty(manifest),
                    file => file["path"]?.ToString() == "WinShot.Core.dll");
                core["path"] = "winshot.core.dll";
            });

        UpdateCandidateVerificationResult result = await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Fact]
    public async Task ReleaseManifestUnsignedTestMarker_IsRejectedForProduction()
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestMutation: manifest =>
                FilesProperty(manifest).Add(
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["path"] = "UNSIGNED-TEST-ARTIFACT.txt",
                        ["bytes"] = 1,
                        ["sha256"] = new string('D', 64),
                    }));

        UpdateCandidateVerificationResult result = await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Fact]
    public async Task ReleaseManifestFileExceedingReleaseLimit_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestMutation: manifest =>
                FilesProperty(manifest)[0]["bytes"] =
                    ReleaseManifestReader.MaximumFileBytes + 1);

        UpdateCandidateVerificationResult result = await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Fact]
    public async Task ReleaseManifestTotalExceedingReleaseLimit_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestMutation: manifest =>
            {
                List<Dictionary<string, object?>> files = FilesProperty(manifest);
                for (int index = 0; index < 4; index++)
                    files[index]["bytes"] = ReleaseManifestReader.MaximumFileBytes;
                files[4]["bytes"] = 1;
            });

        UpdateCandidateVerificationResult result = await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Fact]
    public async Task ReleaseManifestCommercialConfigurationDigestMismatch_IsRejected()
    {
        CandidateFixture fixture = await CreateFixtureAsync(
            releaseManifestMutation: manifest =>
            {
                Dictionary<string, object?> commercialFile = Assert.Single(
                    FilesProperty(manifest),
                    file => file["path"]?.ToString() == "commercial-config.json");
                commercialFile["sha256"] = new string('D', 64);
            });

        UpdateCandidateVerificationResult result = await VerifyFixtureAsync(fixture);

        Assert.Equal(UpdateCandidateVerificationStatus.ManifestInvalid, result.Status);
    }

    [Fact]
    public async Task PreCancelledVerification_ThrowsWithoutTrustingCandidate()
    {
        CandidateFixture fixture = await CreateFixtureAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => UpdateCandidateVerifier.VerifyAsync(
                fixture.PublicKeyPem,
                Expectations(fixture),
                TrustingAuthenticityVerifier.Instance,
                cancellation.Token));
    }

    public void Dispose()
    {
        foreach (string directory in _temporaryDirectories)
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<CandidateFixture> CreateFixtureAsync(
        Action<Dictionary<string, object>>? descriptorMutation = null,
        string releaseManifestVersion = "1.0.1",
        Action<Dictionary<string, object?>>? releaseManifestMutation = null,
        Func<byte[], byte[]>? releaseManifestBytesMutation = null)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"winshot-update-candidate-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _temporaryDirectories.Add(directory);

        byte[] package = Encoding.UTF8.GetBytes("synthetic signed package payload");
        List<Dictionary<string, object?>> manifestFiles =
            RequiredManifestPayloadPaths.Select(
                path => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = path,
                    ["bytes"] = 1,
                    ["sha256"] = path.Equals(
                        "commercial-config.json",
                        StringComparison.Ordinal)
                        ? CommercialConfigurationSha256
                        : new string('D', 64),
                }).ToList();
        var releaseManifestObject =
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schemaVersion"] = 1,
                ["application"] = Application,
                ["version"] = releaseManifestVersion,
                ["runtimeIdentifier"] = RuntimeIdentifier,
                ["packageKind"] = "SIGNED_PRODUCTION",
                ["selfContained"] = true,
                ["trimmed"] = false,
                ["singleFile"] = false,
                ["sourceProvenance"] =
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["commitSha"] = new string('a', 40),
                        ["workingTreeDirty"] = false,
                        ["buildInputsDirty"] = false,
                        ["buildInputsSha256"] = BuildInputsSha256,
                        ["buildInputFileCount"] = 154,
                        ["packagingToolsSha256"] = PackagingToolsSha256,
                        ["packagingToolFileCount"] = 13,
                        ["pinnedSdkVersion"] = "10.0.110",
                    },
                ["commercialConfiguration"] =
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["sha256"] = CommercialConfigurationSha256,
                        ["contractRevision"] = 1,
                        ["applicationMajor"] = 1,
                        ["licenseMode"] = "offline-signed",
                        ["provider"] = null,
                        ["checkoutApproved"] = true,
                        ["buyerTermsApproved"] = true,
                        ["primaryVariantId"] = null,
                    },
                ["files"] = manifestFiles,
            };
        releaseManifestMutation?.Invoke(releaseManifestObject);
        byte[] releaseManifest = JsonSerializer.SerializeToUtf8Bytes(
            releaseManifestObject);
        if (releaseManifestBytesMutation is not null)
            releaseManifest = releaseManifestBytesMutation(releaseManifest);
        await File.WriteAllBytesAsync(
            Path.Combine(directory, PackageFileName),
            package);
        await File.WriteAllBytesAsync(
            Path.Combine(directory, UpdateCandidateVerifier.ReleaseManifestFileName),
            releaseManifest);

        var descriptor = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = 1,
            ["application"] = Application,
            ["packageId"] = PackageId,
            ["channel"] = Channel,
            ["version"] = "1.0.1",
            ["paidMajor"] = 1,
            ["runtimeIdentifier"] = RuntimeIdentifier,
            ["packageKind"] = "SIGNED_PRODUCTION",
            ["packageFileName"] = PackageFileName,
            ["packageBytes"] = package.LongLength,
            ["packageSha256"] = Convert.ToHexString(SHA256.HashData(package)),
            ["releaseManifestBytes"] = releaseManifest.LongLength,
            ["releaseManifestSha256"] =
                Convert.ToHexString(SHA256.HashData(releaseManifest)),
        };
        descriptorMutation?.Invoke(descriptor);
        byte[] descriptorBytes = JsonSerializer.SerializeToUtf8Bytes(descriptor);

        await WriteSignedDescriptorAsync(
            Path.Combine(directory, UpdateCandidateVerifier.DescriptorFileName),
            Path.Combine(directory, UpdateCandidateVerifier.SignatureFileName),
            descriptorBytes);
        using RSA signer = CreateRsa();
        return new CandidateFixture(
            directory,
            signer.ExportSubjectPublicKeyInfoPem());
    }

    private static async Task WriteSignedDescriptorAsync(
        string descriptorPath,
        string signaturePath,
        byte[] descriptorBytes)
    {
        using RSA signer = CreateRsa();
        byte[] signature = signer.SignData(
            descriptorBytes,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);
        await File.WriteAllBytesAsync(descriptorPath, descriptorBytes);
        await File.WriteAllBytesAsync(signaturePath, signature);
    }

    private static UpdateCandidateExpectations Expectations(CandidateFixture fixture) =>
        new(
            Application,
            PackageId,
            Channel,
            RuntimeIdentifier,
            fixture.Directory,
            PublicKeySha256(fixture.PublicKeyPem),
            new Version(1, 0, 0),
            EntitledMajor: 1);

    private static Task<UpdateCandidateVerificationResult> VerifyFixtureAsync(
        CandidateFixture fixture) =>
        UpdateCandidateVerifier.VerifyAsync(
            fixture.PublicKeyPem,
            Expectations(fixture),
            TrustingAuthenticityVerifier.Instance);

    private static Dictionary<string, object?> ObjectProperty(
        Dictionary<string, object?> manifest,
        string propertyName) =>
        Assert.IsType<Dictionary<string, object?>>(manifest[propertyName]);

    private static List<Dictionary<string, object?>> FilesProperty(
        Dictionary<string, object?> manifest) =>
        Assert.IsType<List<Dictionary<string, object?>>>(manifest["files"]);

    private static string PublicKeySha256(string publicKeyPem)
    {
        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        return Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
    }

    private static FileStream OpenLease(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);

    private static byte[] ReadHandle(SafeFileHandle handle)
    {
        long length = RandomAccess.GetLength(handle);
        Assert.InRange(length, 0, int.MaxValue);
        var bytes = new byte[(int)length];
        int total = 0;
        while (total < bytes.Length)
        {
            int read = RandomAccess.Read(
                handle,
                bytes.AsSpan(total),
                fileOffset: total);
            Assert.True(read > 0);
            total += read;
        }

        return bytes;
    }

    private static RSAParameters CreateSigningKey()
    {
        using RSA rsa = RSA.Create(3072);
        return rsa.ExportParameters(includePrivateParameters: true);
    }

    private static RSA CreateRsa()
    {
        RSA rsa = RSA.Create();
        rsa.ImportParameters(SigningKey.Value);
        return rsa;
    }

    private sealed record CandidateFixture(string Directory, string PublicKeyPem);

    private sealed class TrustingAuthenticityVerifier :
        IUpdateCandidateAuthenticityVerifier
    {
        internal static readonly TrustingAuthenticityVerifier Instance = new();

        public ValueTask<UpdateCandidateAuthenticityStatus> VerifyAsync(
            SafeFileHandle packageHandle,
            string packageDisplayName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(packageHandle.IsClosed);
            Assert.Equal(PackageFileName, packageDisplayName);
            return ValueTask.FromResult(UpdateCandidateAuthenticityStatus.Trusted);
        }
    }

    private sealed class RejectingAuthenticityVerifier :
        IUpdateCandidateAuthenticityVerifier
    {
        internal static readonly RejectingAuthenticityVerifier Instance = new();

        public ValueTask<UpdateCandidateAuthenticityStatus> VerifyAsync(
            SafeFileHandle packageHandle,
            string packageDisplayName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(UpdateCandidateAuthenticityStatus.PublisherMismatch);
        }
    }

    private sealed class MutatingAuthenticityVerifier :
        IUpdateCandidateAuthenticityVerifier
    {
        private readonly string _packagePath;

        internal MutatingAuthenticityVerifier(string packagePath)
        {
            _packagePath = packagePath;
        }

        public bool MutationWasBlocked { get; private set; }

        public ValueTask<UpdateCandidateAuthenticityStatus> VerifyAsync(
            SafeFileHandle packageHandle,
            string packageDisplayName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.AppendAllText(_packagePath, "tampered");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MutationWasBlocked = true;
            }

            return ValueTask.FromResult(UpdateCandidateAuthenticityStatus.Trusted);
        }
    }

    private sealed class ThrowingAuthenticityVerifier :
        IUpdateCandidateAuthenticityVerifier
    {
        internal static readonly ThrowingAuthenticityVerifier Instance = new();

        public ValueTask<UpdateCandidateAuthenticityStatus> VerifyAsync(
            SafeFileHandle packageHandle,
            string packageDisplayName,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Synthetic native trust failure.");
    }

    private sealed class ReplacingAuthenticityVerifier :
        IUpdateCandidateAuthenticityVerifier
    {
        private readonly string _packagePath;
        private readonly string _replacementPath;
        private readonly byte[] _expected;

        internal ReplacingAuthenticityVerifier(
            string packagePath,
            string replacementPath,
            byte[] expected)
        {
            _packagePath = packagePath;
            _replacementPath = replacementPath;
            _expected = expected;
        }

        internal bool ReplacementWasBlocked { get; private set; }

        internal bool SymlinkSwapWasBlocked { get; private set; }

        internal byte[]? BytesBeforeSwap { get; private set; }

        internal byte[]? BytesAfterSwap { get; private set; }

        public ValueTask<UpdateCandidateAuthenticityStatus> VerifyAsync(
            SafeFileHandle packageHandle,
            string packageDisplayName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(PackageFileName, packageDisplayName);
            BytesBeforeSwap = ReadHandle(packageHandle);
            Assert.Equal(_expected, BytesBeforeSwap);

            try
            {
                File.Move(_replacementPath, _packagePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ReplacementWasBlocked = true;
            }

            try
            {
                File.Delete(_packagePath);
                File.CreateSymbolicLink(_packagePath, _replacementPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SymlinkSwapWasBlocked = true;
            }

            BytesAfterSwap = ReadHandle(packageHandle);
            return ValueTask.FromResult(UpdateCandidateAuthenticityStatus.Trusted);
        }
    }
}
