using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.App;
using WinShot.App.Overlays;
using WinShot.App.QuickAccess;
using WinShot.App.Recording;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Core.Settings;
using Xunit;

namespace WinShot.Core.Tests;

public class FeatureDeliveryTests
{
    [Fact]
    public void GifDelivery_IsRecordingAndUsesTransientBackingPath()
    {
        var delivery = new CaptureDelivery(
            CreateBitmap(),
            SavedPath: null,
            "GIF ready",
            DateTime.UtcNow,
            new PixelRect(0, 0, 2, 2),
            IsGif: true,
            BackingPath: @"C:\temp\clip.gif");

        Assert.True(delivery.IsRecording);
        Assert.False(delivery.IsVideo);
        Assert.Equal(@"C:\temp\clip.gif", delivery.FilePath);
    }

    [Fact]
    public void RecordingQuickAccess_RequiresTransientFileWhenSaveIsOff()
    {
        var settings = AppSettings.CreateDefault();
        settings.RecordingSaveToFile = false;
        settings.RecordingCopyFile = false;
        settings.RecordingOpenPlayer = false;
        settings.RecordingShowQuickAccess = true;

        Assert.True(RecordingDeliveryPolicy.NeedsTransientFile(settings, isGif: false));
        Assert.True(RecordingDeliveryPolicy.NeedsTransientFile(settings, isGif: true));
    }

    [Fact]
    public void GifDoesNotNeedTransientFileForVideoPlayerOnly()
    {
        var settings = AppSettings.CreateDefault();
        settings.RecordingCopyFile = false;
        settings.RecordingShowQuickAccess = false;
        settings.RecordingOpenPlayer = true;

        Assert.True(RecordingDeliveryPolicy.NeedsTransientFile(settings, isGif: false));
        Assert.False(RecordingDeliveryPolicy.NeedsTransientFile(settings, isGif: true));
    }

    [Fact]
    public async Task QuickAccessSaveMedia_ConcurrentCopiesAreDistinctAndComplete()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinShotTests", Guid.NewGuid().ToString("N"));
        try
        {
            string sourceDirectory = Path.Combine(root, "tmp");
            string saveDirectory = Path.Combine(root, "captures");
            Directory.CreateDirectory(sourceDirectory);
            string source = Path.Combine(sourceDirectory, "clip.mp4");
            File.WriteAllBytes(source, [1, 2, 3, 4]);

            var manager = new SettingsManager(new SettingsStore(Path.Combine(root, "settings.json")));
            var updated = manager.Current.Clone();
            updated.SaveDirectory = saveDirectory;
            manager.Update(updated);

            var quickAccess = new QuickAccessManager(manager);
            string[] saved = await Task.WhenAll(
                Enumerable.Range(0, 4)
                    .Select(_ => quickAccess.SaveMediaAsync(source)));

            Assert.Equal(4, saved.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(saved, path =>
            {
                Assert.Equal(saveDirectory, Path.GetDirectoryName(path));
                Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(path));
            });
            Assert.True(File.Exists(source));
            Assert.Empty(Directory.EnumerateFiles(saveDirectory, "*.partial"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InteractiveWindowSelection_PreservesWindowUntilUserAdjustsIt()
    {
        var bounds = new PixelRect(0, 0, 800, 600);
        var monitor = new MonitorInfo("test", bounds, bounds, true, 96, 96);
        var window = new WindowInfo(new IntPtr(42), "Fixture", new PixelRect(100, 100, 300, 200));
        var session = new SelectionSession(
            [monitor],
            bounds,
            CreateBitmap(800, 600),
            [window],
            interactive: true);

        session.CommitSelection(window.Bounds, window);
        Assert.Same(window, session.CommittedWindow);

        session.OnMouseDown(200, 180);
        Assert.Null(session.CommittedWindow);
    }

    [Fact]
    public void RecordingRegion_InsideNegativeCoordinateDisplay_IsAcceptedUnchanged()
    {
        var monitors = new[]
        {
            new MonitorInfo("left", new PixelRect(-1920, 0, 1920, 1080),
                new PixelRect(-1920, 0, 1920, 1040), false, 96, 96),
            new MonitorInfo("primary", new PixelRect(0, 0, 2560, 1440),
                new PixelRect(0, 0, 2560, 1400), true, 120, 120),
        };
        var requested = new PixelRect(-1800, 100, 1000, 700);

        bool accepted = RecordingRegionPolicy.TryUseSingleDisplay(
            requested,
            monitors,
            out PixelRect actual);

        Assert.True(accepted);
        Assert.Equal(requested, actual);
    }

    [Fact]
    public void RecordingRegion_SpanningDisplays_IsRejectedInsteadOfCropped()
    {
        var monitors = new[]
        {
            new MonitorInfo("left", new PixelRect(-1920, 0, 1920, 1080),
                new PixelRect(-1920, 0, 1920, 1040), false, 96, 96),
            new MonitorInfo("primary", new PixelRect(0, 0, 2560, 1440),
                new PixelRect(0, 0, 2560, 1400), true, 120, 120),
        };

        bool accepted = RecordingRegionPolicy.TryUseSingleDisplay(
            new PixelRect(-200, 100, 800, 600),
            monitors,
            out PixelRect actual);

        Assert.False(accepted);
        Assert.True(actual.IsEmpty);
    }

    private static BitmapSource CreateBitmap(int width = 2, int height = 2)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }
}
