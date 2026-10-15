using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using WinShot.Capture.Gif;
using WinShot.Core.Media;

namespace WinShot.App.Video;

/// <summary>
/// Converts a recorded MP4 into an animated GIF by sampling frames through the OS
/// media composition engine and LZW-encoding them with <see cref="GifEncoder"/>.
/// Runs entirely off the UI thread; frames are streamed one at a time so memory
/// stays bounded regardless of clip length.
/// </summary>
public static class GifExportService
{
    private const int Fps = 15;
    private const int MaxWidth = 720;

    public static Task<string> ConvertAsync(
        string mp4Path, string outputPath, int sourceWidth, int sourceHeight,
        IProgress<double>? progress, CancellationToken ct)
        => Task.Run(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(mp4Path);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
            if (sourceWidth <= 0)
                throw new ArgumentOutOfRangeException(nameof(sourceWidth));
            if (sourceHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(sourceHeight));
            if (!File.Exists(mp4Path))
                throw new FileNotFoundException("The source recording was not found.", mp4Path);
            if (!string.Equals(Path.GetExtension(outputPath), ".gif", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The GIF destination must have a .gif extension.", nameof(outputPath));

            string outputFullPath = Path.GetFullPath(outputPath);
            if (string.Equals(Path.GetFullPath(mp4Path), outputFullPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The source and destination paths must be different.", nameof(outputPath));

            string? outputDirectory = Path.GetDirectoryName(outputFullPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("The GIF destination must include a directory.", nameof(outputPath));
            Directory.CreateDirectory(outputDirectory);
            if (File.Exists(outputFullPath))
                throw new IOException("The GIF destination already exists.");

            var file = await StorageFile.GetFileFromPathAsync(mp4Path).AsTask(ct);
            var clip = await MediaClip.CreateFromFileAsync(file).AsTask(ct);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);

            TimeSpan duration = clip.OriginalDuration;
            var schedule = new GifFrameSchedule(duration, Fps);

            // Scale down to MaxWidth, keeping aspect and even dimensions.
            double scale = sourceWidth > MaxWidth ? (double)MaxWidth / sourceWidth : 1.0;
            int w = Math.Max(2, (int)Math.Round(sourceWidth * scale / 2) * 2);
            int h = Math.Max(2, (int)Math.Round(sourceHeight * scale / 2) * 2);

            string stagingPath = outputFullPath + "." + Guid.NewGuid().ToString("N") + ".partial";

            try
            {
                await using (var fs = new FileStream(
                    stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                {
                    GifEncoder? encoder = null;
                    for (int i = 0; i < schedule.FrameCount; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var thumb = await composition
                            .GetThumbnailAsync(schedule.GetTimestamp(i), w, h, VideoFramePrecision.NearestFrame)
                            .AsTask(ct);
                        using var stream = thumb.AsStreamForRead();
                        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        var bgra = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
                        int fw = bgra.PixelWidth, fh = bgra.PixelHeight;
                        var px = new byte[fw * fh * 4];
                        bgra.CopyPixels(px, fw * 4, 0);

                        encoder ??= new GifEncoder(fs, fw, fh);
                        encoder.AddFrame(px, fw * 4, schedule.GetDelayCentiseconds(i));
                        progress?.Report((double)(i + 1) / schedule.FrameCount);
                    }
                    encoder?.Finish();
                    await fs.FlushAsync(ct);
                }

                File.Move(stagingPath, outputFullPath);
                return outputFullPath;
            }
            catch
            {
                TryDeleteStagingFile(stagingPath);
                throw;
            }
        }, ct);

    private static void TryDeleteStagingFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
