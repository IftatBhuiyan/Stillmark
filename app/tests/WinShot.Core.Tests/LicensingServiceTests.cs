using System.Security.Cryptography;
using System.Net.Http;
using WinShot.Licensing;
using Xunit;

namespace WinShot.Core.Tests;

public class TrialTrackerTests : IDisposable
{
    private readonly string _dir;

    public TrialTrackerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "winshot-trial-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private TrialTracker At(DateTimeOffset now) => new(_dir, () => now, useRegistry: false);

    [Fact]
    public void Oversized_state_file_is_ignored_and_replaced_with_bounded_state()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "trial.dat");
        File.WriteAllBytes(path, new byte[TrialTracker.MaximumStateBytes + 1]);
        DateTimeOffset start = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

        int left = At(start).DaysLeft();

        Assert.Equal(TrialTracker.TrialDays, left);
        Assert.InRange(new FileInfo(path).Length, 1, TrialTracker.MaximumStateBytes);
    }

    [Fact]
    public void Fresh_trial_has_full_window()
    {
        var start = DateTimeOffset.UtcNow;
        Assert.Equal(TrialTracker.TrialDays, At(start).DaysLeft());
    }

    [Fact]
    public void Trial_counts_down_over_days()
    {
        var start = DateTimeOffset.UtcNow;
        At(start).DaysLeft(); // establish first-run

        int left = At(start.AddDays(10)).DaysLeft();
        Assert.Equal(TrialTracker.TrialDays - 10, left);
    }

    [Fact]
    public void Trial_exposes_precise_remaining_time_for_countdown()
    {
        DateTimeOffset start = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        At(start).DaysLeft();

        TimeSpan remaining = At(start.AddDays(3).AddHours(4).AddMinutes(5)).TimeRemaining();

        Assert.Equal(TimeSpan.FromDays(11).Subtract(TimeSpan.FromHours(4).Add(TimeSpan.FromMinutes(5))), remaining);
        Assert.Equal(11, At(start.AddDays(3).AddHours(4).AddMinutes(5)).DaysLeft());
    }

    [Fact]
    public void Trial_reaches_zero_after_window()
    {
        var start = DateTimeOffset.UtcNow;
        At(start).DaysLeft();
        Assert.Equal(0, At(start.AddDays(TrialTracker.TrialDays + 3)).DaysLeft());
    }

    [Fact]
    public void Clock_rollback_does_not_extend_trial()
    {
        var start = DateTimeOffset.UtcNow;
        At(start).DaysLeft();               // day 0
        At(start.AddDays(12)).DaysLeft();   // advance the "last seen" guard to day 12

        // Attacker winds the clock back to day 1 to reclaim trial days.
        int left = At(start.AddDays(1)).DaysLeft();
        Assert.Equal(TrialTracker.TrialDays - 12, left); // still measured from day 12
    }

    [Fact]
    public void First_run_persists_across_instances()
    {
        var start = DateTimeOffset.UtcNow;
        At(start).DaysLeft();
        int left = At(start.AddDays(5)).DaysLeft();
        Assert.Equal(TrialTracker.TrialDays - 5, left);
    }
}

public class LicenseServiceTests : IDisposable
{
    private readonly string _dir;
    public LicenseServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "winshot-lic-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private LicenseService NewService(
        Func<DateTimeOffset> now,
        int currentMajor = 1,
        Func<string?, LicensePayload?>? verifier = null) =>
        new(
            new TrialTracker(_dir, now, useRegistry: false),
            new LicenseStorage(_dir),
            currentMajor,
            verifier ?? (_ => null));

    private LicenseService NewService(DateTimeOffset now) => NewService(() => now);

    [Fact]
    public void New_install_starts_in_trial()
    {
        var svc = NewService(DateTimeOffset.UtcNow);
        Assert.Equal(LicenseState.Trial, svc.Status.State);
        Assert.True(svc.Status.IsActive);
    }

    [Fact]
    public void Expired_trial_gates_the_app()
    {
        var start = DateTimeOffset.UtcNow;
        NewService(start); // establish first run
        var svc = NewService(start.AddDays(30));
        Assert.Equal(LicenseState.Expired, svc.Status.State);
        Assert.False(svc.Status.IsActive);
    }

    [Fact]
    public void Running_process_recomputes_trial_status_after_expiry()
    {
        var now = DateTimeOffset.UtcNow;
        var svc = NewService(() => now);
        Assert.True(svc.Status.IsActive);

        now = now.AddDays(TrialTracker.TrialDays + 1);

        Assert.Equal(LicenseState.Expired, svc.Status.State);
        Assert.False(svc.Status.IsActive);
    }

    [Fact]
    public void Invalid_key_is_rejected()
    {
        var svc = NewService(DateTimeOffset.UtcNow);
        var result = svc.Activate("garbage-key");
        Assert.False(result.Succeeded);
        Assert.Equal(ActivationOutcome.InvalidKey, result.Outcome);
    }

    [Fact]
    public void Current_major_license_activates_and_entitles_only_covered_updates()
    {
        var payload = ValidPayload(major: 2);
        var svc = NewService(() => DateTimeOffset.UtcNow, currentMajor: 2, _ => payload);

        ActivationResult result = svc.Activate("synthetic-test-key");

        Assert.True(result.Succeeded);
        Assert.Equal(LicenseState.Licensed, svc.Status.State);
        Assert.True(svc.IsUpdateEntitled(2));
        Assert.False(svc.IsUpdateEntitled(3));
    }

    [Fact]
    public void Older_major_license_cannot_activate_newer_paid_major()
    {
        var payload = ValidPayload(major: 1);
        var svc = NewService(() => DateTimeOffset.UtcNow, currentMajor: 2, _ => payload);

        ActivationResult result = svc.Activate("synthetic-test-key");

        Assert.False(result.Succeeded);
        Assert.Equal(ActivationOutcome.UpgradeRequired, result.Outcome);
        Assert.Equal(LicenseState.Trial, svc.Status.State);
    }

    [Fact]
    public void Stored_older_major_license_does_not_unlock_newer_paid_major()
    {
        new LicenseStorage(_dir).Save("synthetic-test-key");
        var svc = NewService(
            () => DateTimeOffset.UtcNow,
            currentMajor: 2,
            _ => ValidPayload(major: 1));

        Assert.NotEqual(LicenseState.Licensed, svc.Status.State);
    }

    [Fact]
    public void Signed_key_for_another_product_is_rejected()
    {
        var payload = ValidPayload(major: 1) with { Product = "another-product" };
        var svc = NewService(() => DateTimeOffset.UtcNow, verifier: _ => payload);

        ActivationResult result = svc.Activate("synthetic-test-key");

        Assert.Equal(ActivationOutcome.WrongProduct, result.Outcome);
        Assert.False(result.Succeeded);
    }

    private static LicensePayload ValidPayload(int major) => new()
    {
        Id = "synthetic-license-id",
        Product = "winshot",
        Edition = "pro",
        Major = major,
        IssuedAt = 1_737_158_400,
    };
}

public class LicenseStorageTests : IDisposable
{
    private readonly string _dir;

    public LicenseStorageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "winshot-license-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Saved_license_round_trips_without_plaintext_key_on_disk()
    {
        const string key = "synthetic-secret-license-key";
        var storage = new LicenseStorage(_dir);

        storage.Save(key);

        string persisted = File.ReadAllText(Path.Combine(_dir, "license.dat"));
        Assert.StartsWith("protected:v1:", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(key, persisted, StringComparison.Ordinal);
        Assert.Equal(key, storage.Load());
    }

    [Fact]
    public void Valid_legacy_plaintext_record_loads_and_migrates_in_place()
    {
        const string key = "synthetic-legacy-license-key";
        string path = Path.Combine(_dir, "license.dat");
        File.WriteAllText(
            path,
            $$"""{"Key":"{{key}}","Machine":"{{MachineFingerprint.Current}}"}""");

        var storage = new LicenseStorage(_dir);

        Assert.Equal(key, storage.Load());
        string migrated = File.ReadAllText(path);
        Assert.StartsWith("protected:v1:", migrated, StringComparison.Ordinal);
        Assert.DoesNotContain(key, migrated, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_provider_record_migration_preserves_exact_activation_identity()
    {
        const string key = "synthetic-legacy-provider-key";
        DateTimeOffset confirmedAt =
            new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        string path = Path.Combine(_dir, "license.dat");
        File.WriteAllText(
            path,
            $$"""
            {"Key":"{{key}}","Machine":"{{MachineFingerprint.Current}}","ProviderInstanceId":"provider-instance","ProviderStoreId":101,"ProviderProductId":202,"ProviderVariantId":303,"ProviderConfirmedAt":{{confirmedAt.ToUnixTimeSeconds()}}}
            """);

        var firstStorage = new LicenseStorage(_dir);
        LicenseStorage.Snapshot first = Assert.IsType<LicenseStorage.Snapshot>(
            firstStorage.LoadSnapshot());

        string migrated = File.ReadAllText(path);
        Assert.StartsWith("protected:v1:", migrated, StringComparison.Ordinal);
        Assert.DoesNotContain(key, migrated, StringComparison.Ordinal);

        var reopenedStorage = new LicenseStorage(_dir);
        LicenseStorage.Snapshot reopened =
            Assert.IsType<LicenseStorage.Snapshot>(
                reopenedStorage.LoadSnapshot());
        Assert.Equal(first, reopened);
        Assert.Equal(key, reopened.Key);
        Assert.Equal("provider-instance", reopened.ProviderInstanceId);
        Assert.Equal(101, reopened.ProviderStoreId);
        Assert.Equal(202, reopened.ProviderProductId);
        Assert.Equal(303, reopened.ProviderVariantId);
        Assert.Equal(confirmedAt, reopened.ProviderConfirmedAt);
    }

    [Fact]
    public void Corrupted_protected_record_fails_closed()
    {
        File.WriteAllText(Path.Combine(_dir, "license.dat"), "protected:v1:not-base64");

        Assert.Null(new LicenseStorage(_dir).Load());
    }
}
