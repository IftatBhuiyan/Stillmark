using System.Security.Cryptography;
using WinShot.Licensing;
using System.Text.RegularExpressions;

namespace RelicCommerce;

public sealed record CommerceOptions(
    bool Enabled,
    string DatabasePath,
    string StripeSecretKey,
    string StripeWebhookSecret,
    string StripePriceId,
    string Currency,
    int ApplicationMajor,
    string Edition,
    bool ManagedPayments,
    string SuccessUrl,
    string CancelUrl,
    string PrivateKeyPem,
    string PrivateKeyPassphrase)
{
    public string SmtpHost { get; init; } = "";
    public int SmtpPort { get; init; }
    public string SmtpUser { get; init; } = "";
    public string SmtpPassword { get; init; } = "";
    public string SmtpFrom { get; init; } = "";

    public static CommerceOptions Load(IConfiguration configuration)
    {
        IConfiguration section = configuration.GetSection("RelicCommerce");
        return new(
            ParseBoolean(section["Enabled"]),
            section["DatabasePath"] ?? "relic-commerce.db",
            section["StripeSecretKey"] ?? "",
            section["StripeWebhookSecret"] ?? "",
            section["StripePriceId"] ?? "",
            section["Currency"] ?? "",
            ParseInt(section["ApplicationMajor"]),
            section["Edition"] ?? "",
            ParseBoolean(section["ManagedPayments"]),
            section["SuccessUrl"] ?? "",
            section["CancelUrl"] ?? "",
            section["PrivateKeyPem"] ?? "",
            section["PrivateKeyPassphrase"] ?? "")
        {
            SmtpHost = section["SmtpHost"] ?? "",
            SmtpPort = ParseInt(section["SmtpPort"]),
            SmtpUser = section["SmtpUser"] ?? "",
            SmtpPassword = section["SmtpPassword"] ?? "",
            SmtpFrom = section["SmtpFrom"] ?? "",
        };
    }

    public bool IsReady(out string reason)
    {
        if (!Enabled) { reason = "disabled"; return false; }
        if (string.IsNullOrWhiteSpace(StripeSecretKey)
            || !StripeSecretKey.StartsWith("sk_", StringComparison.Ordinal))
        { reason = "stripe-secret-missing"; return false; }
        if (string.IsNullOrWhiteSpace(StripeWebhookSecret)
            || !StripeWebhookSecret.StartsWith("whsec_", StringComparison.Ordinal))
        { reason = "webhook-secret-missing"; return false; }
        if (!Regex.IsMatch(StripePriceId, "^price_[A-Za-z0-9]+$", RegexOptions.CultureInvariant))
        { reason = "price-missing"; return false; }
        if (!Regex.IsMatch(Currency, "^[a-z]{3}$", RegexOptions.CultureInvariant))
        { reason = "currency-missing"; return false; }
        if (!string.Equals(Currency, "usd", StringComparison.Ordinal))
        { reason = "currency-must-be-usd"; return false; }
        if (ApplicationMajor <= 0 || ApplicationMajor > 999)
        { reason = "major-missing"; return false; }
        if (Edition.Length is < 1 or > 64 || Edition.Any(char.IsControl))
        { reason = "edition-missing"; return false; }
        if (!IsSafeRedirectUrl(SuccessUrl)
            || !SuccessUrl.Contains("{CHECKOUT_SESSION_ID}", StringComparison.Ordinal))
        { reason = "success-url-missing"; return false; }
        if (!IsSafeRedirectUrl(CancelUrl))
        { reason = "cancel-url-missing"; return false; }
        if (string.IsNullOrWhiteSpace(PrivateKeyPem) || string.IsNullOrWhiteSpace(PrivateKeyPassphrase))
        { reason = "signing-key-missing"; return false; }
        if (string.IsNullOrWhiteSpace(DatabasePath))
        { reason = "database-missing"; return false; }
        reason = "";
        return true;
    }

    public void RequireReady()
    {
        if (!IsReady(out string reason))
            throw new InvalidOperationException($"RelicCommerce is not ready: {reason}.");
    }

    public void RequireSigningKeyMatchesDesktopVerifier()
    {
        RequireReady();
        try
        {
            using ECDsa signer = ECDsa.Create();
            signer.ImportFromEncryptedPem(PrivateKeyPem, PrivateKeyPassphrase);
            byte[] configuredPublicKey = signer.ExportSubjectPublicKeyInfo();
            byte[] embeddedPublicKey = Convert.FromBase64String(EmbeddedSigningKey.PublicKeyBase64);
            if (!CryptographicOperations.FixedTimeEquals(configuredPublicKey, embeddedPublicKey))
                throw new InvalidOperationException("RelicCommerce is not ready: signing-key-does-not-match-desktop-verifier.");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or FormatException)
        {
            throw new InvalidOperationException("RelicCommerce is not ready: signing-key-invalid.", ex);
        }
    }

    private static bool ParseBoolean(string? value) =>
        bool.TryParse(value, out bool result) && result;

    private static int ParseInt(string? value) =>
        int.TryParse(value, out int result) ? result : 0;

    private static bool IsSafeRedirectUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return true;
        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
    }
}
