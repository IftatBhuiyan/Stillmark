using WinShot.App;
using WinShot.App.Lifecycle;
using WinShot.Core.History;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class ProductIdentityMigrationTests
{
    [Fact]
    public void DisplayBrand_ChangesWithoutMovingLegacyUserData()
    {
        Assert.Equal("Stillmark", AppInfo.Name);
        Assert.Equal("Stillmark", ProductIdentity.DisplayName);
        Assert.Equal("WinShot", ProductIdentity.DataDirectoryName);

        string expectedRoaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinShot");
        string expectedLocal = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinShot");
        Assert.Equal(expectedRoaming, ProductIdentity.RoamingDataRoot);
        Assert.Equal(expectedLocal, ProductIdentity.LocalDataRoot);
        Assert.Equal(
            Path.Combine(expectedRoaming, "settings.json"),
            ProductIdentity.CreateSettingsStore().FilePath);
        Assert.Equal(
            Path.Combine(expectedLocal, "license.dat"),
            ProductIdentity.CreateLicenseStorage().FilePath);
        Assert.Equal(
            Path.Combine(expectedLocal, "trial.dat"),
            ProductIdentity.CreateTrialTracker().StateFilePath);
        Assert.Equal(
            Path.Combine(expectedLocal, "History"),
            new HistoryService(ProductIdentity.HistoryBaseDirectory).HistoryDirectory);
        Assert.Equal(expectedLocal, ProductIdentity.DiagnosticDirectory);
        Assert.Equal(
            Path.Combine(expectedLocal, "recovery-state.json"),
            ProductIdentity.RecoveryStatePath);
    }

    [Fact]
    public void Gate_CurrentAndLegacyNamesAllowOnlyOnePrimary()
    {
        (string currentMutex, string legacyMutex, string currentEvent, string legacyEvent) =
            CreatePrivateNames();
        using var first = new SingleInstanceGate(
            currentMutex,
            legacyMutex,
            currentEvent,
            legacyEvent);
        using var second = new SingleInstanceGate(
            currentMutex,
            legacyMutex,
            currentEvent,
            legacyEvent);

        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
        second.SignalExistingInstance();
        Assert.NotEqual(WaitHandle.WaitTimeout, WaitHandle.WaitAny(first.HandoffSignals, 1000));
    }

    [Fact]
    public void Gate_LegacyResidentBlocksRenamedResidentAndReceivesHandoff()
    {
        (string currentMutex, string legacyMutex, string currentEvent, string legacyEvent) =
            CreatePrivateNames();
        using var legacySignal = new EventWaitHandle(false, EventResetMode.AutoReset, legacyEvent);
        using var legacyOwner = new Mutex(true, legacyMutex, out bool created);
        Assert.True(created);
        using var renamed = new SingleInstanceGate(
            currentMutex,
            legacyMutex,
            currentEvent,
            legacyEvent);

        Assert.False(renamed.IsPrimary);
        renamed.SignalExistingInstance();
        Assert.True(legacySignal.WaitOne(1000));
    }

    [Fact]
    public void Gate_CurrentResidentBlocksAnotherRenamedResident()
    {
        (string currentMutex, string legacyMutex, string currentEvent, string legacyEvent) =
            CreatePrivateNames();
        using var currentOwner = new Mutex(true, currentMutex, out bool created);
        Assert.True(created);
        using var candidate = new SingleInstanceGate(
            currentMutex,
            legacyMutex,
            currentEvent,
            legacyEvent);

        Assert.False(candidate.IsPrimary);
    }

    private static (string, string, string, string) CreatePrivateNames()
    {
        string token = Guid.NewGuid().ToString("N");
        return (
            $@"Local\RelicScreenshot.Tests.CurrentMutex.{token}",
            $@"Local\RelicScreenshot.Tests.LegacyMutex.{token}",
            $@"Local\RelicScreenshot.Tests.CurrentEvent.{token}",
            $@"Local\RelicScreenshot.Tests.LegacyEvent.{token}");
    }
}
