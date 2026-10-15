using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.App;
using WinShot.App.Editor;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class EditorResultPreparationCoordinatorTests
{
    [Fact]
    public void Begin_SupersedesThePriorLease()
    {
        using var coordinator = new EditorResultPreparationCoordinator();
        using EditorResultPreparationLease first = coordinator.Begin(expectedRevision: 4);
        using EditorResultPreparationLease second = coordinator.Begin(expectedRevision: 5);
        bool firstPublished = false;
        bool secondPublished = false;

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(first.TryPublish(currentRevision: 4, () => firstPublished = true));
        Assert.True(second.TryPublish(currentRevision: 5, () => secondPublished = true));
        Assert.False(firstPublished);
        Assert.True(secondPublished);
    }

    [Fact]
    public void CancelCurrent_CancelsAndRefusesLatePublication()
    {
        using var coordinator = new EditorResultPreparationCoordinator();
        using EditorResultPreparationLease lease = coordinator.Begin(expectedRevision: 7);
        bool published = false;

        coordinator.CancelCurrent();

        Assert.True(lease.Token.IsCancellationRequested);
        Assert.False(lease.TryPublish(currentRevision: 7, () => published = true));
        Assert.False(published);
        Assert.ThrowsAny<OperationCanceledException>(
            () => lease.ThrowIfCannotPublish(currentRevision: 7));
    }

    [Fact]
    public void TryPublish_RefusesRevisionDriftWithoutRunningTheAction()
    {
        using var coordinator = new EditorResultPreparationCoordinator();
        using EditorResultPreparationLease lease = coordinator.Begin(expectedRevision: 10);
        bool published = false;

        Assert.False(lease.TryPublish(
            currentRevision: 11,
            () => published = true));
        Assert.False(published);
        Assert.Throws<EditorDocumentChangedDuringOutputException>(
            () => lease.ThrowIfCannotPublish(currentRevision: 11));
    }

    [Fact]
    public void Dispose_CancelsTheActiveLeaseAndRefusesNewWork()
    {
        var coordinator = new EditorResultPreparationCoordinator();
        using EditorResultPreparationLease lease = coordinator.Begin(expectedRevision: 2);

        coordinator.Dispose();

        Assert.True(lease.Token.IsCancellationRequested);
        Assert.False(lease.TryPublish(currentRevision: 2, static () => { }));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.Begin(expectedRevision: 3));
    }

    [Fact]
    public async Task CancellationAfterStagingRefusesFinalBackingFilePublication()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "WinShotTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            BitmapSource image = BitmapSource.Create(
                1,
                1,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                new byte[] { 5, 10, 15, 255 },
                4);
            image.Freeze();
            EncodedImagePayload payload =
                await StillImageEncodingService.EncodePngAsync(image);
            using var coordinator = new EditorResultPreparationCoordinator();
            using EditorResultPreparationLease lease =
                coordinator.Begin(expectedRevision: 9);
            using (ImageSaveService.PreparedImageWrite staged =
                await ImageSaveService.PreparePngCopyAsync(
                    payload,
                    root,
                    "revision-bound",
                    lease.Token))
            {
                coordinator.CancelCurrent();

                Assert.False(lease.TryPublish(
                    currentRevision: 9,
                    () => staged.Commit()));
            }

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
