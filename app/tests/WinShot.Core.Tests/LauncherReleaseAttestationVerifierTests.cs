using System.Security.Cryptography;
using System.Text;
using WinShot.Launcher;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class LauncherReleaseAttestationVerifierTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"winshot-release-attestation-{Guid.NewGuid():N}");

    public LauncherReleaseAttestationVerifierTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void SignedExactInventory_IsRetainedAgainstMutationUntilLeaseDisposal()
    {
        using RSA signer = CreateSigner();
        LauncherConfiguration configuration = CreateConfiguration(signer);
        string managedPath = CreatePayloadFiles();
        WriteEnvelope(signer, configuration);

        bool trusted = LauncherReleaseAttestationVerifier.TryAcquire(
            _root,
            "1.0.0",
            configuration,
            out IDisposable? lease);

        Assert.True(trusted);
        Assert.NotNull(lease);
        Assert.Throws<IOException>(
            () => File.AppendAllText(managedPath, "tampered"));
        lease.Dispose();
        File.AppendAllText(managedPath, "allowed after lease");
    }

    [Fact]
    public void InjectedFile_IsRefusedEvenWhenUnsignedChecksumsAreRegenerated()
    {
        using RSA signer = CreateSigner();
        LauncherConfiguration configuration = CreateConfiguration(signer);
        CreatePayloadFiles();
        WriteEnvelope(signer, configuration);
        File.WriteAllText(
            Path.Combine(_root, "Injected.dll"),
            "injected executable code");
        File.WriteAllText(
            Path.Combine(_root, "SHA256SUMS"),
            "attacker-regenerated");

        Assert.False(
            LauncherReleaseAttestationVerifier.TryAcquire(
                _root,
                "1.0.0",
                configuration,
                out IDisposable? lease));
        Assert.Null(lease);
    }

    [Fact]
    public void TamperedPayloadAndSignature_AreRefused()
    {
        using RSA signer = CreateSigner();
        LauncherConfiguration configuration = CreateConfiguration(signer);
        string managedPath = CreatePayloadFiles();
        WriteEnvelope(signer, configuration);
        File.AppendAllText(managedPath, "tampered");

        Assert.False(
            LauncherReleaseAttestationVerifier.TryAcquire(
                _root,
                "1.0.0",
                configuration,
                out _));

        Directory.Delete(_root, recursive: true);
        Directory.CreateDirectory(_root);
        CreatePayloadFiles();
        WriteEnvelope(signer, configuration);
        string signaturePath = Path.Combine(
            _root,
            LauncherReleaseAttestationVerifier.SignatureFileName);
        byte[] signature = File.ReadAllBytes(signaturePath);
        signature[0] ^= 0x01;
        File.WriteAllBytes(signaturePath, signature);

        Assert.False(
            LauncherReleaseAttestationVerifier.TryAcquire(
                _root,
                "1.0.0",
                configuration,
                out _));
    }

    [Fact]
    public void WrongPinnedKeyFingerprint_IsRefused()
    {
        using RSA signer = CreateSigner();
        LauncherConfiguration signingConfiguration = CreateConfiguration(signer);
        CreatePayloadFiles();
        WriteEnvelope(signer, signingConfiguration);
        LauncherConfiguration wrongFingerprint = CreateConfiguration(
            signer,
            new string('0', 64));

        Assert.False(
            LauncherReleaseAttestationVerifier.TryAcquire(
                _root,
                "1.0.0",
                wrongFingerprint,
                out _));
    }

    [Fact]
    public void CanonicalParser_RejectsLineEndingCaseAndOrderingMutations()
    {
        using RSA signer = CreateSigner();
        LauncherConfiguration configuration = CreateConfiguration(signer);
        CreatePayloadFiles();
        byte[] canonical = BuildEnvelope(configuration);

        Assert.True(
            LauncherReleaseAttestationVerifier.TryParse(
                canonical,
                "1.0.0",
                configuration,
                out LauncherReleaseAttestation? parsed));
        Assert.NotNull(parsed);

        string text = Encoding.UTF8.GetString(canonical);
        Assert.False(
            LauncherReleaseAttestationVerifier.TryParse(
                Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n")),
                "1.0.0",
                configuration,
                out _));
        Assert.False(
            LauncherReleaseAttestationVerifier.TryParse(
                canonical[..^1],
                "1.0.0",
                configuration,
                out _));

        int hashOffset = text.IndexOf(
            "release-manifest-sha256\t",
            StringComparison.Ordinal)
            + "release-manifest-sha256\t".Length;
        char[] lowercaseHash = text.ToCharArray();
        lowercaseHash[hashOffset] = 'a';
        Assert.False(
            LauncherReleaseAttestationVerifier.TryParse(
                Encoding.UTF8.GetBytes(lowercaseHash),
                "1.0.0",
                configuration,
                out _));

        string[] lines = text[..^1].Split('\n');
        (lines[8], lines[9]) = (lines[9], lines[8]);
        Assert.False(
            LauncherReleaseAttestationVerifier.TryParse(
                Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"),
                "1.0.0",
                configuration,
                out _));
    }

    [Fact]
    public void StrictSubjectPublicKeyInfo_RejectsTrailingDataAndSmallKeys()
    {
        using RSA signer = CreateSigner();
        byte[] exact = signer.ExportSubjectPublicKeyInfo();
        Assert.True(
            StrictRsaSubjectPublicKeyInfo.TryRead(
                exact,
                out RSAParameters parameters));
        Assert.NotNull(parameters.Modulus);
        Assert.Equal(384, parameters.Modulus!.Length);

        Assert.False(
            StrictRsaSubjectPublicKeyInfo.TryRead(
                [.. exact, 0x00],
                out _));
        using RSA small = RSA.Create();
        small.KeySize = 2048;
        Assert.False(
            StrictRsaSubjectPublicKeyInfo.TryRead(
                small.ExportSubjectPublicKeyInfo(),
                out _));
    }

    [Theory]
    [InlineData("./WinShot.dll")]
    [InlineData("folder/./WinShot.dll")]
    [InlineData("folder/../WinShot.dll")]
    [InlineData("CON")]
    [InlineData("COM1.txt")]
    [InlineData("folder./WinShot.dll.")]
    [InlineData("folder/WinShot.dll ")]
    [InlineData("folder/WinShot?.dll")]
    [InlineData("activation-release-attestation.V1")]
    public void InventoryPathPolicy_RejectsWindowsAliasesAndUnsafeNames(
        string path)
    {
        Assert.False(
            LauncherReleaseAttestationVerifier.IsSafeInventoryPath(path));
    }

    [Fact]
    public void BoundedTraversal_RejectsExcessiveDepthBeforeDescent()
    {
        string current = _root;
        for (int index = 0; index < 34; index++)
        {
            current = Path.Combine(current, $"d{index}");
            Directory.CreateDirectory(current);
        }

        Assert.Throws<IOException>(
            () => LauncherReleaseAttestationVerifier.EnumerateBoundedFiles(
                _root));
    }

    [Fact]
    public void BoundedTraversal_RejectsFileCountAboveEnvelopeBudget()
    {
        for (int index = 0; index < 4097; index++)
        {
            File.WriteAllBytes(
                Path.Combine(_root, $"f{index:D4}.bin"),
                []);
        }

        Assert.Throws<IOException>(
            () => LauncherReleaseAttestationVerifier.EnumerateBoundedFiles(
                _root));
    }

    private string CreatePayloadFiles()
    {
        File.WriteAllText(
            Path.Combine(_root, "release-manifest.json"),
            "{\"application\":\"WinShot\",\"version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(_root, "WinShot.exe"), "apphost");
        string managedPath = Path.Combine(_root, "WinShot.dll");
        File.WriteAllText(managedPath, "managed-code");
        File.WriteAllText(Path.Combine(_root, "SHA256SUMS"), "audit-sidecar");
        return managedPath;
    }

    private void WriteEnvelope(
        RSA signer,
        LauncherConfiguration configuration)
    {
        byte[] envelope = BuildEnvelope(configuration);
        byte[] signature = signer.SignData(
            envelope,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);
        File.WriteAllBytes(
            Path.Combine(
                _root,
                LauncherReleaseAttestationVerifier.AttestationFileName),
            envelope);
        File.WriteAllBytes(
            Path.Combine(
                _root,
                LauncherReleaseAttestationVerifier.SignatureFileName),
            signature);
    }

    private byte[] BuildEnvelope(LauncherConfiguration configuration)
    {
        string[] paths = Directory.GetFiles(
                _root,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_root, path).Replace('\\', '/'))
            .Where(path =>
                !path.Equals(
                    LauncherReleaseAttestationVerifier.AttestationFileName,
                    StringComparison.Ordinal)
                && !path.Equals(
                    LauncherReleaseAttestationVerifier.SignatureFileName,
                    StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        string manifestHash = Hash(
            Path.Combine(_root, "release-manifest.json"));
        var builder = new StringBuilder();
        builder.AppendLine("WINSHOT-ACTIVATION-RELEASE-ATTESTATION-V1");
        builder.Append("package-id\t").AppendLine(configuration.PackageId);
        builder.Append("version\t").AppendLine("1.0.0");
        builder.Append("channel\t").AppendLine(configuration.ExpectedReleaseChannel);
        builder.Append("runtime-identifier\t")
            .AppendLine(configuration.ExpectedRuntimeIdentifier);
        builder.Append("paid-major\t")
            .AppendLine(
                configuration.ExpectedPaidMajor.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        builder.Append("release-manifest-sha256\t")
            .AppendLine(manifestHash);
        builder.Append("file-count\t")
            .AppendLine(
                paths.Length.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        foreach (string path in paths)
        {
            string fullPath = Path.Combine(
                _root,
                path.Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(fullPath);
            builder.Append("file\t")
                .Append(Hash(fullPath))
                .Append('\t')
                .Append(
                    info.Length.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))
                .Append('\t')
                .Append(path)
                .Append('\n');
        }

        return new UTF8Encoding(false).GetBytes(
            builder.ToString().Replace("\r\n", "\n"));
    }

    private static LauncherConfiguration CreateConfiguration(
        RSA signer,
        string? fingerprintOverride = null)
    {
        byte[] key = signer.ExportSubjectPublicKeyInfo();
        return new LauncherConfiguration(
            "PROVISIONAL.WinShot",
            "WinShot.exe",
            "WinShot",
            "Iftat Bhuiyan",
            "UNSET",
            "UNSET",
            "stable",
            "win-x64",
            1,
            Convert.ToBase64String(key),
            fingerprintOverride ?? Convert.ToHexString(SHA256.HashData(key)));
    }

    private static RSA CreateSigner()
    {
        RSA signer = RSA.Create();
        signer.KeySize = 3072;
        return signer;
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
