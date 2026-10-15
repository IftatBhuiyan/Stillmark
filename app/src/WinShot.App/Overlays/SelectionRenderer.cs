using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.Capture;
using WinShot.Core;

namespace WinShot.App.Overlays;

/// <summary>
/// Draws the frozen screen, dim mask, hovered-window highlight,
/// selection rectangle, size label and cursor pointer.
/// </summary>
public sealed class SelectionRenderer : FrameworkElement
{
    private static readonly Brush DimBrush = CreateFrozen(new SolidColorBrush(Color.FromArgb(0x59, 0x00, 0x00, 0x00)));
    private static readonly Pen SelectionPen = CreateFrozen(new Pen(Brushes.White, 1.5));
    private static readonly Pen WindowSnapPen = CreateFrozen(new Pen(new SolidColorBrush(Color.FromArgb(0xFF, 0x4D, 0x8D, 0xFF)), 2.0));
    private static readonly Brush LabelBackground = CreateFrozen(new SolidColorBrush(Color.FromArgb(0xD0, 0x20, 0x20, 0x24)));
    private static readonly Typeface LabelTypeface = new(new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Pen PointerUnderlayPen = CreateFrozen(new Pen(new SolidColorBrush(Color.FromArgb(0x4D, 0x00, 0x00, 0x00)), 2.5));
    private static readonly Pen PointerPen = CreateFrozen(new Pen(Brushes.White, 1.0));

    // In a layered (AllowsTransparency) window fully transparent pixels are click-through, so the
    // live/freeze-off overlay paints an almost-invisible fill (alpha 1) to stay hit-testable everywhere.
    private static readonly Brush HitTestFill = CreateFrozen(new SolidColorBrush(Color.FromArgb(0x01, 0x00, 0x00, 0x00)));

    private readonly SelectionSession _session;
    private readonly MonitorInfo _monitor;
    private readonly BitmapSource? _background;

    public SelectionRenderer(SelectionSession session, MonitorInfo monitor, BitmapSource? background)
    {
        _session = session;
        _monitor = monitor;
        _background = background;
    }

    private static T CreateFrozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var full = new Rect(0, 0, width, height);
        if (_background is not null)
            dc.DrawImage(_background, full);
        else
            // Transparent (live) overlay: alpha-1 fill keeps every pixel — including the selection
            // hole and window-highlight area — hit-testable so clicks don't fall through to windows.
            dc.DrawRectangle(HitTestFill, null, full);

        var dpi = VisualTreeHelper.GetDpi(this);
        Point ToLocal(int px, int py) => new(
            (px - _monitor.Bounds.X) / dpi.DpiScaleX,
            (py - _monitor.Bounds.Y) / dpi.DpiScaleY);
        Rect ToLocalRect(PixelRect r) => new(ToLocal(r.X, r.Y), ToLocal(r.Right, r.Bottom));

        bool cursorHere = _monitor.Bounds.Contains(_session.CursorX, _session.CursorY);

        if (_session.SelectionRect is PixelRect sel)
        {
            var rect = ToLocalRect(sel);
            DrawMask(dc, full, rect);
            dc.DrawRectangle(null, SelectionPen, rect);
            DrawPill(dc, $"{sel.Width} × {sel.Height}", NearSelection(rect, full), dpi.PixelsPerDip, 13);
        }
        else if (_session.HoveredWindow is WindowInfo hovered && !hovered.Bounds.Intersect(_monitor.Bounds).IsEmpty)
        {
            var rect = ToLocalRect(hovered.Bounds.Intersect(_session.VirtualBounds));
            DrawMask(dc, full, rect);
            dc.DrawRoundedRectangle(null, WindowSnapPen, rect, 8, 8);
            if (cursorHere)
            {
                string title = hovered.Title.Length > 48 ? hovered.Title[..48] + "…" : hovered.Title;
                string label = string.IsNullOrWhiteSpace(title)
                    ? $"{hovered.Bounds.Width} × {hovered.Bounds.Height}"
                    : $"{title}   {hovered.Bounds.Width} × {hovered.Bounds.Height}";
                DrawPill(dc, label, NearSelection(rect, full), dpi.PixelsPerDip, 12);
            }
        }
        else
        {
            dc.DrawRectangle(DimBrush, null, full);
        }

        if (_session.Interactive && _session.Committed is PixelRect committed && !_session.IsDragging)
            DrawHandles(dc, ToLocalRect(committed.Intersect(_monitor.Bounds.Intersect(_session.VirtualBounds))), ToLocalRect(committed));

        if (cursorHere && _session.PointerVisible)
            DrawPointer(dc, ToLocal(_session.CursorX, _session.CursorY));
    }

    private static void DrawHandles(DrawingContext dc, Rect visible, Rect full)
    {
        const double Half = 4.5;
        foreach (var p in new[]
        {
            new Point(full.Left, full.Top), new Point(full.Right, full.Top),
            new Point(full.Left, full.Bottom), new Point(full.Right, full.Bottom),
        })
        {
            if (p.X < visible.Left - 1 || p.X > visible.Right + 1 || p.Y < visible.Top - 1 || p.Y > visible.Bottom + 1)
                continue;
            var rect = new Rect(p.X - Half, p.Y - Half, Half * 2, Half * 2);
            dc.DrawRoundedRectangle(Brushes.White, PointerUnderlayPen, rect, 2, 2);
        }
    }

    private static void DrawMask(DrawingContext dc, Rect full, Rect hole)
    {
        var mask = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(full), new RectangleGeometry(hole));
        dc.DrawGeometry(DimBrush, null, mask);
    }

    private static void DrawPointer(DrawingContext dc, Point cursor)
    {
        const double LineHalf = 18, RingRadius = 7;

        var left = new Point(cursor.X - LineHalf, cursor.Y);
        var right = new Point(cursor.X + LineHalf, cursor.Y);
        var top = new Point(cursor.X, cursor.Y - LineHalf);
        var bottom = new Point(cursor.X, cursor.Y + LineHalf);

        dc.DrawLine(PointerUnderlayPen, left, right);
        dc.DrawLine(PointerUnderlayPen, top, bottom);
        dc.DrawEllipse(null, PointerUnderlayPen, cursor, RingRadius, RingRadius);

        dc.DrawLine(PointerPen, left, right);
        dc.DrawLine(PointerPen, top, bottom);
        dc.DrawEllipse(null, PointerPen, cursor, RingRadius, RingRadius);
    }

    /// <summary>Anchor point for the info pill: below the rect's bottom-right, flipped inside when cramped.</summary>
    private static Func<double, double, Point> NearSelection(Rect localRect, Rect bounds) => (w, h) =>
    {
        const double Gap = 6;
        double x = Math.Clamp(localRect.Right - w, bounds.Left + Gap, Math.Max(bounds.Left + Gap, bounds.Right - w - Gap));
        double y = localRect.Bottom + Gap;
        if (y + h > bounds.Bottom - Gap)
            y = Math.Max(bounds.Top + Gap, localRect.Bottom - h - Gap);
        return new Point(x, y);
    };

    private static void DrawPill(DrawingContext dc, string content, Func<double, double, Point> place,
        double pixelsPerDip, double fontSize)
    {
        var text = new FormattedText(content, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            LabelTypeface, fontSize, Brushes.White, pixelsPerDip);
        const double PadX = 8, PadY = 4;
        double w = text.Width + PadX * 2, h = text.Height + PadY * 2;
        var origin = place(w, h);
        dc.DrawRoundedRectangle(LabelBackground, null, new Rect(origin.X, origin.Y, w, h), 4, 4);
        dc.DrawText(text, new Point(origin.X + PadX, origin.Y + PadY));
    }
}
