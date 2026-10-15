using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace WinShot.App.Commercial;

internal enum CommercialLicenseMode
{
    OfflineSigned,
}

internal sealed record CommercialStorefrontSettings(
    bool CheckoutApproved,
    bool BuyerTermsApproved,
    long? PrimaryVariantId,
    string CheckoutUrl,
    string AccountUrl);

internal sealed record CommercialReleaseConfiguration(
    int ContractRevision,
    int ApplicationMajor,
    CommercialLicenseMode LicenseMode,
    JsonElement Provider,
    CommercialStorefrontSettings Storefront);

internal sealed record CommercialConfigurationLoadResult(
    CommercialReleaseConfiguration? Configuration,
    string ErrorCode)
{
    internal bool IsValid => Configuration is not null;
}

/// <summary>
/// Loads one credential-free, immutable commercial contract embedded into the signed application.
/// Invalid or partial documents produce only a fixed error code; values are never reflected into
/// diagnostics. The installed sidecar is release evidence only and is not trusted at runtime.
/// </summary>
internal static class CommercialReleaseConfigurationLoader
{
    internal const int MaximumConfigurationBytes = 32 * 1024;
    internal const string ResourceName = "WinShot.CommercialConfiguration.json";
    internal const string StillmarkAccountUrl = "https://getstillmark.com/manage-license";
    internal const string StillmarkPricingUrl = "https://getstillmark.com/pricing";

    private static readonly Lazy<CommercialConfigurationLoadResult> Embedded =
        new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static CommercialConfigurationLoadResult Current => Embedded.Value;

    internal static CommercialConfigurationLoadResult Parse(
        string? json,
        int expectedApplicationMajor)
    {
        if (string.IsNullOrEmpty(json)
            || Encoding.UTF8.GetByteCount(json) > MaximumConfigurationBytes
            || expectedApplicationMajor <= 0)
        {
            return Invalid("invalid-size");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            JsonElement root = document.RootElement;
            if (!TryExactObject(
                    root,
                    [
                        "schemaVersion",
                        "contractRevision",
                        "applicationMajor",
                        "licenseMode",
                        "provider",
                        "storefront",
                    ],
                    out Dictionary<string, JsonElement>? rootValues)
                || !TryInt32(rootValues["schemaVersion"], out int schemaVersion)
                || schemaVersion != 1
                || !TryInt32(rootValues["contractRevision"], out int contractRevision)
                || contractRevision <= 0
                || !TryInt32(rootValues["applicationMajor"], out int applicationMajor)
                || applicationMajor != expectedApplicationMajor
                || !TryString(rootValues["licenseMode"], out string? modeText)
                || !TryStorefront(
                    rootValues["storefront"],
                    out CommercialStorefrontSettings? storefront))
            {
                return Invalid("invalid-root");
            }

            if (modeText.Equals("offline-signed", StringComparison.Ordinal))
            {
                if (rootValues["provider"].ValueKind != JsonValueKind.Null
                    || !TryValidateStripeStorefront(storefront))
                {
                    return Invalid("invalid-offline-contract");
                }

                return Valid(new CommercialReleaseConfiguration(
                    contractRevision,
                    applicationMajor,
                    CommercialLicenseMode.OfflineSigned,
                    rootValues["provider"].Clone(),
                    storefront));
            }
            return Invalid("unsupported-license-mode");
        }
        catch (Exception ex) when (ex is JsonException
            or InvalidOperationException
            or FormatException
            or OverflowException)
        {
            return Invalid("invalid-json");
        }
    }

    private static CommercialConfigurationLoadResult LoadEmbedded()
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(ResourceName);
            if (stream is null
                || (stream.CanSeek && stream.Length > MaximumConfigurationBytes))
            {
                return Invalid("missing-resource");
            }

            using var content = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                if (content.Length + read > MaximumConfigurationBytes)
                    return Invalid("oversized-resource");
                content.Write(buffer, 0, read);
            }

            string json = Encoding.UTF8.GetString(
                content.GetBuffer(),
                0,
                checked((int)content.Length));
            return Parse(json, AppInfo.Major);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return Invalid("resource-read-failed");
        }
    }

    private static bool TryStorefront(
        JsonElement element,
        [NotNullWhen(true)] out CommercialStorefrontSettings? storefront)
    {
        storefront = null;
        if (!TryExactObject(
                element,
                [
                    "checkoutApproved",
                    "buyerTermsApproved",
                    "primaryVariantId",
                    "checkoutUrl",
                    "accountUrl",
                ],
                out Dictionary<string, JsonElement>? values)
            || !TryBoolean(values["checkoutApproved"], out bool checkoutApproved)
            || !TryBoolean(values["buyerTermsApproved"], out bool buyerTermsApproved)
            || !TryNullablePositiveInt64(
                values["primaryVariantId"],
                out long? primaryVariantId)
            || !TryString(values["checkoutUrl"], out string? checkoutUrl)
            || checkoutUrl.Length > CommercialStorefrontPolicy.MaximumUrlLength
            || !TryString(values["accountUrl"], out string? accountUrl)
            || !accountUrl.Equals(StillmarkAccountUrl, StringComparison.Ordinal))
        {
            return false;
        }

        storefront = new CommercialStorefrontSettings(
            checkoutApproved,
            buyerTermsApproved,
            primaryVariantId,
            checkoutUrl,
            accountUrl);
        return true;
    }

    private static bool TryValidateStripeStorefront(CommercialStorefrontSettings storefront)
    {
        if (storefront.CheckoutApproved != storefront.BuyerTermsApproved)
            return false;
        if (!storefront.CheckoutApproved)
        {
            return storefront.PrimaryVariantId is null
                && storefront.CheckoutUrl.Length == 0;
        }

        return storefront.PrimaryVariantId is null
            && storefront.CheckoutUrl.Equals(StillmarkPricingUrl, StringComparison.Ordinal);
    }

    internal static bool IsApprovedCheckoutUrl(string? value)
    {
        return IsApprovedStripeCheckoutUrl(value);
    }

    internal static bool IsApprovedStripeCheckoutUrl(string? value) =>
        value?.Equals(StillmarkPricingUrl, StringComparison.Ordinal) == true;

    private static bool TryExactObject(
        JsonElement element,
        IReadOnlyCollection<string> expectedNames,
        [NotNullWhen(true)] out Dictionary<string, JsonElement>? values)
    {
        values = null;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var expected = new HashSet<string>(expectedNames, StringComparer.Ordinal);
        var found = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name)
                || !found.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }
        if (found.Count != expected.Count)
            return false;

        values = found;
        return true;
    }

    private static bool TryBoolean(JsonElement element, out bool value)
    {
        value = false;
        if (element.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }
        return element.ValueKind == JsonValueKind.False;
    }

    private static bool TryInt32(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    private static bool TryInt64(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    private static bool TryNullablePositiveInt64(JsonElement element, out long? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Null)
            return true;
        if (!TryInt64(element, out long parsed) || parsed <= 0)
            return false;
        value = parsed;
        return true;
    }

    private static bool TryString(
        JsonElement element,
        [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString();
        return value is not null;
    }

    private static CommercialConfigurationLoadResult Valid(
        CommercialReleaseConfiguration configuration) =>
        new(configuration, "");

    private static CommercialConfigurationLoadResult Invalid(string errorCode) =>
        new(null, errorCode);
}
