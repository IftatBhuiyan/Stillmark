using WinShot.App.Recording;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class RecordingRecoveryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "WinShotRecoveryTests",
        Guid.NewGuid().ToString("N"));
    private readonly DateTime _nowUtc =
        new(2026, 7, 28, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Recover_RevalidatesAndPublishesUnderRecoveredName()
    {
        string mediaRoot = Path.Combine(_root, "media");
        string destination = Path.Combine(_root, "captures");
        Directory.CreateDirectory(destination);
        string source = CreateCandidate(mediaRoot, "session.mp4", [1, 2, 3, 4]);
        RecordingRecoveryContext context = Context(mediaRoot, destination);
        var service = new RecordingRecoveryService();
        RecordingOrphanFile candidate =
            Assert.Single(service.Inventory(context, _nowUtc).Files);

        RecordingRecoveryActionResult result =
            service.Recover(candidate.Identity, context, _nowUtc);

        Assert.Equal(RecordingRecoveryActionStatus.Recovered, result.Status);
        Assert.Equal("Recovered session.mp4", result.PublishedFileName);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(
            Path.Combine(destination, result.PublishedFileName!)));
        Assert.False(File.Exists(source));
        Assert.DoesNotContain(_root, candidate.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recover_WhenDestinationFails_PreservesExactSource()
    {
        string mediaRoot = Path.Combine(_root, "media");
        string destinationFile = Path.Combine(_root, "not-a-directory");
        Directory.CreateDirectory(_root);
        File.WriteAllText(destinationFile, "occupied");
        string source = CreateCandidate(mediaRoot, "session.gif", [7, 8, 9]);
        RecordingRecoveryContext context = Context(mediaRoot, destinationFile);
        var service = new RecordingRecoveryService();
        RecordingOrphanFile candidate =
            Assert.Single(service.Inventory(context, _nowUtc).Files);

        RecordingRecoveryActionResult result =
            service.Recover(candidate.Identity, context, _nowUtc);

        Assert.Equal(RecordingRecoveryActionStatus.Failed, result.Status);
        Assert.True(result.SourceRetained);
        Assert.Equal([7, 8, 9], File.ReadAllBytes(source));
    }

    [Fact]
    public void Recover_WhenRecoveredNameExists_DoesNotOverwriteIt()
    {
        string mediaRoot = Path.Combine(_root, "media");
        string destination = Path.Combine(_root, "captures");
        Directory.CreateDirectory(destination);
        string existing = Path.Combine(destination, "Recovered session.mp4");
        File.WriteAllBytes(existing, [9]);
        CreateCandidate(mediaRoot, "session.mp4", [1, 2, 3]);
        RecordingRecoveryContext context = Context(mediaRoot, destination);
        var service = new RecordingRecoveryService();
        RecordingOrphanFile candidate =
            Assert.Single(service.Inventory(context, _nowUtc).Files);

        RecordingRecoveryActionResult result =
            service.Recover(candidate.Identity, context, _nowUtc);

        Assert.Equal(RecordingRecoveryActionStatus.Recovered, result.Status);
        Assert.Equal("Recovered session 2.mp4", result.PublishedFileName);
        Assert.Equal([9], File.ReadAllBytes(existing));
        Assert.Equal(
            [1, 2, 3],
            File.ReadAllBytes(Path.Combine(destination, result.PublishedFileName!)));
    }

    [Fact]
    public void Recover_WhenInventoryIdentityChanged_RejectsStaleItem()
    {
        string mediaRoot = Path.Combine(_root, "media");
        string destination = Path.Combine(_root, "captures");
        Directory.CreateDirectory(destination);
        string source = CreateCandidate(mediaRoot, "session.mp4", [1]);
        RecordingRecoveryContext context = Context(mediaRoot, destination);
        var service = new RecordingRecoveryService();
        RecordingOrphanFile candidate =
            Assert.Single(service.Inventory(context, _nowUtc).Files);
        File.WriteAllBytes(source, [1, 2]);
        File.SetLastWriteTimeUtc(source, _nowUtc.AddHours(-2));

        RecordingRecoveryActionResult result =
            service.Recover(candidate.Identity, context, _nowUtc);

        Assert.Equal(RecordingRecoveryActionStatus.Stale, result.Status);
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.EnumerateFiles(destination));
    }

    [Fact]
    public void Remove_DeletesOnlySelectedFileAndNeverRecursesIntoSibling()
    {
        string mediaRoot = Path.Combine(_root, "media");
        string destination = Path.Combine(_root, "captures");
        Directory.CreateDirectory(destination);
        string source = CreateCandidate(mediaRoot, "session.mp4", [1]);
        string workspace = Path.GetDirectoryName(source)!;
        string sibling = Path.Combine(workspace, "unrelated.txt");
        File.WriteAllText(sibling, "keep");
        MakeOld(workspace, source, sibling);
        RecordingRecoveryContext context = Context(mediaRoot, destination);
        var service = new RecordingRecoveryService();
        RecordingOrphanFile candidate =
            Assert.Single(service.Inventory(context, _nowUtc).Files);

        RecordingRecoveryActionResult result =
            service.Remove(candidate.Identity, context, _nowUtc, userConfirmed: true);

        Assert.Equal(RecordingRecoveryActionStatus.Removed, result.Status);
        Assert.False(File.Exists(source));
        Assert.Equal("keep", File.ReadAllText(sibling));
        Assert.True(Directory.Exists(workspace));
    }

    [Fact]
    public void PartialCandidate_IsExplicitlyLabeledUncertain()
    {
        string mediaRoot = Path.Combine(_root, "media");
        string destination = Path.Combine(_root, "captures");
        Directory.CreateDirectory(destination);
        CreateCandidate(mediaRoot, "unfinished.partial", [1]);
        var service = new RecordingRecoveryService();

        RecordingOrphanFile candidate =
            Assert.Single(service.Inventory(Context(mediaRoot, destination), _nowUtc).Files);

        Assert.True(candidate.IsPartial);
        Assert.Contains("incomplete", candidate.IntegrityLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, candidate.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remove_RejectsFilenameTraversalWithoutTouchingWorkspace()
    {
        string mediaRoot = Path.Combine(_root, "media");
        string destination = Path.Combine(_root, "captures");
        Directory.CreateDirectory(destination);
        string source = CreateCandidate(mediaRoot, "session.mp4", [1]);
        RecordingRecoveryContext context = Context(mediaRoot, destination);
        var service = new RecordingRecoveryService();
        RecordingOrphanFile candidate =
            Assert.Single(service.Inventory(context, _nowUtc).Files);
        RecordingOrphanIdentity malicious = candidate.Identity with
        {
            FileName = @"..\session.mp4",
        };

        RecordingRecoveryActionResult result =
            service.Remove(malicious, context, _nowUtc, userConfirmed: true);

        Assert.Equal(RecordingRecoveryActionStatus.Stale, result.Status);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void Remove_WithoutExplicitConfirmation_RejectsAndPreservesSource()
    {
        string mediaRoot = Path.Combine(_root, "media");
        string destination = Path.Combine(_root, "captures");
        Directory.CreateDirectory(destination);
        string source = CreateCandidate(mediaRoot, "session.mp4", [1]);
        RecordingRecoveryContext context = Context(mediaRoot, destination);
        var service = new RecordingRecoveryService();
        RecordingOrphanFile candidate =
            Assert.Single(service.Inventory(context, _nowUtc).Files);

        RecordingRecoveryActionResult result =
            service.Remove(candidate.Identity, context, _nowUtc, userConfirmed: false);

        Assert.Equal(RecordingRecoveryActionStatus.Rejected, result.Status);
        Assert.True(result.SourceRetained);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void Recover_RejectsWorkspaceThatBecameActiveAfterInventory()
    {
        string mediaRoot = Path.Combine(_root, "media");
        string destination = Path.Combine(_root, "captures");
        Directory.CreateDirectory(destination);
        string source = CreateCandidate(mediaRoot, "session.mp4", [1]);
        RecordingRecoveryContext inventoryContext = Context(mediaRoot, destination);
        var service = new RecordingRecoveryService();
        RecordingOrphanFile candidate =
            Assert.Single(service.Inventory(inventoryContext, _nowUtc).Files);
        RecordingRecoveryContext activeContext = inventoryContext with
        {
            ActiveWorkspacePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetDirectoryName(source)!,
            },
        };

        RecordingRecoveryActionResult result =
            service.Recover(candidate.Identity, activeContext, _nowUtc);

        Assert.Equal(RecordingRecoveryActionStatus.Stale, result.Status);
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.EnumerateFiles(destination));
    }

    [Fact]
    public void Inventory_HonorsPreCanceledRequest()
    {
        string mediaRoot = Path.Combine(_root, "media");
        Directory.CreateDirectory(mediaRoot);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new RecordingRecoveryService().Inventory(
                Context(mediaRoot, Path.Combine(_root, "captures")),
                _nowUtc,
                cancellation.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private RecordingRecoveryContext Context(string mediaRoot, string destination) =>
        new(
            destination,
            [new RecordingOrphanRoot(RecordingOrphanRootKind.MediaTemp, mediaRoot)],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private string CreateCandidate(string root, string fileName, byte[] contents)
    {
        string workspace = Path.Combine(
            root,
            RecordingOutputOwnership.WorkspacePrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        string source = Path.Combine(workspace, fileName);
        File.WriteAllBytes(source, contents);
        MakeOld(workspace, source);
        return source;
    }

    private void MakeOld(string workspace, params string[] files)
    {
        foreach (string file in files)
            File.SetLastWriteTimeUtc(file, _nowUtc.AddHours(-1));
        Directory.SetLastWriteTimeUtc(workspace, _nowUtc.AddHours(-1));
    }
}
