using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinShot.Licensing;

/// <summary>
/// Encodes and verifies WinShot license keys.
///
/// A key is a compact, JWT-like string: <c>base64url(payloadJson) + "." + base64url(signature)</c>.
/// The signature is ECDSA (nistP256 / SHA-256, IEEE-P1363 raw r||s) over the ASCII bytes of the
/// encoded payload segment. Signing requires the private key (seller-only, in the KeyGen tool);
/// verification uses the public key embedded in the app, so it works fully offline.
///
/// Security note: verification proves a key was minted by us and hasn't been altered. It does NOT
/// prevent a determined attacker from patching the compiled binary to skip the check — no purely
/// offline scheme can. The goal is to make casual piracy (download-and-run-free, key sharing being
/// the only easy path) require real effort. See DISTRIBUTION.md.
/// </summary>
public static class LicenseKey
{
    private const int MaximumKeyCharacters = 8 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // Signature is computed over the exact encoded bytes, so canonicalization is not required —
        // but keeping options stable avoids surprises when re-encoding.
        WriteIndented = false,
    };

    /// <summary>Signs a payload into a license key string. Seller-side (needs the private key).</summary>
    public static string Encode(LicensePayload payload, ECDsa privateKey)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
        string encodedPayload = Base64Url.Encode(json);
        byte[] signature = privateKey.SignData(
            Encoding.ASCII.GetBytes(encodedPayload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return encodedPayload + "." + Base64Url.Encode(signature);
    }

    /// <summary>Verifies a key against the app's embedded public key.</summary>
    public static bool TryVerify(string? key, out LicensePayload? payload)
    {
        using ECDsa publicKey = EmbeddedSigningKey.CreatePublicKey();
        return TryVerify(key, publicKey, out payload);
    }

    /// <summary>Verifies a key against a supplied public key (used by tests with an ephemeral pair).</summary>
    public static bool TryVerify(string? key, ECDsa publicKey, out LicensePayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(key))
            return false;
        if (key.Length > MaximumKeyCharacters)
            return false;

        // Tolerate whitespace/newlines a user might paste in.
        string cleaned = new string(key.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (cleaned.Length > MaximumKeyCharacters)
            return false;
        int dot = cleaned.IndexOf('.');
        if (dot <= 0 || dot >= cleaned.Length - 1)
            return false;

        string encodedPayload = cleaned[..dot];
        string encodedSignature = cleaned[(dot + 1)..];

        byte[] signature, json;
        try
        {
            signature = Base64Url.Decode(encodedSignature);
            json = Base64Url.Decode(encodedPayload);
        }
        catch (FormatException)
        {
            return false;
        }

        bool valid;
        try
        {
            valid = publicKey.VerifyData(
                Encoding.ASCII.GetBytes(encodedPayload), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
        if (!valid)
            return false;

        try
        {
            payload = JsonSerializer.Deserialize<LicensePayload>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return false;
        }
        return payload is not null;
    }
}
