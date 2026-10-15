using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinShot.EditorOutput.Perf;

internal static class BenchmarkStatistics
{
    internal static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        EnsureNotEmpty(sorted.Length);
        int midpoint = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[midpoint]
            : (sorted[midpoint - 1] + sorted[midpoint]) / 2;
    }

    internal static long Median(IEnumerable<long> values)
    {
        long[] sorted = values.Order().ToArray();
        EnsureNotEmpty(sorted.Length);
        int midpoint = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[midpoint]
            : checked(
                (sorted[midpoint - 1] / 2) +
                (sorted[midpoint] / 2) +
                ((sorted[midpoint - 1] % 2 + sorted[midpoint] % 2) / 2));
    }

    internal static double P50(IEnumerable<double> values) =>
        NearestRank(values, 0.50);

    internal static double P95(IEnumerable<double> values) =>
        NearestRank(values, 0.95);

    internal static long P95(IEnumerable<long> values)
    {
        long[] sorted = values.Order().ToArray();
        EnsureNotEmpty(sorted.Length);
        return sorted[NearestRankIndex(sorted.Length, 0.95)];
    }

    internal static int P95(IEnumerable<int> values)
    {
        int[] sorted = values.Order().ToArray();
        EnsureNotEmpty(sorted.Length);
        return sorted[NearestRankIndex(sorted.Length, 0.95)];
    }

    private static double NearestRank(
        IEnumerable<double> values,
        double percentile)
    {
        double[] sorted = values.Order().ToArray();
        EnsureNotEmpty(sorted.Length);
        return sorted[NearestRankIndex(sorted.Length, percentile)];
    }

    private static int NearestRankIndex(int count, double percentile) =>
        Math.Clamp((int)Math.Ceiling(percentile * count) - 1, 0, count - 1);

    private static void EnsureNotEmpty(int count)
    {
        if (count == 0)
            throw new ArgumentException("At least one sample is required.");
    }
}

internal static class SyntheticBitmapFactory
{
    internal static BitmapSource Create(int width, int height, out uint seed)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        int stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        seed = unchecked(
            0x9E3779B9u ^
            ((uint)width * 0x85EBCA6Bu) ^
            ((uint)height * 0xC2B2AE35u));
        uint state = seed;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = (byte)NextRandom(ref state);
            pixels[offset + 1] = (byte)NextRandom(ref state);
            pixels[offset + 2] = (byte)NextRandom(ref state);
            pixels[offset + 3] = byte.MaxValue;
        }

        BitmapSource source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride);
        source.Freeze();
        return source;
    }

    private static uint NextRandom(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }
}

internal static class BenchmarkHash
{
    internal static string Bytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    internal static string File(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static string Bitmap(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        int stride = checked(
            ((bitmap.PixelWidth * bitmap.Format.BitsPerPixel) + 7) / 8);
        var row = new byte[stride];
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        for (int y = 0; y < bitmap.PixelHeight; y++)
        {
            bitmap.CopyPixels(
                new Int32Rect(0, y, bitmap.PixelWidth, 1),
                row,
                stride,
                0);
            hash.AppendData(row);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static BitmapSource DecodePng(ReadOnlyMemory<byte> bytes)
    {
        using var stream = new MemoryStream(
            bytes.ToArray(),
            writable: false);
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (!frame.IsFrozen && frame.CanFreeze)
            frame.Freeze();
        return frame;
    }
}

internal sealed class SafeTemporaryWorkspace : IDisposable
{
    private const string ParentName = "WinShotEditorOutputPerf";

    private readonly string _parent;
    private readonly string _leaf;
    private readonly HashSet<string> _ownedFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    internal SafeTemporaryWorkspace()
    {
        _parent = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), ParentName));
        Directory.CreateDirectory(_parent);
        RejectReparsePoint(_parent);

        string leafName = Guid.NewGuid().ToString("N");
        _leaf = Path.Combine(_parent, leafName);
        Directory.CreateDirectory(_leaf);
        RejectReparsePoint(_leaf);
    }

    internal string DirectoryPath => _leaf;

    internal string GetPath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!string.Equals(
                Path.GetFileName(fileName),
                fileName,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The workspace file name must be one path component.",
                nameof(fileName));
        }

        return ValidateDirectChild(Path.Combine(_leaf, fileName));
    }

    internal void TrackOwnedFile(string path)
    {
        string validated = ValidateDirectChild(path);
        _ownedFiles.Add(validated);
    }

    internal void AssertNoPartialFiles()
    {
        if (Directory.EnumerateFiles(_leaf, ".winshot-*.partial").Any())
        {
            throw new InvalidOperationException(
                "An owned staging file remained after preparation.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (string path in _ownedFiles)
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        string[] residue = Directory
            .EnumerateFileSystemEntries(_leaf)
            .ToArray();
        if (residue.Length != 0)
        {
            throw new InvalidOperationException(
                "The isolated performance workspace retained unexpected state.");
        }

        Directory.Delete(_leaf, recursive: false);
        try
        {
            Directory.Delete(_parent, recursive: false);
        }
        catch (IOException)
        {
            // Another concurrent harness may own another GUID leaf.
        }
    }

    private string ValidateDirectChild(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? parent = Path.GetDirectoryName(fullPath);
        if (!string.Equals(parent, _leaf, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A workspace path escaped its owned GUID leaf.");
        }

        return fullPath;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The isolated performance workspace cannot use a reparse point.");
        }
    }
}

internal sealed class PeakPrivateBytesMonitor : IDisposable
{
    private readonly ManualResetEventSlim _started = new(false);
    private readonly ManualResetEventSlim _sampleReady = new(false);
    private readonly ManualResetEventSlim _stop = new(false);
    private Thread? _thread;
    private long _baseline;
    private long _peak;
    private bool _stopped;

    internal void Start()
    {
        if (_thread is not null)
            throw new InvalidOperationException("The monitor was already started.");

        _thread = new Thread(Sample)
        {
            IsBackground = true,
            Name = "WinShot.EditorOutput.PrivateBytes",
        };
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(2)))
            throw new TimeoutException("The private-byte monitor did not start.");
        _baseline = ReadPrivateBytes();
        _peak = _baseline;
        _sampleReady.Set();
    }

    internal long Stop()
    {
        if (_thread is null)
            throw new InvalidOperationException("The monitor was not started.");
        if (_stopped)
            return Math.Max(0, _peak - _baseline);

        _stopped = true;
        _stop.Set();
        if (!_thread.Join(TimeSpan.FromSeconds(2)))
            throw new TimeoutException("The private-byte monitor did not stop.");
        RecordPeak(ReadPrivateBytes());
        return Math.Max(0, _peak - _baseline);
    }

    public void Dispose()
    {
        if (_thread is not null && !_stopped)
            _ = Stop();
        _started.Dispose();
        _sampleReady.Dispose();
        _stop.Dispose();
    }

    private void Sample()
    {
        _started.Set();
        _sampleReady.Wait();
        while (!_stop.Wait(TimeSpan.FromMilliseconds(1)))
            RecordPeak(ReadPrivateBytes());
        RecordPeak(ReadPrivateBytes());
    }

    private void RecordPeak(long value)
    {
        long observed = Volatile.Read(ref _peak);
        while (value > observed)
        {
            long previous = Interlocked.CompareExchange(
                ref _peak,
                value,
                observed);
            if (previous == observed)
                return;
            observed = previous;
        }
    }

    private static long ReadPrivateBytes()
    {
        var counters = new ProcessMemoryCounters
        {
            Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>(),
        };
        if (!GetProcessMemoryInfo(
                GetCurrentProcess(),
                ref counters,
                counters.Size))
        {
            throw new InvalidOperationException(
                "The private-byte monitor could not query the current process.");
        }

        return checked((long)counters.PrivateUsage);
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(
        nint process,
        ref ProcessMemoryCounters counters,
        uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        internal uint Size;
        internal uint PageFaultCount;
        internal nuint PeakWorkingSetSize;
        internal nuint WorkingSetSize;
        internal nuint QuotaPeakPagedPoolUsage;
        internal nuint QuotaPagedPoolUsage;
        internal nuint QuotaPeakNonPagedPoolUsage;
        internal nuint QuotaNonPagedPoolUsage;
        internal nuint PagefileUsage;
        internal nuint PeakPagefileUsage;
        internal nuint PrivateUsage;
    }
}

internal sealed record MeasuredSourceFingerprint(
    int Count,
    string AggregateSha256);

internal static class MeasuredSource
{
    private static readonly string[] ResourceNames =
    [
        "WinShot.EditorOutput.Measured.Annotations.cs",
        "WinShot.EditorOutput.Measured.AppSettings.cs",
        "WinShot.EditorOutput.Measured.EditorDocument.cs",
        "WinShot.EditorOutput.Measured.EditorResultPreparationCache.cs",
        "WinShot.EditorOutput.Measured.EditorResultPreparationCoordinator.cs",
        "WinShot.EditorOutput.Measured.EncodedImagePayload.cs",
        "WinShot.EditorOutput.Measured.FileNameTemplate.cs",
        "WinShot.EditorOutput.Measured.ImageBeautifier.cs",
        "WinShot.EditorOutput.Measured.ImageSaveService.cs",
        "WinShot.EditorOutput.Measured.OutputFileReservation.cs",
        "WinShot.EditorOutput.Measured.PixelFilters.cs",
        "WinShot.EditorOutput.Measured.RegionEffectProcessing.cs",
        "WinShot.EditorOutput.Measured.StillImageEncodingService.cs",
    ];

    internal static MeasuredSourceFingerprint Fingerprint()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string[] actual = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(
                "WinShot.EditorOutput.Measured.",
                StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actual.SequenceEqual(ResourceNames, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The measured-source resource inventory changed.");
        }

        using IncrementalHash aggregate = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        foreach (string name in ResourceNames)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            aggregate.AppendData(nameBytes);
            aggregate.AppendData([0]);
            using Stream source = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException(
                    "A measured-source resource was unavailable.");
            byte[] hash = SHA256.HashData(source);
            aggregate.AppendData(hash);
        }

        return new MeasuredSourceFingerprint(
            ResourceNames.Length,
            Convert.ToHexString(aggregate.GetHashAndReset()));
    }
}

internal static class HarnessSource
{
    private static readonly string[] ResourceNames =
    [
        "WinShot.EditorOutput.Harness.BenchmarkInfrastructure.cs",
        "WinShot.EditorOutput.Harness.BenchmarkModels.cs",
        "WinShot.EditorOutput.Harness.DispatcherHeartbeatBenchmark.cs",
        "WinShot.EditorOutput.Harness.EditorOutputBenchmark.cs",
        "WinShot.EditorOutput.Harness.GlobalUsings.cs",
        "WinShot.EditorOutput.Harness.HarnessSelfTest.cs",
        "WinShot.EditorOutput.Harness.Program.cs",
        "WinShot.EditorOutput.Harness.Test-EditorOutputHarness.ps1",
        "WinShot.EditorOutput.Harness.WinShot.EditorOutput.Perf.csproj",
    ];

    internal static MeasuredSourceFingerprint Fingerprint()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string[] actual = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(
                "WinShot.EditorOutput.Harness.",
                StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actual.SequenceEqual(ResourceNames, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The harness-source resource inventory changed.");
        }

        using IncrementalHash aggregate = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        foreach (string name in ResourceNames)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            aggregate.AppendData(nameBytes);
            aggregate.AppendData([0]);
            using Stream source = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException(
                    "A harness-source resource was unavailable.");
            byte[] hash = SHA256.HashData(source);
            aggregate.AppendData(hash);
        }

        return new MeasuredSourceFingerprint(
            ResourceNames.Length,
            Convert.ToHexString(aggregate.GetHashAndReset()));
    }
}

internal static class DispatcherPump
{
    internal static T Run<T>(Func<Task<T>> operationFactory)
    {
        ArgumentNullException.ThrowIfNull(operationFactory);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("A WPF STA thread is required.");

        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.VerifyAccess();
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(dispatcher));
        var frame = new DispatcherFrame();
        Task<T>? operation = null;

        _ = dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() =>
            {
                try
                {
                    operation = operationFactory();
                }
                catch (Exception exception)
                {
                    operation = Task.FromException<T>(exception);
                }

                _ = operation.ContinueWith(
                    completed =>
                    {
                        _ = dispatcher.BeginInvoke(
                            DispatcherPriority.Send,
                            new Action(() => frame.Continue = false));
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }));

        try
        {
            Dispatcher.PushFrame(frame);
            return (operation
                    ?? throw new InvalidOperationException(
                        "The dispatcher did not start the operation."))
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
