using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinShot.App.Editor;

public enum ToolKind
{
    Select,
    Arrow,
    Line,
    Rect,
    Ellipse,
    Freehand,
    Highlighter,
    Text,
    Step,
    Blur,
    Pixelate,
    Spotlight,
    Crop,
}

/// <summary>A vector annotation over the capture. Coordinates are in image pixel space.</summary>
public abstract class Annotation
{
    public abstract Annotation Clone();
    public abstract void Render(DrawingContext dc, EditorDocument document);
    public abstract Rect GetBounds(EditorDocument document);
    public abstract void Offset(Vector delta);

    public virtual bool HitTest(Point p, EditorDocument document)
    {
        var bounds = GetBounds(document);
        bounds.Inflate(6, 6);
        return bounds.Contains(p);
    }

    protected static Pen MakePen(Color color, double thickness) => new(new SolidColorBrush(color), thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };

    protected static double DistanceToSegment(Point p, Point a, Point b)
    {
        Vector ab = b - a;
        double lengthSq = ab.LengthSquared;
        if (lengthSq < 0.001)
            return (p - a).Length;
        double t = Math.Clamp(Vector.Multiply(p - a, ab) / lengthSq, 0, 1);
        return (p - (a + ab * t)).Length;
    }
}

/// <summary>Arrow, line, rectangle or ellipse defined by two drag points.</summary>
public sealed class ShapeAnnotation : Annotation
{
    public ToolKind Kind;
    public Point Start;
    public Point End;
    public Color Color;
    public double Thickness;

    public override Annotation Clone() => (ShapeAnnotation)MemberwiseClone();

    public override void Render(DrawingContext dc, EditorDocument document)
    {
        var pen = MakePen(Color, Thickness);
        switch (Kind)
        {
            case ToolKind.Line:
                dc.DrawLine(pen, Start, End);
                break;

            case ToolKind.Arrow:
                RenderArrow(dc, pen);
                break;

            case ToolKind.Rect:
                dc.DrawRoundedRectangle(null, pen, new Rect(Start, End), 2, 2);
                break;

            case ToolKind.Ellipse:
                var rect = new Rect(Start, End);
                dc.DrawEllipse(null, pen,
                    new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2),
                    rect.Width / 2, rect.Height / 2);
                break;
        }
    }

    private void RenderArrow(DrawingContext dc, Pen pen)
    {
        Vector direction = End - Start;
        if (direction.Length < 0.5)
            return;
        direction.Normalize();

        double headLength = Math.Max(10, Thickness * 4);
        double headWidth = headLength * 0.75;
        Point headBase = End - direction * headLength;
        var normal = new Vector(-direction.Y, direction.X);

        dc.DrawLine(pen, Start, headBase);

        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(End, isFilled: true, isClosed: true);
            ctx.LineTo(headBase + normal * headWidth / 2, false, false);
            ctx.LineTo(headBase - normal * headWidth / 2, false, false);
        }
        head.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Color), null, head);
    }

    public override Rect GetBounds(EditorDocument document)
    {
        var rect = new Rect(Start, End);
        rect.Inflate(Thickness, Thickness);
        return rect;
    }

    public override bool HitTest(Point p, EditorDocument document)
    {
        if (Kind is ToolKind.Line or ToolKind.Arrow)
            return DistanceToSegment(p, Start, End) <= Thickness + 6;
        return base.HitTest(p, document);
    }

    public override void Offset(Vector delta)
    {
        Start += delta;
        End += delta;
    }
}

/// <summary>Freehand pen stroke or highlighter stroke.</summary>
public sealed class FreehandAnnotation : Annotation
{
    public List<Point> Points = new();
    public Color Color;
    public double Thickness;
    public bool IsHighlighter;

    public override Annotation Clone()
    {
        var copy = (FreehandAnnotation)MemberwiseClone();
        copy.Points = new List<Point>(Points);
        return copy;
    }

    public override void Render(DrawingContext dc, EditorDocument document)
    {
        if (Points.Count < 2)
            return;

        Color color = IsHighlighter ? Color.FromArgb(0x59, Color.R, Color.G, Color.B) : Color;
        double thickness = IsHighlighter ? Thickness * 4.5 : Thickness;
        var pen = MakePen(color, thickness);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(Points[0], isFilled: false, isClosed: false);
            ctx.PolyLineTo(Points.Skip(1).ToList(), true, true);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    public override Rect GetBounds(EditorDocument document)
    {
        if (Points.Count == 0)
            return Rect.Empty;
        double minX = Points.Min(p => p.X), maxX = Points.Max(p => p.X);
        double minY = Points.Min(p => p.Y), maxY = Points.Max(p => p.Y);
        var rect = new Rect(new Point(minX, minY), new Point(maxX, maxY));
        rect.Inflate(Thickness * (IsHighlighter ? 3 : 1), Thickness * (IsHighlighter ? 3 : 1));
        return rect;
    }

    public override void Offset(Vector delta)
    {
        for (int i = 0; i < Points.Count; i++)
            Points[i] += delta;
    }
}

public sealed class TextAnnotation : Annotation
{
    private static readonly Typeface Typeface = new(new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public Point Position;
    public string Text = "";
    public Color Color;
    public double FontSize;

    public override Annotation Clone() => (TextAnnotation)MemberwiseClone();

    private FormattedText Format(Color color) => new(
        Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        Typeface, FontSize, new SolidColorBrush(color), 1.0);

    public override void Render(DrawingContext dc, EditorDocument document)
    {
        if (string.IsNullOrWhiteSpace(Text))
            return;
        // Soft dark offset copy keeps light text readable on light backgrounds.
        dc.DrawText(Format(Color.FromArgb(0x66, 0x00, 0x00, 0x00)), Position + new Vector(1.2, 1.6));
        dc.DrawText(Format(Color), Position);
    }

    public override Rect GetBounds(EditorDocument document)
    {
        if (string.IsNullOrWhiteSpace(Text))
            return new Rect(Position, new Size(1, FontSize));
        var format = Format(Color);
        return new Rect(Position, new Size(format.WidthIncludingTrailingWhitespace, format.Height));
    }

    public override void Offset(Vector delta) => Position += delta;
}

/// <summary>Auto-numbered step marker; numbers follow list order so deletions renumber cleanly.</summary>
public sealed class StepAnnotation : Annotation
{
    private static readonly Typeface Typeface = new(new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public Point Center;
    public Color Color;
    public double Radius = 15;

    public override Annotation Clone() => (StepAnnotation)MemberwiseClone();

    public override void Render(DrawingContext dc, EditorDocument document)
    {
        int number = document.GetStepNumber(this);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x59, 0x00, 0x00, 0x00)), null,
            Center + new Vector(0, 1.5), Radius + 1, Radius + 1);
        dc.DrawEllipse(new SolidColorBrush(Color), new Pen(Brushes.White, 1.5), Center, Radius, Radius);

        var text = new FormattedText(number.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface, Radius, Brushes.White, 1.0);
        dc.DrawText(text, new Point(Center.X - text.Width / 2, Center.Y - text.Height / 2));
    }

    public override Rect GetBounds(EditorDocument document) =>
        new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);

    public override void Offset(Vector delta) => Center += delta;
}

/// <summary>Blur or pixelate over a region of the background image.</summary>
public sealed class RegionEffectAnnotation : Annotation
{
    public Rect Region;
    public bool IsPixelate;

    public override Annotation Clone() =>
        (RegionEffectAnnotation)MemberwiseClone();

    public override void Render(DrawingContext dc, EditorDocument document)
    {
        if (!TryCreateRequest(document, out RegionEffectRequest request, out Rect clipped))
            return;

        if (document.TryGetPreparedRegionEffect(request, out BitmapSource? prepared))
            dc.DrawImage(prepared, clipped);
        else
            dc.DrawRectangle(SystemColors.ControlDarkBrush, null, clipped);
    }

    public override Rect GetBounds(EditorDocument document) => Region;

    public override void Offset(Vector delta)
    {
        Region = new Rect(Region.X + delta.X, Region.Y + delta.Y, Region.Width, Region.Height);
    }

    internal bool TryCreateRequest(
        EditorDocument document,
        out RegionEffectRequest request,
        out Rect clipped)
    {
        clipped = Rect.Intersect(
            Region,
            new Rect(
                0,
                0,
                document.Background.PixelWidth,
                document.Background.PixelHeight));
        if (clipped.IsEmpty || clipped.Width < 2 || clipped.Height < 2)
        {
            request = default;
            return false;
        }

        var pixelRegion = new Int32Rect(
            (int)clipped.X,
            (int)clipped.Y,
            Math.Max(2, (int)clipped.Width),
            Math.Max(2, (int)clipped.Height));
        if (IsPixelate)
        {
            request = new RegionEffectRequest(
                document.BackgroundIdentity,
                pixelRegion,
                RegionEffectKind.Pixelate,
                Math.Max(
                    6,
                    (int)(Math.Min(
                        pixelRegion.Width,
                        pixelRegion.Height) / 16.0)),
                Passes: 1);
        }
        else
        {
            request = new RegionEffectRequest(
                document.BackgroundIdentity,
                pixelRegion,
                RegionEffectKind.BoxBlur,
                Parameter: 7,
                Passes: 3);
        }

        return true;
    }
}

/// <summary>Dims everything except the region — draws attention like a stage spotlight.</summary>
public sealed class SpotlightAnnotation : Annotation
{
    public Rect Region;

    public override Annotation Clone() => (SpotlightAnnotation)MemberwiseClone();

    public override void Render(DrawingContext dc, EditorDocument document)
    {
        var full = new Rect(0, 0, document.Background.PixelWidth, document.Background.PixelHeight);
        var mask = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(full),
            new RectangleGeometry(Region, 6, 6));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x8C, 0x00, 0x00, 0x00)), null, mask);
    }

    public override Rect GetBounds(EditorDocument document) => Region;

    public override void Offset(Vector delta) =>
        Region = new Rect(Region.X + delta.X, Region.Y + delta.Y, Region.Width, Region.Height);
}
