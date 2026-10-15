namespace WinShot.Core.Naming;

public static class FileNameTemplate
{
    /// <summary>
    /// Expands {yyyy} {MM} {dd} {HH} {mm} {ss} {ms} tokens and sanitizes the result
    /// into a valid Windows file name (without extension).
    /// </summary>
    public static string Expand(string template, DateTime timestamp)
    {
        string name = template
            .Replace("{yyyy}", timestamp.ToString("yyyy"))
            .Replace("{MM}", timestamp.ToString("MM"))
            .Replace("{dd}", timestamp.ToString("dd"))
            .Replace("{HH}", timestamp.ToString("HH"))
            .Replace("{mm}", timestamp.ToString("mm"))
            .Replace("{ss}", timestamp.ToString("ss"))
            .Replace("{ms}", timestamp.ToString("fff"));

        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');

        name = name.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(name)
            ? "Relic_Screenshot_" + timestamp.ToString("yyyyMMdd_HHmmss")
            : name;
    }

    /// <summary>
    /// Returns an advisory path that does not exist at the instant it is checked. This method does
    /// not reserve the path and is therefore not safe for concurrent writers. New persistence
    /// code should use <see cref="ReserveUniqueFile"/>.
    /// </summary>
    public static string GetUniquePath(string directory, string baseName, string extension)
    {
        string path = Path.Combine(directory, baseName + extension);
        int n = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(directory, $"{baseName} {n}{extension}");
            n++;
        }
        return path;
    }

    /// <summary>
    /// Atomically creates and owns a unique output placeholder using <see cref="FileMode.CreateNew"/>.
    /// Write through the returned lease's stream, then commit to preserve the file.
    /// </summary>
    public static OutputFileReservation ReserveUniqueFile(
        string directory,
        string baseName,
        string extension,
        int maxAttempts = OutputFileReservation.DefaultMaxAttempts)
    {
        return OutputFileReservation.Create(directory, baseName, extension, maxAttempts);
    }
}
