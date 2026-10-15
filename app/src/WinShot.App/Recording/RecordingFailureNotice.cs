using System.IO;

namespace WinShot.App.Recording;

/// <summary>
/// Stable user-facing recorder failure copy. Callers may pass a recovery path, but only its file
/// name is surfaced; exception and native callback text stays inside privacy-safe diagnostics.
/// </summary>
internal static class RecordingFailureNotice
{
    internal static string StartFailure(string? preservedPath)
    {
        return preservedPath is null
            ? "Recording could not start. Check screen-recording permissions and audio/video " +
              "device availability, then try again."
            : "Recording could not start. A partial recording was preserved as " +
              $"{Path.GetFileName(preservedPath)}.";
    }

    internal static string RecordingFailure(string? preservedPath)
    {
        return preservedPath is null
            ? "Recording stopped because the recorder reported a failure. " +
              "Check diagnostics and try again."
            : "Recording stopped because the recorder reported a failure. " +
              $"A partial recording was preserved as {Path.GetFileName(preservedPath)}.";
    }

    internal static string CompletionFailure(string? preservedPath)
    {
        return preservedPath is null
            ? "Recording completion could not finish normally. " +
              "Check the save location and try again."
            : "Recording completion could not finish normally. The recording was preserved as " +
              $"{Path.GetFileName(preservedPath)}. Check diagnostics for technical details.";
    }

    internal static string GifFailure(string? preservedPath)
    {
        return preservedPath is null
            ? "GIF conversion failed, and Stillmark could not confirm a preserved source " +
              "recording. Check diagnostics for technical details."
            : $"GIF conversion failed. The MP4 was preserved as " +
              $"{Path.GetFileName(preservedPath)}. Check diagnostics for technical details.";
    }
}
