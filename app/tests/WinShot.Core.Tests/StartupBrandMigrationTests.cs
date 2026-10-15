using WinShot.Core.Startup;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class StartupBrandMigrationTests
{
    private static readonly StartupLaunchTarget InstalledTarget = new(
        ApplicationPathKind.InstalledStableLauncher,
        @"C:\Users\Test\AppData\Local\WinShot\WinShot.exe");

    [Fact]
    public void EnabledMovesOwnedLegacyValueAfterWritingCurrent()
    {
        var store = new RecordingNamedStartupStore();
        store.Values["WinShot"] =
            "\"C:\\Users\\Test\\AppData\\Local\\WinShot\\WinShot.exe\" --tray";

        StartupSynchronizationResult result = StartupService.SynchronizeBrandMigration(
            true,
            InstalledTarget,
            "Stillmark",
            "WinShot",
            null,
            store);

        Assert.Equal(StartupSynchronizationStatus.RegistrationWritten, result.Status);
        Assert.True(store.Values.ContainsKey("Stillmark"));
        Assert.False(store.Values.ContainsKey("WinShot"));
        Assert.Equal(
            ["write:Stillmark", "delete:WinShot"],
            store.Mutations);
    }

    [Fact]
    public void EnabledPreservesUnrelatedCurrentOrLegacyValues()
    {
        var currentConflict = new RecordingNamedStartupStore();
        currentConflict.Values["Stillmark"] = "\"C:\\Other\\Other.exe\" --tray";
        var legacyConflict = new RecordingNamedStartupStore();
        legacyConflict.Values["WinShot"] = 42;

        StartupSynchronizationResult currentResult = StartupService.SynchronizeBrandMigration(
            true, InstalledTarget, "Stillmark", "WinShot", null, currentConflict);
        StartupSynchronizationResult legacyResult = StartupService.SynchronizeBrandMigration(
            true, InstalledTarget, "Stillmark", "WinShot", null, legacyConflict);

        Assert.Equal(
            StartupSynchronizationStatus.ConflictingRegistrationPreserved,
            currentResult.Status);
        Assert.Equal(
            StartupSynchronizationStatus.ConflictingRegistrationPreserved,
            legacyResult.Status);
        Assert.Empty(currentConflict.Mutations);
        Assert.Empty(legacyConflict.Mutations);
    }

    [Fact]
    public void WriteFailureLeavesOwnedLegacyValueInPlace()
    {
        var store = new RecordingNamedStartupStore
        {
            WriteFailure = new UnauthorizedAccessException("synthetic write failure"),
        };
        store.Values["WinShot"] =
            "\"C:\\Users\\Test\\AppData\\Local\\WinShot\\WinShot.exe\" --tray";

        StartupSynchronizationResult result = StartupService.SynchronizeBrandMigration(
            true, InstalledTarget, "Stillmark", "WinShot", null, store);

        Assert.Equal(StartupSynchronizationStatus.RegistryAccessFailed, result.Status);
        Assert.True(store.Values.ContainsKey("WinShot"));
        Assert.DoesNotContain("delete:WinShot", store.Operations);
    }

    [Fact]
    public void DisabledRemovesBothOwnedValues()
    {
        const string command =
            "\"C:\\Users\\Test\\AppData\\Local\\WinShot\\WinShot.exe\" --tray";
        var store = new RecordingNamedStartupStore();
        store.Values["Stillmark"] = command;
        store.Values["WinShot"] = command;

        StartupSynchronizationResult result = StartupService.SynchronizeBrandMigration(
            false, InstalledTarget, "Stillmark", "WinShot", null, store);

        Assert.Equal(StartupSynchronizationStatus.RegistrationRemoved, result.Status);
        Assert.Empty(store.Values);
    }

    [Fact]
    public void CleanupFailureReportsPartialMutation()
    {
        var failure = new UnauthorizedAccessException("synthetic delete failure");
        var store = new RecordingNamedStartupStore { DeleteFailure = failure };
        store.Values["WinShot"] =
            "\"C:\\Users\\Test\\AppData\\Local\\WinShot\\WinShot.exe\" --tray";

        StartupSynchronizationResult result = StartupService.SynchronizeBrandMigration(
            true, InstalledTarget, "Stillmark", "WinShot", null, store);

        Assert.Equal(
            StartupSynchronizationStatus.RegistrationPartiallySynchronized,
            result.Status);
        Assert.True(result.RegistryChanged);
        Assert.True(result.RequiresUserAttention);
        Assert.Same(failure, result.Failure);
        Assert.True(store.Values.ContainsKey("Stillmark"));
        Assert.True(store.Values.ContainsKey("WinShot"));
    }

    [Fact]
    public void RecognizesExactFormerExecutablePath()
    {
        var renamedTarget = new StartupLaunchTarget(
            ApplicationPathKind.InstalledStableLauncher,
            @"C:\Users\Test\AppData\Local\RelicScreenshot\RelicScreenshot.exe");
        var store = new RecordingNamedStartupStore();
        store.Values["WinShot"] =
            "\"C:\\Users\\Test\\AppData\\Local\\WinShot\\WinShot.exe\" --tray";

        StartupSynchronizationResult result = StartupService.SynchronizeBrandMigration(
            true,
            renamedTarget,
            "Stillmark",
            "WinShot",
            @"C:\Users\Test\AppData\Local\WinShot\WinShot.exe",
            store);

        Assert.Equal(StartupSynchronizationStatus.RegistrationWritten, result.Status);
        Assert.Contains("RelicScreenshot.exe", Assert.IsType<string>(store.Values["Stillmark"]));
        Assert.False(store.Values.ContainsKey("WinShot"));
    }

    private sealed class RecordingNamedStartupStore : INamedStartupRegistrationStore
    {
        public Dictionary<string, object?> Values { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public List<string> Operations { get; } = [];
        public IEnumerable<string> Mutations => Operations.Where(operation =>
            operation.StartsWith("write:", StringComparison.Ordinal)
            || operation.StartsWith("delete:", StringComparison.Ordinal));
        public Exception? WriteFailure { get; init; }
        public Exception? DeleteFailure { get; init; }

        public object? ReadValue(string valueName)
        {
            Operations.Add("read:" + valueName);
            return Values.GetValueOrDefault(valueName);
        }

        public void WriteValue(string valueName, string launchCommand)
        {
            Operations.Add("write:" + valueName);
            if (WriteFailure is not null)
                throw WriteFailure;
            Values[valueName] = launchCommand;
        }

        public void DeleteValue(string valueName)
        {
            Operations.Add("delete:" + valueName);
            if (DeleteFailure is not null)
                throw DeleteFailure;
            Values.Remove(valueName);
        }
    }
}
