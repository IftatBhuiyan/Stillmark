using WinShot.Licensing;

namespace WinShot.App;

/// <summary>
/// Allows a locally built Debug application to remain usable after the customer trial expires.
/// This is deliberately a compile-time app-host policy, not a licensing mode or runtime switch.
/// </summary>
internal static class LocalDeveloperUsePolicy
{
#if WINSHOT_LOCAL_DEVELOPER_USE
    private static readonly string BinaryMarker =
        "WINSHOT_LOCAL_DEVELOPER_USE_ENTITLEMENT_V1";

    internal const bool IsEnabled = true;
#else
    internal const bool IsEnabled = false;
#endif

    internal static bool AllowsUsage(LicenseStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return IsEnabled || status.IsActive;
    }

    internal static void PreserveBinaryMarker()
    {
#if WINSHOT_LOCAL_DEVELOPER_USE
        GC.KeepAlive(BinaryMarker);
#endif
    }
}
