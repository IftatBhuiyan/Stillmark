using System.Security.Cryptography;
using System.Text;
using WinShot.KeyGen;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class KeyGenSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "WinShot-KeyGenSafety-" + Guid.NewGuid().ToString("N"));

    public KeyGenSafetyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void OutputPolicy_RequiresExplicitAbsoluteLocalNewFile()
    {
        string safe = CreateDirectory("safe");
        var policy = new KeyMaterialPathPolicy(Array.Empty<string>());

        Assert.Throws<ArgumentException>(() =>
            policy.RequireNewFile(null, "--out", ".pem"));
        Assert.Throws<InvalidOperationException>(() =>
            policy.RequireNewFile("relative.pem", "--out", ".pem"));
        Assert.Throws<InvalidOperationException>(() =>
            policy.RequireNewFile(@"\\server\share\key.pem", "--out", ".pem"));
        Assert.Throws<InvalidOperationException>(() =>
            policy.RequireNewFile(Path.Combine(safe, "key.txt"), "--out", ".pem"));
        Assert.Throws<InvalidOperationException>(() =>
            policy.RequireNewFile(Path.Combine(safe, "CON.pem"), "--out", ".pem"));
        Assert.Throws<InvalidOperationException>(() =>
            policy.RequireNewFile(Path.Combine(safe, "key:stream.pem"), "--out", ".pem"));
        Assert.Throws<DirectoryNotFoundException>(() =>
            policy.RequireNewFile(
                Path.Combine(_root, "missing", "key.pem"),
                "--out",
                ".pem"));

        string accepted = policy.RequireNewFile(
            Path.Combine(safe, "key.pem"),
            "--out",
            ".pem");

        Assert.Equal(Path.Combine(safe, "key.pem"), accepted);
    }

    [Fact]
    public void OutputPolicy_RejectsForbiddenRootAndExistingDestination()
    {
        string forbidden = CreateDirectory("forbidden");
        string safe = CreateDirectory("safe");
        var policy = new KeyMaterialPathPolicy(new[] { forbidden });
        string existing = Path.Combine(safe, "existing.pem");
        File.WriteAllText(existing, "sentinel");

        Assert.Throws<InvalidOperationException>(() =>
            policy.RequireNewFile(
                Path.Combine(forbidden, "key.pem"),
                "--out",
                ".pem"));
        Assert.Throws<InvalidOperationException>(() =>
            policy.RequireNewFile(existing, "--out", ".pem"));
        Assert.Equal("sentinel", File.ReadAllText(existing));
    }

    [Fact]
    public void PathPolicy_RejectsReparsePointInDirectoryChain()
    {
        string safe = CreateDirectory("safe");
        var policy = new KeyMaterialPathPolicy(
            Array.Empty<string>(),
            path => string.Equals(path, safe, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : File.GetAttributes(path));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            policy.RequireNewFile(
                Path.Combine(safe, "key.pem"),
                "--out",
                ".pem"));

        Assert.Contains("reparse point", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InputPolicy_RejectsMissingAndReparsePointFiles()
    {
        string safe = CreateDirectory("safe");
        string privateKey = Path.Combine(safe, "key.pem");
        File.WriteAllText(privateKey, "fixture");
        var policy = new KeyMaterialPathPolicy(
            Array.Empty<string>(),
            path => string.Equals(path, privateKey, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Normal | FileAttributes.ReparsePoint
                : File.GetAttributes(path));

        Assert.Throws<FileNotFoundException>(() =>
            policy.RequireExistingFile(
                Path.Combine(safe, "missing.pem"),
                "--priv",
                ".pem"));
        Assert.Throws<InvalidOperationException>(() =>
            policy.RequireExistingFile(privateKey, "--priv", ".pem"));
    }

    [Fact]
    public void EncryptedPrivateKey_RoundTripsWithoutPlaintextPem()
    {
        string path = Path.Combine(_root, "encrypted.pem");
        char[] passphrase = "correct horse battery staple".ToCharArray();
        byte[] payload = Encoding.UTF8.GetBytes("seller-tool-roundtrip");

        try
        {
            using var original = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            KeyCustodyFiles.WriteEncryptedPrivateKey(path, original, passphrase);

            string pem = File.ReadAllText(path);
            Assert.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", pem, StringComparison.Ordinal);
            Assert.DoesNotContain("-----BEGIN EC PRIVATE KEY-----", pem, StringComparison.Ordinal);
            Assert.DoesNotContain("-----BEGIN PRIVATE KEY-----", pem, StringComparison.Ordinal);

            using ECDsa loaded = KeyCustodyFiles.LoadEncryptedPrivateKey(path, passphrase);
            byte[] signature = loaded.SignData(payload, HashAlgorithmName.SHA256);
            Assert.True(original.VerifyData(payload, signature, HashAlgorithmName.SHA256));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(passphrase.AsSpan()));
        }
    }

    [Fact]
    public void EncryptedPrivateKey_WrongPassphraseFailsWithoutSensitiveDetails()
    {
        string path = Path.Combine(_root, "encrypted.pem");
        char[] correct = "correct horse battery staple".ToCharArray();
        char[] wrong = "wrong horse battery staple!!".ToCharArray();
        try
        {
            using var original = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            KeyCustodyFiles.WriteEncryptedPrivateKey(path, original, correct);

            CryptographicException error = Assert.Throws<CryptographicException>(() =>
                KeyCustodyFiles.LoadEncryptedPrivateKey(path, wrong));

            Assert.Equal("Encrypted private key could not be opened.", error.Message);
            Assert.DoesNotContain(path, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(new string(wrong), error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Zero(correct);
            Zero(wrong);
        }
    }

    [Fact]
    public void PrivateKeyLoader_RejectsUnencryptedLegacyPem()
    {
        string path = Path.Combine(_root, "legacy.pem");
        char[] passphrase = "correct horse battery staple".ToCharArray();
        try
        {
            using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(path, privateKey.ExportECPrivateKeyPem());

            CryptographicException error = Assert.Throws<CryptographicException>(() =>
                KeyCustodyFiles.LoadEncryptedPrivateKey(path, passphrase));

            Assert.Equal("Only an encrypted PKCS#8 private key is accepted.", error.Message);
        }
        finally
        {
            Zero(passphrase);
        }
    }

    [Fact]
    public void SecretWriters_CreateNewAndNeverOverwrite()
    {
        string path = Path.Combine(_root, "license.txt");
        KeyCustodyFiles.WriteSingleLicense(path, "first-token");

        Assert.Throws<IOException>(() =>
            KeyCustodyFiles.WriteSingleLicense(path, "replacement-token"));

        Assert.Equal("first-token", KeyCustodyFiles.ReadLicenseKey(path));
        Assert.DoesNotContain("replacement-token", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void BatchWriter_RejectsUnboundedCountsBeforeCreatingOutput()
    {
        string zeroPath = Path.Combine(_root, "zero.csv");
        string excessivePath = Path.Combine(_root, "excessive.csv");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            KeyCustodyFiles.WriteLicenseBatch(zeroPath, 0, () => ("id", "token")));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            KeyCustodyFiles.WriteLicenseBatch(
                excessivePath,
                KeyCustodyFiles.MaximumBatchCount + 1,
                () => ("id", "token")));

        Assert.False(File.Exists(zeroPath));
        Assert.False(File.Exists(excessivePath));
    }

    [Fact]
    public void BatchWriter_StreamsExactRowsIntoNewFile()
    {
        string path = Path.Combine(_root, "keys.csv");
        int minted = 0;

        KeyCustodyFiles.WriteLicenseBatch(path, 3, () =>
        {
            minted++;
            return ($"WS-{minted}", $"token-{minted}");
        });

        Assert.Equal(3, minted);
        Assert.Equal(
            new[]
            {
                "id,license_key",
                "WS-1,token-1",
                "WS-2,token-2",
                "WS-3,token-3",
            },
            File.ReadAllLines(path));
        Assert.Throws<IOException>(() =>
            KeyCustodyFiles.WriteLicenseBatch(path, 1, () => ("WS-X", "replacement")));
        Assert.DoesNotContain("replacement", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void BatchWriter_RetainsItsNewPartialFileForExplicitReviewWhenMintingFails()
    {
        string path = Path.Combine(_root, "partial.csv");
        int minted = 0;

        Assert.Throws<InvalidOperationException>(() =>
            KeyCustodyFiles.WriteLicenseBatch(path, 3, () =>
            {
                minted++;
                if (minted == 2)
                    throw new InvalidOperationException("synthetic mint failure");
                return ($"WS-{minted}", $"token-{minted}");
            }));

        Assert.True(File.Exists(path));
        Assert.Contains("WS-1,token-1", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain("WS-2", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void ProgramSource_HasNoLegacySecretDefaultsOrTokenArgument()
    {
        string repositoryRoot = FindRepositoryRoot();
        string source = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "WinShot.KeyGen", "Program.cs"));

        Assert.DoesNotContain("Directory.GetCurrentDirectory()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExportECPrivateKeyPem()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"winshot-keys.csv\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--key <token>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.WriteLine(LicenseKey.Encode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown command '{args[0]}'", source, StringComparison.Ordinal);
        Assert.Contains("WriteEncryptedPrivateKey", source, StringComparison.Ordinal);
        Assert.Contains("WriteSingleLicense", source, StringComparison.Ordinal);
        Assert.Contains("WriteLicenseBatch", source, StringComparison.Ordinal);
        Assert.Contains("--key-file", source, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
        string resolved = Path.GetFullPath(_root);
        if (!string.Equals(
                Path.GetDirectoryName(resolved),
                tempRoot,
                StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(resolved) is not { } leaf ||
            !leaf.StartsWith("WinShot-KeyGenSafety-", StringComparison.Ordinal) ||
            leaf.Length != "WinShot-KeyGenSafety-".Length + 32)
        {
            throw new InvalidOperationException("Refusing to remove a non-owned test directory.");
        }
        Directory.Delete(resolved, recursive: true);
    }

    private string CreateDirectory(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Zero(char[] value) =>
        CryptographicOperations.ZeroMemory(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(value.AsSpan()));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "WinShot.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Test repository root was not found.");
    }
}
