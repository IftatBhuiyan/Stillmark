using System.Diagnostics.CodeAnalysis;
using WinShot.Licensing;

namespace WinShot.App.Commercial;

/// <summary>
/// Credential-free storefront composition from the same immutable document used by the release.
/// Checkout remains unavailable unless the complete cross-checked contract is valid.
/// </summary>
internal static class CommercialStorefrontConfiguration
{
    internal static CommercialStorefrontPolicy Create(LicenseEnforcementMode enforcementMode)
    {
        CommercialConfigurationLoadResult loaded =
            CommercialReleaseConfigurationLoader.Current;
        CommercialStorefrontSettings? storefront = loaded.Configuration?.Storefront;
        return CommercialStorefrontPolicy.Create(new CommercialStorefrontPolicyInput(
            enforcementMode,
            loaded.IsValid && storefront!.CheckoutApproved,
            loaded.IsValid && storefront!.BuyerTermsApproved,
            loaded.IsValid ? storefront!.CheckoutUrl : "",
            loaded.IsValid && enforcementMode == LicenseEnforcementMode.OfflineSigned
                ? storefront!.AccountUrl
                : CommercialReleaseConfigurationLoader.StillmarkAccountUrl));
    }
}

internal sealed record CommercialStorefrontPolicyInput(
    LicenseEnforcementMode EnforcementMode,
    bool CheckoutApproved,
    bool BuyerTermsApproved,
    string? CheckoutUrl,
    string? AccountUrl);

/// <summary>
/// Pure, bounded policy for all buyer-facing storefront links and claims.
/// </summary>
internal sealed record CommercialStorefrontPolicy(
    Uri? CheckoutUri,
    Uri? AccountUri,
    string LicensedDetail,
    string TrialDetail,
    string ExpiredDetail,
    string ActivationHelp)
{
    internal const int MaximumUrlLength = 2_048;
    internal const int MaximumClaimLength = 160;

    private const string StillmarkAccountUrl = "https://getstillmark.com/manage-license";

    internal bool CheckoutAvailable => CheckoutUri is not null;
    internal bool AccountAvailable => AccountUri is not null;

    internal static CommercialStorefrontPolicy Create(CommercialStorefrontPolicyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        bool enforcementModeKnown = input.EnforcementMode == LicenseEnforcementMode.OfflineSigned;
        Uri? checkoutUri = enforcementModeKnown
            && input.CheckoutApproved
            && input.BuyerTermsApproved
            ? TryCreateCheckoutUri(input.CheckoutUrl)
            : null;
        Uri? accountUri = TryCreateAccountUri(input.AccountUrl, input.EnforcementMode);

        string expiredDetail = checkoutUri is null
            ? "Your trial has ended. Review purchase options on the Stillmark website, or sign in to activate a purchase."
            : "Your trial has ended. Review purchase options below, or sign in to activate a purchase.";

        return new CommercialStorefrontPolicy(
            checkoutUri,
            accountUri,
            EnsureBounded("This installation is activated."),
            EnsureBounded("Your 14-day trial includes full access. You can review license options on the Stillmark website at any time."),
            EnsureBounded(expiredDetail),
            EnsureBounded("Sign in to the account used for your purchase to activate this computer."));
    }

    private static Uri? TryCreateCheckoutUri(string? value)
    {
        return CommercialReleaseConfigurationLoader.IsApprovedCheckoutUrl(value)
            && Uri.TryCreate(value, UriKind.Absolute, out Uri? candidate)
                ? candidate
                : null;
    }

    private static Uri? TryCreateAccountUri(string? value, LicenseEnforcementMode mode)
    {
        if (mode == LicenseEnforcementMode.OfflineSigned
            && value?.Equals(StillmarkAccountUrl, StringComparison.Ordinal) == true
            && TryCreateHttpsUri(value, out Uri? candidate))
        {
            return candidate;
        }
        return null;
    }

    private static bool TryCreateHttpsUri(
        string? value,
        [NotNullWhen(true)] out Uri? candidate)
    {
        candidate = null;
        if (string.IsNullOrEmpty(value)
            || value.Length > MaximumUrlLength
            || !value.Equals(value.Trim(), StringComparison.Ordinal)
            || value.IndexOfAny(['\r', '\n', '\0']) >= 0
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            || !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || parsed.IsLoopback
            || !parsed.IsDefaultPort
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        candidate = parsed;
        return true;
    }

    private static string EnsureBounded(string claim)
    {
        if (claim.Length > MaximumClaimLength)
            throw new InvalidOperationException("A buyer-facing storefront claim exceeds its bound.");

        return claim;
    }
}
