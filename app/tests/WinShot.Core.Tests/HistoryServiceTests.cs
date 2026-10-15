using WinShot.Core.History;
using System.Text.Json;
using Xunit;

namespace WinShot.Core.Tests;

public class HistoryServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "WinShotTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private HistoryService CreateService() => new(_dir);

    [Fact]
    public void Load_NullAndIncompleteEntries_DoNotBreakHistoryActions()
    {
        var valid = MakeEntry("valid");
        string json = "[null,{}," + JsonSerializer.Serialize(valid) + "]";
        File.WriteAllText(Path.Combine(_dir, "history.json"), json);

        var service = CreateService();

        Assert.Equal(valid, Assert.Single(service.Entries));
        Assert.True(service.Remove(valid.Id));
        Assert.Empty(service.Entries);
    }

    private HistoryEntry MakeEntry(string id, bool managed = true)
    {
        Directory.CreateDirectory(_dir);
        var service = new HistoryService(_dir);
        string path = managed
            ? service.CreateManagedPath(DateTime.Now, id)
            : Path.Combine(_dir, $"user_{id}.png");
        File.WriteAllText(path, "fake png");
        return new HistoryEntry(id, DateTime.Now, path, managed, 100, 50);
    }

    [Fact]
    public void Add_InsertsNewestFirst()
    {
        var service = CreateService();
        service.Add(MakeEntry("a"));
        service.Add(MakeEntry("b"));
        Assert.Equal("b", service.Entries[0].Id);
        Assert.Equal("a", service.Entries[1].Id);
    }

    [Fact]
    public void Add_BeyondCap_PrunesOldestAndDeletesManagedFile()
    {
        var service = CreateService();
        var first = MakeEntry("first");
        service.Add(first);
        for (int i = 0; i < HistoryService.MaxEntries; i++)
            service.Add(MakeEntry($"e{i}"));

        Assert.Equal(HistoryService.MaxEntries, service.Entries.Count);
        Assert.DoesNotContain(service.Entries, e => e.Id == "first");
        Assert.False(File.Exists(first.FilePath), "pruned managed copy should be deleted");
    }

    [Fact]
    public void EditedSave_IsRecordedAndRemainsOutsideManagedCleanup()
    {
        var service = CreateService();
        var saved = MakeEntry("edited", managed: false);

        Assert.True(service.RecordSavedImage(saved.FilePath, saved.Timestamp, 640, 360));
        HistoryEntry entry = Assert.Single(CreateService().Entries);
        Assert.Equal((640, 360), (entry.Width, entry.Height));
        Assert.False(entry.IsManagedCopy);
        Assert.True(service.Remove(entry.Id));
        Assert.True(File.Exists(saved.FilePath));
    }

    [Fact]
    public void EditedSave_OverwritingTrackedImage_RefreshesWithoutDuplicateOrOwnershipLoss()
    {
        var service = CreateService();
        var original = MakeEntry("original");
        service.Add(original);
        service.Add(MakeEntry("newer"));
        DateTime savedAt = original.Timestamp.AddMinutes(5);

        Assert.True(service.RecordSavedImage(original.FilePath, savedAt, 300, 200));

        Assert.Equal(2, service.Entries.Count);
        HistoryEntry updated = CreateService().Entries[0];
        Assert.Equal(original.Id, updated.Id);
        Assert.True(updated.IsManagedCopy);
        Assert.Equal(savedAt, updated.Timestamp);
        Assert.Equal((300, 200), (updated.Width, updated.Height));
        Assert.True(File.Exists(original.FilePath));
    }

    [Fact]
    public void Add_LockedOldestCopy_DoesNotOrphanItWhenHistoryIsFull()
    {
        var service = CreateService();
        var oldest = MakeEntry("oldest");
        Assert.True(service.Add(oldest));
        for (int i = 1; i < HistoryService.MaxEntries; i++)
            Assert.True(service.Add(MakeEntry($"e{i}")));
        var newest = MakeEntry("newest");

        using (new FileStream(oldest.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(service.Add(newest));
            Assert.Equal(HistoryService.MaxEntries, service.Entries.Count);
            Assert.Contains(CreateService().Entries, entry => entry.Id == oldest.Id);
            Assert.True(File.Exists(oldest.FilePath));
            Assert.False(File.Exists(newest.FilePath));
        }
        Assert.True(service.Add(MakeEntry("retry")));
        Assert.False(File.Exists(oldest.FilePath));
    }

    [Fact]
    public void Remove_UserSavedFile_IsNeverDeleted()
    {
        var service = CreateService();
        var entry = MakeEntry("user1", managed: false);
        service.Add(entry);
        service.Remove(entry.Id);

        Assert.Empty(service.Entries);
        Assert.True(File.Exists(entry.FilePath), "user's own file must survive history removal");
    }

    [Fact]
    public void Remove_ManagedCopy_DeletesFile()
    {
        var service = CreateService();
        var entry = MakeEntry("m1");
        service.Add(entry);
        service.Remove(entry.Id);
        Assert.False(File.Exists(entry.FilePath));
    }

    [Fact]
    public void Remove_LockedManagedCopy_RetainsEntryAndReportsCleanupFailure()
    {
        var service = CreateService();
        var entry = MakeEntry("locked-remove");
        service.Add(entry);

        using (new FileStream(
            entry.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            bool removed = service.Remove(entry.Id, out bool cleanupFailed);

            Assert.False(removed);
            Assert.True(cleanupFailed);
            Assert.Single(service.Entries);
            Assert.Equal(entry.Id, service.Entries[0].Id);
            Assert.True(File.Exists(entry.FilePath));

            var reloaded = CreateService();
            Assert.Equal(entry.Id, Assert.Single(reloaded.Entries).Id);
        }

        Assert.True(service.Remove(entry.Id, out bool retryCleanupFailed));
        Assert.False(retryCleanupFailed);
        Assert.Empty(service.Entries);
        Assert.False(File.Exists(entry.FilePath));
    }

    [Fact]
    public void Clear_LockedManagedCopy_RetainsOnlyUndeletedEntryAndReportsFailure()
    {
        var service = CreateService();
        var locked = MakeEntry("locked-clear");
        var removable = MakeEntry("removable-clear");
        service.Add(locked);
        service.Add(removable);

        using (new FileStream(
            locked.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            bool cleared = service.Clear(out bool cleanupFailed);

            Assert.False(cleared);
            Assert.True(cleanupFailed);
            var remaining = Assert.Single(service.Entries);
            Assert.Equal(locked.Id, remaining.Id);
            Assert.True(File.Exists(locked.FilePath));
            Assert.False(File.Exists(removable.FilePath));
        }

        Assert.True(service.Clear(out bool retryCleanupFailed));
        Assert.False(retryCleanupFailed);
        Assert.Empty(service.Entries);
        Assert.False(File.Exists(locked.FilePath));
    }

    [Fact]
    public void Remove_LockedMetadata_RetainsEntryAndReportsMetadataFailure()
    {
        var service = CreateService();
        var entry = MakeEntry("locked-metadata");
        service.Add(entry);
        string metadataPath = Path.Combine(_dir, "history.json");

        using (new FileStream(
            metadataPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            bool removed = service.Remove(entry.Id, out bool cleanupFailed);

            Assert.False(removed);
            Assert.False(cleanupFailed);
            Assert.Single(service.Entries);
            Assert.Equal(entry.Id, service.Entries[0].Id);
            Assert.True(File.Exists(entry.FilePath));
        }
    }

    [Fact]
    public void Remove_ManagedFlagOutsideHistoryDirectory_NeverDeletesFile()
    {
        var service = CreateService();
        string externalPath = Path.Combine(
            Path.GetTempPath(),
            "WinShotExternal_" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllText(externalPath, "not owned by WinShot history");

        try
        {
            service.Add(new HistoryEntry(
                "tampered",
                DateTime.Now,
                externalPath,
                IsManagedCopy: true,
                Width: 100,
                Height: 50));
            service.Remove("tampered");

            Assert.True(
                File.Exists(externalPath),
                "a persisted IsManagedCopy flag must not authorize deletion outside History");
        }
        finally
        {
            File.Delete(externalPath);
        }
    }

    [Fact]
    public void Entries_PersistAcrossInstances()
    {
        var service = CreateService();
        service.Add(MakeEntry("persist1"));

        var reloaded = CreateService();
        Assert.Single(reloaded.Entries);
        Assert.Equal("persist1", reloaded.Entries[0].Id);
    }

    [Fact]
    public void Load_ExistingFileBeyondCap_KeepsOnlyNewestEntries()
    {
        Directory.CreateDirectory(_dir);
        var entries = Enumerable.Range(0, HistoryService.MaxEntries + 12)
            .Select(i => new HistoryEntry(
                $"entry-{i}",
                DateTime.UnixEpoch.AddMinutes(-i),
                Path.Combine(_dir, $"entry-{i}.png"),
                IsManagedCopy: false,
                Width: 100,
                Height: 50))
            .ToList();
        File.WriteAllText(
            Path.Combine(_dir, "history.json"),
            JsonSerializer.Serialize(entries));

        var service = CreateService();

        Assert.Equal(HistoryService.MaxEntries, service.Entries.Count);
        Assert.Equal("entry-0", service.Entries[0].Id);
        Assert.Equal($"entry-{HistoryService.MaxEntries - 1}", service.Entries[^1].Id);
    }

    [Fact]
    public void Load_OversizedMetadata_IgnoresItWithoutAllocatingItsFullLength()
    {
        Directory.CreateDirectory(_dir);
        string metadataPath = Path.Combine(_dir, "history.json");
        using (var stream = new FileStream(
            metadataPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        {
            stream.SetLength(HistoryService.MaximumMetadataBytes + 1L);
        }

        var service = CreateService();

        Assert.Empty(service.Entries);
        Assert.Equal(
            HistoryService.MaximumMetadataBytes + 1L,
            new FileInfo(metadataPath).Length);
    }

    [Fact]
    public void Add_OversizedMetadata_RollsBackAndPreservesPriorFile()
    {
        var service = CreateService();
        HistoryEntry original = MakeEntry("original");
        service.Add(original);
        string metadataPath = Path.Combine(_dir, "history.json");
        byte[] priorMetadata = File.ReadAllBytes(metadataPath);

        bool added = service.Add(new HistoryEntry(
            "oversized",
            DateTime.Now,
            new string('x', HistoryService.MaximumMetadataBytes),
            IsManagedCopy: false,
            Width: 100,
            Height: 50));

        Assert.False(added);
        Assert.Single(service.Entries);
        Assert.Equal(original.Id, service.Entries[0].Id);
        Assert.Equal(priorMetadata, File.ReadAllBytes(metadataPath));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Add_LockedMetadata_RollsBackAndDeletesOnlyNewManagedCopy()
    {
        var service = CreateService();
        HistoryEntry original = MakeEntry("original");
        service.Add(original);
        HistoryEntry addition = MakeEntry("addition");
        string metadataPath = Path.Combine(_dir, "history.json");

        using (new FileStream(
            metadataPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            Assert.False(service.Add(addition));
        }

        Assert.Single(service.Entries);
        Assert.Equal(original.Id, service.Entries[0].Id);
        Assert.True(File.Exists(original.FilePath));
        Assert.False(File.Exists(addition.FilePath));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Clear_RemovesEverythingAndManagedFilesOnly()
    {
        var service = CreateService();
        var managed = MakeEntry("m2");
        var user = MakeEntry("u2", managed: false);
        service.Add(managed);
        service.Add(user);
        service.Clear();

        Assert.Empty(service.Entries);
        Assert.False(File.Exists(managed.FilePath));
        Assert.True(File.Exists(user.FilePath));
    }
}
