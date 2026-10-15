using WinShot.App.Commercial;
using WinShot.Licensing;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class CommercialStorefrontConfigurationTests
{
    private static readonly string[] ForbiddenClaims = ["device", "email", "free update", "full feature", "lifetime", "permanent"];

    [Fact]
    public void Approved_account_configuration_exposes_the_first_party_purchase_and_license_routes()
    {
        CommercialStorefrontPolicy policy = CommercialStorefrontConfiguration.Create(LicenseEnforcementMode.OfflineSigned);
        Assert.True(policy.CheckoutAvailable);
        Assert.Equal("https://getstillmark.com/pricing", policy.CheckoutUri?.AbsoluteUri);
        Assert.True(policy.AccountAvailable);
        Assert.Equal("https://getstillmark.com/manage-license", policy.AccountUri?.AbsoluteUri);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Checkout_requires_both_explicit_approvals(bool checkoutApproved, bool buyerTermsApproved)
    {
        CommercialStorefrontPolicy policy = CreatePolicy("https://getstillmark.com/pricing", checkoutApproved, buyerTermsApproved);
        Assert.False(policy.CheckoutAvailable);
    }

    [Fact]
    public void Approved_first_party_pricing_page_is_available()
    {
        CommercialStorefrontPolicy policy = CreatePolicy("https://getstillmark.com/pricing", true, true);
        Assert.True(policy.CheckoutAvailable);
        Assert.Equal("https://getstillmark.com/pricing", policy.CheckoutUri?.AbsoluteUri);
        Assert.Equal("https://getstillmark.com/manage-license", policy.AccountUri?.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" https://getstillmark.com/pricing")]
    [InlineData("https://getstillmark.com/pricing ")]
    [InlineData("http://getstillmark.com/pricing")]
    [InlineData("https://getstillmark.com/pricing?email=buyer@example.com")]
    [InlineData("https://getstillmark.com/pricing#fragment")]
    [InlineData("https://evil.example/pricing")]
    [InlineData("javascript:alert(1)")]
    public void Checkout_rejects_urls_outside_the_exact_approved_route(string checkoutUrl)
    {
        CommercialStorefrontPolicy policy = CreatePolicy(checkoutUrl, true, true);
        Assert.False(policy.CheckoutAvailable);
        Assert.Null(policy.CheckoutUri);
    }

    [Theory]
    [InlineData(LicenseEnforcementMode.OfflineSigned)]
    [InlineData(LicenseEnforcementMode.ProviderValidated)]
    [InlineData((LicenseEnforcementMode)99)]
    public void Claims_remain_bounded_and_neutral_for_every_enforcement_state(LicenseEnforcementMode enforcementMode)
    {
        CommercialStorefrontPolicy policy = CreatePolicy("https://getstillmark.com/pricing", true, true, enforcementMode);
        foreach (string claim in AllClaims(policy)) {
            Assert.InRange(claim.Length, 1, CommercialStorefrontPolicy.MaximumClaimLength);
            foreach (string forbiddenClaim in ForbiddenClaims)
                Assert.DoesNotContain(forbiddenClaim, claim, StringComparison.OrdinalIgnoreCase);
        }
        if (enforcementMode != LicenseEnforcementMode.OfflineSigned)
            Assert.False(policy.CheckoutAvailable);
    }

    private static CommercialStorefrontPolicy CreatePolicy(string? checkoutUrl, bool checkoutApproved, bool buyerTermsApproved, LicenseEnforcementMode enforcementMode = LicenseEnforcementMode.OfflineSigned) =>
        CommercialStorefrontPolicy.Create(new CommercialStorefrontPolicyInput(enforcementMode, checkoutApproved, buyerTermsApproved, checkoutUrl, "https://getstillmark.com/manage-license"));

    private static string[] AllClaims(CommercialStorefrontPolicy policy) => [policy.LicensedDetail, policy.TrialDetail, policy.ExpiredDetail, policy.ActivationHelp];
}
