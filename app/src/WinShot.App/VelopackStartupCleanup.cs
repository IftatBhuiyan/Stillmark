using WinShot.Core.Startup;

namespace WinShot.App;

/// <summary>
/// Removes this product's sign-in entries before Velopack removes its package root. User data is
/// deliberately outside that root and is never touched here.
/// </summary>
internal static class VelopackStartupCleanup
{
    internal static void RemoveOwnedRegistrations()
    {
        try
        {
            string? processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
                return;

            string localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            InstalledApplicationLayout? installedLayout =
                InstalledLaunchPolicy.DiscoverInstalledLayout(processPath, localAppData);
            if (installedLayout is null)
                return;

            StartupLaunchTarget launchTarget = InstalledLaunchPolicy.Resolve(
                processPath,
                installedLayout);
            if (!launchTarget.IsInstallerOwned)
                return;

            ImageFileRegistration.Unregister();

            // The existing policy removes only exact commands owned by these two brand values and
            // preserves unrelated values with either name. A failed registry operation is safe to
            // leave for manual cleanup because Velopack does not own the user's data or registry.
            StartupService.SynchronizeBrandMigration(
                enabled: false,
                launchTarget: launchTarget,
                currentValueName: ProductIdentity.StartupValueName,
                legacyValueName: ProductIdentity.LegacyStartupValueName,
                legacyExecutablePath: launchTarget.ExecutablePath);
        }
        catch
        {
            // Uninstall hooks are best-effort and must not delay Velopack's bounded uninstall path.
        }
    }
}
