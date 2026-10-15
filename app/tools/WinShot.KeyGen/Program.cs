using System.Globalization;
using System.Security.Cryptography;
using WinShot.KeyGen;
using WinShot.Licensing;

// WinShot.KeyGen — seller-side license tooling. NEVER ship this or the private key with the app.
//
//   new-keypair  --out <absolute-path.pem>
//       Generate an encrypted signing key pair. Prompts twice for the private-key passphrase and
//       prints only the public key (paste into EmbeddedSigningKey.PublicKeyBase64).
//
//   issue        --priv <absolute-path.pem> --out <absolute-path.txt>
//                [--id <id>] [--maj <n>] [--edition pro] [--name "..."]
//                [--email "..."] [--purchased-at yyyy-MM-ddTHH:mm:ssZ | --upto yyyy-MM-dd]
//       Use --purchased-at for a permanent license with one calendar year of updates.
//       Mint a single license key into a new file. The bearer token is never printed.
//
//   issue-batch  --priv <absolute-path.pem> --count <n> --out <absolute-path.csv>
//                [--maj <n>] [--edition pro]
//       Mint a bounded pool and stream it into a new CSV.
//
//   verify       --pub <base64> --key-file <absolute-path.txt>
//       Verify a key file against a public key and print its decoded payload.

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine(
            "Usage: WinShot.KeyGen <new-keypair|issue|issue-batch|verify> [explicit safe options]");
        return 1;
    }

    try
    {
        return args[0] switch
        {
            "new-keypair" => NewKeypair(args),
            "issue" => Issue(args),
            "issue-batch" => IssueBatch(args),
            "verify" => Verify(args),
            _ => Fail("Unknown command. Run without arguments for usage."),
        };
    }
    catch (Exception ex)
    {
        return Fail(ex.Message);
    }
}

static int NewKeypair(string[] args)
{
    EnsureOptions(args, "--out");
    var pathPolicy = KeyMaterialPathPolicy.CreateDefault();
    string privateKeyPath = pathPolicy.RequireNewFile(
        Require(args, "--out"),
        "--out",
        ".pem");
    char[] passphrase = ReadConfirmedSecret(
        "Private-key passphrase: ",
        "Confirm private-key passphrase: ");
    try
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        KeyCustodyFiles.WriteEncryptedPrivateKey(privateKeyPath, ecdsa, passphrase);
        string publicBase64 = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

        Console.WriteLine("Generated encrypted nistP256 signing key pair.");
        Console.WriteLine("Private key written to the explicit output file.");
        Console.WriteLine("Keep the encrypted file and passphrase separate, offline, and backed up.");
        Console.WriteLine();
        Console.WriteLine("Public key (paste into EmbeddedSigningKey.PublicKeyBase64):");
        Console.WriteLine();
        Console.WriteLine(publicBase64);
        return 0;
    }
    finally
    {
        CryptographicOperations.ZeroMemory(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(passphrase.AsSpan()));
    }
}

static int Issue(string[] args)
{
    EnsureOptions(args, "--priv", "--out", "--id", "--maj", "--edition", "--name", "--email", "--upto", "--purchased-at");
    string? purchasedAtText = Opt(args, "--purchased-at");
    if (purchasedAtText is not null && Opt(args, "--upto") is not null)
        throw new ArgumentException("Use --purchased-at for a new purchase or --upto for a legacy key, not both.");
    var pathPolicy = KeyMaterialPathPolicy.CreateDefault();
    string outputPath = pathPolicy.RequireNewFile(
        Require(args, "--out"),
        "--out",
        ".txt");
    string privateKeyPath = pathPolicy.RequireExistingFile(
        Require(args, "--priv"),
        "--priv",
        ".pem");
    var payload = BuildPayload(
        id: Opt(args, "--id") ?? NewId(),
        maj: ParseBoundedInteger(Opt(args, "--maj") ?? "1", "--maj", 1, 999),
        edition: Opt(args, "--edition") ?? "pro",
        name: Opt(args, "--name") ?? "",
        email: Opt(args, "--email") ?? "",
        upto: ParseUpto(Opt(args, "--upto")));

    if (purchasedAtText is not null)
    {
        DateTimeOffset purchasedAt = DateTimeOffset.ParseExact(
            purchasedAtText,
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);
        payload = LicenseEntitlement.CreatePurchase(payload.Id, purchasedAt, payload.Major, payload.Edition)
            with { Name = payload.Name, Email = payload.Email };
    }

    using ECDsa priv = LoadPrivate(privateKeyPath);
    string key = LicenseKey.Encode(payload, priv);
    KeyCustodyFiles.WriteSingleLicense(outputPath, key);
    key = string.Empty;
    Console.WriteLine($"Wrote license '{payload.Id}' to the explicit output file.");
    return 0;
}

static int IssueBatch(string[] args)
{
    EnsureOptions(args, "--priv", "--count", "--maj", "--edition", "--out");
    var pathPolicy = KeyMaterialPathPolicy.CreateDefault();
    string outputPath = pathPolicy.RequireNewFile(
        Require(args, "--out"),
        "--out",
        ".csv");
    string privateKeyPath = pathPolicy.RequireExistingFile(
        Require(args, "--priv"),
        "--priv",
        ".pem");
    int count = ParseBoundedInteger(
        Require(args, "--count"),
        "--count",
        1,
        KeyCustodyFiles.MaximumBatchCount);
    int maj = ParseBoundedInteger(Opt(args, "--maj") ?? "1", "--maj", 1, 999);
    string edition = Opt(args, "--edition") ?? "pro";
    ValidatePayloadText(edition, "--edition", 32, allowEmpty: false, identifierOnly: true);

    using ECDsa priv = LoadPrivate(privateKeyPath);
    KeyCustodyFiles.WriteLicenseBatch(outputPath, count, () =>
    {
        string id = NewId();
        string key = LicenseKey.Encode(BuildPayload(id, maj, edition, "", "", 0), priv);
        return (id, key);
    });
    Console.WriteLine(
        $"Wrote {count} keys (major v{maj}, edition '{edition}') to the explicit output file.");
    return 0;
}

static int Verify(string[] args)
{
    EnsureOptions(args, "--pub", "--key-file");
    var pathPolicy = KeyMaterialPathPolicy.CreateDefault();
    string pubBase64 = Require(args, "--pub");
    string keyPath = pathPolicy.RequireExistingFile(
        Require(args, "--key-file"),
        "--key-file",
        ".txt");
    string key = KeyCustodyFiles.ReadLicenseKey(keyPath);
    using var pub = ECDsa.Create();
    pub.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pubBase64), out _);

    if (LicenseKey.TryVerify(key, pub, out var payload) && payload is not null)
    {
        Console.WriteLine("VALID");
        Console.WriteLine($"  id      : {payload.Id}");
        Console.WriteLine($"  product : {payload.Product}");
        Console.WriteLine($"  edition : {payload.Edition}");
        Console.WriteLine($"  major   : v{payload.Major}");
        Console.WriteLine($"  issued  : {DateTimeOffset.FromUnixTimeSeconds(payload.IssuedAt):yyyy-MM-dd}");
        if (payload.UpdatesUntil > 0)
            Console.WriteLine($"  upto    : {DateTimeOffset.FromUnixTimeSeconds(payload.UpdatesUntil):yyyy-MM-dd}");
        if (!string.IsNullOrEmpty(payload.Name)) Console.WriteLine($"  name    : {payload.Name}");
        if (!string.IsNullOrEmpty(payload.Email)) Console.WriteLine($"  email   : {payload.Email}");
        return 0;
    }
    Console.WriteLine("INVALID");
    return 2;
}

// ---- helpers ----

static LicensePayload BuildPayload(string id, int maj, string edition, string name, string email, long upto)
{
    ValidatePayloadText(id, "--id", 64, allowEmpty: false, identifierOnly: true);
    ValidatePayloadText(edition, "--edition", 32, allowEmpty: false, identifierOnly: true);
    ValidatePayloadText(name, "--name", 256, allowEmpty: true, identifierOnly: false);
    ValidatePayloadText(email, "--email", 320, allowEmpty: true, identifierOnly: false);
    if (maj < 1 || maj > 999)
        throw new ArgumentOutOfRangeException(nameof(maj), "License major must be between 1 and 999.");

    return new LicensePayload
    {
        Version = 1,
        Id = id,
        Product = "winshot",
        Edition = edition,
        Name = name,
        Email = email,
        Major = maj,
        IssuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        UpdatesUntil = upto,
    };
}

static ECDsa LoadPrivate(string pemPath)
{
    char[] passphrase = ReadSecret("Private-key passphrase: ");
    try
    {
        return KeyCustodyFiles.LoadEncryptedPrivateKey(pemPath, passphrase);
    }
    finally
    {
        CryptographicOperations.ZeroMemory(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(passphrase.AsSpan()));
    }
}

static long ParseUpto(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return 0;
    var date = DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    return new DateTimeOffset(date, TimeSpan.Zero).ToUnixTimeSeconds();
}

static string NewId() => "WS-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5));

static int ParseBoundedInteger(string value, string optionName, int minimum, int maximum)
{
    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ||
        parsed < minimum ||
        parsed > maximum)
    {
        throw new ArgumentException(
            $"{optionName} must be an integer between {minimum} and {maximum}.");
    }
    return parsed;
}

static void ValidatePayloadText(
    string value,
    string optionName,
    int maximumCharacters,
    bool allowEmpty,
    bool identifierOnly)
{
    if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) ||
        value.Length > maximumCharacters ||
        value.Any(char.IsControl) ||
        (identifierOnly && value.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character != '-')))
    {
        throw new ArgumentException($"{optionName} contains invalid or excessive text.");
    }
}

static char[] ReadConfirmedSecret(string firstPrompt, string confirmationPrompt)
{
    char[] first = ReadSecret(firstPrompt);
    char[]? confirmation = null;
    try
    {
        confirmation = ReadSecret(confirmationPrompt);
        KeyCustodyFiles.ValidateNewPassphrase(first);
        KeyCustodyFiles.ValidateNewPassphrase(confirmation);
        if (!CryptographicOperations.FixedTimeEquals(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(first.AsSpan()),
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(confirmation.AsSpan())))
        {
            throw new ArgumentException("Private-key passphrases do not match.");
        }
        return first;
    }
    catch
    {
        CryptographicOperations.ZeroMemory(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(first.AsSpan()));
        throw;
    }
    finally
    {
        if (confirmation is not null)
        {
            CryptographicOperations.ZeroMemory(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(confirmation.AsSpan()));
        }
    }
}

static char[] ReadSecret(string prompt)
{
    if (Console.IsInputRedirected)
    {
        throw new InvalidOperationException(
            "Secret entry requires an interactive console; redirected passphrases are refused.");
    }

    Console.Error.Write(prompt);
    char[] buffer = new char[1024];
    int count = 0;
    try
    {
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return buffer.AsSpan(0, count).ToArray();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (count > 0)
                    buffer[--count] = '\0';
                continue;
            }
            if (!char.IsControl(key.KeyChar))
            {
                if (count >= buffer.Length)
                    throw new ArgumentException("Secret input exceeds the seller-tool boundary.");
                buffer[count++] = key.KeyChar;
            }
        }
    }
    finally
    {
        CryptographicOperations.ZeroMemory(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer.AsSpan()));
    }
}

static string? Opt(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string Require(string[] args, string name) =>
    Opt(args, name) ?? throw new ArgumentException($"Missing required option {name}");

static void EnsureOptions(string[] args, params string[] allowedOptions)
{
    var allowed = new HashSet<string>(allowedOptions, StringComparer.Ordinal);
    var seen = new HashSet<string>(StringComparer.Ordinal);
    for (int index = 1; index < args.Length; index++)
    {
        string option = args[index];
        if (!option.StartsWith("--", StringComparison.Ordinal) || !allowed.Contains(option))
            throw new ArgumentException("Unknown or misplaced seller-tool option.");
        if (!seen.Add(option))
            throw new ArgumentException($"Duplicate option {option} is refused.");
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Missing required value for {option}.");
        index++;
    }
}

static int Fail(string message)
{
    Console.Error.WriteLine("Error: " + message);
    return 1;
}
