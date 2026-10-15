using System.IO;

namespace WinShot.App;

/// <summary>
/// Retention for files WinShot creates only as clipboard/drag backing or media staging. The
/// service never recurses and only deletes canonical immediate children with approved extensions.
/// </summary>
internal static class OwnedTransientFileRetention
{
    private const int ClipboardMaxFiles = 15;
    private static readonly TimeSpan ClipboardMaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan PartialMaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan MediaMaxAge = TimeSpan.FromDays(7);
    private static readonly IReadOnlySet<string> ClipboardExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png" };
    private static readonly IReadOnlySet<string> PartialExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".partial" };
    private static readonly IReadOnlySet<string> MediaExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".gif" };

    internal static string ClipboardDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinShot",
        "Clipboard");

    internal static string MediaDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinShot",
        "tmp");

    internal static void PurgeAtStartup(DateTime utcNow)
    {
        Purge(
            ClipboardDirectory,
            ClipboardExtensions,
            ClipboardMaxAge,
            ClipboardMaxFiles,
            utcNow);
        Purge(MediaDirectory, PartialExtensions, PartialMaxAge, maxFiles: 24, utcNow);
        Purge(MediaDirectory, MediaExtensions, MediaMaxAge, maxFiles: 20, utcNow);
    }

    internal static void PurgeClipboard(DateTime utcNow) =>
        Purge(
            ClipboardDirectory,
            ClipboardExtensions,
            ClipboardMaxAge,
            ClipboardMaxFiles - 1,
            utcNow);

    internal static void PurgeMedia(DateTime utcNow)
    {
        Purge(MediaDirectory, PartialExtensions, PartialMaxAge, maxFiles: 24, utcNow);
        Purge(MediaDirectory, MediaExtensions, MediaMaxAge, maxFiles: 19, utcNow);
    }

    internal static void Purge(
        string directory,
        IReadOnlySet<string> allowedExtensions,
        TimeSpan maxAge,
        int maxFiles,
        DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(allowedExtensions);
        if (maxAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxAge));
        if (maxFiles < 0)
            throw new ArgumentOutOfRangeException(nameof(maxFiles));

        string ownedDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(ownedDirectory))
            return;

        List<FileInfo> candidates;
        try
        {
            candidates = new DirectoryInfo(ownedDirectory)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(file => IsOwnedCandidate(file, ownedDirectory, allowedExtensions))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        for (int index = 0; index < candidates.Count; index++)
        {
            FileInfo file = candidates[index];
            bool beyondCount = index >= maxFiles;
            bool beyondAge = utcNow - file.LastWriteTimeUtc > maxAge;
            if (!beyondCount && !beyondAge)
                continue;

            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Clipboard consumers, converters, or AV may still hold the file. Retry later.
            }
        }
    }

    private static bool IsOwnedCandidate(
        FileInfo file,
        string ownedDirectory,
        IReadOnlySet<string> allowedExtensions)
    {
        try
        {
            string fullPath = Path.GetFullPath(file.FullName);
            string? parent = Path.GetDirectoryName(fullPath);
            return string.Equals(parent, ownedDirectory, StringComparison.OrdinalIgnoreCase)
                && allowedExtensions.Contains(file.Extension);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
