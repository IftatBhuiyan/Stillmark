using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinShot.App.Editor;

internal sealed class EditorDocumentChangedDuringOutputException : InvalidOperationException
{
    internal EditorDocumentChangedDuringOutputException()
        : base("The editor document changed while exact output was being prepared.")
    {
    }
}

/// <summary>Optional "beautify" wrap around the exported image: gradient margin, rounded corners, shadow.</summary>
public sealed class CanvasBackdrop
{
    /// <summary>Null disables the backdrop entirely.</summary>
    public Brush? Fill { get; set; }

    public double Padding { get; set; } = 56;
    public double CornerRadius { get; set; } = 12;
    public bool Shadow { get; set; } = true;
}

/// <summary>
/// The editable capture: background bitmap + ordered vector annotations, with
/// snapshot-based undo/redo. Annotations stay editable until final render.
/// </summary>
public sealed class EditorDocument : IDisposable
{
    private const int MaxUndoDepth = 50;

    private sealed record Snapshot(
        BitmapSource Background,
        int BackgroundVersion,
        long BackgroundIdentity,
        List<Annotation> Annotations,
        Brush? BackdropFill,
        double BackdropPadding,
        double BackdropCornerRadius,
        bool BackdropShadow);

    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    private readonly RegionEffectPipeline _regionEffectPipeline;
    private readonly Dispatcher? _ownerDispatcher;
    private RegionEffectPreparedSnapshot? _exactRegionEffectSnapshot;
    private long _nextBackgroundIdentity = 1;
    private bool _disposed;

    public BitmapSource Background { get; private set; }

    /// <summary>Bumped when the background changes (crop) so effect caches invalidate.</summary>
    public int BackgroundVersion { get; private set; }

    internal long BackgroundIdentity { get; private set; } = 1;

    public long Revision { get; private set; }

    public List<Annotation> Annotations { get; private set; } = new();
    public Annotation? Selected { get; set; }

    /// <summary>Backdrop styling is presentation-level and intentionally outside undo history.</summary>
    public CanvasBackdrop Backdrop { get; } = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public event Action? Changed;

    public EditorDocument(BitmapSource background)
        : this(background, processor: null)
    {
    }

    internal EditorDocument(
        BitmapSource background,
        IRegionEffectProcessor? processor,
        long regionEffectCacheByteLimit = RegionEffectPipeline.DefaultCacheByteLimit,
        int regionEffectCacheEntryLimit = RegionEffectPipeline.DefaultCacheEntryLimit)
    {
        ArgumentNullException.ThrowIfNull(background);
        _ownerDispatcher =
            SynchronizationContext.Current is DispatcherSynchronizationContext
                ? Dispatcher.CurrentDispatcher
                : null;
        Background = FreezeForWorker(background);
        _regionEffectPipeline = new RegionEffectPipeline(
            processor,
            regionEffectCacheByteLimit,
            regionEffectCacheEntryLimit);
    }

    public void Add(Annotation annotation)
    {
        VerifyOwnerThread();
        ThrowIfDisposed();
        PushUndo();
        Annotations.Add(annotation);
        NotifyChanged();
    }

    public void Delete(Annotation annotation)
    {
        VerifyOwnerThread();
        ThrowIfDisposed();
        if (!Annotations.Contains(annotation))
            return;
        PushUndo();
        Annotations.Remove(annotation);
        if (Selected == annotation)
            Selected = null;
        NotifyChanged();
    }

    public void Crop(Int32Rect region)
    {
        VerifyOwnerThread();
        ThrowIfDisposed();
        PushUndo();
        var cropped = new CroppedBitmap(Background, region);
        cropped.Freeze();
        Background = cropped;
        BackgroundVersion++;
        BackgroundIdentity = ++_nextBackgroundIdentity;
        var delta = new Vector(-region.X, -region.Y);
        foreach (var annotation in Annotations)
            annotation.Offset(delta);
        NotifyChanged();
    }

    /// <summary>Call once before a move-drag starts so the whole drag is one undo step.</summary>
    public void PushUndo()
    {
        VerifyOwnerThread();
        ThrowIfDisposed();
        _undo.Push(TakeSnapshot());
        if (_undo.Count > MaxUndoDepth)
        {
            var keep = _undo.Take(MaxUndoDepth).ToArray();
            _undo.Clear();
            for (int i = keep.Length - 1; i >= 0; i--)
                _undo.Push(keep[i]);
        }
        _redo.Clear();
    }

    public void Undo()
    {
        VerifyOwnerThread();
        ThrowIfDisposed();
        if (_undo.Count == 0)
            return;
        _redo.Push(TakeSnapshot());
        Restore(_undo.Pop());
    }

    public void Redo()
    {
        VerifyOwnerThread();
        ThrowIfDisposed();
        if (_redo.Count == 0)
            return;
        _undo.Push(TakeSnapshot());
        Restore(_redo.Pop());
    }

    private Snapshot TakeSnapshot() =>
        new(
            Background,
            BackgroundVersion,
            BackgroundIdentity,
            Annotations.Select(a => a.Clone()).ToList(),
            Backdrop.Fill,
            Backdrop.Padding,
            Backdrop.CornerRadius,
            Backdrop.Shadow);

    private void Restore(Snapshot snapshot)
    {
        Background = snapshot.Background;
        BackgroundVersion = snapshot.BackgroundVersion;
        BackgroundIdentity = snapshot.BackgroundIdentity;
        _nextBackgroundIdentity = Math.Max(
            _nextBackgroundIdentity,
            snapshot.BackgroundIdentity);
        Annotations = snapshot.Annotations.Select(a => a.Clone()).ToList();
        Backdrop.Fill = snapshot.BackdropFill;
        Backdrop.Padding = snapshot.BackdropPadding;
        Backdrop.CornerRadius = snapshot.BackdropCornerRadius;
        Backdrop.Shadow = snapshot.BackdropShadow;
        Selected = null;
        NotifyChanged();
    }

    public int GetStepNumber(StepAnnotation step)
    {
        int number = 0;
        foreach (var annotation in Annotations)
        {
            if (annotation is StepAnnotation s)
            {
                number++;
                if (ReferenceEquals(s, step))
                    return number;
            }
        }
        return number + 1; // a draft not yet in the list previews as the next number
    }

    public void NotifyChanged()
    {
        VerifyOwnerThread();
        ThrowIfDisposed();
        Revision++;
        Changed?.Invoke();
    }

    public async Task PrepareRegionEffectsAsync(
        CancellationToken cancellationToken = default)
    {
        VerifyOwnerThread();
        ThrowIfDisposed();
        while (true)
        {
            BitmapSource source = Background;
            long backgroundIdentity = BackgroundIdentity;
            RegionEffectRequest[] requests = GetCurrentRegionEffectRequests();
            try
            {
                await _regionEffectPipeline.PrepareAsync(
                    source,
                    requests,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                // A newer document state superseded this internal worker generation.
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (ReferenceEquals(source, Background) &&
                backgroundIdentity == BackgroundIdentity &&
                requests.SequenceEqual(GetCurrentRegionEffectRequests()) &&
                AreRegionEffectsPrepared())
            {
                return;
            }
        }
    }

    internal bool TryGetPreparedRegionEffect(
        RegionEffectRequest request,
        out BitmapSource? image)
    {
        if (_exactRegionEffectSnapshot is { } exact)
        {
            if (!exact.TryGet(request, out image))
            {
                throw new InvalidOperationException(
                    "An exact region-effect snapshot was incomplete.");
            }

            return true;
        }

        return _regionEffectPipeline.TryGet(request, out image);
    }

    internal bool AreRegionEffectsPrepared()
    {
        foreach (RegionEffectRequest request in GetCurrentRegionEffectRequests())
        {
            if (!_regionEffectPipeline.TryGet(request, out _))
                return false;
        }

        return true;
    }

    internal (int EntryCount, long ByteCount) GetRegionEffectCacheUsage() =>
        _regionEffectPipeline.GetCacheUsage();

    /// <summary>Flattens background + annotations into the final bitmap at 1:1 pixel scale.</summary>
    public BitmapSource RenderFinal()
    {
        VerifyOwnerThread();
        ThrowIfDisposed();
        if (_exactRegionEffectSnapshot is not null)
            throw new InvalidOperationException("Nested exact rendering is not supported.");

        int width = Background.PixelWidth, height = Background.PixelHeight;
        RegionEffectRequest[] requests = GetCurrentRegionEffectRequests();
        RegionEffectPreparedSnapshot exact =
            _regionEffectPipeline.CapturePreparedSnapshot(requests);
        _exactRegionEffectSnapshot = exact;
        try
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(Background, new Rect(0, 0, width, height));
                foreach (var annotation in Annotations)
                    annotation.Render(dc, this);
            }
            var bitmap = new RenderTargetBitmap(
                width,
                height,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            _exactRegionEffectSnapshot = null;
        }
    }

    /// <summary>Final render plus the optional backdrop wrap (gradient margin, rounded corners, shadow).</summary>
    public BitmapSource RenderExport()
    {
        VerifyOwnerThread();
        ThrowIfDisposed();
        var flat = RenderFinal();
        if (Backdrop.Fill is null)
            return flat;

        int padding = (int)Math.Round(Backdrop.Padding);
        int width = flat.PixelWidth + padding * 2;
        int height = flat.PixelHeight + padding * 2;

        var content = new Border
        {
            Width = flat.PixelWidth,
            Height = flat.PixelHeight,
            CornerRadius = new CornerRadius(Backdrop.CornerRadius),
            Background = new ImageBrush(flat) { Stretch = Stretch.Fill },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (Backdrop.Shadow)
        {
            content.Effect = new DropShadowEffect
            {
                BlurRadius = Math.Max(16, padding * 0.6),
                ShadowDepth = padding * 0.12,
                Direction = 270,
                Color = Colors.Black,
                Opacity = 0.4,
            };
        }

        var container = new Grid { Width = width, Height = height, Background = Backdrop.Fill };
        container.Children.Add(content);
        container.Measure(new Size(width, height));
        container.Arrange(new Rect(0, 0, width, height));

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(container);
        bitmap.Freeze();
        return bitmap;
    }

    public async Task<BitmapSource> RenderExportAsync(
        CancellationToken cancellationToken = default)
    {
        long expectedRevision = Revision;
        return await RenderExportAsync(expectedRevision, cancellationToken);
    }

    internal async Task<BitmapSource> RenderExportAsync(
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await PrepareRegionEffectsAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (Revision != expectedRevision)
            throw new EditorDocumentChangedDuringOutputException();
        return RenderExport();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Changed = null;
        _regionEffectPipeline.Dispose();
    }

    private RegionEffectRequest[] GetCurrentRegionEffectRequests() =>
        Annotations
            .OfType<RegionEffectAnnotation>()
            .Select(annotation =>
                annotation.TryCreateRequest(this, out RegionEffectRequest request, out _)
                    ? request
                    : (RegionEffectRequest?)null)
            .Where(request => request.HasValue)
            .Select(request => request!.Value)
            .Distinct()
            .ToArray();

    private static BitmapSource FreezeForWorker(BitmapSource background)
    {
        if (background.IsFrozen)
            return background;
        if (!background.CanFreeze)
        {
            throw new ArgumentException(
                "The editor background cannot be frozen for worker processing.",
                nameof(background));
        }

        BitmapSource frozen = background.CloneCurrentValue();
        frozen.Freeze();
        return frozen;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void VerifyOwnerThread()
    {
        if (_ownerDispatcher is not null &&
            !_ownerDispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "Editor document mutation and exact rendering must run on its owner thread.");
        }
    }
}
