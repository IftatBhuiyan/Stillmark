using System.Net;
using System.Net.Http;
using WinShot.Licensing;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class LicensingProviderBoundaryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "winshot-provider-license-tests",
        Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    public LicensingProviderBoundaryTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception) { }
    }

    [Fact]
    public void Existing_offline_license_remains_offline_when_offline_mode_is_selected()
    {
        var provider = new FakeValidator();
        var storage = new LicenseStorage(_directory);
        storage.Save("synthetic-key");
        LicenseService service = CreateService(provider, storage, providerMode: false);

        Assert.Equal(LicenseState.Licensed, service.Status.State);
        Assert.Equal(0, provider.ValidateCalls);
    }

    [Fact]
    public void Synchronous_activation_cannot_bypass_provider_mode()
    {
        var provider = new FakeValidator();
        var storage = new LicenseStorage(_directory);
        LicenseService service = CreateService(provider, storage);

        ActivationResult result = service.Activate("synthetic-key");

        Assert.Equal(ActivationOutcome.ProviderRequired, result.Outcome);
        Assert.Null(storage.Load());
        Assert.Equal(0, provider.ActivateCalls);
    }

    [Fact]
    public async Task Invalid_commercial_configuration_denies_paid_activation_without_network_use()
    {
        var provider = new FakeValidator();
        var storage = new LicenseStorage(_directory);
        var service = new LicenseService(
            new TrialTracker(_directory, () => _now, useRegistry: false),
            storage,
            currentMajor: 1,
            _ => ValidPayload(),
            LicenseEnforcementOptions.InvalidConfiguration);

        ActivationResult synchronous = service.Activate("synthetic-key");
        ActivationResult asynchronous = await service.ActivateAsync("synthetic-key");

        Assert.Equal(ActivationOutcome.ConfigurationError, synchronous.Outcome);
        Assert.Equal(ActivationOutcome.ConfigurationError, asynchronous.Outcome);
        Assert.NotEqual(LicenseState.Licensed, service.Status.State);
        Assert.Null(storage.Load());
        Assert.Equal(0, provider.ActivateCalls);
        Assert.Equal(0, provider.ValidateCalls);
    }

    [Theory]
    [InlineData(LicenseCheck.Invalid, ActivationOutcome.ProviderRejected)]
    [InlineData(LicenseCheck.Unreachable, ActivationOutcome.ServiceUnavailable)]
    public async Task Provider_uncertainty_or_rejection_never_persists_entitlement(
        LicenseCheck providerResult,
        ActivationOutcome expectedOutcome)
    {
        var provider = new FakeValidator
        {
            ActivateResult = providerResult == LicenseCheck.Invalid
                ? LicenseValidation.Bad("synthetic provider detail")
                : LicenseValidation.Unreachable("synthetic network detail"),
        };
        var storage = new LicenseStorage(_directory);
        LicenseService service = CreateService(provider, storage);

        ActivationResult result = await service.ActivateAsync("synthetic-key");

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Null(storage.Load());
        Assert.DoesNotContain("synthetic", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Confirmed_provider_activation_persists_instance_and_grants_within_grace()
    {
        var provider = new FakeValidator();
        var storage = new LicenseStorage(_directory);
        LicenseService service = CreateService(provider, storage);

        ActivationResult result = await service.ActivateAsync("synthetic-key");

        Assert.True(result.Succeeded);
        Assert.Equal(LicenseState.Licensed, service.Status.State);
        LicenseStorage.Snapshot snapshot = Assert.IsType<LicenseStorage.Snapshot>(storage.LoadSnapshot());
        Assert.Equal("provider-instance", snapshot.ProviderInstanceId);
        Assert.Equal(101, snapshot.ProviderStoreId);
        Assert.Equal(202, snapshot.ProviderProductId);
        Assert.Equal(_now, snapshot.ProviderConfirmedAt);
        Assert.Equal(303, snapshot.ProviderVariantId);
        Assert.Null(result.Status!.License);
        Assert.Equal(1, result.Status.EntitledMajor);
        Assert.Equal("pro", result.Status.Edition);
    }

    [Theory]
    [InlineData(999, 202, 303)]
    [InlineData(101, 999, 303)]
    [InlineData(101, 202, 999)]
    public async Task Service_rejects_valid_claim_from_wrong_provider_identity(
        long storeId,
        long productId,
        long variantId)
    {
        var provider = new FakeValidator
        {
            ActivateResult = new LicenseValidation(
                LicenseCheck.Valid,
                "active",
                "provider-instance",
                storeId,
                productId,
                variantId,
                null,
                null,
                null),
        };
        var storage = new LicenseStorage(_directory);
        LicenseService service = CreateService(provider, storage);

        ActivationResult result = await service.ActivateAsync("provider-generated-key");

        Assert.Equal(ActivationOutcome.ProviderRejected, result.Outcome);
        Assert.Null(storage.Load());
    }

    [Fact]
    public async Task Compile_time_variant_mapping_controls_major_entitlement()
    {
        var provider = new FakeValidator();
        var storage = new LicenseStorage(_directory);
        LicenseService service = CreateService(
            provider,
            storage,
            currentMajor: 2,
            productIdentity: ProductIdentity(entitledMajor: 1));

        ActivationResult result = await service.ActivateAsync("provider-generated-key");

        Assert.Equal(ActivationOutcome.UpgradeRequired, result.Outcome);
        Assert.Null(storage.Load());
    }

    [Fact]
    public async Task Provider_entitlement_survives_restart_within_grace_and_covers_only_mapped_major()
    {
        var provider = new FakeValidator();
        var storage = new LicenseStorage(_directory);
        LicenseService firstRun = CreateService(provider, storage);
        Assert.True((await firstRun.ActivateAsync("provider-generated-key")).Succeeded);

        _now = _now.AddDays(6);
        LicenseService restarted = CreateService(provider, storage);

        Assert.Equal(LicenseState.Licensed, restarted.Status.State);
        Assert.True(restarted.IsUpdateEntitled(1));
        Assert.False(restarted.IsUpdateEntitled(2));
    }

    [Fact]
    public async Task Changed_compile_time_variant_allowlist_invalidates_cached_provider_entitlement()
    {
        var provider = new FakeValidator();
        var storage = new LicenseStorage(_directory);
        LicenseService firstRun = CreateService(provider, storage);
        Assert.True((await firstRun.ActivateAsync("provider-generated-key")).Succeeded);

        LicenseService changedMapping = CreateService(
            provider,
            storage,
            productIdentity: ProductIdentity(variantId: 304));

        Assert.NotEqual(LicenseState.Licensed, changedMapping.Status.State);
    }

    [Fact]
    public async Task Changed_store_or_product_identity_invalidates_cached_grace()
    {
        var provider = new FakeValidator();
        var storage = new LicenseStorage(_directory);
        LicenseService firstRun = CreateService(provider, storage);
        Assert.True((await firstRun.ActivateAsync("provider-generated-key")).Succeeded);

        LicenseService changedStore = CreateService(
            provider,
            storage,
            productIdentity: new LicenseProductIdentity(
                999,
                202,
                [new LicenseVariantEntitlement(303, 1, "pro")]));

        Assert.NotEqual(LicenseState.Licensed, changedStore.Status.State);
    }

    [Fact]
    public async Task Repeated_activation_does_not_allocate_a_second_provider_instance()
    {
        var provider = new FakeValidator();
        LicenseService service = CreateService(provider, new LicenseStorage(_directory));

        Assert.True((await service.ActivateAsync("synthetic-key")).Succeeded);
        Assert.True((await service.ActivateAsync("synthetic-key")).Succeeded);

        Assert.Equal(1, provider.ActivateCalls);
        Assert.Equal("WinShot Windows device", provider.LastInstanceName);
    }

    [Fact]
    public async Task Reactivation_after_grace_revalidates_existing_instance_instead_of_consuming_a_seat()
    {
        var provider = new FakeValidator();
        LicenseService service = CreateService(provider, new LicenseStorage(_directory));
        Assert.True((await service.ActivateAsync("provider-generated-key")).Succeeded);

        _now = _now.AddDays(8);
        ActivationResult reactivated = await service.ActivateAsync("provider-generated-key");

        Assert.True(reactivated.Succeeded);
        Assert.Equal(1, provider.ActivateCalls);
        Assert.Equal(1, provider.ValidateCalls);
    }

    [Fact]
    public async Task Explicit_provider_rejection_revokes_cached_confirmation_immediately()
    {
        var provider = new FakeValidator();
        var storage = new LicenseStorage(_directory);
        LicenseService service = CreateService(provider, storage);
        Assert.True((await service.ActivateAsync("synthetic-key")).Succeeded);
        provider.ValidateResult = LicenseValidation.Bad("disabled");

        ProviderRefreshResult refresh = await service.RefreshProviderEntitlementAsync();

        Assert.Equal(ProviderRefreshOutcome.Rejected, refresh.Outcome);
        Assert.NotEqual(LicenseState.Licensed, service.Status.State);
        Assert.Null(storage.LoadSnapshot()!.ProviderConfirmedAt);
    }

    [Fact]
    public async Task Unreachable_refresh_keeps_confirmation_only_inside_explicit_grace()
    {
        var provider = new FakeValidator();
        LicenseService service = CreateService(provider, new LicenseStorage(_directory));
        Assert.True((await service.ActivateAsync("synthetic-key")).Succeeded);
        provider.ValidateResult = LicenseValidation.Unreachable();

        _now = _now.AddDays(6);
        Assert.Equal(ProviderRefreshOutcome.Unreachable,
            (await service.RefreshProviderEntitlementAsync()).Outcome);
        Assert.Equal(LicenseState.Licensed, service.Status.State);

        _now = _now.AddDays(2);
        Assert.Equal(LicenseState.Trial, service.Status.State);
    }

    [Fact]
    public async Task Refresh_rejects_a_different_instance_even_if_a_validator_claims_valid()
    {
        var provider = new FakeValidator();
        LicenseService service = CreateService(provider, new LicenseStorage(_directory));
        Assert.True((await service.ActivateAsync("synthetic-key")).Succeeded);
        provider.ValidateResult = provider.ValidateResult with { InstanceId = "different-instance" };

        ProviderRefreshResult result = await service.RefreshProviderEntitlementAsync();

        Assert.Equal(ProviderRefreshOutcome.Rejected, result.Outcome);
        Assert.NotEqual(LicenseState.Licensed, service.Status.State);
    }

    [Fact]
    public async Task Failed_deactivation_keeps_local_entitlement_and_confirmed_release_clears_it()
    {
        var provider = new FakeValidator();
        LicenseService service = CreateService(provider, new LicenseStorage(_directory));
        Assert.True((await service.ActivateAsync("synthetic-key")).Succeeded);
        provider.DeactivateResult = LicenseDeactivation.Unreachable();

        DeactivationResult unavailable = await service.DeactivateAsync();

        Assert.Equal(DeactivationOutcome.Unreachable, unavailable.Outcome);
        Assert.Equal(LicenseState.Licensed, service.Status.State);

        provider.DeactivateResult = new LicenseDeactivation(
            LicenseDeactivationCheck.Deactivated,
            999,
            202,
            303,
            null);
        DeactivationResult wrongProduct = await service.DeactivateAsync();
        Assert.Equal(DeactivationOutcome.Rejected, wrongProduct.Outcome);
        Assert.Equal(LicenseState.Licensed, service.Status.State);

        provider.DeactivateResult = new LicenseDeactivation(
            LicenseDeactivationCheck.Deactivated,
            101,
            202,
            303,
            null);
        DeactivationResult success = await service.DeactivateAsync();
        Assert.True(success.Succeeded);
        Assert.NotEqual(LicenseState.Licensed, service.Status.State);
    }

    [Fact]
    public async Task Retired_variant_can_still_release_its_exact_stored_provider_seat()
    {
        var provider = new FakeValidator();
        var storage = new LicenseStorage(_directory);
        LicenseService firstRun = CreateService(provider, storage);
        Assert.True((await firstRun.ActivateAsync("synthetic-key")).Succeeded);

        provider.DeactivateResult = new LicenseDeactivation(
            LicenseDeactivationCheck.Deactivated,
            101,
            202,
            303,
            null);
        LicenseService changedMapping = CreateService(
            provider,
            storage,
            productIdentity: ProductIdentity(variantId: 304));

        DeactivationResult result = await changedMapping.DeactivateAsync();

        Assert.True(result.Succeeded);
        Assert.Null(storage.LoadSnapshot());
    }

    [Fact]
    public void Provider_mode_cannot_be_configured_without_compile_time_entitlement_mapping()
    {
        var provider = new FakeValidator();

        Assert.Throws<ArgumentNullException>(
            () => LicenseEnforcementOptions.RequireProvider(
                provider,
                productIdentity: null!,
                offlineGracePeriod: TimeSpan.FromDays(7)));
        Assert.Throws<ArgumentException>(
            () => new LicenseProductIdentity(101, 202, []));
    }

    [Fact]
    public void Oversized_persisted_license_record_fails_closed()
    {
        File.WriteAllBytes(
            Path.Combine(_directory, "license.dat"),
            new byte[(32 * 1024) + 1]);

        Assert.Null(new LicenseStorage(_directory).Load());
    }

    [Fact]
    public void Oversized_key_is_rejected_without_replacing_prior_valid_record()
    {
        var storage = new LicenseStorage(_directory);
        storage.Save("synthetic-key");

        Assert.Throws<ArgumentException>(() => storage.Save(new string('k', (8 * 1024) + 1)));
        Assert.Equal("synthetic-key", storage.Load());
    }

    [Fact]
    public void Oversized_offline_key_is_rejected_before_base64_decode()
    {
        string oversized = new('a', (8 * 1024) + 1);

        Assert.False(LicenseKey.TryVerify(oversized, out _));
    }

    private LicenseService CreateService(
        FakeValidator provider,
        LicenseStorage storage,
        bool providerMode = true,
        int currentMajor = 1,
        LicenseProductIdentity? productIdentity = null)
    {
        LicenseProductIdentity identity = productIdentity ?? ProductIdentity();
        LicenseEnforcementOptions enforcement = providerMode
            ? LicenseEnforcementOptions.RequireProvider(
                provider,
                identity,
                "WinShot Windows device",
                TimeSpan.FromDays(7),
                () => _now)
            : LicenseEnforcementOptions.OfflineSigned;
        return new LicenseService(
            new TrialTracker(_directory, () => _now, useRegistry: false),
            storage,
            currentMajor,
            providerMode ? _ => null : _ => ValidPayload(),
            enforcement);
    }

    private static LicensePayload ValidPayload() => new()
    {
        Id = "synthetic-license",
        Product = "winshot",
        Edition = "pro",
        Major = 1,
        IssuedAt = 1_737_158_400,
    };

    private static LicenseProductIdentity ProductIdentity(
        long variantId = 303,
        int entitledMajor = 1,
        string edition = "pro") => new(
        101,
        202,
        [new LicenseVariantEntitlement(variantId, entitledMajor, edition)]);

    private sealed class FakeValidator : ILicenseValidator
    {
        public int ActivateCalls { get; private set; }
        public int ValidateCalls { get; private set; }
        public string? LastInstanceName { get; private set; }

        public LicenseValidation ActivateResult { get; set; } = new(
            LicenseCheck.Valid,
            "active",
            "provider-instance",
            101,
            202,
            303,
            null,
            null,
            null);

        public LicenseValidation ValidateResult { get; set; } = new(
            LicenseCheck.Valid,
            "active",
            "provider-instance",
            101,
            202,
            303,
            null,
            null,
            null);

        public LicenseDeactivation DeactivateResult { get; set; } = new(
            LicenseDeactivationCheck.Deactivated,
            101,
            202,
            303,
            null);

        public Task<LicenseValidation> ActivateAsync(
            string licenseKey,
            string instanceName,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ActivateCalls++;
            LastInstanceName = instanceName;
            return Task.FromResult(ActivateResult);
        }

        public Task<LicenseValidation> ValidateAsync(
            string licenseKey,
            string? instanceId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ValidateCalls++;
            return Task.FromResult(ValidateResult);
        }

        public Task<LicenseDeactivation> DeactivateAsync(
            string licenseKey,
            string instanceId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(DeactivateResult);
        }
    }

}
