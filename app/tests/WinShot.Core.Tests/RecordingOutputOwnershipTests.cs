using WinShot.App.Recording;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class RecordingOutputOwnershipTests
{
    [Fact]
    public void Publish_SkipsExistingNamesWithoutOverwritingThem()
    {
        string directory = CreateTestDirectory();
        try
        {
            string existing = Path.Combine(directory, "capture.mp4");
            File.WriteAllText(existing, "existing");
            var output = RecordingOutputOwnership.Create(
                directory,
                "capture",
                ".mp4",
                maxAttempts: 3);
            File.WriteAllText(output.NativePath, "new recording");

            string published = output.Publish();

            Assert.Equal(Path.Combine(directory, "capture 2.mp4"), published);
            Assert.Equal("existing", File.ReadAllText(existing));
            Assert.Equal("new recording", File.ReadAllText(published));
            Assert.False(Directory.Exists(output.WorkspaceDirectory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentPublish_ProducesDistinctCompleteFiles()
    {
        string directory = CreateTestDirectory();
        try
        {
            var first = RecordingOutputOwnership.Create(directory, "capture", ".mp4");
            var second = RecordingOutputOwnership.Create(directory, "capture", ".mp4");
            File.WriteAllText(first.NativePath, "first");
            File.WriteAllText(second.NativePath, "second");

            string[] paths = await Task.WhenAll(
                Task.Run(first.Publish),
                Task.Run(second.Publish));

            Assert.Equal(2, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(
                ["first", "second"],
                paths.Select(File.ReadAllText).OrderBy(value => value).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Publish_WhenCollisionBudgetIsExhausted_PreservesNativeOutput()
    {
        string directory = CreateTestDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "capture.mp4"), "one");
            File.WriteAllText(Path.Combine(directory, "capture 2.mp4"), "two");
            var output = RecordingOutputOwnership.Create(
                directory,
                "capture",
                ".mp4",
                maxAttempts: 2);
            File.WriteAllText(output.NativePath, "recoverable");

            IOException error = Assert.Throws<IOException>(output.Publish);

            Assert.Contains("2 name collisions", error.Message);
            Assert.Equal("one", File.ReadAllText(Path.Combine(directory, "capture.mp4")));
            Assert.Equal("two", File.ReadAllText(Path.Combine(directory, "capture 2.mp4")));
            Assert.Equal("recoverable", File.ReadAllText(output.NativePath));
            Assert.True(Directory.Exists(output.WorkspaceDirectory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Create_WhenWorkspaceNamesCollide_RetriesWithinBound()
    {
        string directory = CreateTestDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "occupied"));

            var output = RecordingOutputOwnership.Create(
                directory,
                "capture",
                ".mp4",
                maxAttempts: 2,
                attempt => attempt == 1 ? "occupied" : "owned");

            Assert.Equal(Path.Combine(directory, "owned"), output.WorkspaceDirectory);
            output.DeleteOwnedOutput();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Create_WhenWorkspaceBudgetIsExhausted_DoesNotClaimExistingDirectory()
    {
        string directory = CreateTestDirectory();
        try
        {
            string occupied = Path.Combine(directory, "occupied");
            Directory.CreateDirectory(occupied);
            string foreign = Path.Combine(occupied, "foreign.txt");
            File.WriteAllText(foreign, "keep");

            Assert.Throws<IOException>(() => RecordingOutputOwnership.Create(
                directory,
                "capture",
                ".mp4",
                maxAttempts: 2,
                _ => "occupied"));

            Assert.Equal("keep", File.ReadAllText(foreign));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DeleteOwnedOutput_DoesNotDeleteUnexpectedWorkspaceContent()
    {
        string directory = CreateTestDirectory();
        try
        {
            var output = RecordingOutputOwnership.Create(directory, "capture", ".mp4");
            File.WriteAllText(output.NativePath, "owned");
            string foreign = Path.Combine(output.WorkspaceDirectory, "foreign.txt");
            File.WriteAllText(foreign, "keep");

            Assert.Throws<IOException>(output.DeleteOwnedOutput);

            Assert.False(File.Exists(output.NativePath));
            Assert.Equal("keep", File.ReadAllText(foreign));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Publish_DoesNotRelabelSuccessWhenUnexpectedWorkspaceContentPreventsCleanup()
    {
        string directory = CreateTestDirectory();
        try
        {
            var output = RecordingOutputOwnership.Create(directory, "capture", ".mp4");
            File.WriteAllText(output.NativePath, "recording");
            string foreign = Path.Combine(output.WorkspaceDirectory, "foreign.txt");
            File.WriteAllText(foreign, "keep");

            string published = output.Publish();

            Assert.Equal("recording", File.ReadAllText(published));
            Assert.Equal("keep", File.ReadAllText(foreign));
            Assert.True(output.IsPublished);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("com1.anything")]
    public void Create_RejectsReservedWindowsDeviceNames(string baseName)
    {
        string directory = CreateTestDirectory();
        try
        {
            Assert.Throws<ArgumentException>(
                () => RecordingOutputOwnership.Create(directory, baseName, ".mp4"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CopyAndPublish_PreservesSourceAndSkipsExistingDestination()
    {
        string sourceDirectory = CreateTestDirectory();
        string destinationDirectory = CreateTestDirectory();
        try
        {
            string source = Path.Combine(sourceDirectory, "capture.mp4");
            File.WriteAllBytes(source, [1, 2, 3, 4, 5]);
            string existing = Path.Combine(destinationDirectory, "capture.mp4");
            File.WriteAllBytes(existing, [9]);

            string published = RecordingOutputOwnership.CopyAndPublish(
                source,
                destinationDirectory,
                "capture",
                ".mp4");

            Assert.Equal(Path.Combine(destinationDirectory, "capture 2.mp4"), published);
            Assert.Equal([9], File.ReadAllBytes(existing));
            Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(published));
            Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(source));
        }
        finally
        {
            Directory.Delete(sourceDirectory, recursive: true);
            Directory.Delete(destinationDirectory, recursive: true);
        }
    }

    [Fact]
    public void CopyAndPublish_WhenAlreadyCanceled_PreservesSourceAndCreatesNoWorkspace()
    {
        string sourceDirectory = CreateTestDirectory();
        string destinationDirectory = CreateTestDirectory();
        try
        {
            string source = Path.Combine(sourceDirectory, "capture.mp4");
            File.WriteAllBytes(source, [1, 2, 3]);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                RecordingOutputOwnership.CopyAndPublish(
                    source,
                    destinationDirectory,
                    "capture",
                    ".mp4",
                    cancellationToken: cancellation.Token));

            Assert.Equal([1, 2, 3], File.ReadAllBytes(source));
            Assert.Empty(Directory.EnumerateFileSystemEntries(destinationDirectory));
        }
        finally
        {
            Directory.Delete(sourceDirectory, recursive: true);
            Directory.Delete(destinationDirectory, recursive: true);
        }
    }

    private static string CreateTestDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "WinShot.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
