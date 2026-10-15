using System.Security.Cryptography;
using WinShot.Licensing;
using Xunit;

namespace WinShot.Core.Tests;

public class LicenseKeyTests
{
    private static (ECDsa priv, ECDsa pub) NewPair()
    {
        var priv = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(priv.ExportSubjectPublicKeyInfo(), out _);
        return (priv, pub);
    }

    private static LicensePayload SamplePayload() => new()
    {
        Id = "WS-ABCDEF0102",
        Product = "winshot",
        Edition = "pro",
        Major = 1,
        IssuedAt = 1_737_158_400,
    };

    [Fact]
    public void Encode_then_Verify_roundtrips_payload()
    {
        var (priv, pub) = NewPair();
        var payload = SamplePayload();

        string key = LicenseKey.Encode(payload, priv);
        bool ok = LicenseKey.TryVerify(key, pub, out var parsed);

        Assert.True(ok);
        Assert.NotNull(parsed);
        Assert.Equal(payload.Id, parsed!.Id);
        Assert.Equal(payload.Product, parsed.Product);
        Assert.Equal(payload.Major, parsed.Major);
        Assert.Equal(payload.IssuedAt, parsed.IssuedAt);
    }

    [Fact]
    public void Verify_tolerates_pasted_whitespace()
    {
        var (priv, pub) = NewPair();
        string key = LicenseKey.Encode(SamplePayload(), priv);
        string messy = "  " + key.Insert(key.Length / 2, "\r\n ") + "\n";

        Assert.True(LicenseKey.TryVerify(messy, pub, out _));
    }

    [Fact]
    public void Tampered_payload_fails_verification()
    {
        var (priv, pub) = NewPair();
        string key = LicenseKey.Encode(SamplePayload(), priv);

        // Flip a character in the payload segment.
        int dot = key.IndexOf('.');
        char c = key[0] == 'A' ? 'B' : 'A';
        string tampered = c + key[1..dot] + key[dot..];

        Assert.False(LicenseKey.TryVerify(tampered, pub, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void Key_from_another_signer_is_rejected()
    {
        var (priv, _) = NewPair();
        var (_, otherPub) = NewPair();
        string key = LicenseKey.Encode(SamplePayload(), priv);

        Assert.False(LicenseKey.TryVerify(key, otherPub, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-key")]
    [InlineData("only-one.")]
    [InlineData(".only-two")]
    public void Malformed_keys_return_false(string? key)
    {
        var (_, pub) = NewPair();
        Assert.False(LicenseKey.TryVerify(key, pub, out _));
    }

    [Fact]
    public void Embedded_public_key_loads()
    {
        // Guards against a bad paste into EmbeddedSigningKey.PublicKeyBase64.
        using ECDsa pub = EmbeddedSigningKey.CreatePublicKey();
        Assert.Equal(256, pub.KeySize);
    }
}
