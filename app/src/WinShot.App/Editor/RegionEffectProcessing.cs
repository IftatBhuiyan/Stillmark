using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinShot.App.Editor;

internal enum RegionEffectKind
{
    Pixelate,
    BoxBlur,
}

internal readonly record struct RegionEffectRequest(
    long BackgroundIdentity,
    Int32Rect PixelRegion,
    RegionEffectKind Kind,
    int Parameter,
    int Passes)
{
    internal long OutputByteCount => checked(
        checked((long)PixelRegion.Width * PixelRegion.Height) * 4);
}

internal interface IRegionEffectProcessor
{
    BitmapSource Process(
        BitmapSource source,
        RegionEffectRequest request,
        CancellationToken cancellationToken);
}

internal sealed class PixelFilterRegionEffectProcessor : IRegionEffectProcessor
{
    public BitmapSource Process(
        BitmapSource source,
        RegionEffectRequest request,
        CancellationToken cancellationToken) =>
        request.Kind switch
        {
            RegionEffectKind.Pixelate => PixelFilters.Pixelate(
                source,
                request.PixelRegion,
                request.Parameter,
                cancellationToken),
            RegionEffectKind.BoxBlur => PixelFilters.BoxBlur(
                source,
                request.PixelRegion,
                request.Parameter,
                request.Passes,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
}

internal sealed class RegionEffectPreparedSnapshot
{
    private readonly IReadOnlyDictionary<RegionEffectRequest, BitmapSource> _images;

    internal RegionEffectPreparedSnapshot(
        IReadOnlyDictionary<RegionEffectRequest, BitmapSource> images)
    {
        _images = images;
    }

    internal bool TryGet(
        RegionEffectRequest request,
        out BitmapSource? image) =>
        _images.TryGetValue(request, out image);
}

/// <summary>
/// Owns one cancelable filter worker and a byte-bounded completed-result cache for one editor
/// document. Results are committed only when their generation is still current.
/// </summary>
internal sealed class RegionEffectPipeline : IDisposable
{
    internal const long DefaultCacheByteLimit = 128L * 1024 * 1024;
    internal const long DefaultWorkingByteLimit = 384L * 1024 * 1024;
    internal const int DefaultCacheEntryLimit = 32;

    private sealed record CacheEntry(
        BitmapSource Image,
        long ByteCount,
        LinkedListNode<RegionEffectRequest> RecencyNode);

    private readonly object _sync = new();
    private readonly IRegionEffectProcessor _processor;
    private readonly long _cacheByteLimit;
    private readonly long _workingByteLimit;
    private readonly int _cacheEntryLimit;
    private readonly SemaphoreSlim _workerGate = new(1, 1);
    private readonly Dictionary<RegionEffectRequest, CacheEntry> _cache = new();
    private readonly LinkedList<RegionEffectRequest> _recency = new();

    private CancellationTokenSource? _inFlightCancellation;
    private Task? _inFlightTask;
    private BitmapSource? _inFlightSource;
    private RegionEffectRequest[] _inFlightDesiredRequests = [];
    private long _generation;
    private long _cachedBytes;
    private bool _disposed;

    internal RegionEffectPipeline(
        IRegionEffectProcessor? processor = null,
        long cacheByteLimit = DefaultCacheByteLimit,
        int cacheEntryLimit = DefaultCacheEntryLimit,
        long workingByteLimit = DefaultWorkingByteLimit)
    {
        if (cacheByteLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(cacheByteLimit));
        if (cacheEntryLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(cacheEntryLimit));
        if (workingByteLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(workingByteLimit));

        _processor = processor ?? new PixelFilterRegionEffectProcessor();
        _cacheByteLimit = cacheByteLimit;
        _cacheEntryLimit = cacheEntryLimit;
        _workingByteLimit = workingByteLimit;
    }

    internal bool TryGet(
        RegionEffectRequest request,
        out BitmapSource? image)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_cache.TryGetValue(request, out CacheEntry? entry))
            {
                image = null;
                return false;
            }

            _recency.Remove(entry.RecencyNode);
            _recency.AddLast(entry.RecencyNode);
            image = entry.Image;
            return true;
        }
    }

    internal Task PrepareAsync(
        BitmapSource source,
        IReadOnlyList<RegionEffectRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(requests);
        if (!source.IsFrozen)
        {
            throw new InvalidOperationException(
                "Region-effect source bitmaps must be frozen before worker use.");
        }

        RegionEffectRequest[] uniqueRequests = requests
            .Distinct()
            .ToArray();
        ValidateBatch(source, uniqueRequests);

        Task preparation;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_inFlightTask is { IsCompleted: false } &&
                ReferenceEquals(_inFlightSource, source) &&
                _inFlightDesiredRequests.SequenceEqual(uniqueRequests))
            {
                preparation = _inFlightTask;
            }
            else
            {
                if (_inFlightTask is { IsCompleted: false })
                {
                    _inFlightCancellation?.Cancel();
                    _generation++;
                    _inFlightTask = null;
                    _inFlightSource = null;
                    _inFlightDesiredRequests = [];
                    _inFlightCancellation = null;
                }

                RegionEffectRequest[] missingRequests = uniqueRequests
                    .Where(request => !_cache.ContainsKey(request))
                    .ToArray();
                if (missingRequests.Length == 0)
                    return Task.CompletedTask;

                var workerCancellation = new CancellationTokenSource();
                long generation = ++_generation;
                preparation = new Task(
                    () => ExecuteBatch(
                        generation,
                        source,
                        missingRequests,
                        workerCancellation),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach);
                _inFlightCancellation = workerCancellation;
                _inFlightTask = preparation;
                _inFlightSource = source;
                _inFlightDesiredRequests = uniqueRequests;
                ObserveFault(preparation);
                preparation.Start(TaskScheduler.Default);
            }
        }

        return cancellationToken.CanBeCanceled
            ? AwaitWithCallerCancellationAsync(
                preparation,
                cancellationToken)
            : preparation;
    }

    internal RegionEffectPreparedSnapshot CapturePreparedSnapshot(
        IReadOnlyList<RegionEffectRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        lock (_sync)
        {
            ThrowIfDisposed();
            var images = new Dictionary<RegionEffectRequest, BitmapSource>();
            foreach (RegionEffectRequest request in requests.Distinct())
            {
                if (!_cache.TryGetValue(request, out CacheEntry? entry))
                {
                    throw new InvalidOperationException(
                        "Current region effects must be prepared before exact rendering.");
                }

                _recency.Remove(entry.RecencyNode);
                _recency.AddLast(entry.RecencyNode);
                images.Add(request, entry.Image);
            }

            return new RegionEffectPreparedSnapshot(images);
        }
    }

    internal (int EntryCount, long ByteCount) GetCacheUsage()
    {
        lock (_sync)
            return (_cache.Count, _cachedBytes);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _generation++;
            _inFlightCancellation?.Cancel();
            _inFlightTask = null;
            _inFlightSource = null;
            _inFlightDesiredRequests = [];
            _inFlightCancellation = null;
            _cache.Clear();
            _recency.Clear();
            _cachedBytes = 0;
        }
    }

    private async Task AwaitWithCallerCancellationAsync(
        Task preparation,
        CancellationToken cancellationToken)
    {
        using CancellationTokenRegistration registration =
            cancellationToken.Register(
                static state =>
                {
                    var ownership =
                        (Tuple<RegionEffectPipeline, Task>)state!;
                    ownership.Item1.CancelIfCurrent(ownership.Item2);
                },
                Tuple.Create(this, preparation));
        try
        {
            await preparation.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private void CancelIfCurrent(Task preparation)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_inFlightTask, preparation) &&
                !preparation.IsCompleted)
            {
                _inFlightCancellation?.Cancel();
            }
        }
    }

    private static void ObserveFault(Task preparation)
    {
        _ = preparation.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ExecuteBatch(
        long generation,
        BitmapSource source,
        RegionEffectRequest[] requests,
        CancellationTokenSource cancellation)
    {
        var completed = new List<(RegionEffectRequest Request, BitmapSource Image)>(
            requests.Length);
        try
        {
            _workerGate.Wait(cancellation.Token);
            try
            {
                foreach (RegionEffectRequest request in requests)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    BitmapSource image = _processor.Process(
                        source,
                        request,
                        cancellation.Token);
                    if (!image.IsFrozen)
                    {
                        throw new InvalidOperationException(
                            "Region-effect workers must return frozen bitmaps.");
                    }
                    if (image.PixelWidth != request.PixelRegion.Width ||
                        image.PixelHeight != request.PixelRegion.Height ||
                        image.Format != PixelFormats.Bgra32)
                    {
                        throw new InvalidOperationException(
                            "Region-effect output shape did not match the request.");
                    }
                    completed.Add((request, image));
                }
            }
            finally
            {
                _workerGate.Release();
            }

            lock (_sync)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                if (generation != _generation)
                    throw new OperationCanceledException(cancellation.Token);

                foreach ((RegionEffectRequest request, BitmapSource image) in completed)
                    AddToCache(request, image);
            }
        }
        finally
        {
            lock (_sync)
            {
                if (generation == _generation)
                {
                    _inFlightTask = null;
                    _inFlightSource = null;
                    _inFlightDesiredRequests = [];
                    _inFlightCancellation = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private void AddToCache(
        RegionEffectRequest request,
        BitmapSource image)
    {
        if (_cache.TryGetValue(request, out CacheEntry? existing))
        {
            _cachedBytes -= existing.ByteCount;
            _recency.Remove(existing.RecencyNode);
            _cache.Remove(request);
        }

        var node = new LinkedListNode<RegionEffectRequest>(request);
        long byteCount = request.OutputByteCount;
        _recency.AddLast(node);
        _cache.Add(request, new CacheEntry(image, byteCount, node));
        _cachedBytes = checked(_cachedBytes + byteCount);

        while (_cache.Count > _cacheEntryLimit ||
               _cachedBytes > _cacheByteLimit)
        {
            LinkedListNode<RegionEffectRequest> oldest =
                _recency.First
                ?? throw new InvalidOperationException(
                    "Region-effect cache accounting became inconsistent.");
            _recency.RemoveFirst();
            CacheEntry removed = _cache[oldest.Value];
            _cache.Remove(oldest.Value);
            _cachedBytes -= removed.ByteCount;
        }
    }

    private void ValidateBatch(
        BitmapSource source,
        IReadOnlyList<RegionEffectRequest> requests)
    {
        if (requests.Count > _cacheEntryLimit)
        {
            throw new InvalidOperationException(
                "The current region effects exceed the document cache entry budget.");
        }

        long requestedBytes = 0;
        long estimatedWorkingBytes = 0;
        foreach (RegionEffectRequest request in requests)
        {
            Int32Rect region = request.PixelRegion;
            if (request.BackgroundIdentity <= 0 ||
                region.X < 0 ||
                region.Y < 0 ||
                region.Width <= 0 ||
                region.Height <= 0 ||
                region.X > source.PixelWidth - region.Width ||
                region.Y > source.PixelHeight - region.Height)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requests),
                    "A region-effect request was outside its frozen source.");
            }

            long requestBytes = request.OutputByteCount;
            if (requestBytes > _cacheByteLimit)
            {
                throw new InvalidOperationException(
                    "A region effect exceeds the document cache budget.");
            }
            requestedBytes = checked(requestedBytes + requestBytes);
            long workingMultiplier = request.Kind == RegionEffectKind.BoxBlur
                ? 3
                : 2;
            estimatedWorkingBytes = checked(
                estimatedWorkingBytes +
                checked(requestBytes * workingMultiplier));
        }

        if (requestedBytes > _cacheByteLimit)
        {
            throw new InvalidOperationException(
                "The current region effects exceed the document cache budget.");
        }
        if (estimatedWorkingBytes > _workingByteLimit)
        {
            throw new InvalidOperationException(
                "The current region effects exceed the worker memory budget.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
