namespace WinShot.Core.Startup;

public enum InstalledRecoveryLaunchRoute
{
    Normal,
    ContinueRecovered,
    RedirectToStableLauncher,
    Reject,
}

public readonly record struct InstalledRecoveryLaunchDecision(
    InstalledRecoveryLaunchRoute Route,
    string? StableLauncherPath)
{
    public bool IsRecoveredStart =>
        Route is InstalledRecoveryLaunchRoute.ContinueRecovered;
}

/// <summary>
/// Routes the public Windows Application Restart command back through an installed stable launcher.
/// The stable launcher translates it to the private child marker after selecting and authenticating
/// the active payload, which prevents a recovered payload from redirecting indefinitely.
/// </summary>
public static class InstalledRecoveryRedirectPolicy
{
    public const string TrayArgument = "--tray";
    public const string PublicRecoveredArgument = "--recovered";
    public const string LauncherRecoveredArgument = "--launcher-recovered";

    public static bool ContainsRecoveryIntent(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Any(static argument =>
            string.Equals(
                argument,
                PublicRecoveredArgument,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                argument,
                LauncherRecoveredArgument,
                StringComparison.OrdinalIgnoreCase));
    }

    public static InstalledRecoveryLaunchDecision Evaluate(
        IReadOnlyList<string> arguments,
        string processExecutablePath,
        string localApplicationDataPath,
        string expectedPackageId)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPackageId);
        if (!string.Equals(
                expectedPackageId,
                Path.GetFileName(expectedPackageId),
                StringComparison.Ordinal)
            || expectedPackageId.IndexOf(Path.DirectorySeparatorChar) >= 0
            || expectedPackageId.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
        {
            throw new ArgumentException(
                "The expected package ID must be one safe path segment.",
                nameof(expectedPackageId));
        }

        bool publicRecovery = HasExactRecoveryShape(
            arguments,
            PublicRecoveredArgument);
        bool launcherRecovery = HasExactRecoveryShape(
            arguments,
            LauncherRecoveredArgument);
        if (!publicRecovery && !launcherRecovery)
        {
            return ContainsRecoveryIntent(arguments)
                ? new InstalledRecoveryLaunchDecision(
                    InstalledRecoveryLaunchRoute.Reject,
                    null)
                : new InstalledRecoveryLaunchDecision(
                    InstalledRecoveryLaunchRoute.Normal,
                    null);
        }

        InstalledApplicationLayout? layout;
        try
        {
            // Recovery routing classifies the strict installed path shape independently of current
            // file existence. A missing root launcher must produce a failed redirect and shutdown,
            // never a fallback start of an old versioned payload.
            layout = InstalledLaunchPolicy.DiscoverInstalledLayout(
                processExecutablePath,
                localApplicationDataPath,
                static _ => true);
        }
        catch (ArgumentException)
        {
            return new InstalledRecoveryLaunchDecision(
                InstalledRecoveryLaunchRoute.Reject,
                null);
        }

        if (layout is not null)
        {
            string expectedRoot = Path.Combine(
                Path.GetFullPath(localApplicationDataPath),
                expectedPackageId);
            if (!string.Equals(
                    layout.InstallRootPath,
                    expectedRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new InstalledRecoveryLaunchDecision(
                    InstalledRecoveryLaunchRoute.Reject,
                    null);
            }
        }

        StartupLaunchTarget target = InstalledLaunchPolicy.Resolve(
            processExecutablePath,
            layout);
        bool isInstalledPayload =
            target.PathKind is ApplicationPathKind.InstalledPayload;
        bool isVelopackPayload =
            isInstalledPayload && IsCurrentPayloadPath(processExecutablePath);

        if (launcherRecovery)
        {
            if (isVelopackPayload)
            {
                // Velopack's root stub does not translate the private launcher marker. Admit only
                // the public Windows recovery shape for a current payload, otherwise the marker
                // would either be ignored or re-enter this policy indefinitely.
                return new InstalledRecoveryLaunchDecision(
                    InstalledRecoveryLaunchRoute.Reject,
                    null);
            }

            return isInstalledPayload
                ? new InstalledRecoveryLaunchDecision(
                    InstalledRecoveryLaunchRoute.ContinueRecovered,
                    target.ExecutablePath)
                : new InstalledRecoveryLaunchDecision(
                    InstalledRecoveryLaunchRoute.Reject,
                    null);
        }

        if (isVelopackPayload)
        {
            // Velopack's root executable is an execution stub, not the custom stable launcher that
            // understands --launcher-recovered. Restart the current payload directly.
            return new InstalledRecoveryLaunchDecision(
                InstalledRecoveryLaunchRoute.ContinueRecovered,
                null);
        }

        if (isInstalledPayload)
        {
            return new InstalledRecoveryLaunchDecision(
                InstalledRecoveryLaunchRoute.RedirectToStableLauncher,
                target.ExecutablePath);
        }

        // Portable/development builds have no stable root and retain Windows' direct recovery
        // behavior. The private launcher marker is never admitted for those shapes.
        return new InstalledRecoveryLaunchDecision(
            InstalledRecoveryLaunchRoute.ContinueRecovered,
            null);
    }

    private static bool HasExactRecoveryShape(
        IReadOnlyList<string> arguments,
        string recoveryArgument) =>
        arguments.Count == 2
        && string.Equals(
            arguments[0],
            TrayArgument,
            StringComparison.Ordinal)
        && string.Equals(
            arguments[1],
            recoveryArgument,
            StringComparison.Ordinal);

    private static bool IsCurrentPayloadPath(string processExecutablePath)
    {
        string? directory = Path.GetDirectoryName(processExecutablePath);
        return directory is not null
            && string.Equals(
                Path.GetFileName(directory),
                "current",
                StringComparison.OrdinalIgnoreCase);
    }
}
