namespace WinShot.App.Lifecycle;

internal enum ApplicationServicingOwner
{
    None,
    UnpackagedHost,
    WindowsPackagePlatform,
}

internal readonly record struct ApplicationServicingDecision(
    ApplicationServicingOwner Owner,
    bool AllowCustomUpdateHandoff,
    bool AllowStableLauncherRouting);

/// <summary>
/// Selects exactly one servicing authority. A packaged process must never mutate or launch an
/// updater from its Windows-managed payload; an ambiguous deployment is denied both paths.
/// </summary>
internal static class ApplicationServicingPolicy
{
    internal static ApplicationServicingDecision Evaluate(
        ApplicationDeploymentKind deploymentKind) =>
        deploymentKind switch
        {
            ApplicationDeploymentKind.Unpackaged =>
                new ApplicationServicingDecision(
                    ApplicationServicingOwner.UnpackagedHost,
                    AllowCustomUpdateHandoff: true,
                    AllowStableLauncherRouting: true),
            ApplicationDeploymentKind.Packaged =>
                new ApplicationServicingDecision(
                    ApplicationServicingOwner.WindowsPackagePlatform,
                    AllowCustomUpdateHandoff: false,
                    AllowStableLauncherRouting: false),
            _ => new ApplicationServicingDecision(
                ApplicationServicingOwner.None,
                AllowCustomUpdateHandoff: false,
                AllowStableLauncherRouting: false),
        };
}
