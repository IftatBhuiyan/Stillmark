using System.Security.Cryptography;

namespace WinShot.Licensing;

/// <summary>
/// The PUBLIC half of the license-signing key pair, embedded in the app so keys can be verified
/// offline. The PRIVATE half never ships — it lives only with the seller (see tools/WinShot.KeyGen
/// and DISTRIBUTION.md) and is what makes keys unforgeable.
///
/// This is a nistP256 public key exported as SubjectPublicKeyInfo (DER), base64-encoded.
///
/// IMPORTANT: This is the current embedded verifier, but source alone does not establish that its
/// counterpart has approved production custody. A production private key must be encrypted, stored
/// outside repositories/cloud-sync roots, and backed up offline. Losing it prevents new issuance;
/// leaking it permits forged keys. Rotation requires replacing this value and defining how existing
/// customer licenses remain valid.
/// </summary>
public static class EmbeddedSigningKey
{
    // Current embedded verifier. Production provenance/custody remains a release-owner gate.
    public const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEd+pHiTSnx9FlsUpzBLhe44f4O/k+uovoJIrB/tEOmgxYcKArAcbVMRa1qVzTavDUgcN9f3EPv9CtluOoK6x6fw==";

    /// <summary>Creates a fresh ECDsa loaded with the embedded public key. Dispose after use.</summary>
    public static ECDsa CreatePublicKey()
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeyBase64), out _);
        return ecdsa;
    }
}
