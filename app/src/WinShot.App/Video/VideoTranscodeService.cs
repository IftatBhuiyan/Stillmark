using System.IO;
using System.Runtime.InteropServices;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using WinShot.Core.Media;

namespace WinShot.App.Video;

public enum AudioConvertMode
{
    Keep,
    Mono,
    Mute,
}

public sealed record VideoConvertOptions(
    TimeSpan TrimStart,
    TimeSpan TrimEnd,      // TimeSpan.Zero = no end trim
    int Width,
    int Height,
    double Quality,        // 0..1 → bitrate scale
    AudioConvertMode Audio);

/// <summary>
/// Trim / resize / re-encode MP4s with the OS transcoding engine
/// (Windows.Media.Transcoding — no external binaries).
/// </summary>
public static class VideoTranscodeService
{
    /// <summary>Bits per pixel per frame at Quality=1.0 — H.264 screen content heuristic.</summary>
    private const double BitsPerPixelHigh = 0.14;
    private const int AudioBitsPerSecond = 128_000;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);

    public static uint VideoBitrate(int width, int height, double quality, double fps = 30) =>
        (uint)Math.Max(600_000, width * height * fps * BitsPerPixelHigh * Math.Clamp(quality, 0.15, 1.0));

    /// <summary>Rough output size for the footer estimate.</summary>
    public static long EstimateBytes(TimeSpan duration, VideoConvertOptions options)
    {
        var trim = VideoTrimRange.Resolve(duration, options.TrimStart, options.TrimEnd);
        double seconds = Math.Max(0.1, trim.OutputDuration.TotalSeconds);
        long video = (long)(VideoBitrate(options.Width, options.Height, options.Quality) / 8.0 * seconds);
        long audio = options.Audio == AudioConvertMode.Mute ? 0 : (long)(AudioBitsPerSecond / 8.0 * seconds);
        return video + audio;
    }

    public static async Task<string> ConvertAsync(
        string inputPath, VideoConvertOptions options, IProgress<double>? progress, CancellationToken cancel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        cancel.ThrowIfCancellationRequested();
        string inputFullPath = Path.GetFullPath(inputPath);
        string? directory = Path.GetDirectoryName(inputFullPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("The source video must include a directory.", nameof(inputPath));

        var source = await StorageFile.GetFileFromPathAsync(inputFullPath).AsTask(cancel);
        var videoProperties = await source.Properties.GetVideoPropertiesAsync().AsTask(cancel);
        var trim = VideoTrimRange.Resolve(
            videoProperties.Duration, options.TrimStart, options.TrimEnd);

        string outputName = Path.GetFileNameWithoutExtension(inputFullPath) + " (edited).mp4";
        string stagingName =
            Path.GetFileNameWithoutExtension(inputFullPath)
            + " (edited)."
            + Guid.NewGuid().ToString("N")
            + ".partial";
        string stagingPath = Path.Combine(directory, stagingName);
        var folder = await StorageFolder.GetFolderFromPathAsync(directory).AsTask(cancel);
        StorageFile? destination = null;

        try
        {
            destination = await folder
                .CreateFileAsync(stagingName, CreationCollisionOption.FailIfExists)
                .AsTask(cancel);

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Width = (uint)Math.Max(2, options.Width / 2 * 2);
            profile.Video.Height = (uint)Math.Max(2, options.Height / 2 * 2);
            profile.Video.Bitrate = VideoBitrate(options.Width, options.Height, options.Quality);
            if (options.Audio == AudioConvertMode.Mute)
                profile.Audio = null;
            else if (options.Audio == AudioConvertMode.Mono && profile.Audio is not null)
                profile.Audio.ChannelCount = 1;

            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            if (trim.StartOffset > TimeSpan.Zero)
                transcoder.TrimStartTime = trim.StartOffset;
            if (trim.EndOffset > TimeSpan.Zero)
                transcoder.TrimStopTime = trim.EndOffset;

            var prepared = await transcoder
                .PrepareFileTranscodeAsync(source, destination, profile)
                .AsTask(cancel);
            if (!prepared.CanTranscode)
            {
                throw new InvalidOperationException(
                    "This video can't be converted (" + prepared.FailureReason + ").");
            }

            var operation = prepared.TranscodeAsync();
            operation.Progress = (_, percent) =>
            {
                if (!cancel.IsCancellationRequested)
                    progress?.Report(percent / 100.0);
            };
            using (cancel.Register(() => operation.Cancel()))
            {
                await operation;
            }

            cancel.ThrowIfCancellationRequested();
            await destination
                .RenameAsync(outputName, NameCollisionOption.GenerateUniqueName)
                .AsTask(cancel);
            cancel.ThrowIfCancellationRequested();
            return destination.Path;
        }
        catch
        {
            if (destination is not null)
                await TryDeleteAsync(destination);
            await TryDeletePathAsync(stagingPath);
            throw;
        }
    }

    private static async Task TryDeleteAsync(StorageFile file)
    {
        try
        {
            using var cleanupCancellation = new CancellationTokenSource(CleanupTimeout);
            await file.DeleteAsync().AsTask(cleanupCancellation.Token);
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or OperationCanceledException
            or COMException)
        {
            // Best-effort cleanup must not replace the original transcode failure.
        }
    }

    private static async Task TryDeletePathAsync(string path)
    {
        try
        {
            await Task.Run(() =>
            {
                if (File.Exists(path))
                    File.Delete(path);
            }).WaitAsync(CleanupTimeout);
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or OperationCanceledException
            or TimeoutException)
        {
            // A bounded cleanup failure must not replace the original transcode failure.
        }
    }
}
