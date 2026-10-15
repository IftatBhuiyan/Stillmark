using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace WinShot.App.Editor;

/// <summary>
/// The drawing surface: renders the document scaled-to-fit and routes mouse input
/// to the active tool. All annotation coordinates are image pixels; this element
/// converts to/from view space.
/// </summary>
public sealed class EditorCanvas : FrameworkElement
{
    private const double Padding = 16;
    private const double MinZoomFactor = 0.25;
    private const double MaxZoomFactor = 4.0;

    public EditorDocument? Document { get; private set; }
    private ToolKind _activeTool = ToolKind.Arrow;
    private EditorKeyboardInteraction? _keyboardInteraction;
    private int _keyboardImageWidth;
    private int _keyboardImageHeight;
    private long _regionEffectPreparationGeneration;
    private string? _lastRegionEffectFailure;
    private CancellationTokenSource? _regionEffectPreviewCancellation;
    private double _zoomFactor = 1.0;
    private Vector _panImage;

    public ToolKind ActiveTool
    {
        get => _activeTool;
        set
        {
            if (_activeTool == value)
                return;

            _activeTool = value;
            if (_keyboardInteraction is not null)
            {
                _keyboardInteraction.SetTool(value);
                UpdateKeyboardAccessibility();
                InvalidateVisual();
            }
        }
    }
    public Color CurrentColor { get; set; } = Color.FromRgb(0xFF, 0x45, 0x3A);
    public double CurrentThickness { get; set; } = 3.5;
    public double CurrentFontSize { get; set; } = 22;

    /// <summary>Raised when the text tool wants an edit box at an image-space point.</summary>
    public event Action<Point>? TextRequested;

    /// <summary>Raised only for discrete keyboard gesture results, not cursor movement.</summary>
    public event Action<string>? KeyboardAnnouncementRequested;

    /// <summary>Raised for bounded asynchronous preview failures that need a live status surface.</summary>
    public event Action<string>? RegionEffectStatusChanged;

    /// <summary>Raised when the viewport zoom or pan changes.</summary>
    public event Action? ViewportChanged;

    private Annotation? _draft;
    private Rect? _regionDraft;      // crop / blur / pixelate / spotlight drag rectangle
    private Point _pressImage;
    private bool _pressed;
    private Annotation? _moving;
    private bool _moveUndoPushed;
    private Point _moveLast;

    public EditorCanvas()
    {
        Focusable = true;
        IsKeyboardFocusWithinChanged += (_, _) => InvalidateVisual();
        Unloaded += (_, _) =>
        {
            _regionEffectPreviewCancellation?.Cancel();
            _regionEffectPreviewCancellation = null;
        };
    }

    public void SetDocument(EditorDocument document)
    {
        if (Document is not null)
        {
            _regionEffectPreviewCancellation?.Cancel();
            Document.Changed -= OnDocumentChanged;
        }
        Document = document;
        ResetViewport();
        document.Changed += OnDocumentChanged;
        ResetKeyboardInteraction(document, initialCursor: null);
        QueueRegionEffectPreparation(document);
        InvalidateVisual();
    }

    private void OnDocumentChanged()
    {
        if (Document is { } document &&
            (document.Background.PixelWidth != _keyboardImageWidth ||
             document.Background.PixelHeight != _keyboardImageHeight))
        {
            ResetKeyboardInteraction(document, _keyboardInteraction?.Cursor);
        }

        if (Document is { } current)
            QueueRegionEffectPreparation(current);
        InvalidateVisual();
    }

    private void ResetViewport()
    {
        _zoomFactor = 1.0;
        _panImage = default;
        ViewportChanged?.Invoke();
    }

    private void QueueRegionEffectPreparation(EditorDocument document)
    {
        _regionEffectPreviewCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _regionEffectPreviewCancellation = cancellation;
        long generation = ++_regionEffectPreparationGeneration;
        _ = PrepareRegionEffectsAsync(
            document,
            generation,
            cancellation);
    }

    private async Task PrepareRegionEffectsAsync(
        EditorDocument document,
        long generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            await document.PrepareRegionEffectsAsync(cancellation.Token);
            if (ReferenceEquals(Document, document) &&
                generation == _regionEffectPreparationGeneration)
            {
                _lastRegionEffectFailure = null;
                UpdateKeyboardAccessibility();
                InvalidateVisual();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer document state superseded this preview request.
        }
        catch (ObjectDisposedException)
        {
            // The editor closed while its bounded worker was winding down.
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(Document, document) ||
                generation != _regionEffectPreparationGeneration)
            {
                return;
            }

            string failure = ex.GetType().FullName ?? ex.GetType().Name;
            if (!string.Equals(
                    failure,
                    _lastRegionEffectFailure,
                    StringComparison.Ordinal))
            {
                _lastRegionEffectFailure = failure;
                App.LogError(ex);
            }
            AutomationProperties.SetItemStatus(
                this,
                "Region effect preview is unavailable. Exact output will retry.");
            RegionEffectStatusChanged?.Invoke(
                "Region effect preview is unavailable. Exact output will retry.");
            InvalidateVisual();
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_regionEffectPreviewCancellation, cancellation))
                _regionEffectPreviewCancellation = null;
        }
    }

    private void ResetKeyboardInteraction(
        EditorDocument document,
        Point? initialCursor)
    {
        _keyboardImageWidth = document.Background.PixelWidth;
        _keyboardImageHeight = document.Background.PixelHeight;
        _keyboardInteraction = new EditorKeyboardInteraction(
            _keyboardImageWidth,
            _keyboardImageHeight,
            ActiveTool,
            initialCursor);
        UpdateKeyboardAccessibility();
    }

    // ---- Coordinate mapping ----

    /// <summary>Backdrop margin around the image (image-space units), when a backdrop is active.</summary>
    private double BackdropPad => Document?.Backdrop.Fill is null ? 0 : Document!.Backdrop.Padding;

    private (double Scale, Point Offset) GetLayout()
    {
        if (Document is null || ActualWidth <= 0)
            return (1, new Point());
        double cw = Document.Background.PixelWidth + BackdropPad * 2;
        double ch = Document.Background.PixelHeight + BackdropPad * 2;
        double fitScale = Math.Min(1.0, Math.Min(
            (ActualWidth - Padding * 2) / cw,
            (ActualHeight - Padding * 2) / ch));
        double scale = Math.Max(fitScale * _zoomFactor, 0.02);
        Point centered = new((ActualWidth - cw * scale) / 2, (ActualHeight - ch * scale) / 2);
        return (scale, centered + _panImage * scale);
    }

    public Point ViewToImage(Point view)
    {
        var (scale, offset) = GetLayout();
        double x = (view.X - offset.X) / scale - BackdropPad;
        double y = (view.Y - offset.Y) / scale - BackdropPad;
        return new Point(
            Math.Clamp(x, 0, Document?.Background.PixelWidth ?? 0),
            Math.Clamp(y, 0, Document?.Background.PixelHeight ?? 0));
    }

    public Point ImageToView(Point image)
    {
        var (scale, offset) = GetLayout();
        return new Point(
            (image.X + BackdropPad) * scale + offset.X,
            (image.Y + BackdropPad) * scale + offset.Y);
    }

    public double ViewScale => GetLayout().Scale;

    public double ZoomFactor => _zoomFactor;

    // ---- Rendering ----

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent hit-test surface even outside the image.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Document is null)
            return;

        var (scale, offset) = GetLayout();
        double iw = Document.Background.PixelWidth, ih = Document.Background.PixelHeight;
        double pad = BackdropPad;

        dc.PushTransform(new TranslateTransform(offset.X, offset.Y));
        dc.PushTransform(new ScaleTransform(scale, scale));

        bool hasBackdrop = Document.Backdrop.Fill is not null;
        if (hasBackdrop)
        {
            dc.DrawRectangle(Document.Backdrop.Fill, null, new Rect(0, 0, iw + pad * 2, ih + pad * 2));
            dc.PushTransform(new TranslateTransform(pad, pad));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, iw, ih),
                Document.Backdrop.CornerRadius, Document.Backdrop.CornerRadius));
        }

        dc.DrawImage(Document.Background, new Rect(0, 0, iw, ih));

        foreach (var annotation in Document.Annotations)
            annotation.Render(dc, Document);
        _draft?.Render(dc, Document);

        if (_regionDraft is Rect region)
        {
            var dashed = new Pen(Brushes.White, 1.5 / scale) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
            if (ActiveTool == ToolKind.Crop)
            {
                var mask = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new RectangleGeometry(new Rect(0, 0, iw, ih)), new RectangleGeometry(region));
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x73, 0x00, 0x00, 0x00)), null, mask);
            }
            dc.DrawRectangle(null, dashed, region);
        }

        if (Document.Selected is Annotation selected)
        {
            var bounds = selected.GetBounds(Document);
            bounds.Inflate(4 / scale, 4 / scale);
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x4D, 0x8D, 0xFF)), 1.5 / scale)
            {
                DashStyle = new DashStyle(new double[] { 4, 3 }, 0),
            };
            dc.DrawRectangle(null, pen, bounds);
        }

        if (IsKeyboardFocused && _keyboardInteraction is { } keyboard)
            DrawKeyboardCursor(dc, keyboard, scale);

        if (hasBackdrop)
        {
            dc.Pop(); // clip
            dc.Pop(); // pad translate
        }
        dc.Pop();
        dc.Pop();

        if (IsKeyboardFocused)
        {
            var focusPen = new Pen(SystemColors.HighlightBrush, 2);
            dc.DrawRectangle(
                null,
                focusPen,
                new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)));
        }
    }

    private static void DrawKeyboardCursor(
        DrawingContext dc,
        EditorKeyboardInteraction keyboard,
        double scale)
    {
        double radius = 7 / scale;
        double thickness = 1.5 / scale;
        var cursorPen = new Pen(SystemColors.HighlightBrush, thickness);
        var contrastPen = new Pen(SystemColors.WindowBrush, 3 / scale);
        Point cursor = keyboard.Cursor;

        dc.DrawEllipse(
            SystemColors.WindowBrush,
            cursorPen,
            cursor,
            3 / scale,
            3 / scale);
        dc.DrawLine(
            contrastPen,
            cursor + new Vector(-radius, 0),
            cursor + new Vector(radius, 0));
        dc.DrawLine(
            contrastPen,
            cursor + new Vector(0, -radius),
            cursor + new Vector(0, radius));
        dc.DrawLine(
            cursorPen,
            cursor + new Vector(-radius, 0),
            cursor + new Vector(radius, 0));
        dc.DrawLine(
            cursorPen,
            cursor + new Vector(0, -radius),
            cursor + new Vector(0, radius));

        if (keyboard.Anchor is Point anchor)
        {
            var anchorPen = new Pen(SystemColors.HighlightBrush, thickness)
            {
                DashStyle = new DashStyle([3 / scale, 2 / scale], 0),
            };
            dc.DrawEllipse(
                null,
                anchorPen,
                anchor,
                9 / scale,
                9 / scale);
        }
    }

    // ---- Input ----

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Document is null || e.Delta == 0)
            return;

        Focus();
        Point pointer = e.GetPosition(this);
        Point anchor = ViewToImage(pointer);
        double next = Math.Clamp(
            _zoomFactor * Math.Pow(1.1, e.Delta / 120.0),
            MinZoomFactor,
            MaxZoomFactor);
        if (Math.Abs(next - _zoomFactor) > 0.0001)
        {
            _zoomFactor = next;
            var (scale, centered) = GetLayoutWithoutPan();
            _panImage = new Vector(
                (pointer.X - centered.X) / scale - (anchor.X + BackdropPad),
                (pointer.Y - centered.Y) / scale - (anchor.Y + BackdropPad));
            ViewportChanged?.Invoke();
            InvalidateVisual();
        }

        e.Handled = true;
    }

    private (double Scale, Point Offset) GetLayoutWithoutPan()
    {
        if (Document is null || ActualWidth <= 0)
            return (1, new Point());
        double cw = Document.Background.PixelWidth + BackdropPad * 2;
        double ch = Document.Background.PixelHeight + BackdropPad * 2;
        double fitScale = Math.Min(1.0, Math.Min(
            (ActualWidth - Padding * 2) / cw,
            (ActualHeight - Padding * 2) / ch));
        double scale = Math.Max(fitScale * _zoomFactor, 0.02);
        return (scale, new Point((ActualWidth - cw * scale) / 2, (ActualHeight - ch * scale) / 2));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Document is null)
            return;
        Focus();
        CaptureMouse();
        _pressed = true;
        _pressImage = ViewToImage(e.GetPosition(this));
        if (_keyboardInteraction is { } keyboard)
        {
            keyboard.Cancel();
            keyboard.MoveTo(_pressImage);
            UpdateKeyboardAccessibility();
        }

        switch (ActiveTool)
        {
            case ToolKind.Select:
                Document.Selected = HitTest(_pressImage);
                _moving = Document.Selected;
                _moveUndoPushed = false;
                _moveLast = _pressImage;
                Document.NotifyChanged();
                break;

            case ToolKind.Text:
                _pressed = false;
                ReleaseMouseCapture();
                TextRequested?.Invoke(_pressImage);
                break;

            case ToolKind.Step:
                _pressed = false;
                ReleaseMouseCapture();
                Document.Add(new StepAnnotation { Center = _pressImage, Color = CurrentColor });
                break;

            case ToolKind.Arrow or ToolKind.Line or ToolKind.Rect or ToolKind.Ellipse:
                _draft = new ShapeAnnotation
                {
                    Kind = ActiveTool,
                    Start = _pressImage,
                    End = _pressImage,
                    Color = CurrentColor,
                    Thickness = CurrentThickness,
                };
                break;

            case ToolKind.Freehand or ToolKind.Highlighter:
                _draft = new FreehandAnnotation
                {
                    Points = { _pressImage },
                    Color = CurrentColor,
                    Thickness = CurrentThickness,
                    IsHighlighter = ActiveTool == ToolKind.Highlighter,
                };
                break;

            case ToolKind.Blur or ToolKind.Pixelate or ToolKind.Spotlight or ToolKind.Crop:
                _regionDraft = new Rect(_pressImage, _pressImage);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Document is null || _keyboardInteraction is not { } keyboard)
            return;

        EditorKeyboardAction action;
        switch (e.Key)
        {
            case Key.Left:
                action = keyboard.Move(
                    EditorKeyboardDirection.Left,
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                break;
            case Key.Right:
                action = keyboard.Move(
                    EditorKeyboardDirection.Right,
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                break;
            case Key.Up:
                action = keyboard.Move(
                    EditorKeyboardDirection.Up,
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                break;
            case Key.Down:
                action = keyboard.Move(
                    EditorKeyboardDirection.Down,
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                break;
            case Key.Enter or Key.Space:
                if (e.IsRepeat)
                {
                    e.Handled = true;
                    return;
                }
                action = keyboard.Activate();
                break;
            case Key.Escape:
                action = keyboard.Cancel();
                if (action.Kind == EditorKeyboardActionKind.None)
                {
                    CancelDraft();
                    if (Document.Selected is not null)
                    {
                        Document.Selected = null;
                        Document.NotifyChanged();
                        KeyboardAnnouncementRequested?.Invoke(
                            "Annotation selection cleared.");
                    }
                }
                break;
            default:
                return;
        }

        ApplyKeyboardAction(action);
        UpdateKeyboardAccessibility();
        InvalidateVisual();
        e.Handled = true;
    }

    private void ApplyKeyboardAction(EditorKeyboardAction action)
    {
        if (Document is not { } document)
            return;

        string announcement = action.Announcement;
        switch (action.Kind)
        {
            case EditorKeyboardActionKind.SelectionRequested:
                document.Selected = HitTest(action.Start);
                document.NotifyChanged();
                announcement = document.Selected is null
                    ? $"No annotation found at {FormatKeyboardPoint(action.Start)}."
                    : $"Annotation selected at {FormatKeyboardPoint(action.Start)}.";
                break;
            case EditorKeyboardActionKind.TextRequested:
                TextRequested?.Invoke(action.Start);
                break;
            case EditorKeyboardActionKind.StepRequested:
                document.Add(new StepAnnotation
                {
                    Center = action.Start,
                    Color = CurrentColor,
                });
                break;
            case EditorKeyboardActionKind.ShapeCommitted:
                document.Add(new ShapeAnnotation
                {
                    Kind = action.Tool,
                    Start = action.Start,
                    End = action.End,
                    Color = CurrentColor,
                    Thickness = CurrentThickness,
                });
                break;
            case EditorKeyboardActionKind.RegionCommitted:
                ApplyKeyboardRegion(document, action);
                break;
            case EditorKeyboardActionKind.StrokeCommitted
                when action.Points is { Count: >= 2 } points:
                var stroke = new FreehandAnnotation
                {
                    Color = CurrentColor,
                    Thickness = CurrentThickness,
                    IsHighlighter = action.Tool == ToolKind.Highlighter,
                };
                stroke.Points.AddRange(points);
                document.Add(stroke);
                break;
        }

        if (!string.IsNullOrEmpty(announcement))
            KeyboardAnnouncementRequested?.Invoke(announcement);
    }

    private static string FormatKeyboardPoint(Point point) =>
        FormattableString.Invariant(
            $"{point.X:0.##}, {point.Y:0.##} pixels");

    private static void ApplyKeyboardRegion(
        EditorDocument document,
        EditorKeyboardAction action)
    {
        var region = new Rect(action.Start, action.End);
        switch (action.Tool)
        {
            case ToolKind.Blur:
                document.Add(new RegionEffectAnnotation
                {
                    Region = region,
                    IsPixelate = false,
                });
                break;
            case ToolKind.Pixelate:
                document.Add(new RegionEffectAnnotation
                {
                    Region = region,
                    IsPixelate = true,
                });
                break;
            case ToolKind.Spotlight:
                document.Add(new SpotlightAnnotation { Region = region });
                break;
            case ToolKind.Crop:
                document.Crop(new Int32Rect(
                    (int)Math.Round(region.X),
                    (int)Math.Round(region.Y),
                    Math.Max(1, (int)Math.Round(region.Width)),
                    Math.Max(1, (int)Math.Round(region.Height))));
                break;
        }
    }

    private void UpdateKeyboardAccessibility()
    {
        if (_keyboardInteraction is not { } keyboard)
            return;

        AutomationProperties.SetItemStatus(this, keyboard.Status);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed || Document is null)
            return;
        var current = ViewToImage(e.GetPosition(this));

        if (_moving is not null)
        {
            var delta = current - _moveLast;
            if (delta.Length > 0)
            {
                if (!_moveUndoPushed)
                {
                    Document.PushUndo();
                    _moveUndoPushed = true;
                }
                _moving.Offset(delta);
                _moveLast = current;
                Document.NotifyChanged();
            }
            return;
        }

        switch (_draft)
        {
            case ShapeAnnotation shape:
                shape.End = current;
                break;
            case FreehandAnnotation freehand:
                if ((current - freehand.Points[^1]).Length >= 1.5)
                    freehand.Points.Add(current);
                break;
        }
        if (_regionDraft is not null)
            _regionDraft = new Rect(_pressImage, current);

        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_pressed || Document is null)
            return;
        _pressed = false;
        ReleaseMouseCapture();
        _moving = null;

        if (_draft is not null)
        {
            bool bigEnough = _draft switch
            {
                ShapeAnnotation s => (s.End - s.Start).Length >= 3,
                FreehandAnnotation f => f.Points.Count >= 2,
                _ => true,
            };
            if (bigEnough)
                Document.Add(_draft);
            _draft = null;
        }

        if (_regionDraft is Rect region)
        {
            _regionDraft = null;
            if (region.Width >= 4 && region.Height >= 4)
            {
                switch (ActiveTool)
                {
                    case ToolKind.Blur:
                        Document.Add(new RegionEffectAnnotation { Region = region, IsPixelate = false });
                        break;
                    case ToolKind.Pixelate:
                        Document.Add(new RegionEffectAnnotation { Region = region, IsPixelate = true });
                        break;
                    case ToolKind.Spotlight:
                        Document.Add(new SpotlightAnnotation { Region = region });
                        break;
                    case ToolKind.Crop:
                        Document.Crop(new System.Windows.Int32Rect(
                            (int)Math.Round(region.X), (int)Math.Round(region.Y),
                            Math.Max(1, (int)Math.Round(region.Width)),
                            Math.Max(1, (int)Math.Round(region.Height))));
                        break;
                }
            }
        }
        InvalidateVisual();
    }

    public void CancelDraft()
    {
        _draft = null;
        _regionDraft = null;
        _pressed = false;
        _moving = null;
        _keyboardInteraction?.Cancel();
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        UpdateKeyboardAccessibility();
        InvalidateVisual();
    }

    private Annotation? HitTest(Point imagePoint)
    {
        if (Document is null)
            return null;
        for (int i = Document.Annotations.Count - 1; i >= 0; i--)
        {
            if (Document.Annotations[i].HitTest(imagePoint, Document))
                return Document.Annotations[i];
        }
        return null;
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new FrameworkElementAutomationPeer(this);
}
