namespace WinShot.App.Updates;

internal static class AppUpdateConfiguration
{
    // Set together in the first signed production build. Never accept a feed or trust key
    // from editable settings, environment variables, or the downloaded feed itself.
    internal const string FeedUrl = ""; // Open-source releases are downloaded from GitHub; never use the retired commercial feed.
    internal const string CatalogPublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAtsQliLO2Uo0ariLfgIJv
nxGP9f3UU9Gv8DkUCDbFhbkBIw73qi7uQ7/aUovTR7yqtiFL/9322QfQa8s/rQ5t
pep4knhPmn/D7xqmuBcsI24QrbDwNZfGPKCUXlXIry0RN70boGq27DRZUbr8NeSe
/YMUQKFW+Hm56GJud8GkCiuWCDK+0vuJHRMVprLm8i0hsIvw5jJCUzH2hUsgCRN5
AHWh7ExUvzloVOm9FAk9u3tkUvb2GQHItU/d3KtncR3SHNQ1ggTcdhwiLnLWiXKb
nhtZh4G9fwwTd09w2uVc+SSFe40PBQXL35e4uU3ps/tXD087/ko81SwGXj5+PzVq
peLWqzk84cfe24XMjWmjKox0F17sp+6uM+ZrO60lOu7YmPZQ7yvz9lN7fbO+620L
vhiq0JfVdD01sPZQ3JsOcM3m+C/UW35G4ZSBzWCRHPLRv6X7ePT5aJ00pAe1cU5Q
Pz1W1tmUzqFBvd8tHqvYQEwHI9AH1wXsTs0BqTwXJTh1AgMBAAE=
-----END PUBLIC KEY-----
""";
    internal const string CatalogPublicKeySha256 = "400BBC56E6AD99391CBC0252E1A90BD1D8401F93610FBE52C9B276BC24C0D20B";

    internal static bool IsConfigured => AppInfo.PublishedAtUtc is not null
        && AppInfo.ExpectedPublisherSubject != "UNSET"
        && AppInfo.ExpectedArtifactSigningIdentityEku != "UNSET"
        && Uri.TryCreate(FeedUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrWhiteSpace(CatalogPublicKeyPem)
        && CatalogPublicKeySha256.Length == 64;

    internal static VelopackUpdateBackend CreateBackend()
    {
        if (!IsConfigured) throw new InvalidOperationException("Production updates are not configured.");
        return new(new Uri(FeedUrl), CatalogPublicKeyPem, CatalogPublicKeySha256);
    }
}
