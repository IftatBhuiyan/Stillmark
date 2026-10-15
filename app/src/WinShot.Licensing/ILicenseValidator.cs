namespace WinShot.Licensing;

/// <summary>Outcome of a remote license check.</summary>
public enum LicenseCheck
{
    /// <summary>The key is genuine and currently entitled.</summary>
    Valid,

    /// <summary>The server answered and the key is not valid (unknown, expired, disabled, seat limit).</summary>
    Invalid,

    /// <summary>Could not reach the server (offline / timeout). Caller should fall back to the grace window.</summary>
    Unreachable,
}

/// <summary>Normalized result of an activate/validate call, provider-agnostic.</summary>
public sealed record LicenseValidation(
    LicenseCheck Result,
    string? Status,            // provider status, when a remote validator is configured
    string? InstanceId,        // the activated instance id (needed for later validate/deactivate)
    long StoreId,
    long ProductId,
    long VariantId,
    string? CustomerEmail,
    DateTimeOffset? ExpiresAt, // null = perpetual (one-time purchase)
    string? Error)
{
    public static LicenseValidation Unreachable(string? error = null) =>
        new(LicenseCheck.Unreachable, null, null, 0, 0, 0, null, null, error);

    public static LicenseValidation Bad(string? error) =>
        new(LicenseCheck.Invalid, null, null, 0, 0, 0, null, null, error);
}

/// <summary>Normalized result of releasing one provider-managed activation.</summary>
public enum LicenseDeactivationCheck
{
    Deactivated,
    Rejected,
    Unreachable,
}

public sealed record LicenseDeactivation(
    LicenseDeactivationCheck Result,
    long StoreId,
    long ProductId,
    long VariantId,
    string? Error)
{
    public static LicenseDeactivation Unreachable(string? error = null) =>
        new(LicenseDeactivationCheck.Unreachable, 0, 0, 0, error);

    public static LicenseDeactivation Rejected(string? error = null) =>
        new(LicenseDeactivationCheck.Rejected, 0, 0, 0, error);
}

/// <summary>
/// Exact provider identity that may grant entitlement. Production callers supply an immutable
/// entitlement mapping for every accepted product variant; provider-supplied names never define it.
/// </summary>
public sealed record LicenseVariantEntitlement
{
    public LicenseVariantEntitlement(long variantId, int entitledMajor, string edition)
    {
        if (variantId <= 0)
            throw new ArgumentOutOfRangeException(nameof(variantId));
        if (entitledMajor <= 0)
            throw new ArgumentOutOfRangeException(nameof(entitledMajor));
        ArgumentException.ThrowIfNullOrWhiteSpace(edition);
        string normalizedEdition = edition.Trim();
        if (normalizedEdition.Length > 64)
            throw new ArgumentException("The edition name cannot exceed 64 characters.", nameof(edition));

        VariantId = variantId;
        EntitledMajor = entitledMajor;
        Edition = normalizedEdition;
    }

    public long VariantId { get; }
    public int EntitledMajor { get; }
    public string Edition { get; }
}

public sealed class LicenseProductIdentity
{
    private readonly Dictionary<long, LicenseVariantEntitlement> _variants;

    public LicenseProductIdentity(
        long storeId,
        long productId,
        IEnumerable<LicenseVariantEntitlement> variants)
    {
        ArgumentNullException.ThrowIfNull(variants);
        if (storeId <= 0)
            throw new ArgumentOutOfRangeException(nameof(storeId));
        if (productId <= 0)
            throw new ArgumentOutOfRangeException(nameof(productId));

        _variants = new Dictionary<long, LicenseVariantEntitlement>();
        foreach (LicenseVariantEntitlement variant in variants)
        {
            ArgumentNullException.ThrowIfNull(variant);
            if (!_variants.TryAdd(variant.VariantId, variant))
                throw new ArgumentException("Each provider variant must be mapped exactly once.", nameof(variants));
        }
        if (_variants.Count == 0)
            throw new ArgumentException(
                "At least one provider variant entitlement mapping is required.",
                nameof(variants));

        StoreId = storeId;
        ProductId = productId;
    }

    public long StoreId { get; }
    public long ProductId { get; }
    public IReadOnlyCollection<long> VariantIds => _variants.Keys.ToArray();

    public bool Matches(long storeId, long productId, long variantId) =>
        storeId == StoreId
        && productId == ProductId
        && _variants.ContainsKey(variantId);

    public bool TryGetEntitlement(
        long variantId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out LicenseVariantEntitlement? entitlement) =>
        _variants.TryGetValue(variantId, out entitlement);
}

/// <summary>
/// Abstracts the licensing backend so <see cref="LicenseService"/> can be unit-tested with a fake and
/// a remote licensing backend can be swapped without touching the gate/UI.
/// </summary>
public interface ILicenseValidator
{
    /// <summary>First-run activation: registers this machine as an instance of the license.</summary>
    Task<LicenseValidation> ActivateAsync(string licenseKey, string instanceName, CancellationToken ct = default);

    /// <summary>Re-check an already-activated license (pass the instance id to also verify the seat).</summary>
    Task<LicenseValidation> ValidateAsync(string licenseKey, string? instanceId, CancellationToken ct = default);

    /// <summary>Release a seat (moving to another machine). Local state should only be cleared when confirmed.</summary>
    Task<LicenseDeactivation> DeactivateAsync(
        string licenseKey,
        string instanceId,
        CancellationToken ct = default);
}
