using System.IO;
using WinShot.Core.Settings;
using WinShot.Licensing;

namespace WinShot.App;

/// <summary>
/// Separates the customer-facing brand from durable identifiers that existing installations own.
/// The legacy data root is intentionally retained: moving privacy-sensitive state during startup
/// would add partial-copy, rollback, and split-profile failure modes without a user benefit.
/// </summary>
internal static class ProductIdentity
{
    internal const string DisplayName = "Stillmark";
    // Keep the existing registry value so upgrading does not create a second startup entry.
    internal const string StartupValueName = "Relic Screenshot";
    internal const string LegacyStartupValueName = "WinShot";
    internal const string DataDirectoryName = "WinShot";

    internal const string SingleInstanceMutexName = @"Local\RelicScreenshot.SingleInstance";
    internal const string LegacySingleInstanceMutexName = @"Local\WinShot.SingleInstance";
    internal const string ShowMainSignalName = @"Local\RelicScreenshot.ShowMain";
    internal const string LegacyShowMainSignalName = @"Local\WinShot.ShowMain";

    internal static string RoamingDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        DataDirectoryName);

    internal static string LocalDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        DataDirectoryName);

    internal static string HistoryBaseDirectory => LocalDataRoot;

    internal static string DiagnosticDirectory => LocalDataRoot;

    internal static string RecoveryStatePath =>
        Path.Combine(LocalDataRoot, "recovery-state.json");

    internal static SettingsStore CreateSettingsStore() =>
        new(Path.Combine(RoamingDataRoot, "settings.json"));

    internal static LicenseStorage CreateLicenseStorage() => new(LocalDataRoot);

    internal static TrialTracker CreateTrialTracker() => new(LocalDataRoot);
}
