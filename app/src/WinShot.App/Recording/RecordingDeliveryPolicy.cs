using WinShot.Core.Settings;

namespace WinShot.App.Recording;

/// <summary>
/// Decides whether an unsaved recording still needs a transient file for a requested after-action.
/// Kept separate from the native recorder so the setting combinations are regression-testable.
/// </summary>
internal static class RecordingDeliveryPolicy
{
    public static bool NeedsTransientFile(AppSettings settings, bool isGif)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.RecordingCopyFile
            || settings.RecordingShowQuickAccess
            || !isGif && settings.RecordingOpenPlayer;
    }
}
