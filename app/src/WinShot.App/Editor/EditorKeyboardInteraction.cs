using System.Globalization;
using System.Windows;

namespace WinShot.App.Editor;

internal enum EditorKeyboardToolFamily
{
    Selection,
    Point,
    TwoPointShape,
    TwoPointRegion,
    Stroke,
}

internal enum EditorKeyboardDirection
{
    Left,
    Right,
    Up,
    Down,
}

internal enum EditorKeyboardActionKind
{
    None,
    GestureStarted,
    GestureCancelled,
    SelectionRequested,
    TextRequested,
    StepRequested,
    ShapeCommitted,
    RegionCommitted,
    StrokeCommitted,
    GeometryTooSmall,
}

internal sealed record EditorKeyboardAction(
    EditorKeyboardActionKind Kind,
    ToolKind Tool,
    Point Start,
    Point End,
    IReadOnlyList<Point>? Points,
    string Announcement)
{
    internal static EditorKeyboardAction None(ToolKind tool) =>
        new(EditorKeyboardActionKind.None, tool, default, default, null, string.Empty);
}

/// <summary>
/// UI-free keyboard interaction state for the editor canvas. It works only in image-space
/// coordinates and returns declarative actions for the eventual WPF adapter to apply.
/// </summary>
internal sealed class EditorKeyboardInteraction
{
    internal const double NormalMovePixels = 1;
    internal const double AcceleratedMovePixels = 10;
    internal const double MinimumShapeLength = 3;
    internal const double MinimumRegionEdge = 4;

    private readonly double _maximumX;
    private readonly double _maximumY;
    private Point? _anchor;
    private List<Point>? _stroke;

    internal ToolKind Tool { get; private set; }
    internal Point Cursor { get; private set; }
    internal Point? Anchor => _anchor;
    internal bool IsGestureActive => _anchor is not null;

    internal string Status => BuildStatus();

    internal EditorKeyboardInteraction(
        double imageWidth,
        double imageHeight,
        ToolKind tool,
        Point? initialCursor = null)
    {
        if (!double.IsFinite(imageWidth) || imageWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth));
        if (!double.IsFinite(imageHeight) || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageHeight));

        _maximumX = imageWidth;
        _maximumY = imageHeight;
        Tool = tool;
        Cursor = Clamp(initialCursor ?? new Point(imageWidth / 2, imageHeight / 2));
    }

    internal static EditorKeyboardToolFamily Classify(ToolKind tool) =>
        tool switch
        {
            ToolKind.Select => EditorKeyboardToolFamily.Selection,
            ToolKind.Text or ToolKind.Step => EditorKeyboardToolFamily.Point,
            ToolKind.Arrow or ToolKind.Line or ToolKind.Rect or ToolKind.Ellipse =>
                EditorKeyboardToolFamily.TwoPointShape,
            ToolKind.Blur or ToolKind.Pixelate or ToolKind.Spotlight or ToolKind.Crop =>
                EditorKeyboardToolFamily.TwoPointRegion,
            ToolKind.Freehand or ToolKind.Highlighter => EditorKeyboardToolFamily.Stroke,
            _ => throw new ArgumentOutOfRangeException(nameof(tool)),
        };

    internal EditorKeyboardAction SetTool(ToolKind tool)
    {
        bool cancelled = IsGestureActive;
        Tool = tool;
        _anchor = null;
        _stroke = null;
        return cancelled
            ? Action(
                EditorKeyboardActionKind.GestureCancelled,
                Cursor,
                Cursor,
                announcement: "Keyboard annotation cancelled.")
            : EditorKeyboardAction.None(Tool);
    }

    internal EditorKeyboardAction Move(
        EditorKeyboardDirection direction,
        bool accelerated = false)
    {
        double distance = accelerated ? AcceleratedMovePixels : NormalMovePixels;
        Vector delta = direction switch
        {
            EditorKeyboardDirection.Left => new Vector(-distance, 0),
            EditorKeyboardDirection.Right => new Vector(distance, 0),
            EditorKeyboardDirection.Up => new Vector(0, -distance),
            EditorKeyboardDirection.Down => new Vector(0, distance),
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };

        Point previous = Cursor;
        Cursor = Clamp(Cursor + delta);
        if (_stroke is not null && Cursor != previous)
            _stroke.Add(Cursor);

        return EditorKeyboardAction.None(Tool);
    }

    internal EditorKeyboardAction MoveTo(Point point)
    {
        Point previous = Cursor;
        Cursor = Clamp(point);
        if (_stroke is not null && Cursor != previous)
            _stroke.Add(Cursor);

        return EditorKeyboardAction.None(Tool);
    }

    internal EditorKeyboardAction Activate()
    {
        return Classify(Tool) switch
        {
            EditorKeyboardToolFamily.Selection => Action(
                EditorKeyboardActionKind.SelectionRequested,
                Cursor,
                Cursor,
                announcement: $"Select annotation at {FormatPoint(Cursor)}."),
            EditorKeyboardToolFamily.Point => ActivatePointTool(),
            EditorKeyboardToolFamily.TwoPointShape => ActivateTwoPointShape(),
            EditorKeyboardToolFamily.TwoPointRegion => ActivateTwoPointRegion(),
            EditorKeyboardToolFamily.Stroke => ActivateStroke(),
            _ => throw new InvalidOperationException("Unsupported keyboard tool family."),
        };
    }

    internal EditorKeyboardAction Cancel()
    {
        if (!IsGestureActive)
            return EditorKeyboardAction.None(Tool);

        _anchor = null;
        _stroke = null;
        return Action(
            EditorKeyboardActionKind.GestureCancelled,
            Cursor,
            Cursor,
            announcement: "Keyboard annotation cancelled.");
    }

    private EditorKeyboardAction ActivatePointTool()
    {
        EditorKeyboardActionKind kind = Tool == ToolKind.Text
            ? EditorKeyboardActionKind.TextRequested
            : EditorKeyboardActionKind.StepRequested;
        string subject = Tool == ToolKind.Text ? "Text" : "Numbered step";
        return Action(
            kind,
            Cursor,
            Cursor,
            announcement: $"{subject} position selected at {FormatPoint(Cursor)}.");
    }

    private EditorKeyboardAction ActivateTwoPointShape()
    {
        if (_anchor is not Point start)
            return StartGesture();

        if ((Cursor - start).Length < MinimumShapeLength)
        {
            return Action(
                EditorKeyboardActionKind.GeometryTooSmall,
                start,
                Cursor,
                announcement:
                    $"{ToolLabel(Tool)} needs at least {FormatNumber(MinimumShapeLength)} pixels between start and end.");
        }

        _anchor = null;
        return Action(
            EditorKeyboardActionKind.ShapeCommitted,
            start,
            Cursor,
            announcement:
                $"{ToolLabel(Tool)} placed from {FormatPoint(start)} to {FormatPoint(Cursor)}.");
    }

    private EditorKeyboardAction ActivateTwoPointRegion()
    {
        if (_anchor is not Point start)
            return StartGesture();

        Rect region = new(start, Cursor);
        if (region.Width < MinimumRegionEdge || region.Height < MinimumRegionEdge)
        {
            return Action(
                EditorKeyboardActionKind.GeometryTooSmall,
                start,
                Cursor,
                announcement:
                    $"{ToolLabel(Tool)} needs width and height of at least {FormatNumber(MinimumRegionEdge)} pixels.");
        }

        _anchor = null;
        return Action(
            EditorKeyboardActionKind.RegionCommitted,
            start,
            Cursor,
            announcement:
                $"{ToolLabel(Tool)} region placed from {FormatPoint(start)} to {FormatPoint(Cursor)}.");
    }

    private EditorKeyboardAction ActivateStroke()
    {
        if (_anchor is null)
        {
            _anchor = Cursor;
            _stroke = [Cursor];
            return Action(
                EditorKeyboardActionKind.GestureStarted,
                Cursor,
                Cursor,
                announcement: $"{ToolLabel(Tool)} stroke started at {FormatPoint(Cursor)}.");
        }

        if (_stroke is null || _stroke.Count < 2)
        {
            return Action(
                EditorKeyboardActionKind.GeometryTooSmall,
                _anchor.Value,
                Cursor,
                announcement: $"{ToolLabel(Tool)} stroke needs at least two distinct points.");
        }

        Point[] points = _stroke.ToArray();
        Point start = _anchor.Value;
        _anchor = null;
        _stroke = null;
        return Action(
            EditorKeyboardActionKind.StrokeCommitted,
            start,
            Cursor,
            points,
            announcement:
                $"{ToolLabel(Tool)} stroke placed with {points.Length.ToString(CultureInfo.InvariantCulture)} points.");
    }

    private EditorKeyboardAction StartGesture()
    {
        _anchor = Cursor;
        return Action(
            EditorKeyboardActionKind.GestureStarted,
            Cursor,
            Cursor,
            announcement: $"{ToolLabel(Tool)} start set at {FormatPoint(Cursor)}.");
    }

    private EditorKeyboardAction Action(
        EditorKeyboardActionKind kind,
        Point start,
        Point end,
        IReadOnlyList<Point>? points = null,
        string announcement = "") =>
        new(kind, Tool, start, end, points, announcement);

    private Point Clamp(Point point) =>
        new(
            Math.Clamp(FiniteOrZero(point.X), 0, _maximumX),
            Math.Clamp(FiniteOrZero(point.Y), 0, _maximumY));

    private string BuildStatus()
    {
        string location = $"Cursor {FormatPoint(Cursor)}.";
        if (_anchor is Point anchor)
        {
            string action = Classify(Tool) == EditorKeyboardToolFamily.Stroke
                ? "Press Enter or Space to finish the stroke, or Escape to cancel."
                : "Press Enter or Space to finish, or Escape to cancel.";
            return $"{ToolLabel(Tool)} tool. Start {FormatPoint(anchor)}; {location} {action}";
        }

        string instruction = Classify(Tool) switch
        {
            EditorKeyboardToolFamily.Selection =>
                "Press Enter or Space to select the annotation under the cursor.",
            EditorKeyboardToolFamily.Point when Tool == ToolKind.Text =>
                "Press Enter or Space to place text.",
            EditorKeyboardToolFamily.Point =>
                "Press Enter or Space to place a numbered step.",
            EditorKeyboardToolFamily.Stroke =>
                "Press Enter or Space to begin a keyboard stroke.",
            _ => "Press Enter or Space to set the start point.",
        };
        return $"{ToolLabel(Tool)} tool. {location} {instruction}";
    }

    private static double FiniteOrZero(double value) =>
        double.IsFinite(value) ? value : 0;

    private static string FormatPoint(Point point) =>
        $"{FormatNumber(point.X)}, {FormatNumber(point.Y)} pixels";

    private static string FormatNumber(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string ToolLabel(ToolKind tool) =>
        tool switch
        {
            ToolKind.Select => "Select",
            ToolKind.Arrow => "Arrow",
            ToolKind.Line => "Line",
            ToolKind.Rect => "Rectangle",
            ToolKind.Ellipse => "Ellipse",
            ToolKind.Freehand => "Pen",
            ToolKind.Highlighter => "Highlighter",
            ToolKind.Text => "Text",
            ToolKind.Step => "Numbered step",
            ToolKind.Blur => "Blur",
            ToolKind.Pixelate => "Pixelate",
            ToolKind.Spotlight => "Spotlight",
            ToolKind.Crop => "Crop",
            _ => throw new ArgumentOutOfRangeException(nameof(tool)),
        };
}
