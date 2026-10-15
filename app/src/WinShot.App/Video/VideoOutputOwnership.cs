using System.IO;

namespace WinShot.App.Video;

/// <summary>Best-effort bounded cleanup for completed outputs rejected as stale by the editor.</summary>
internal static class VideoOutputOwnership
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);

    public static async Task TryDeleteStaleOutputAsync(string sourcePath, string outputPath)
    {
        if (!IsGeneratedEditedOutput(sourcePath, outputPath))
            return;

        try
        {
            await Task.Run(() =>
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
            }).WaitAsync(CleanupTimeout);
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or OperationCanceledException
            or TimeoutException)
        {
            // Cleanup must never risk the source or replace the superseded operation's outcome.
        }
    }

    internal static bool IsGeneratedEditedOutput(string sourcePath, string outputPath)
    {
        try
        {
            string source = Path.GetFullPath(sourcePath);
            string output = Path.GetFullPath(outputPath);
            if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetExtension(output), ".mp4", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Path.GetDirectoryName(source),
                    Path.GetDirectoryName(output),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string expectedPrefix = Path.GetFileNameWithoutExtension(source) + " (edited)";
            string outputName = Path.GetFileNameWithoutExtension(output);
            if (outputName.Equals(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                return true;

            string collisionPrefix = expectedPrefix + " (";
            if (!outputName.StartsWith(collisionPrefix, StringComparison.OrdinalIgnoreCase)
                || !outputName.EndsWith(')'))
            {
                return false;
            }

            ReadOnlySpan<char> suffix = outputName.AsSpan(
                collisionPrefix.Length,
                outputName.Length - collisionPrefix.Length - 1);
            return int.TryParse(suffix, out int collisionNumber) && collisionNumber >= 2;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
