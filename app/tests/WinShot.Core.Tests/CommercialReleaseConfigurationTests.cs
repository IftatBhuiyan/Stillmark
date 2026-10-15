using WinShot.App;
using WinShot.App.Commercial;
using WinShot.Licensing;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class CommercialReleaseConfigurationTests
{
    private const string OfflineConfiguration = """
        {
          "schemaVersion": 1,
          "contractRevision": 1,
          "applicationMajor": 1,
          "licenseMode": "offline-signed",
          "provider": null,
          "storefront": {
            "checkoutApproved": false,
            "buyerTermsApproved": false,
            "primaryVariantId": null,
            "checkoutUrl": "",
            "accountUrl": "https://getstillmark.com/manage-license"
          }
        }
        """;

    private const string StripeConfiguration = """
        {
          "schemaVersion": 1,
          "contractRevision": 4,
          "applicationMajor": 1,
          "licenseMode": "offline-signed",
          "provider": null,
          "storefront": {
            "checkoutApproved": true,
            "buyerTermsApproved": true,
            "primaryVariantId": null,
            "checkoutUrl": "https://getstillmark.com/pricing",
            "accountUrl": "https://getstillmark.com/manage-license"
          }
        }
        """;

    [Fact]
    public void Embedded_shipping_configuration_is_valid_safe_and_offline()
    {
        CommercialConfigurationLoadResult loaded = CommercialReleaseConfigurationLoader.Current;
        Assert.True(loaded.IsValid);
        Assert.Equal(CommercialLicenseMode.OfflineSigned, loaded.Configuration!.LicenseMode);
        Assert.Equal(AppInfo.Major, loaded.Configuration.ApplicationMajor);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, loaded.Configuration.Provider.ValueKind);
        Assert.True(loaded.Configuration.Storefront.CheckoutApproved);
        Assert.True(loaded.Configuration.Storefront.BuyerTermsApproved);
        Assert.Equal("https://getstillmark.com/pricing", loaded.Configuration.Storefront.CheckoutUrl);
        Assert.Equal(LicenseEnforcementMode.OfflineSigned, LicenseProviderConfiguration.Create().Mode);
    }

    [Fact]
    public void Stripe_checkout_uses_the_first_party_license_path()
    {
        CommercialConfigurationLoadResult loaded = CommercialReleaseConfigurationLoader.Parse(StripeConfiguration, 1);
        Assert.True(loaded.IsValid);
        Assert.Equal("https://getstillmark.com/pricing", loaded.Configuration!.Storefront.CheckoutUrl);
        Assert.Equal("https://getstillmark.com/manage-license", loaded.Configuration.Storefront.AccountUrl);
        Assert.False(CommercialReleaseConfigurationLoader.Parse(
            StripeConfiguration.Replace("https://getstillmark.com/pricing", "https://evil.example/pricing", StringComparison.Ordinal), 1).IsValid);
    }

    [Fact]
    public void Retired_checkout_provider_mode_is_rejected()
    {
        string retired = StripeConfiguration.Replace("offline-signed", "unsupported-provider", StringComparison.Ordinal);
        Assert.False(CommercialReleaseConfigurationLoader.Parse(retired, 1).IsValid);
    }

    [Theory]
    [InlineData("""{"schemaVersion":1}""")]
    [InlineData("""{"schemaVersion":1,"schemaVersion":1}""")]
    [InlineData("""{"schemaVersion":2,"contractRevision":1,"applicationMajor":1,"licenseMode":"offline-signed","provider":null,"storefront":{"checkoutApproved":false,"buyerTermsApproved":false,"primaryVariantId":null,"checkoutUrl":"","accountUrl":"https://getstillmark.com/manage-license"}}""")]
    [InlineData("""{"schemaVersion":1,"contractRevision":1,"applicationMajor":1,"licenseMode":"offline-signed","provider":null,"storefront":{"checkoutApproved":true,"buyerTermsApproved":false,"primaryVariantId":null,"checkoutUrl":"","accountUrl":"https://getstillmark.com/manage-license"}}""")]
    [InlineData("""{"schemaVersion":1,"contractRevision":1,"applicationMajor":1,"licenseMode":"offline-signed","provider":null,"storefront":{"checkoutApproved":false,"buyerTermsApproved":false,"primaryVariantId":null,"checkoutUrl":"","accountUrl":"https://getstillmark.com/manage-license"},"unknown":true}""")]
    public void Partial_duplicate_unknown_or_mixed_documents_fail_closed(string json)
    {
        CommercialConfigurationLoadResult loaded = CommercialReleaseConfigurationLoader.Parse(json, 1);
        Assert.False(loaded.IsValid);
        Assert.NotEmpty(loaded.ErrorCode);
    }

    [Fact]
    public void Oversized_or_too_deep_documents_fail_closed()
    {
        string oversized = new('x', CommercialReleaseConfigurationLoader.MaximumConfigurationBytes + 1);
        string deep = new string('[', 10) + "0" + new string(']', 10);
        Assert.False(CommercialReleaseConfigurationLoader.Parse(oversized, 1).IsValid);
        Assert.False(CommercialReleaseConfigurationLoader.Parse(deep, 1).IsValid);
    }

    [Fact]
    public void Application_major_must_match()
    {
        Assert.False(CommercialReleaseConfigurationLoader.Parse(OfflineConfiguration, 2).IsValid);
    }
}
