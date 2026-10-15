namespace WinShot.App.Lifecycle;

internal static class WindowsApplicationRestartPolicy
{
    private const uint ConservativeFlags =
        WinShot.Interop.NativeMethods.RESTART_NO_PATCH
        | WinShot.Interop.NativeMethods.RESTART_NO_REBOOT;

    internal static uint GetFlags(ApplicationDeploymentKind deploymentKind) =>
        deploymentKind is ApplicationDeploymentKind.Packaged
            ? WinShot.Interop.NativeMethods.RESTART_NO_REBOOT
            : ConservativeFlags;
}
