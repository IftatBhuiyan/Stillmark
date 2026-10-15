using System.Collections.Concurrent;
using WinShot.Core.Naming;
using Xunit;

namespace WinShot.Core.Tests;

public class FileNameTemplateTests
{
    private static readonly DateTime Timestamp = new(2026, 7, 16, 14, 5, 9, 123);

    [Fact]
    public void Expand_ReplacesAllTokens()
    {
        string result = FileNameTemplate.Expand("Shot_{yyyy}-{MM}-{dd}_{HH}-{mm}-{ss}.{ms}", Timestamp);
        Assert.Equal("Shot_2026-07-16_14-05-09.123", result);
    }

    [Fact]
    public void Expand_SanitizesInvalidFileNameCharacters()
    {
        string result = FileNameTemplate.Expand("a<b>c:d|e", Timestamp);
        Assert.Equal("a-b-c-d-e", result);
    }

    [Fact]
    public void Expand_EmptyTemplate_FallsBackToDefaultName()
    {
        string result = FileNameTemplate.Expand("   ", Timestamp);
        Assert.Equal("Relic_Screenshot_20260716_140509", result);
    }

    [Fact]
    public void GetUniquePath_AppendsCounterOnCollision()
    {
        string dir = Path.Combine(Path.GetTempPath(), "WinShotTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string first = FileNameTemplate.GetUniquePath(dir, "shot", ".png");
            Assert.Equal(Path.Combine(dir, "shot.png"), first);

            File.WriteAllText(first, "");
            string second = FileNameTemplate.GetUniquePath(dir, "shot", ".png");
            Assert.Equal(Path.Combine(dir, "shot 2.png"), second);

            File.WriteAllText(second, "");
            string third = FileNameTemplate.GetUniquePath(dir, "shot", ".png");
            Assert.Equal(Path.Combine(dir, "shot 3.png"), third);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReservationCommitPreservesExactWrittenFile()
    {
        string dir = CreateTestDirectory();
        try
        {
            string path;
            using (var reservation = FileNameTemplate.ReserveUniqueFile(dir, "shot", ".png"))
            {
                path = reservation.Path;
                reservation.Stream.Write([1, 2, 3, 4]);
                reservation.Commit();

                Assert.True(reservation.IsCommitted);
                Assert.Throws<ObjectDisposedException>(() => _ = reservation.Stream);
            }

            Assert.Equal(Path.Combine(dir, "shot.png"), path);
            Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void UncommittedReservationDeletesOnlyItsOwnedPlaceholder()
    {
        string dir = CreateTestDirectory();
        string existing = Path.Combine(dir, "shot.png");
        File.WriteAllText(existing, "caller-owned");
        try
        {
            string reservedPath;
            using (var reservation = FileNameTemplate.ReserveUniqueFile(dir, "shot", ".png"))
            {
                reservedPath = reservation.Path;
                Assert.Equal(Path.Combine(dir, "shot 2.png"), reservedPath);
                reservation.Stream.WriteByte(42);
            }

            Assert.False(File.Exists(reservedPath));
            Assert.Equal("caller-owned", File.ReadAllText(existing));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ConcurrentReservationsProduceUniquePaths()
    {
        string dir = CreateTestDirectory();
        try
        {
            var paths = new ConcurrentBag<string>();
            Parallel.For(0, 48, index =>
            {
                using var reservation = FileNameTemplate.ReserveUniqueFile(
                    dir,
                    "simultaneous",
                    ".png",
                    maxAttempts: 64);
                reservation.Stream.WriteByte((byte)index);
                paths.Add(reservation.Path);
                reservation.Commit();
            });

            string[] reserved = [.. paths];
            Assert.Equal(48, reserved.Length);
            Assert.Equal(48, reserved.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(48, Directory.EnumerateFiles(dir, "*.png").Count());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReservationStopsAfterBoundedAttempts()
    {
        string dir = CreateTestDirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir, "shot.png"), "1");
            File.WriteAllText(Path.Combine(dir, "shot 2.png"), "2");
            File.WriteAllText(Path.Combine(dir, "shot 3.png"), "3");

            Assert.Throws<IOException>(
                () => FileNameTemplate.ReserveUniqueFile(dir, "shot", ".png", maxAttempts: 3));
            Assert.Equal(3, Directory.EnumerateFiles(dir).Count());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("", ".png")]
    [InlineData("..", ".png")]
    [InlineData("folder/name", ".png")]
    [InlineData("CON", ".png")]
    [InlineData("shot", "png")]
    [InlineData("shot", ".")]
    public void ReservationRejectsInvalidNameComponents(string baseName, string extension)
    {
        string dir = CreateTestDirectory();
        try
        {
            Assert.ThrowsAny<ArgumentException>(
                () => FileNameTemplate.ReserveUniqueFile(dir, baseName, extension));
            Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReservationValidatesDirectoryAttemptsAndMaximumNameLength()
    {
        string missing = Path.Combine(Path.GetTempPath(), "WinShotTests_" + Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(
            () => FileNameTemplate.ReserveUniqueFile(missing, "shot", ".png"));

        string dir = CreateTestDirectory();
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => FileNameTemplate.ReserveUniqueFile(dir, "shot", ".png", maxAttempts: 0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => FileNameTemplate.ReserveUniqueFile(
                    dir,
                    "shot",
                    ".png",
                    maxAttempts: OutputFileReservation.MaximumAttempts + 1));

            string tooLong = new('a', OutputFileReservation.MaximumFileNameLength);
            Assert.Throws<PathTooLongException>(
                () => FileNameTemplate.ReserveUniqueFile(dir, tooLong, ".png"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string CreateTestDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "WinShotTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
