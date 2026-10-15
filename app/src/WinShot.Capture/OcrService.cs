using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace WinShot.Capture;

/// <summary>Text recognition via the Windows built-in OCR engine (offline, uses installed language packs).</summary>
public static class OcrService
{
    public static async Task<string> RecognizeAsync(
        BitmapSource image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        OcrEngine? engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
        if (engine is null)
            throw new InvalidOperationException("No OCR language pack is installed in Windows.");

        BitmapSource threadSafeImage = image;
        if (!threadSafeImage.IsFrozen)
        {
            threadSafeImage = image.Clone();
            if (!threadSafeImage.CanFreeze)
                throw new InvalidOperationException("The OCR image cannot be processed safely across threads.");
            threadSafeImage.Freeze();
        }

        using SoftwareBitmap softwareBitmap = await Task.Run(
            () => CreateSoftwareBitmap(threadSafeImage, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        OcrResult result = await engine
            .RecognizeAsync(softwareBitmap)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var text = new StringBuilder();
        foreach (OcrLine line in result.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (text.Length > 0)
                text.AppendLine();
            text.Append(line.Text);
        }

        return text.ToString();
    }

    private static SoftwareBitmap CreateSoftwareBitmap(
        BitmapSource image,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BitmapSource source = image;
        int maxDimension = checked((int)OcrEngine.MaxImageDimension);
        OcrImageSize target = OcrImageSizing.Calculate(
            image.PixelWidth,
            image.PixelHeight,
            maxDimension);
        if (target.Scale < 1)
        {
            source = new TransformedBitmap(image, new ScaleTransform(target.Scale, target.Scale));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth, height = converted.PixelHeight;
        long byteCount = checked((long)width * height * 4);
        if (width > maxDimension ||
            height > maxDimension ||
            byteCount > OcrImageSizing.DefaultMaxBgraBytes ||
            byteCount > Array.MaxLength)
            throw new InvalidOperationException("The OCR selection is too large to process safely.");

        var pixels = new byte[(int)byteCount];
        converted.CopyPixels(pixels, checked(width * 4), 0);
        cancellationToken.ThrowIfCancellationRequested();

        return SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
    }
}
