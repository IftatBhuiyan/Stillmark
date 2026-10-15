using WinShot.Core.Startup;

namespace WinShot.App.Lifecycle;

internal enum AppLaunchIntent
{
    Interactive,
    Background,
    Recovery,
}

/// <summary>
/// Classifies the complete process argument vector before single-instance handoff. Only exact,
/// installer-owned quiet shapes may avoid showing the already-running dashboard; malformed or
/// future arguments remain visible instead of silently disappearing.
/// </summary>
internal static class AppLaunchIntentPolicy
{
    internal static AppLaunchIntent Classify(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count == 1
            && Is(arguments[0], InstalledRecoveryRedirectPolicy.TrayArgument))
        {
            return AppLaunchIntent.Background;
        }

        if (arguments.Count == 2
            && Is(arguments[0], InstalledRecoveryRedirectPolicy.TrayArgument)
            && (Is(
                    arguments[1],
                    InstalledRecoveryRedirectPolicy.PublicRecoveredArgument)
                || Is(
                    arguments[1],
                    InstalledRecoveryRedirectPolicy.LauncherRecoveredArgument)))
        {
            return AppLaunchIntent.Recovery;
        }

        return AppLaunchIntent.Interactive;
    }

    internal static bool ShouldShowExistingInstance(
        AppLaunchIntent intent) =>
        intent == AppLaunchIntent.Interactive;

    private static bool Is(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
}
