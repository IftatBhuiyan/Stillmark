using WinShot.App;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class OwnedTransientFileRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "WinShotRetentionTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Purge_DeletesOldAndBeyondCountApprovedFilesOnly()
    {
        Directory.CreateDirectory(_root);
        DateTime now = new(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        string oldPng = Create("old.png", now.AddDays(-2));
        string newestPng = Create("new.png", now.AddMinutes(-1));
        string secondPng = Create("second.png", now.AddMinutes(-2));
        string unrelated = Create("keep.txt", now.AddDays(-30));

        OwnedTransientFileRetention.Purge(
            _root,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png" },
            TimeSpan.FromHours(24),
            maxFiles: 1,
            now);

        Assert.False(File.Exists(oldPng));
        Assert.True(File.Exists(newestPng));
        Assert.False(File.Exists(secondPng));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void Purge_DoesNotTraverseIntoSiblingOrChildDirectories()
    {
        Directory.CreateDirectory(_root);
        DateTime now = new(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        string childDirectory = Path.Combine(_root, "child");
        Directory.CreateDirectory(childDirectory);
        string childFile = Path.Combine(childDirectory, "old.png");
        File.WriteAllText(childFile, "child");
        File.SetLastWriteTimeUtc(childFile, now.AddDays(-2));

        string siblingDirectory = _root + "-sibling";
        Directory.CreateDirectory(siblingDirectory);
        string siblingFile = Path.Combine(siblingDirectory, "old.png");
        File.WriteAllText(siblingFile, "sibling");
        File.SetLastWriteTimeUtc(siblingFile, now.AddDays(-2));

        try
        {
            OwnedTransientFileRetention.Purge(
                _root,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png" },
                TimeSpan.Zero,
                maxFiles: 0,
                now);

            Assert.True(File.Exists(childFile));
            Assert.True(File.Exists(siblingFile));
        }
        finally
        {
            Directory.Delete(siblingDirectory, recursive: true);
        }
    }

    private string Create(string name, DateTime lastWriteUtc)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, name);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
