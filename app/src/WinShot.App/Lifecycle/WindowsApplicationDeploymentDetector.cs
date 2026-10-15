using System.Text;

namespace WinShot.App.Lifecycle;

internal enum ApplicationDeploymentKind
{
    Unknown,
    Unpackaged,
    Packaged,
}

internal interface ICurrentPackageFullNameNativeBoundary
{
    int GetCurrentPackageFullName(
        ref uint packageFullNameLength,
        StringBuilder? packageFullName);
}

internal sealed class CurrentPackageFullNameNativeBoundary
    : ICurrentPackageFullNameNativeBoundary
{
    public int GetCurrentPackageFullName(
        ref uint packageFullNameLength,
        StringBuilder? packageFullName) =>
        WinShot.Interop.NativeMethods.GetCurrentPackageFullName(
            ref packageFullNameLength,
            packageFullName);
}

/// <summary>
/// Classifies only whether the current process has package identity. The probe intentionally never
/// requests, retains, or exposes the package full name.
/// </summary>
internal static class WindowsApplicationDeploymentDetector
{
    // PACKAGE_FULL_NAME_MAX_LENGTH is 127 characters, excluding the terminating null.
    internal const uint MaximumPackageFullNameBufferCharacters = 128;

    internal static ApplicationDeploymentKind Detect() =>
        Detect(new CurrentPackageFullNameNativeBoundary());

    internal static ApplicationDeploymentKind Detect(
        ICurrentPackageFullNameNativeBoundary native)
    {
        ArgumentNullException.ThrowIfNull(native);

        try
        {
            uint requiredCharacters = 0;
            int result = native.GetCurrentPackageFullName(
                ref requiredCharacters,
                packageFullName: null);

            if (result == WinShot.Interop.NativeMethods.APPMODEL_ERROR_NO_PACKAGE)
            {
                return requiredCharacters == 0
                    ? ApplicationDeploymentKind.Unpackaged
                    : ApplicationDeploymentKind.Unknown;
            }

            if (result == WinShot.Interop.NativeMethods.ERROR_INSUFFICIENT_BUFFER)
            {
                return requiredCharacters is >= 2
                        and <= MaximumPackageFullNameBufferCharacters
                    ? ApplicationDeploymentKind.Packaged
                    : ApplicationDeploymentKind.Unknown;
            }

            return ApplicationDeploymentKind.Unknown;
        }
        catch
        {
            // Package detection changes recovery policy only. Any unavailable or ambiguous native
            // result keeps the conservative unpackaged restart flags.
            return ApplicationDeploymentKind.Unknown;
        }
    }
}
