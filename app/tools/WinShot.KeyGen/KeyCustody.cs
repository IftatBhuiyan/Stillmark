using System.Security.Cryptography;
using System.Text;

namespace WinShot.KeyGen;

internal sealed class KeyMaterialPathPolicy
{
    private const int MaximumPathCharacters = 512;
    private readonly string[] _forbiddenRoots;
    private readonly Func<string, FileAttributes> _getAttributes;

    internal KeyMaterialPathPolicy(
        IEnumerable<string> forbiddenRoots,
        Func<string, FileAttributes>? getAttributes = null)
    {
        _forbiddenRoots = forbiddenRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _getAttributes = getAttributes ?? File.GetAttributes;
    }

    internal static KeyMaterialPathPolicy CreateDefault()
    {
        var forbiddenRoots = new List<string>
        {
            Path.GetTempPath(),
        };
        string? repositoryRoot = TryFindRepositoryRoot();
        if (repositoryRoot is not null)
            forbiddenRoots.Add(repositoryRoot);

        foreach (string variable in new[]
                 {
                     "OneDrive",
                     "OneDriveConsumer",
                     "OneDriveCommercial",
                 })
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
                forbiddenRoots.Add(value);
        }

        return new KeyMaterialPathPolicy(forbiddenRoots);
    }

    internal string RequireNewFile(
        string? suppliedPath,
        string optionName,
        string requiredExtension)
    {
        string path = RequireSafeAbsolutePath(suppliedPath, optionName, requiredExtension);
        if (File.Exists(path) || Directory.Exists(path))
            throw new InvalidOperationException($"{optionName} must name a new file; overwriting is refused.");
        return path;
    }

    internal string RequireExistingFile(
        string? suppliedPath,
        string optionName,
        string requiredExtension)
    {
        string path = RequireSafeAbsolutePath(suppliedPath, optionName, requiredExtension);
        if (!File.Exists(path))
            throw new FileNotFoundException($"{optionName} must name an existing file.");
        try
        {
            if ((_getAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"{optionName} cannot be a reparse point.");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{optionName} could not be inspected safely.");
        }
        return path;
    }

    private string RequireSafeAbsolutePath(
        string? suppliedPath,
        string optionName,
        string requiredExtension)
    {
        if (string.IsNullOrWhiteSpace(suppliedPath))
            throw new ArgumentException($"Missing required option {optionName}.");
        if (suppliedPath.Length > MaximumPathCharacters ||
            suppliedPath.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0 ||
            !Path.IsPathFullyQualified(suppliedPath) ||
            suppliedPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            suppliedPath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            suppliedPath.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{optionName} must be a bounded absolute local file path.");
        }

        string path;
        try
        {
            path = Path.GetFullPath(suppliedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"{optionName} is not a valid local file path.");
        }

        if (!string.Equals(
                Path.GetExtension(path),
                requiredExtension,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{optionName} must use the '{requiredExtension}' extension.");
        }

        string leaf = Path.GetFileName(path);
        string deviceStem = leaf.Split('.', 2)[0].TrimEnd(' ', '.');
        if (leaf.EndsWith(' ') ||
            leaf.EndsWith('.') ||
            leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            leaf.Contains(':') ||
            deviceStem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            deviceStem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            deviceStem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            deviceStem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (deviceStem.Length == 4 &&
                (deviceStem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 deviceStem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                deviceStem[3] is >= '1' and <= '9'))
        {
            throw new InvalidOperationException(
                $"{optionName} contains an unsafe Windows file name.");
        }

        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            throw new DirectoryNotFoundException($"{optionName}'s parent directory must already exist.");

        foreach (string forbiddenRoot in _forbiddenRoots)
        {
            if (IsAtOrWithin(path, forbiddenRoot))
            {
                throw new InvalidOperationException(
                    $"{optionName} cannot be inside the repository, temporary storage, or a known cloud-sync root.");
            }
        }

        AssertNoReparsePoint(parent, optionName);
        return path;
    }

    private void AssertNoReparsePoint(string directory, string optionName)
    {
        try
        {
            var current = new DirectoryInfo(directory);
            while (current is not null)
            {
                if ((_getAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        $"{optionName}'s directory chain cannot contain a reparse point.");
                }
                current = current.Parent;
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"{optionName}'s directory chain could not be inspected safely.");
        }
    }

    private static bool IsAtOrWithin(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            return true;
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoot(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string? TryFindRepositoryRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "WinShot.sln")) &&
                    Directory.Exists(Path.Combine(current.FullName, "tools", "WinShot.KeyGen")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
        }

        // A copied seller tool remains usable on an offline machine. Repository exclusion is
        // enforced whenever a WinShot source root is discoverable from the executable or CWD.
        return null;
    }
}

internal static class KeyCustodyFiles
{
    internal const int MaximumBatchCount = 10_000;
    internal const int MaximumPrivateKeyBytes = 64 * 1024;
    internal const int MaximumLicenseFileBytes = 16 * 1024;
    internal const int MinimumPassphraseCharacters = 16;
    private const int MaximumPassphraseCharacters = 1024;
    private const int Pbkdf2Iterations = 210_000;
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);

    internal static void ValidateNewPassphrase(ReadOnlySpan<char> passphrase)
    {
        if (passphrase.Length < MinimumPassphraseCharacters ||
            passphrase.Length > MaximumPassphraseCharacters ||
            ContainsControl(passphrase))
        {
            throw new ArgumentException(
                $"The private-key passphrase must contain {MinimumPassphraseCharacters} through " +
                $"{MaximumPassphraseCharacters} characters and no control characters.");
        }
    }

    internal static void WriteEncryptedPrivateKey(
        string path,
        ECDsa privateKey,
        ReadOnlySpan<char> passphrase)
    {
        ValidateNewPassphrase(passphrase);
        var parameters = new PbeParameters(
            PbeEncryptionAlgorithm.Aes256Cbc,
            HashAlgorithmName.SHA256,
            Pbkdf2Iterations);
        string encryptedPem = privateKey.ExportEncryptedPkcs8PrivateKeyPem(passphrase, parameters);
        try
        {
            WriteNewText(path, encryptedPem + Environment.NewLine);
        }
        finally
        {
            encryptedPem = string.Empty;
        }
    }

    internal static ECDsa LoadEncryptedPrivateKey(
        string path,
        ReadOnlySpan<char> passphrase)
    {
        if (passphrase.IsEmpty || passphrase.Length > MaximumPassphraseCharacters)
            throw new CryptographicException("Encrypted private key could not be opened.");

        string pem;
        try
        {
            pem = ReadBoundedText(path, MaximumPrivateKeyBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CryptographicException("Encrypted private key could not be opened.");
        }
        if (!pem.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal) ||
            pem.Contains("-----BEGIN EC PRIVATE KEY-----", StringComparison.Ordinal) ||
            pem.Contains("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal))
        {
            throw new CryptographicException(
                "Only an encrypted PKCS#8 private key is accepted.");
        }

        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromEncryptedPem(pem, passphrase);
            return ecdsa;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            ecdsa.Dispose();
            throw new CryptographicException("Encrypted private key could not be opened.");
        }
        finally
        {
            pem = string.Empty;
        }
    }

    internal static void WriteSingleLicense(string path, string licenseKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(licenseKey);
        if (licenseKey.Length > MaximumLicenseFileBytes)
            throw new ArgumentException("License key exceeds the seller-tool output boundary.");
        WriteNewText(path, licenseKey + Environment.NewLine);
    }

    internal static void WriteLicenseBatch(
        string path,
        int count,
        Func<(string Id, string LicenseKey)> mint)
    {
        if (count < 1 || count > MaximumBatchCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                $"Batch count must be between 1 and {MaximumBatchCount}.");
        }
        ArgumentNullException.ThrowIfNull(mint);

        try
        {
            using var stream = OpenNewFile(path);
            using var writer = new StreamWriter(
                stream,
                Utf8NoBom,
                bufferSize: 16 * 1024,
                leaveOpen: true);
            writer.NewLine = "\n";
            writer.WriteLine("id,license_key");
            for (int index = 0; index < count; index++)
            {
                (string id, string licenseKey) = mint();
                if (string.IsNullOrWhiteSpace(id) ||
                    id.IndexOfAny(new[] { ',', '\r', '\n' }) >= 0)
                {
                    throw new InvalidOperationException(
                        "Generated license identifier is not CSV-safe.");
                }
                if (string.IsNullOrWhiteSpace(licenseKey) ||
                    licenseKey.Length > MaximumLicenseFileBytes ||
                    licenseKey.IndexOfAny(new[] { ',', '\r', '\n' }) >= 0)
                {
                    throw new InvalidOperationException("Generated license key is not CSV-safe.");
                }
                writer.Write(id);
                writer.Write(',');
                writer.WriteLine(licenseKey);
            }
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                "Secret output could not be completed; any newly created partial file is retained for explicit review.");
        }
    }

    internal static string ReadLicenseKey(string path)
    {
        try
        {
            return ReadBoundedText(path, MaximumLicenseFileBytes).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("License key file could not be read.");
        }
    }

    private static bool ContainsControl(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (char.IsControl(character))
                return true;
        }
        return false;
    }

    private static string ReadBoundedText(string path, int maximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
            throw new IOException("Secret input file is empty or exceeds the seller-tool boundary.");
        using var reader = new StreamReader(
            stream,
            Utf8NoBom,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: false);
        return reader.ReadToEnd();
    }

    private static void WriteNewText(string path, string contents)
    {
        try
        {
            using var stream = OpenNewFile(path);
            using var writer = new StreamWriter(
                stream,
                Utf8NoBom,
                bufferSize: 4096,
                leaveOpen: true);
            writer.Write(contents);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                "Secret output could not be completed; any newly created partial file is retained for explicit review.");
        }
    }

    private static FileStream OpenNewFile(string path) =>
        new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);

}
