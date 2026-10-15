using System.IO;

namespace WinShot.App.Recording;

internal readonly record struct RecordingRecoveryContext(
    string SaveDirectory,
    IReadOnlyList<RecordingOrphanRoot> Roots,
    IReadOnlySet<string> ActiveWorkspacePaths);

internal enum RecordingRecoveryActionStatus
{
    Recovered,
    Removed,
    Stale,
    Rejected,
    Failed,
    Canceled,
}

internal sealed record RecordingRecoveryActionResult(
    RecordingRecoveryActionStatus Status,
    string Message,
    string? PublishedFileName = null,
    bool SourceRetained = false,
    Exception? Error = null);

/// <summary>
/// Performs user-directed orphan actions. Inventory identities never contain paths; each action
/// resolves one GUID workspace beneath a current known root and validates its exact file snapshot.
/// </summary>
internal sealed class RecordingRecoveryService
{
    private const int MaximumRecoveredBaseNameLength = 180;
    private readonly RecordingOrphanInventoryLimits _limits;

    internal RecordingRecoveryService(RecordingOrphanInventoryLimits? limits = null)
    {
        _limits = limits ?? RecordingOrphanInventoryLimits.Default;
        _limits.Validate();
    }

    internal RecordingOrphanInventoryResult Inventory(
        RecordingRecoveryContext context,
        DateTime utcNow,
        CancellationToken cancellationToken = default) =>
        RecordingOrphanInventory.Scan(
            context.Roots,
            context.ActiveWorkspacePaths,
            utcNow,
            _limits,
            cancellationToken);

    internal RecordingRecoveryActionResult Recover(
        RecordingOrphanIdentity identity,
        RecordingRecoveryContext context,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolveExact(identity, context, utcNow, out ResolvedOrphan orphan))
            return StaleResult();

        try
        {
            string destinationDirectory = PrepareDestination(context.SaveDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            string baseName = BuildRecoveredBaseName(identity.FileName);
            string publishedPath = RecordingOutputOwnership.CopyAndPublish(
                orphan.FilePath,
                destinationDirectory,
                baseName,
                Path.GetExtension(identity.FileName),
                cancellationToken: cancellationToken);

            bool sourceRetained = true;
            if (TryResolveExact(identity, context, utcNow, out ResolvedOrphan current)
                && string.Equals(
                    current.FilePath,
                    orphan.FilePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(current.FilePath);
                    sourceRetained = false;
                    TryDeleteEmptyWorkspace(current.WorkspacePath);
                }
                catch (Exception ex) when (ex is IOException
                                           or UnauthorizedAccessException
                                           or System.Security.SecurityException)
                {
                    sourceRetained = true;
                }
            }

            return new RecordingRecoveryActionResult(
                RecordingRecoveryActionStatus.Recovered,
                sourceRetained
                    ? "A recovered copy was saved. The original workspace was retained because it could not be safely removed."
                    : "The recording was recovered into your captures folder.",
                Path.GetFileName(publishedPath),
                sourceRetained);
        }
        catch (OperationCanceledException)
        {
            return new RecordingRecoveryActionResult(
                RecordingRecoveryActionStatus.Canceled,
                "Recovery was canceled. The original recording was kept.",
                SourceRetained: true);
        }
        catch (Exception ex) when (IsExpectedFileFailure(ex))
        {
            return new RecordingRecoveryActionResult(
                RecordingRecoveryActionStatus.Failed,
                "Stillmark could not recover this item. The original recording was kept.",
                SourceRetained: true,
                Error: ex);
        }
    }

    internal RecordingRecoveryActionResult Remove(
        RecordingOrphanIdentity identity,
        RecordingRecoveryContext context,
        DateTime utcNow,
        bool userConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (!userConfirmed)
        {
            return new RecordingRecoveryActionResult(
                RecordingRecoveryActionStatus.Rejected,
                "Removal requires confirmation. The recovery item was kept.",
                SourceRetained: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolveExact(identity, context, utcNow, out ResolvedOrphan orphan))
            return StaleResult();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveExact(identity, context, utcNow, out ResolvedOrphan current)
                || !string.Equals(
                    current.FilePath,
                    orphan.FilePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return StaleResult();
            }

            File.Delete(current.FilePath);
            TryDeleteEmptyWorkspace(current.WorkspacePath);
            return new RecordingRecoveryActionResult(
                RecordingRecoveryActionStatus.Removed,
                "The selected recovery item was removed.");
        }
        catch (OperationCanceledException)
        {
            return new RecordingRecoveryActionResult(
                RecordingRecoveryActionStatus.Canceled,
                "Removal was canceled. The recovery item was kept.",
                SourceRetained: true);
        }
        catch (Exception ex) when (IsExpectedFileFailure(ex))
        {
            return new RecordingRecoveryActionResult(
                RecordingRecoveryActionStatus.Failed,
                "Stillmark could not remove this item. It was left in place.",
                SourceRetained: true,
                Error: ex);
        }
    }

    private bool TryResolveExact(
        RecordingOrphanIdentity identity,
        RecordingRecoveryContext context,
        DateTime utcNow,
        out ResolvedOrphan orphan)
    {
        orphan = null!;
        if (identity.WorkspaceId == Guid.Empty
            || string.IsNullOrWhiteSpace(identity.FileName)
            || !string.Equals(
                Path.GetFileName(identity.FileName),
                identity.FileName,
                StringComparison.Ordinal)
            || identity.Length < 0
            || !IsCandidateExtension(Path.GetExtension(identity.FileName)))
        {
            return false;
        }

        RecordingOrphanRoot[] matchingRoots = context.Roots
            .Where(root => root.Kind == identity.RootKind)
            .Take(2)
            .ToArray();
        if (matchingRoots.Length != 1)
            return false;

        try
        {
            string rootPath = Path.GetFullPath(matchingRoots[0].Path);
            if (!RecordingOrphanInventory.IsLocalDrivePath(rootPath)
                || !Directory.Exists(rootPath)
                || !RecordingOrphanInventory.HasNoReparsePointComponents(
                    rootPath,
                    File.GetAttributes))
            {
                return false;
            }

            string workspaceName =
                RecordingOutputOwnership.WorkspacePrefix
                + identity.WorkspaceId.ToString("N");
            string workspacePath = Path.GetFullPath(Path.Combine(rootPath, workspaceName));
            if (!IsImmediateChild(workspacePath, rootPath)
                || IsActiveWorkspace(workspacePath, context.ActiveWorkspacePaths)
                || !Directory.Exists(workspacePath)
                || !RecordingOrphanInventory.HasNoReparsePointComponents(
                    workspacePath,
                    File.GetAttributes))
            {
                return false;
            }

            var workspaceInfo = new DirectoryInfo(workspacePath);
            workspaceInfo.Refresh();
            if (!RecordingOrphanInventory.TryParseWorkspaceId(
                    workspaceInfo.Name,
                    out Guid actualWorkspaceId)
                || actualWorkspaceId != identity.WorkspaceId
                || RecordingOrphanInventory.IsRecent(
                    workspaceInfo.LastWriteTimeUtc,
                    utcNow,
                    _limits.MinimumAge))
            {
                return false;
            }

            string filePath = Path.GetFullPath(Path.Combine(workspacePath, identity.FileName));
            if (!IsImmediateChild(filePath, workspacePath)
                || !File.Exists(filePath)
                || !RecordingOrphanInventory.HasNoReparsePointComponents(
                    filePath,
                    File.GetAttributes))
            {
                return false;
            }

            var fileInfo = new FileInfo(filePath);
            fileInfo.Refresh();
            if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0
                || fileInfo.Length != identity.Length
                || fileInfo.LastWriteTimeUtc != identity.LastWriteTimeUtc
                || RecordingOrphanInventory.IsRecent(
                    fileInfo.LastWriteTimeUtc,
                    utcNow,
                    _limits.MinimumAge))
            {
                return false;
            }

            orphan = new ResolvedOrphan(workspacePath, filePath);
            return true;
        }
        catch (Exception ex) when (IsExpectedFileFailure(ex))
        {
            return false;
        }
    }

    private static string PrepareDestination(string saveDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDirectory);
        string fullPath = Path.GetFullPath(saveDirectory);
        if (!RecordingOrphanInventory.IsLocalDrivePath(fullPath))
            throw new IOException("The configured recovery destination is not a local directory.");
        Directory.CreateDirectory(fullPath);
        if (!RecordingOrphanInventory.HasNoReparsePointComponents(
                fullPath,
                File.GetAttributes))
        {
            throw new IOException("The configured recovery destination is not a direct local directory.");
        }
        return fullPath;
    }

    private static string BuildRecoveredBaseName(string fileName)
    {
        string original = Path.GetFileNameWithoutExtension(fileName);
        int maximumOriginalLength = Math.Max(
            1,
            MaximumRecoveredBaseNameLength - "Recovered ".Length);
        if (original.Length > maximumOriginalLength)
            original = original[..maximumOriginalLength].TrimEnd();
        return "Recovered " + original;
    }

    private static bool IsCandidateExtension(string extension) =>
        extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".partial", StringComparison.OrdinalIgnoreCase);

    private static bool IsImmediateChild(string path, string parent) =>
        string.Equals(
            Path.GetDirectoryName(path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)),
            parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsActiveWorkspace(
        string workspacePath,
        IReadOnlySet<string> activeWorkspacePaths)
    {
        foreach (string activePath in activeWorkspacePaths)
        {
            try
            {
                if (string.Equals(
                        Path.GetFullPath(activePath),
                        workspacePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or NotSupportedException
                                       or PathTooLongException)
            {
            }
        }
        return false;
    }

    private static void TryDeleteEmptyWorkspace(string workspacePath)
    {
        try
        {
            Directory.Delete(workspacePath, recursive: false);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            // Never recurse. Unexpected siblings remain untouched for separate review.
        }
    }

    private static bool IsExpectedFileFailure(Exception ex) =>
        ex is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException;

    private static RecordingRecoveryActionResult StaleResult() =>
        new(
            RecordingRecoveryActionStatus.Stale,
            "This item changed or is no longer available. Refresh the list before trying again.",
            SourceRetained: true);

    private sealed record ResolvedOrphan(string WorkspacePath, string FilePath);
}
