using System.Text.Json.Serialization;

namespace WinShot.Licensing;

/// <summary>
/// The signed contents of a WinShot license key. Kept intentionally small so keys stay short.
/// Field names are terse because they are serialized into the (base64-encoded) key string.
/// </summary>
public sealed record LicensePayload
{
    /// <summary>License format version (not the app version). Lets us evolve the schema.</summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    /// <summary>Unique license id / order reference (used for revocation lists later).</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    /// <summary>Product code, guards against a key minted for a different product.</summary>
    [JsonPropertyName("p")]
    public string Product { get; init; } = "winshot";

    /// <summary>Edition, e.g. "pro". Reserved for future tiers.</summary>
    [JsonPropertyName("ed")]
    public string Edition { get; init; } = "pro";

    /// <summary>Buyer name (optional; empty for pre-minted pool keys).</summary>
    [JsonPropertyName("n")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Name { get; init; } = "";

    /// <summary>Buyer email (optional; empty for pre-minted pool keys).</summary>
    [JsonPropertyName("e")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Email { get; init; } = "";

    /// <summary>Legacy entitled major, or purchase version for a dated license.</summary>
    [JsonPropertyName("maj")]
    public int Major { get; init; } = 1;

    /// <summary>Issued-at, or confirmed purchase time for format 2 (Unix seconds).</summary>
    [JsonPropertyName("iat")]
    public long IssuedAt { get; init; }

    /// <summary>Exclusive update cutoff (Unix seconds). 0 is allowed only for legacy format 1 keys.</summary>
    [JsonPropertyName("upto")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long UpdatesUntil { get; init; }

    /// <summary>Format 3 binding: a product-specific hash, never a raw hardware ID.</summary>
    [JsonPropertyName("dev")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceId { get; init; }

    [JsonPropertyName("aid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActivationId { get; init; }

    /// <summary>Last server seat-check time, separate from the original purchase time.</summary>
    [JsonPropertyName("lat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long LeaseIssuedAt { get; init; }

    /// <summary>Offline activation deadline; does not change which releases the buyer owns.</summary>
    [JsonPropertyName("exp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ValidUntil { get; init; }
}
