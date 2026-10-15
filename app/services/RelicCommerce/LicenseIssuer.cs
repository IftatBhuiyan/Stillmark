using System.Security.Cryptography;
using WinShot.Licensing;

namespace RelicCommerce;

public sealed class LicenseIssuer
{
    private readonly CommerceOptions _options;

    public LicenseIssuer(CommerceOptions options) => _options = options;

    public string Issue(LicenseOrder order)
    {
        _options.RequireReady();
        using ECDsa signer = ECDsa.Create();
        signer.ImportFromEncryptedPem(_options.PrivateKeyPem, _options.PrivateKeyPassphrase);
        LicensePayload payload = LicenseEntitlement.CreatePurchase(
            order.LicenseId,
            order.PurchasedAtUtc,
            order.ApplicationMajor,
            order.Edition) with
        {
            Name = order.Name,
            Email = order.Email,
        };
        return LicenseKey.Encode(payload, signer);
    }

}
