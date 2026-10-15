using System.Security.Cryptography;
using System.Text;
using WinShot.Core.Update;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class VelopackReleaseCatalogVerifierTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"velopack-catalog-{Guid.NewGuid():N}");

    public VelopackReleaseCatalogVerifierTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SignedCatalog_BindsStablePackageAndReleaseTimestamp()
    {
        using RSA signer = CreateSigner();
        byte[] package = Encoding.UTF8.GetBytes("signed-package");
        string packageName = "RelicScreenshot-1.2.3-full.nupkg";
        string packagePath = Path.Combine(_root, packageName);
        File.WriteAllBytes(packagePath, package);
        byte[] catalog = BuildCatalog(
            packageName,
            package.LongLength,
            Hash(package),
            releasedAt: 1_780_000_000);
        byte[] signature = Sign(signer, catalog);

        Assert.True(
            VelopackReleaseCatalogVerifier.TryVerify(
                catalog,
                signature,
                signer.ExportRSAPublicKeyPem(),
                Fingerprint(signer),
                out VelopackReleaseCatalogEntry? entry));
        Assert.NotNull(entry);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_780_000_000), entry!.ReleasedAtUtc);
        Assert.True(
            VelopackReleaseCatalogVerifier.Matches(
                entry,
                new VelopackReleaseExpectations(
                    "Stillmark",
                    "RelicScreenshot",
                    "stable",
                    "win-x64",
                    new Version(1, 2, 3),
                    packageName)));
        Assert.True(VelopackReleaseCatalogVerifier.VerifyPackage(packagePath, entry));
    }

    [Fact]
    public void CatalogTamperingAndWrongPinnedKey_AreRefused()
    {
        using RSA signer = CreateSigner();
        byte[] catalog = BuildCatalog(
            "RelicScreenshot-1.2.3-full.nupkg",
            4,
            new string('A', 64),
            releasedAt: 1_780_000_000);
        byte[] signature = Sign(signer, catalog);
        byte[] tampered = [.. catalog];
        tampered[^2] ^= 0x01;

        Assert.False(
            VelopackReleaseCatalogVerifier.TryVerify(
                tampered,
                signature,
                signer.ExportRSAPublicKeyPem(),
                Fingerprint(signer),
                out _));
        Assert.False(
            VelopackReleaseCatalogVerifier.TryVerify(
                catalog,
                signature,
                signer.ExportRSAPublicKeyPem(),
                new string('0', 64),
                out _));
    }

    [Theory]
    [InlineData("..\\RelicScreenshot-1.2.3-full.nupkg")]
    [InlineData("folder/RelicScreenshot-1.2.3-full.nupkg")]
    [InlineData("RelicScreenshot-1.2.3-full.exe")]
    [InlineData("RelicScreenshot-1.2.3..nupkg")]
    public void UnsafePackageFileNames_AreRefused(string packageName)
    {
        using RSA signer = CreateSigner();
        byte[] catalog = BuildCatalog(
            packageName,
            4,
            new string('A', 64),
            releasedAt: 1_780_000_000);

        Assert.False(
            VelopackReleaseCatalogVerifier.TryVerify(
                catalog,
                Sign(signer, catalog),
                signer.ExportRSAPublicKeyPem(),
                Fingerprint(signer),
                out _));
    }

    [Fact]
    public void ChangedDownloadedPackage_IsRefusedByBoundDigest()
    {
        using RSA signer = CreateSigner();
        byte[] original = Encoding.UTF8.GetBytes("original");
        const string packageName = "RelicScreenshot-1.2.3-full.nupkg";
        string packagePath = Path.Combine(_root, packageName);
        File.WriteAllBytes(packagePath, original);
        byte[] catalog = BuildCatalog(
            packageName,
            original.LongLength,
            Hash(original),
            releasedAt: 1_780_000_000);
        Assert.True(
            VelopackReleaseCatalogVerifier.TryVerify(
                catalog,
                Sign(signer, catalog),
                signer.ExportRSAPublicKeyPem(),
                Fingerprint(signer),
                out VelopackReleaseCatalogEntry? entry));

        File.AppendAllText(packagePath, "tampered");
        Assert.False(VelopackReleaseCatalogVerifier.VerifyPackage(packagePath, entry));
    }

    private static RSA CreateSigner()
    {
        RSA signer = RSA.Create();
        signer.KeySize = 3072;
        return signer;
    }

    private static byte[] BuildCatalog(
        string packageName,
        long packageBytes,
        string packageSha256,
        long releasedAt)
    {
        string text = $$"""
            {
              "schemaVersion": 1,
              "application": "Stillmark",
              "packageId": "RelicScreenshot",
              "channel": "stable",
              "runtimeIdentifier": "win-x64",
              "version": "1.2.3",
              "releasedAtUtc": {{releasedAt}},
              "packageFileName": "{{packageName}}",
              "packageBytes": {{packageBytes}},
              "packageSha256": "{{packageSha256}}"
            }
            """;
        return new UTF8Encoding(false).GetBytes(text.Replace("\r\n", "\n"));
    }

    private static byte[] Sign(RSA signer, byte[] bytes) => signer.SignData(
        bytes,
        HashAlgorithmName.SHA256,
        RSASignaturePadding.Pss);

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static string Fingerprint(RSA signer) =>
        Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo()));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
