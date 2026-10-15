namespace WinShot.App.Lifecycle;

internal enum StartupRegistrationRoute
{
    Unavailable,
    UnpackagedRunValue,
    PackagedStartupTask,
}

/// <summary>
/// Keeps packaged startup registration separate from the unpackaged HKCU Run-value contract.
/// Ambiguous deployment detection never mutates either registration surface.
/// </summary>
internal static class PackagedStartupRegistrationRoutePolicy
{
    internal static StartupRegistrationRoute Evaluate(
        ApplicationDeploymentKind deploymentKind) =>
        deploymentKind switch
        {
            ApplicationDeploymentKind.Unpackaged =>
                StartupRegistrationRoute.UnpackagedRunValue,
            ApplicationDeploymentKind.Packaged =>
                StartupRegistrationRoute.PackagedStartupTask,
            _ => StartupRegistrationRoute.Unavailable,
        };
}
