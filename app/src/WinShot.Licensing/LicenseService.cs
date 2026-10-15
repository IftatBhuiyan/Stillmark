namespace WinShot.Licensing;

public enum ActivationOutcome
{
    Success,
    InvalidKey,
    WrongProduct,
    UpgradeRequired,
    ProviderRequired,
    ProviderRejected,
    ServiceUnavailable,
    ConfigurationError,
    DifferentComputer,
    ActivationCheckRequired,
}

public sealed record ActivationResult(ActivationOutcome Outcome, string Message, LicenseStatus? Status)
{
    public bool Succeeded => Outcome == ActivationOutcome.Success;
}

/// <summary>
/// The single entry point the app uses for licensing. Combines the trial clock, on-disk activation,
/// and offline key verification into one <see cref="LicenseStatus"/>, and handles activation.
/// Cheap to construct and query. Entitlement is re-evaluated for every status read so a process
/// left open across the end of its trial cannot keep using a stale startup snapshot.
/// </summary>
public sealed class LicenseService
{
    private const string ProductCode = "winshot";
    private const int MaximumLicenseKeyCharacters = 8 * 1024;

    private readonly TrialTracker _trial;
    private readonly LicenseStorage _storage;
    private readonly int _currentMajor;
    private readonly DateTimeOffset? _currentReleaseDateUtc;
    private readonly Func<string?, LicensePayload?> _verifyKey;
    private readonly LicenseEnforcementOptions _enforcement;
    private readonly Func<string?> _deviceId;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _providerGate = new(1, 1);

    public LicenseStatus Status => Evaluate();
    public LicenseEnforcementMode EnforcementMode => _enforcement.Mode;
    public event Action? Changed;

    public LicenseService(
        TrialTracker? trial = null,
        LicenseStorage? storage = null,
        int currentMajor = 1,
        LicenseEnforcementOptions? enforcement = null,
        DateTimeOffset? currentReleaseDateUtc = null,
        Func<string?>? deviceId = null,
        Func<DateTimeOffset>? now = null)
        : this(
            trial ?? new TrialTracker(),
            storage ?? new LicenseStorage(),
            currentMajor,
            VerifyEmbeddedKey,
            enforcement,
            currentReleaseDateUtc, deviceId, now)
    {
    }

    internal LicenseService(
        TrialTracker trial,
        LicenseStorage storage,
        int currentMajor,
        Func<string?, LicensePayload?> keyVerifier,
        LicenseEnforcementOptions? enforcement = null,
        DateTimeOffset? currentReleaseDateUtc = null,
        Func<string?>? deviceId = null,
        Func<DateTimeOffset>? now = null)
    {
        if (currentMajor <= 0)
            throw new ArgumentOutOfRangeException(nameof(currentMajor));

        ArgumentNullException.ThrowIfNull(trial);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(keyVerifier);

        _trial = trial;
        _storage = storage;
        _currentMajor = currentMajor;
        _currentReleaseDateUtc = currentReleaseDateUtc;
        _verifyKey = keyVerifier;
        _enforcement = enforcement ?? LicenseEnforcementOptions.OfflineSigned;
        _deviceId = deviceId ?? (() => LicenseDeviceIdentity.Current);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _ = Evaluate(); // Service creation is the start of a fresh installation's trial window.
    }

    public void Refresh() => Changed?.Invoke();

    private LicenseStatus Evaluate()
    {
        LicensePayload? uncoveredLicense = null;
        LicenseAccessIssue accessIssue = LicenseAccessIssue.None;
        LicenseStorage.Snapshot? stored = _storage.LoadSnapshot();
        if (_enforcement.Mode == LicenseEnforcementMode.OfflineSigned)
        {
            LicensePayload? payload = stored is null ? null : _verifyKey(stored.Key);
            if (payload is not null && IsUsableForCurrentVersion(payload))
                return new LicenseStatus(LicenseState.Licensed, 0, payload);
            if (payload is not null && LicenseEntitlement.IsValid(payload)
                && string.Equals(payload.Product, ProductCode, StringComparison.OrdinalIgnoreCase))
            {
                uncoveredLicense = payload;
                accessIssue = !LicenseEntitlement.MatchesDevice(payload, _deviceId())
                    ? LicenseAccessIssue.DifferentComputer
                    : !LicenseEntitlement.HasCurrentActivation(payload, _now())
                    ? LicenseAccessIssue.ActivationCheckRequired
                    : payload.UpdatesUntil > 0 && _currentReleaseDateUtc is null
                    ? LicenseAccessIssue.ReleaseDateMissing
                    : LicenseAccessIssue.ReleaseNotCovered;
            }
        }
        else if (stored is not null
            && TryGetStoredProviderEntitlement(stored, out ProviderLicenseEntitlement? entitlement))
        {
            return new LicenseStatus(
                LicenseState.Licensed,
                0,
                License: null,
                ProviderEntitlement: entitlement);
        }

        // Not licensed — fall back to the trial clock. A previously-run installation keeps its
        // original trial window; a fresh machine starts a new one.
        TimeSpan trialRemaining = _trial.TimeRemaining();
        int daysLeft = trialRemaining <= TimeSpan.Zero
            ? 0
            : (int)Math.Ceiling(trialRemaining.TotalDays);
        return daysLeft > 0
            ? new LicenseStatus(
                LicenseState.Trial,
                daysLeft,
                uncoveredLicense,
                AccessIssue: accessIssue,
                TrialRemaining: trialRemaining)
            : new LicenseStatus(LicenseState.Expired, 0, uncoveredLicense, AccessIssue: accessIssue);
    }

    /// <summary>Validates and stores a pasted license key, then refreshes status.</summary>
    public ActivationResult Activate(string? keyText)
    {
        if (_enforcement.Mode == LicenseEnforcementMode.ConfigurationInvalid)
        {
            return new ActivationResult(
                ActivationOutcome.ConfigurationError,
                "Licensing is unavailable because this installation has an invalid commercial configuration.",
                null);
        }
        if (_enforcement.Mode == LicenseEnforcementMode.ProviderValidated)
        {
            return NormalizeProviderKey(keyText) is null
                ? new ActivationResult(
                    ActivationOutcome.InvalidKey,
                    "Enter the license key from your purchase receipt.",
                    null)
                : new ActivationResult(
                    ActivationOutcome.ProviderRequired,
                    "An internet connection is required to activate this license on this device.",
                    null);
        }

        var local = ValidateLocalActivation(keyText);
        if (local.Error is not null)
            return local.Error;

        _storage.Save(local.NormalizedKey!);
        Refresh();
        return new ActivationResult(ActivationOutcome.Success, "Stillmark is now activated. Thank you!", Status);
    }

    /// <summary>
    /// Activates according to the configured contract. Offline mode verifies WinShot's ECDSA token.
    /// Provider mode accepts the provider-generated key but grants only the caller-supplied
    /// immutable entitlement mapped to an exactly matching store, product, variant, and instance.
    /// </summary>
    public async Task<ActivationResult> ActivateAsync(
        string? keyText,
        CancellationToken ct = default)
    {
        if (_enforcement.Mode == LicenseEnforcementMode.ConfigurationInvalid)
            return Activate(keyText);
        if (_enforcement.Mode == LicenseEnforcementMode.OfflineSigned)
            return Activate(keyText);

        string? providerKey = NormalizeProviderKey(keyText);
        if (providerKey is null)
        {
            return new ActivationResult(
                ActivationOutcome.InvalidKey,
                "Enter the license key from your purchase receipt.",
                null);
        }

        await _providerGate.WaitAsync(ct);
        try
        {
            LicenseStorage.Snapshot? existing = _storage.LoadSnapshot();
            if (existing is not null
                && string.Equals(existing.Key, providerKey, StringComparison.Ordinal)
                && TryGetStoredProviderEntitlement(existing, out _))
            {
                return new ActivationResult(
                    ActivationOutcome.Success,
                    "Stillmark is already activated on this device.",
                    Status);
            }
            if (existing is not null
                && string.Equals(existing.Key, providerKey, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(existing.ProviderInstanceId))
            {
                LicenseValidation existingValidation = await _enforcement.Validator!.ValidateAsync(
                    providerKey,
                    existing.ProviderInstanceId,
                    ct);
                if (existingValidation.Result == LicenseCheck.Unreachable)
                {
                    return new ActivationResult(
                        ActivationOutcome.ServiceUnavailable,
                        "The license service could not be reached. Check your connection and try again.",
                        null);
                }
                if (existingValidation.Result == LicenseCheck.Valid)
                {
                    if (!string.Equals(
                            existingValidation.InstanceId,
                            existing.ProviderInstanceId,
                            StringComparison.Ordinal)
                        || !TryMapProviderEntitlement(
                            existingValidation,
                            out LicenseVariantEntitlement? existingMapping))
                    {
                        return new ActivationResult(
                            ActivationOutcome.ProviderRejected,
                            "The license service did not confirm this license for Stillmark.",
                            null);
                    }
                    if (existingMapping.EntitledMajor < _currentMajor)
                    {
                        return new ActivationResult(
                            ActivationOutcome.UpgradeRequired,
                            $"This purchase covers v{existingMapping.EntitledMajor}.x. This build requires a v{_currentMajor}.x license.",
                            null);
                    }

                    _storage.SaveProviderActivation(
                        providerKey,
                        existing.ProviderInstanceId,
                        existingValidation.StoreId,
                        existingValidation.ProductId,
                        existingValidation.VariantId,
                        _enforcement.Now());
                    Refresh();
                    return new ActivationResult(
                        ActivationOutcome.Success,
                        "Stillmark is now activated. Thank you!",
                        Status);
                }
            }

            LicenseValidation validation = await _enforcement.Validator!.ActivateAsync(
                providerKey,
                _enforcement.InstanceName!,
                ct);
            if (validation.Result == LicenseCheck.Unreachable)
            {
                return new ActivationResult(
                    ActivationOutcome.ServiceUnavailable,
                    "The license service could not be reached. Check your connection and try again.",
                    null);
            }
            if (validation.Result != LicenseCheck.Valid
                || string.IsNullOrWhiteSpace(validation.InstanceId)
                || !TryMapProviderEntitlement(validation, out LicenseVariantEntitlement? mapped))
            {
                return new ActivationResult(
                    ActivationOutcome.ProviderRejected,
                    "The license service did not confirm this license for Stillmark.",
                    null);
            }
            if (mapped.EntitledMajor < _currentMajor)
            {
                return new ActivationResult(
                    ActivationOutcome.UpgradeRequired,
                    $"This purchase covers v{mapped.EntitledMajor}.x. This build requires a v{_currentMajor}.x license.",
                    null);
            }

            _storage.SaveProviderActivation(
                providerKey,
                validation.InstanceId,
                validation.StoreId,
                validation.ProductId,
                validation.VariantId,
                _enforcement.Now());
            Refresh();
            return new ActivationResult(
                ActivationOutcome.Success,
                "Stillmark is now activated. Thank you!",
                Status);
        }
        finally
        {
            _providerGate.Release();
        }
    }

    /// <summary>Removes the stored license (e.g. moving to another machine) and reverts to trial/expired.</summary>
    public void Deactivate()
    {
        if (_enforcement.Mode == LicenseEnforcementMode.ProviderValidated)
            throw new InvalidOperationException(
                "Provider-managed licenses must use DeactivateAsync so the activation seat is released.");
        _storage.Clear();
        Refresh();
    }

    public async Task<DeactivationResult> DeactivateAsync(CancellationToken ct = default)
    {
        if (_enforcement.Mode != LicenseEnforcementMode.ProviderValidated)
        {
            Deactivate();
            return new DeactivationResult(DeactivationOutcome.Success, "The license was removed from this device.");
        }

        await _providerGate.WaitAsync(ct);
        try
        {
            LicenseStorage.Snapshot? stored = _storage.LoadSnapshot();
            if (stored is null || string.IsNullOrWhiteSpace(stored.ProviderInstanceId))
            {
                _storage.Clear();
                Refresh();
                return new DeactivationResult(DeactivationOutcome.Success, "The local license was removed.");
            }

            LicenseDeactivation result = await _enforcement.Validator!.DeactivateAsync(
                stored.Key,
                stored.ProviderInstanceId,
                ct);
            if (result.Result == LicenseDeactivationCheck.Unreachable)
            {
                return new DeactivationResult(
                    DeactivationOutcome.Unreachable,
                    "The license service could not be reached. This device remains activated.");
            }
            if (result.Result != LicenseDeactivationCheck.Deactivated)
            {
                return new DeactivationResult(
                    DeactivationOutcome.Rejected,
                    "The license service did not release this device.");
            }
            bool exactStoredIdentity =
                stored.ProviderStoreId is { } storedStoreId
                && stored.ProviderProductId is { } storedProductId
                && stored.ProviderVariantId is { } storedVariantId
                && result.StoreId == storedStoreId
                && result.ProductId == storedProductId
                && result.VariantId == storedVariantId;
            bool legacyCurrentIdentity =
                stored.ProviderStoreId is null
                && stored.ProviderProductId is null
                && stored.ProviderVariantId is { } legacyVariantId
                && result.VariantId == legacyVariantId
                && _enforcement.ProductIdentity!.Matches(
                    result.StoreId,
                    result.ProductId,
                    result.VariantId);
            if (!exactStoredIdentity && !legacyCurrentIdentity)
            {
                return new DeactivationResult(
                    DeactivationOutcome.Rejected,
                    "The license service did not confirm this product while releasing the device.");
            }

            _storage.Clear();
            Refresh();
            return new DeactivationResult(
                DeactivationOutcome.Success,
                "The license was removed and this device activation was released.");
        }
        finally
        {
            _providerGate.Release();
        }
    }

    /// <summary>
    /// Whether a release is covered. Dated licenses require the candidate's authenticated release
    /// date; legacy keys retain their major-version terms. Trial users may update while evaluating.
    /// </summary>
    public bool IsUpdateEntitled(int targetMajor, DateTimeOffset? releasedAtUtc = null)
    {
        if (targetMajor <= 0)
            return false;
        LicenseStatus status = Status;
        return status.State switch
        {
            LicenseState.Licensed when status.License is { } payload =>
                LicenseEntitlement.CoversRelease(payload, targetMajor, releasedAtUtc),
            LicenseState.Licensed => targetMajor <= status.EntitledMajor,
            LicenseState.Trial => true,
            _ => false,
        };
    }

    /// <summary>
    /// Revalidates an already activated provider instance. This is intended to run in the
    /// background after the tray is ready; callers should not block application startup on it.
    /// Explicit rejection removes the cached confirmation immediately. Network uncertainty leaves
    /// the last confirmation untouched, so the configured grace policy makes the entitlement
    /// decision.
    /// </summary>
    public async Task<ProviderRefreshResult> RefreshProviderEntitlementAsync(
        CancellationToken ct = default)
    {
        if (_enforcement.Mode != LicenseEnforcementMode.ProviderValidated)
        {
            return new ProviderRefreshResult(
                ProviderRefreshOutcome.NotConfigured,
                _enforcement.Mode == LicenseEnforcementMode.OfflineSigned
                    ? "Offline signed-license enforcement is active."
                    : "Provider licensing is unavailable in this installation.");
        }

        await _providerGate.WaitAsync(ct);
        try
        {
            LicenseStorage.Snapshot? stored = _storage.LoadSnapshot();
            if (stored is null)
            {
                return new ProviderRefreshResult(
                    ProviderRefreshOutcome.NoStoredLicense,
                    "No stored license is available to validate.");
            }
            if (string.IsNullOrWhiteSpace(stored.ProviderInstanceId))
            {
                return new ProviderRefreshResult(
                    ProviderRefreshOutcome.ActivationRequired,
                    "This license has not been activated with the configured provider.");
            }
            long? storedVariantId = stored.ProviderVariantId;
            if (storedVariantId is { } cachedVariantId
                && (!_enforcement.ProductIdentity!.TryGetEntitlement(
                        cachedVariantId,
                        out LicenseVariantEntitlement? storedEntitlement)
                    || storedEntitlement.EntitledMajor < _currentMajor))
            {
                return new ProviderRefreshResult(
                    ProviderRefreshOutcome.ActivationRequired,
                    "The stored provider variant is not entitled to this version.");
            }

            LicenseValidation validation = await _enforcement.Validator!.ValidateAsync(
                stored.Key,
                stored.ProviderInstanceId,
                ct);
            if (validation.Result == LicenseCheck.Unreachable)
            {
                Refresh();
                return new ProviderRefreshResult(
                    ProviderRefreshOutcome.Unreachable,
                    "The license service could not be reached.");
            }
            if (validation.Result != LicenseCheck.Valid
                || !string.Equals(
                    validation.InstanceId,
                    stored.ProviderInstanceId,
                    StringComparison.Ordinal)
                || (storedVariantId is { } expectedVariantId
                    && validation.VariantId != expectedVariantId)
                || !TryMapProviderEntitlement(
                    validation,
                    out LicenseVariantEntitlement? refreshedEntitlement)
                || refreshedEntitlement.EntitledMajor < _currentMajor)
            {
                _storage.ClearProviderConfirmation(stored);
                Refresh();
                return new ProviderRefreshResult(
                    ProviderRefreshOutcome.Rejected,
                    "The license service did not confirm this device activation.");
            }

            if (storedVariantId is null)
            {
                _storage.SaveProviderActivation(
                    stored.Key,
                    stored.ProviderInstanceId,
                    validation.StoreId,
                    validation.ProductId,
                    validation.VariantId,
                    _enforcement.Now());
            }
            else
            {
                _storage.UpdateProviderConfirmation(stored, _enforcement.Now());
            }
            Refresh();
            return new ProviderRefreshResult(
                ProviderRefreshOutcome.Confirmed,
                "The device activation was confirmed.");
        }
        finally
        {
            _providerGate.Release();
        }
    }

    private bool TryGetStoredProviderEntitlement(
        LicenseStorage.Snapshot stored,
        out ProviderLicenseEntitlement? entitlement)
    {
        entitlement = null;
        if (string.IsNullOrWhiteSpace(stored.ProviderInstanceId)
            || stored.ProviderStoreId is not { } storeId
            || stored.ProviderProductId is not { } productId
            || stored.ProviderVariantId is not { } variantId
            || stored.ProviderConfirmedAt is not { } confirmedAt)
            return false;
        if (!_enforcement.ProductIdentity!.Matches(storeId, productId, variantId)
            || !_enforcement.ProductIdentity.TryGetEntitlement(
                variantId,
                out LicenseVariantEntitlement? mapped)
            || mapped.EntitledMajor < _currentMajor)
            return false;

        DateTimeOffset now = _enforcement.Now();
        if (confirmedAt > now.AddMinutes(5))
            return false;
        TimeSpan elapsed = now > confirmedAt ? now - confirmedAt : TimeSpan.Zero;
        // Provider timestamps are persisted as Unix seconds, so preserve one second of storage
        // resolution even when a strict zero-length offline grace is selected.
        if (elapsed > _enforcement.OfflineGracePeriod.Add(TimeSpan.FromSeconds(1)))
            return false;

        entitlement = new ProviderLicenseEntitlement(
            variantId,
            mapped.EntitledMajor,
            mapped.Edition);
        return true;
    }

    private bool TryMapProviderEntitlement(
        LicenseValidation validation,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out LicenseVariantEntitlement? entitlement)
    {
        entitlement = null;
        return _enforcement.ProductIdentity!.Matches(
                validation.StoreId,
                validation.ProductId,
                validation.VariantId)
            && _enforcement.ProductIdentity.TryGetEntitlement(
                validation.VariantId,
                out entitlement);
    }

    private (string? NormalizedKey, ActivationResult? Error) ValidateLocalActivation(string? keyText)
    {
        string? normalizedKey = NormalizeLicenseKey(keyText);
        LicensePayload? payload = _verifyKey(normalizedKey);
        if (payload is null
            || !LicenseEntitlement.IsValid(payload))
        {
            return (null, new ActivationResult(
                ActivationOutcome.InvalidKey,
                "That license key isn't valid. Check for a copy/paste error.",
                null));
        }

        if (!string.Equals(payload.Product, ProductCode, StringComparison.OrdinalIgnoreCase))
        {
            return (null, new ActivationResult(
                ActivationOutcome.WrongProduct,
                "That key is for a different product.",
                null));
        }

        if (!LicenseEntitlement.MatchesDevice(payload, _deviceId()))
        {
            return (null, new ActivationResult(ActivationOutcome.DifferentComputer,
                "This activation belongs to another computer. Sign in to activate this computer or transfer your license.", null));
        }
        if (!LicenseEntitlement.HasCurrentActivation(payload, _now()))
        {
            return (null, new ActivationResult(ActivationOutcome.ActivationCheckRequired,
                "Connect to the internet and sign in to refresh this computer's activation. Your included releases are still yours.", null));
        }
        if (payload.UpdatesUntil > 0 && _currentReleaseDateUtc is null)
        {
            return (null, new ActivationResult(
                ActivationOutcome.ConfigurationError,
                "This development build has no published release date. Install a released build to activate this license.",
                null));
        }

        if (!LicenseEntitlement.CoversRelease(payload, _currentMajor, _currentReleaseDateUtc))
        {
            return (null, new ActivationResult(
                ActivationOutcome.UpgradeRequired,
                payload.UpdatesUntil > 0
                    ? "This version was released after your included updates ended. Your license still works with earlier eligible versions."
                    : $"That license covers Stillmark v{payload.Major}.x. This build requires a v{_currentMajor}.x license.",
                null));
        }

        return (normalizedKey, null);
    }

    private static string? NormalizeLicenseKey(string? key)
    {
        if (key is null
            || key.Length == 0
            || key.Length > MaximumLicenseKeyCharacters * 2
            || string.IsNullOrWhiteSpace(key))
            return null;
        string normalized = new(
            key.Where(character => !char.IsWhiteSpace(character))
                .Take(MaximumLicenseKeyCharacters + 1)
                .ToArray());
        return normalized.Length <= MaximumLicenseKeyCharacters ? normalized : null;
    }

    private static string? NormalizeProviderKey(string? key)
    {
        if (key is null
            || key.Length == 0
            || key.Length > MaximumLicenseKeyCharacters
            || string.IsNullOrWhiteSpace(key))
            return null;
        string normalized = key.Trim();
        return normalized;
    }

    private bool IsUsableForCurrentVersion(LicensePayload payload) =>
        LicenseEntitlement.CoversRelease(payload, _currentMajor, _currentReleaseDateUtc)
        && LicenseEntitlement.MatchesDevice(payload, _deviceId())
        && LicenseEntitlement.HasCurrentActivation(payload, _now())
        && string.Equals(payload.Product, ProductCode, StringComparison.OrdinalIgnoreCase);

    private static LicensePayload? VerifyEmbeddedKey(string? key)
    {
        return LicenseKey.TryVerify(key, out var payload) ? payload : null;
    }
}
