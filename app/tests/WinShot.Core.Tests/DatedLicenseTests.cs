using System.Security.Cryptography;
using WinShot.Licensing;
using WinShot.Core.Update;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class DatedLicenseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "winshot-dated-license-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Purchased = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private static LicensePayload Purchase => LicenseEntitlement.CreatePurchase("synthetic-order", Purchased);

    private LicenseService Service(DateTimeOffset now, DateTimeOffset? releasedAt, LicensePayload? payload = null, int major = 1) =>
        new(new TrialTracker(_root, () => now, useRegistry: false), new LicenseStorage(_root),
            major, key => key == "synthetic-key" ? payload ?? Purchase : null,
            currentReleaseDateUtc: releasedAt);

    [Fact]
    public void Paid_release_remains_usable_and_can_be_reactivated_years_after_updates_end()
    {
        var release = Purchased.AddMonths(6);
        Assert.True(Service(Purchased, release).Activate("synthetic-key").Succeeded);
        var later = Service(Purchased.AddYears(5), release);
        Assert.Equal(LicenseState.Licensed, later.Status.State);
        later.Deactivate();
        Assert.True(later.Activate("synthetic-key").Succeeded);
    }

    [Fact]
    public void New_major_released_during_included_year_is_covered()
    {
        var service = Service(Purchased, Purchased.AddMonths(2), major: 2);
        Assert.True(service.Activate("synthetic-key").Succeeded);
        Assert.True(service.IsUpdateEntitled(3, Purchased.AddMonths(10)));
        Assert.False(service.IsUpdateEntitled(3));
    }

    [Fact]
    public void Same_major_released_at_or_after_cutoff_cannot_activate_or_update()
    {
        Assert.True(Service(Purchased, Purchased).Activate("synthetic-key").Succeeded);
        var active = Service(Purchased.AddYears(2), Purchased);
        Assert.True(active.IsUpdateEntitled(1, Purchased.AddYears(1).AddTicks(-1)));
        Assert.False(active.IsUpdateEntitled(1, Purchased.AddYears(1)));
        var future = Service(Purchased.AddYears(2), Purchased.AddYears(1));
        Assert.Equal(LicenseState.Expired, future.Status.State);
        Assert.Equal(LicenseAccessIssue.ReleaseNotCovered, future.Status.AccessIssue);
        Assert.Equal(Purchase, future.Status.License);
        Assert.Equal(ActivationOutcome.UpgradeRequired, future.Activate("synthetic-key").Outcome);
        Assert.Equal("synthetic-key", new LicenseStorage(_root).Load());
    }

    [Fact]
    public void Missing_release_date_fails_closed_with_an_explanation()
    {
        Assert.Equal(ActivationOutcome.ConfigurationError,
            Service(Purchased, null).Activate("synthetic-key").Outcome);
        Assert.True(Service(Purchased, Purchased).Activate("synthetic-key").Succeeded);
        var undated = Service(Purchased.AddYears(2), null);
        Assert.Equal(LicenseAccessIssue.ReleaseDateMissing, undated.Status.AccessIssue);
        Assert.Equal(Purchase, undated.Status.License);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public void Invalid_cutoffs_do_not_grant_access(long cutoff)
    {
        Assert.Equal(ActivationOutcome.InvalidKey,
            Service(Purchased, Purchased, Purchase with { UpdatesUntil = cutoff })
                .Activate("synthetic-key").Outcome);
    }

    [Fact]
    public void Leap_day_purchase_gets_a_calendar_year_and_signature_preserves_cutoff()
    {
        var leapDay = new DateTimeOffset(2028, 2, 29, 12, 0, 0, TimeSpan.Zero);
        var payload = LicenseEntitlement.CreatePurchase("synthetic-leap-order", leapDay);
        Assert.Equal(new DateTimeOffset(2029, 2, 28, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), payload.UpdatesUntil);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.True(LicenseKey.TryVerify(LicenseKey.Encode(payload, key), key, out var decoded));
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void Update_preflight_uses_release_dates_across_major_versions()
    {
        var input = new UpdatePreflightInput(new Version(2, 0, 0), new Version(3, 0, 0), 1,
            Purchased.AddYears(1), Purchased, Purchased.AddMonths(6));
        Assert.True(UpdatePreflightPolicy.Evaluate(input).CanStage);
        Assert.Equal(UpdatePreflightStatus.CandidateReleaseNotEntitled,
            UpdatePreflightPolicy.Evaluate(input with { CandidateReleasedAtUtc = Purchased.AddYears(1) }).Status);
        Assert.Equal(UpdatePreflightStatus.ReleaseDateMissing,
            UpdatePreflightPolicy.Evaluate(input with { CandidateReleasedAtUtc = null }).Status);
        Assert.Equal(UpdatePreflightStatus.CurrentReleaseNotEntitled,
            UpdatePreflightPolicy.Evaluate(input with { CurrentReleasedAtUtc = Purchased.AddYears(1) }).Status);
    }

    [Fact]
    public void Computer_activation_rejects_copied_keys_and_requires_a_current_seat_check()
    {
        var device = new string('a', 64);
        var now = Purchased.AddYears(4);
        var payload = Purchase with
        {
            Version = 3, DeviceId = device, ActivationId = Guid.NewGuid().ToString("D"),
            LeaseIssuedAt = now.ToUnixTimeSeconds(), ValidUntil = now.AddDays(30).ToUnixTimeSeconds()
        };
        var service = new LicenseService(new TrialTracker(_root, () => now, useRegistry: false),
            new LicenseStorage(_root), 1, _ => payload, currentReleaseDateUtc: Purchased,
            deviceId: () => device, now: () => now);
        Assert.True(service.Activate("synthetic-key").Succeeded);
        Assert.True(service.IsUpdateEntitled(1, Purchased.AddMonths(11)));
        // The original purchase cutoff is retained even when the seat is refreshed years later.
        Assert.False(service.IsUpdateEntitled(1, Purchased.AddYears(1)));
        var otherComputer = new LicenseService(new TrialTracker(_root, () => now, useRegistry: false),
            new LicenseStorage(_root), 1, _ => payload, currentReleaseDateUtc: Purchased,
            deviceId: () => new string('b', 64), now: () => now);
        Assert.Equal(ActivationOutcome.DifferentComputer, otherComputer.Activate("synthetic-key").Outcome);
        Assert.Equal(LicenseAccessIssue.DifferentComputer, otherComputer.Status.AccessIssue);
        now = now.AddDays(30);
        Assert.Equal(LicenseAccessIssue.ActivationCheckRequired, service.Status.AccessIssue);
        Assert.Equal(ActivationOutcome.ActivationCheckRequired, service.Activate("synthetic-key").Outcome);
        Assert.True(LicenseEntitlement.CoversRelease(payload, 1, Purchased));
    }

    [Fact]
    public void Device_binding_and_lease_are_signed_and_malformed_leases_fail_closed()
    {
        var payload = Purchase with { Version = 3, DeviceId = new string('a', 64),
            ActivationId = Guid.NewGuid().ToString("D"), LeaseIssuedAt = Purchased.ToUnixTimeSeconds(),
            ValidUntil = Purchased.AddDays(30).ToUnixTimeSeconds() };
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.True(LicenseKey.TryVerify(LicenseKey.Encode(payload, key), key, out var decoded));
        Assert.Equal(payload, decoded);
        Assert.True(LicenseEntitlement.IsValid(payload));
        Assert.False(LicenseEntitlement.IsValid(payload with { DeviceId = null }));
        Assert.False(LicenseEntitlement.IsValid(payload with { ActivationId = "../other" }));
        Assert.False(LicenseEntitlement.IsValid(payload with { ValidUntil = 0 }));
        Assert.False(LicenseEntitlement.IsValid(payload with { ValidUntil = payload.ValidUntil + 1 }));
        Assert.False(LicenseEntitlement.HasCurrentActivation(payload, Purchased.AddMinutes(-6)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
