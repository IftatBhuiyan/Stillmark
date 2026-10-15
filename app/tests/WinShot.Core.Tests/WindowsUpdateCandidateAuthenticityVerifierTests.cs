using Microsoft.Win32.SafeHandles;
using WinShot.Core.Update;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class WindowsUpdateCandidateAuthenticityVerifierTests : IDisposable
{
    private const string Publisher = "CN=WinShot Test Publisher";
    private const string IdentityEku =
        "1.3.6.1.4.1.311.97.990309390.766961637.194916062.941502583";
    private readonly List<string> _temporaryFiles = [];

    [Theory]
    [InlineData("")]
    [InlineData("1.3.6.1.4.1.311.97")]
    [InlineData("1.3.6.1.4.1.311.97.")]
    [InlineData("1.3.6.1.4.1.311.97.1.0")]
    [InlineData("1.3.6.1.4.1.311.97.1.3.1.29433.35007")]
    [InlineData("1.3.6.1.4.1.311.97.1.4.1.29433.35007")]
    [InlineData("1.3.6.1.4.1.311.97.01")]
    [InlineData("1.3.6.1.4.1.311.97.not-a-number")]
    public void Constructor_RejectsNonDurableIdentityPins(string identityEku)
    {
        Assert.Throws<ArgumentException>(
            () => new WindowsUpdateCandidateAuthenticityVerifier(
                new WindowsAuthenticodeExpectations(Publisher, identityEku)));
    }

    [Fact]
    public void Evaluate_RequiresExactPublisherIdentityAndCodeSigningUsage()
    {
        var verifier = CreateVerifier();
        Assert.Equal(
            UpdateCandidateAuthenticityStatus.Trusted,
            verifier.Evaluate(Evidence()));
        Assert.Equal(
            UpdateCandidateAuthenticityStatus.PublisherMismatch,
            verifier.Evaluate(Evidence(publisher: Publisher.ToLowerInvariant())));
        Assert.Equal(
            UpdateCandidateAuthenticityStatus.PublisherMismatch,
            verifier.Evaluate(Evidence(identityEku: "1.3.6.1.4.1.311.97.99")));
        Assert.Equal(
            UpdateCandidateAuthenticityStatus.PublisherMismatch,
            verifier.Evaluate(Evidence(includeCodeSigning: false)));
        Assert.Equal(
            UpdateCandidateAuthenticityStatus.PublisherMismatch,
            verifier.Evaluate(Evidence(includePublicTrustMarker: false)));
        Assert.Equal(
            UpdateCandidateAuthenticityStatus.PublisherMismatch,
            verifier.Evaluate(Evidence(hasSingleEkuExtension: false)));
        Assert.Equal(
            UpdateCandidateAuthenticityStatus.PublisherMismatch,
            verifier.Evaluate(Evidence(hasDuplicateEku: true)));
        Assert.Equal(
            UpdateCandidateAuthenticityStatus.PublisherMismatch,
            verifier.Evaluate(
                Evidence(
                    additionalIdentityEku:
                        "1.3.6.1.4.1.311.97.990309390.766961637.194916062.1")));
    }

    [Fact]
    public void Evaluate_DoesNotTreatRotatingLeafThumbprintAsDurableIdentity()
    {
        var verifier = CreateVerifier();

        Assert.Equal(
            UpdateCandidateAuthenticityStatus.Trusted,
            verifier.Evaluate(Evidence(leafThumbprint: new string('A', 40))));
        Assert.Equal(
            UpdateCandidateAuthenticityStatus.Trusted,
            verifier.Evaluate(Evidence(leafThumbprint: new string('B', 40))));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Evaluate_RequiresRfc3161AttributeAndTrustedTimestampSigner(
        bool hasRfc3161Timestamp,
        bool hasTrustedTimestampSigner)
    {
        var verifier = CreateVerifier();
        Assert.Equal(
            UpdateCandidateAuthenticityStatus.TimestampInvalid,
            verifier.Evaluate(
                Evidence(
                    hasRfc3161Timestamp: hasRfc3161Timestamp,
                    hasTrustedTimestampSigner: hasTrustedTimestampSigner)));
    }

    [Fact]
    public async Task UnsignedFile_IsNotTrusted()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"winshot-unsigned-authenticode-{Guid.NewGuid():N}.exe");
        await File.WriteAllTextAsync(path, "not a signed portable executable");
        _temporaryFiles.Add(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous);

        UpdateCandidateAuthenticityStatus result =
            await CreateVerifier().VerifyAsync(
                stream.SafeFileHandle,
                Path.GetFileName(path),
                CancellationToken.None);

        Assert.Equal(UpdateCandidateAuthenticityStatus.Untrusted, result);
        Assert.False(stream.SafeFileHandle.IsClosed);
    }

    [Fact]
    public void NativeWinTrust_AcceptsAValidEmbeddedSignatureHandle()
    {
        if (!OperatingSystem.IsWindows())
            return;
        string path = typeof(object).Assembly.Location;
        if (!File.Exists(path))
            return;
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        WindowsAuthenticodeNativeResult result =
            WindowsAuthenticodeNative.Verify(stream.SafeFileHandle, path);

        Assert.Equal(0, result.TrustResult);
        Assert.False(result.PolicyRejected);
        Assert.NotNull(result.Evidence);
        Assert.True(result.Evidence.HasRfc3161Timestamp);
        Assert.True(result.Evidence.HasTrustedTimestampSigner);
        Assert.False(stream.SafeFileHandle.IsClosed);
    }

    [Fact]
    public async Task InvalidDisplayName_FailsBeforeNativeVerification()
    {
        using SafeFileHandle handle = new(
            new IntPtr(-1),
            ownsHandle: false);

        UpdateCandidateAuthenticityStatus result =
            await CreateVerifier().VerifyAsync(
                handle,
                @"subdirectory\package.exe",
                CancellationToken.None);

        Assert.Equal(
            UpdateCandidateAuthenticityStatus.VerificationUnavailable,
            result);
    }

    [Fact]
    public async Task PreCancelledVerification_DoesNotStartNativeWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using SafeFileHandle handle = new(
            new IntPtr(-1),
            ownsHandle: false);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await CreateVerifier().VerifyAsync(
                handle,
                "package.exe",
                cancellation.Token));
    }

    public void Dispose()
    {
        foreach (string path in _temporaryFiles)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static WindowsUpdateCandidateAuthenticityVerifier CreateVerifier() =>
        new(new WindowsAuthenticodeExpectations(Publisher, IdentityEku));

    private static WindowsAuthenticodeEvidence Evidence(
        string publisher = Publisher,
        string identityEku = IdentityEku,
        string? leafThumbprint = null,
        bool includeCodeSigning = true,
        bool includePublicTrustMarker = true,
        bool hasSingleEkuExtension = true,
        bool hasDuplicateEku = false,
        string? additionalIdentityEku = null,
        bool hasRfc3161Timestamp = true,
        bool hasTrustedTimestampSigner = true)
    {
        var usages = new HashSet<string>(StringComparer.Ordinal)
        {
            identityEku,
        };
        if (includeCodeSigning)
            usages.Add(WindowsAuthenticodeNative.CodeSigningEkuOid);
        if (includePublicTrustMarker)
            usages.Add(WindowsAuthenticodeNative.PublicTrustMarkerEkuOid);
        if (additionalIdentityEku is not null)
            usages.Add(additionalIdentityEku);
        return new WindowsAuthenticodeEvidence(
            publisher,
            leafThumbprint ?? new string('A', 40),
            usages,
            hasSingleEkuExtension,
            hasDuplicateEku,
            hasRfc3161Timestamp,
            hasTrustedTimestampSigner);
    }
}
