using System.IO;

namespace WinShot.App.Recording;

internal enum RecordingOrphanRootKind
{
    MediaTemp,
    SaveDirectory,
}

internal readonly record struct RecordingOrphanRoot(
    RecordingOrphanRootKind Kind,
    string Path);

internal readonly record struct RecordingOrphanFile(
    RecordingOrphanIdentity Identity,
    string FileName,
    string Extension,
    long Length,
    DateTime LastWriteTimeUtc,
    TimeSpan Age)
{
    internal RecordingOrphanRootKind RootKind => Identity.RootKind;

    internal bool IsPartial =>
        Extension.Equals(".partial", StringComparison.OrdinalIgnoreCase);

    internal string IntegrityLabel => IsPartial
        ? "Partial or incomplete - it may not be playable."
        : "Recovery candidate - media integrity has not been verified.";
}

/// <summary>
/// Opaque action identity. It deliberately carries no directory path: actions resolve the GUID
/// beneath the current configured root and revalidate the exact metadata before touching disk.
/// </summary>
internal readonly record struct RecordingOrphanIdentity(
    RecordingOrphanRootKind RootKind,
    Guid WorkspaceId,
    string FileName,
    long Length,
    DateTime LastWriteTimeUtc);

internal sealed record RecordingOrphanInventoryResult(
    IReadOnlyList<RecordingOrphanFile> Files,
    int WorkspacesInspected,
    int WorkspacesSkipped,
    int FilesSkipped,
    bool Truncated);

internal readonly record struct RecordingOrphanInventoryLimits(
    int MaximumRoots,
    int MaximumWorkspaces,
    int MaximumFilesPerWorkspace,
    int MaximumReportedFiles,
    long MaximumFileBytes,
    long MaximumTotalBytes,
    TimeSpan MinimumAge)
{
    internal static RecordingOrphanInventoryLimits Default => new(
        MaximumRoots: 2,
        MaximumWorkspaces: 32,
        MaximumFilesPerWorkspace: 4,
        MaximumReportedFiles: 32,
        MaximumFileBytes: 32L * 1024 * 1024 * 1024,
        MaximumTotalBytes: 64L * 1024 * 1024 * 1024,
        MinimumAge: TimeSpan.FromMinutes(10));

    internal void Validate()
    {
        if (MaximumRoots is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(MaximumRoots));
        if (MaximumWorkspaces is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(MaximumWorkspaces));
        if (MaximumFilesPerWorkspace is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(MaximumFilesPerWorkspace));
        if (MaximumReportedFiles is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(MaximumReportedFiles));
        if (MaximumFileBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumFileBytes));
        if (MaximumTotalBytes < MaximumFileBytes)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes));
        if (MinimumAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MinimumAge));
    }
}

/// <summary>
/// Passively inventories bounded metadata for files stranded in WinShot's exact recording
/// workspaces. It never recurses, follows links, opens media content, publishes, or deletes files.
/// Returned records intentionally contain no directory paths.
/// </summary>
internal static class RecordingOrphanInventory
{
    private const int MaximumPathComponents = 64;
    private static readonly IReadOnlySet<string> CandidateExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4",
            ".gif",
            ".partial",
        };

    internal static RecordingOrphanInventoryResult Scan(
        IEnumerable<RecordingOrphanRoot> roots,
        IReadOnlySet<string>? activeWorkspacePaths,
        DateTime utcNow,
        RecordingOrphanInventoryLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        RecordingOrphanInventoryLimits policy =
            limits ?? RecordingOrphanInventoryLimits.Default;
        policy.Validate();

        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (activeWorkspacePaths is not null)
        {
            foreach (string path in activeWorkspacePaths)
            {
                try
                {
                    active.Add(Path.GetFullPath(path));
                }
                catch (Exception ex) when (ex is ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException)
                {
                }
            }
        }

        var files = new List<RecordingOrphanFile>();
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int rootsVisited = 0;
        int inspected = 0;
        int workspacesSkipped = 0;
        int filesSkipped = 0;
        long totalBytes = 0;
        bool truncated = false;

        foreach (RecordingOrphanRoot root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rootsVisited++;
            if (rootsVisited > policy.MaximumRoots)
            {
                truncated = true;
                break;
            }

            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(root.Path);
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or NotSupportedException
                                       or PathTooLongException)
            {
                workspacesSkipped++;
                continue;
            }
            if (!seenRoots.Add(fullRoot)
                || !IsLocalDrivePath(fullRoot)
                || !HasNoReparsePointComponents(fullRoot, File.GetAttributes)
                || !Directory.Exists(fullRoot))
            {
                workspacesSkipped++;
                continue;
            }

            IEnumerable<DirectoryInfo> candidates;
            try
            {
                candidates = new DirectoryInfo(fullRoot)
                    .EnumerateDirectories(
                        RecordingOutputOwnership.WorkspacePrefix + "*",
                        SearchOption.TopDirectoryOnly);
                foreach (DirectoryInfo workspace in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (inspected == policy.MaximumWorkspaces)
                    {
                        truncated = true;
                        break;
                    }
                    inspected++;

                    if (!IsExactWorkspaceName(workspace.Name)
                        || active.Contains(workspace.FullName)
                        || IsReparsePoint(workspace)
                        || IsRecent(workspace.LastWriteTimeUtc, utcNow, policy.MinimumAge))
                    {
                        workspacesSkipped++;
                        continue;
                    }

                    FileInfo[] workspaceFiles;
                    try
                    {
                        workspaceFiles = workspace
                            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                            .Take(policy.MaximumFilesPerWorkspace + 1)
                            .ToArray();
                    }
                    catch (Exception ex) when (ex is IOException
                                               or UnauthorizedAccessException
                                               or System.Security.SecurityException)
                    {
                        workspacesSkipped++;
                        continue;
                    }
                    if (workspaceFiles.Length > policy.MaximumFilesPerWorkspace)
                    {
                        truncated = true;
                        filesSkipped += workspaceFiles.Length;
                        continue;
                    }

                    foreach (FileInfo file in workspaceFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (files.Count == policy.MaximumReportedFiles)
                        {
                            truncated = true;
                            filesSkipped++;
                            continue;
                        }

                        try
                        {
                            if (IsReparsePoint(file)
                                || !CandidateExtensions.Contains(file.Extension)
                                || IsRecent(file.LastWriteTimeUtc, utcNow, policy.MinimumAge)
                                || file.Length < 0
                                || file.Length > policy.MaximumFileBytes
                                || totalBytes > policy.MaximumTotalBytes - file.Length)
                            {
                                filesSkipped++;
                                continue;
                            }

                            if (!TryParseWorkspaceId(workspace.Name, out Guid workspaceId))
                            {
                                filesSkipped++;
                                continue;
                            }

                            files.Add(new RecordingOrphanFile(
                                new RecordingOrphanIdentity(
                                    root.Kind,
                                    workspaceId,
                                    file.Name,
                                    file.Length,
                                    file.LastWriteTimeUtc),
                                file.Name,
                                file.Extension,
                                file.Length,
                                file.LastWriteTimeUtc,
                                utcNow - file.LastWriteTimeUtc));
                            totalBytes += file.Length;
                        }
                        catch (Exception ex) when (ex is IOException
                                                   or UnauthorizedAccessException
                                                   or System.Security.SecurityException)
                        {
                            filesSkipped++;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or System.Security.SecurityException)
            {
                workspacesSkipped++;
            }

            if (inspected == policy.MaximumWorkspaces)
            {
                truncated = true;
                break;
            }
        }

        return new RecordingOrphanInventoryResult(
            files,
            inspected,
            workspacesSkipped,
            filesSkipped,
            truncated);
    }

    internal static bool HasNoReparsePointComponents(
        string path,
        Func<string, FileAttributes> getAttributes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(getAttributes);

        try
        {
            string fullPath = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                return false;
            string[] components = Path.GetRelativePath(root, fullPath).Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            if (components.Length == 0 || components.Length > MaximumPathComponents)
                return false;

            string current = root;
            foreach (string component in components)
            {
                current = Path.Combine(current, component);
                FileAttributes attributes = getAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static bool TryParseWorkspaceId(string name, out Guid workspaceId)
    {
        workspaceId = default;
        if (!name.StartsWith(
                RecordingOutputOwnership.WorkspacePrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        ReadOnlySpan<char> id = name.AsSpan(
            RecordingOutputOwnership.WorkspacePrefix.Length);
        return Guid.TryParseExact(id, "N", out workspaceId);
    }

    private static bool IsExactWorkspaceName(string name) =>
        TryParseWorkspaceId(name, out _);

    internal static bool IsLocalDrivePath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        string? root = Path.GetPathRoot(path);
        return root is { Length: >= 3 }
            && root[1] == ':'
            && (root[2] == Path.DirectorySeparatorChar
                || root[2] == Path.AltDirectorySeparatorChar);
    }

    private static bool IsReparsePoint(FileSystemInfo entry) =>
        (entry.Attributes & FileAttributes.ReparsePoint) != 0;

    internal static bool IsRecent(
        DateTime lastWriteTimeUtc,
        DateTime utcNow,
        TimeSpan minimumAge) =>
        lastWriteTimeUtc > utcNow || utcNow - lastWriteTimeUtc < minimumAge;
}
