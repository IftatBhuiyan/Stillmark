using WinShot.App.Recording;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class RecordingOrphanInventoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "WinShotOrphanInventoryTests",
        Guid.NewGuid().ToString("N"));
    private readonly DateTime _nowUtc =
        new(2026, 7, 28, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Scan_ReportsOnlyBoundedMetadataWithoutDirectoryPath()
    {
        string workspace = CreateWorkspace();
        string media = Path.Combine(workspace, "capture.mp4");
        File.WriteAllBytes(media, [1, 2, 3, 4]);
        MakeOld(workspace, media);

        RecordingOrphanInventoryResult result = Scan();

        RecordingOrphanFile item = Assert.Single(result.Files);
        Assert.Equal(RecordingOrphanRootKind.MediaTemp, item.RootKind);
        Assert.NotEqual(Guid.Empty, item.Identity.WorkspaceId);
        Assert.Equal("capture.mp4", item.FileName);
        Assert.Equal(".mp4", item.Extension);
        Assert.Equal(4, item.Length);
        Assert.Equal(TimeSpan.FromHours(1), item.Age);
        Assert.DoesNotContain(_root, item.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Scan_SkipsRecentActiveUnexpectedAndNestedContent()
    {
        string active = CreateWorkspace();
        string activeFile = Path.Combine(active, "active.mp4");
        File.WriteAllBytes(activeFile, [1]);
        MakeOld(active, activeFile);

        string recent = CreateWorkspace();
        string recentFile = Path.Combine(recent, "recent.gif");
        File.WriteAllBytes(recentFile, [2]);
        File.SetLastWriteTimeUtc(recentFile, _nowUtc.AddMinutes(-1));
        Directory.SetLastWriteTimeUtc(recent, _nowUtc.AddMinutes(-1));

        string unexpected = Path.Combine(
            _root,
            RecordingOutputOwnership.WorkspacePrefix + "not-a-guid");
        Directory.CreateDirectory(unexpected);
        File.WriteAllBytes(Path.Combine(unexpected, "unexpected.mp4"), [3]);
        Directory.SetLastWriteTimeUtc(unexpected, _nowUtc.AddHours(-1));

        string nestedWorkspace = CreateWorkspace();
        string nestedDirectory = Path.Combine(nestedWorkspace, "nested");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllBytes(Path.Combine(nestedDirectory, "nested.mp4"), [4]);
        Directory.SetLastWriteTimeUtc(nestedDirectory, _nowUtc.AddHours(-1));
        Directory.SetLastWriteTimeUtc(nestedWorkspace, _nowUtc.AddHours(-1));

        RecordingOrphanInventoryResult result = RecordingOrphanInventory.Scan(
            [new RecordingOrphanRoot(RecordingOrphanRootKind.MediaTemp, _root)],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { active },
            _nowUtc);

        Assert.Empty(result.Files);
        Assert.True(result.WorkspacesSkipped >= 3);
    }

    [Fact]
    public void Scan_EnforcesFileCountAndSizeBudgets()
    {
        string workspace = CreateWorkspace();
        string first = Path.Combine(workspace, "first.mp4");
        string second = Path.Combine(workspace, "second.gif");
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [2]);
        MakeOld(workspace, first, second);
        var limits = new RecordingOrphanInventoryLimits(
            MaximumRoots: 1,
            MaximumWorkspaces: 2,
            MaximumFilesPerWorkspace: 4,
            MaximumReportedFiles: 1,
            MaximumFileBytes: 1,
            MaximumTotalBytes: 1,
            MinimumAge: TimeSpan.FromMinutes(10));

        RecordingOrphanInventoryResult result = RecordingOrphanInventory.Scan(
            [new RecordingOrphanRoot(RecordingOrphanRootKind.MediaTemp, _root)],
            activeWorkspacePaths: null,
            utcNow: _nowUtc,
            limits: limits);

        Assert.Single(result.Files);
        Assert.True(result.Truncated);
        Assert.True(result.FilesSkipped >= 1);
    }

    [Fact]
    public void Scan_SkipsFileLargerThanConfiguredMetadataBudget()
    {
        string workspace = CreateWorkspace();
        string media = Path.Combine(workspace, "large.mp4");
        using (FileStream stream = File.Create(media))
            stream.SetLength(3);
        MakeOld(workspace, media);
        var limits = new RecordingOrphanInventoryLimits(
            MaximumRoots: 1,
            MaximumWorkspaces: 2,
            MaximumFilesPerWorkspace: 2,
            MaximumReportedFiles: 2,
            MaximumFileBytes: 2,
            MaximumTotalBytes: 2,
            MinimumAge: TimeSpan.FromMinutes(10));

        RecordingOrphanInventoryResult result = RecordingOrphanInventory.Scan(
            [new RecordingOrphanRoot(RecordingOrphanRootKind.MediaTemp, _root)],
            activeWorkspacePaths: null,
            utcNow: _nowUtc,
            limits: limits);

        Assert.Empty(result.Files);
        Assert.Equal(1, result.FilesSkipped);
    }

    [Fact]
    public void ReparsePointInRootComponent_IsRejectedBeforeEnumeration()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        string candidate = Path.Combine(root, "safe", "linked", "recordings");

        bool safe = RecordingOrphanInventory.HasNoReparsePointComponents(
            candidate,
            path => Path.GetFileName(path).Equals(
                    "linked",
                    StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : FileAttributes.Directory);

        Assert.False(safe);
    }

    [Fact]
    public void NetworkRoot_IsRejectedWithoutEnumeration()
    {
        RecordingOrphanInventoryResult result = RecordingOrphanInventory.Scan(
            [new RecordingOrphanRoot(
                RecordingOrphanRootKind.SaveDirectory,
                @"\\unreachable.invalid\recordings")],
            activeWorkspacePaths: null,
            utcNow: _nowUtc);

        Assert.Empty(result.Files);
        Assert.Equal(0, result.WorkspacesInspected);
        Assert.Equal(1, result.WorkspacesSkipped);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private RecordingOrphanInventoryResult Scan()
    {
        return RecordingOrphanInventory.Scan(
            [new RecordingOrphanRoot(RecordingOrphanRootKind.MediaTemp, _root)],
            activeWorkspacePaths: null,
            utcNow: _nowUtc);
    }

    private string CreateWorkspace()
    {
        Directory.CreateDirectory(_root);
        string workspace = Path.Combine(
            _root,
            RecordingOutputOwnership.WorkspacePrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    private void MakeOld(string workspace, params string[] files)
    {
        foreach (string file in files)
            File.SetLastWriteTimeUtc(file, _nowUtc.AddHours(-1));
        Directory.SetLastWriteTimeUtc(workspace, _nowUtc.AddHours(-1));
    }
}
