using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.App;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class ClipboardServiceTests
{
    [Fact]
    public void CreateTextDataObject_SensitiveTextExcludesClipboardMonitoring()
    {
        const string recognizedText = "synthetic OCR fixture";

        var data = ClipboardService.CreateTextDataObject(recognizedText, sensitive: true);

        Assert.Equal(recognizedText, data.GetText());
        Assert.True(data.GetDataPresent(
            ClipboardService.ExcludeFromClipboardMonitorFormat,
            autoConvert: false));
    }

    [Fact]
    public void CreateTextDataObject_NormalTextDoesNotChangeClipboardHistoryPolicy()
    {
        var data = ClipboardService.CreateTextDataObject("ordinary text", sensitive: false);

        Assert.False(data.GetDataPresent(
            ClipboardService.ExcludeFromClipboardMonitorFormat,
            autoConvert: false));
    }

    [Fact]
    public void CreateImageDataObject_ExistingBackingFilePublishesImagePngAndFileDrop()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "WinShotTests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "result.png");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, [137, 80, 78, 71, 13, 10, 26, 10]);

        try
        {
            EncodedImagePayload payload = CreatePayload();

            var data = ClipboardService.CreateImageDataObject(payload, path);

            Assert.True(data.ContainsImage());
            Assert.True(data.GetDataPresent("PNG", autoConvert: false));
            Assert.Equal([path], data.GetFileDropList().Cast<string>());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CreateImageDataObject_MissingBackingFileOmitsFileDrop()
    {
        string missingPath = Path.Combine(
            Path.GetTempPath(),
            "WinShotTests",
            Guid.NewGuid().ToString("N"),
            "missing.png");

        var data = ClipboardService.CreateImageDataObject(CreatePayload(), missingPath);

        Assert.True(data.ContainsImage());
        Assert.True(data.GetDataPresent("PNG", autoConvert: false));
        Assert.False(data.ContainsFileDropList());
    }

    private static EncodedImagePayload CreatePayload()
    {
        BitmapSource image = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            new byte[] { 10, 20, 30, 255 },
            stride: 4);
        image.Freeze();
        return StillImageEncodingService.EncodePng(image);
    }
}
