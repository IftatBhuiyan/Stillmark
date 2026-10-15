using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WinShot.App;

public static class ClipboardService
{
    internal const string ExcludeFromClipboardMonitorFormat =
        "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>
    /// Puts an image on the clipboard in every representation apps look for:
    /// an opaque bitmap/DIB (legacy apps; transparency flattened to white so window
    /// shots never paste black), a PNG stream with alpha (modern apps), and — when a
    /// file path is supplied — a file drop so pasting into Explorer/chat apps
    /// attaches the actual file.
    /// </summary>
    public static void SetImage(BitmapSource image, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        SetImage(StillImageEncodingService.EncodePng(image), filePath);
    }

    /// <summary>
    /// Puts a previously encoded image on the clipboard without encoding its PNG representation
    /// again. The opaque bitmap/DIB remains available for legacy paste targets.
    /// </summary>
    public static void SetImage(EncodedImagePayload payload, string? filePath = null)
    {
        SetWithRetry(CreateImageDataObject(payload, filePath));
    }

    internal static DataObject CreateImageDataObject(
        EncodedImagePayload payload,
        string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var data = new DataObject();
        BitmapSource image = payload.Image;
        bool hasAlpha = image.Format == System.Windows.Media.PixelFormats.Pbgra32
            || image.Format == System.Windows.Media.PixelFormats.Bgra32;
        data.SetImage(hasAlpha ? ImageBeautifier.FlattenToWhite(image) : image);

        data.SetData("PNG", payload.OpenPngStream(), autoConvert: false);

        if (filePath is not null && File.Exists(filePath))
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { filePath });

        return data;
    }

    public static void SetText(string text, bool sensitive = false)
    {
        SetWithRetry(CreateTextDataObject(text, sensitive));
    }

    internal static DataObject CreateTextDataObject(string text, bool sensitive)
    {
        ArgumentNullException.ThrowIfNull(text);

        var data = new DataObject();
        data.SetText(text);
        if (sensitive)
        {
            // OCR can contain passwords, recovery codes, addresses, or other private text.
            // Microsoft's registered marker excludes every format in this clipboard item from
            // Windows clipboard history and cross-device Cloud Clipboard synchronization.
            data.SetData(ExcludeFromClipboardMonitorFormat, true, autoConvert: false);
        }
        return data;
    }

    /// <summary>Puts a file on the clipboard (paste into Explorer, chat apps, etc.).</summary>
    public static void SetFile(string path)
    {
        var data = new DataObject();
        data.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
        SetWithRetry(data);
    }

    private static void SetWithRetry(DataObject data)
    {
        // The clipboard can be transiently locked by other processes; retry briefly.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
        }
    }
}
