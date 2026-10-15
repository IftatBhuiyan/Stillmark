using System.Windows;
using WinShot.App.Editor;
using Xunit;

namespace WinShot.Core.Tests;

public class EditorKeyboardInteractionTests
{
    [Fact]
    public void Constructor_CentersCursorAndClampsRequestedCoordinates()
    {
        var centered = new EditorKeyboardInteraction(101, 51, ToolKind.Arrow);
        var clamped = new EditorKeyboardInteraction(
            101,
            51,
            ToolKind.Arrow,
            new Point(double.PositiveInfinity, -30));

        Assert.Equal(new Point(50.5, 25.5), centered.Cursor);
        Assert.Equal(new Point(0, 0), clamped.Cursor);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveOrNonFiniteImageBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new EditorKeyboardInteraction(0, 10, ToolKind.Arrow));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new EditorKeyboardInteraction(10, double.NaN, ToolKind.Arrow));
    }

    [Fact]
    public void Move_UsesOneOrTenPixelsAndClampsEveryEdge()
    {
        var state = new EditorKeyboardInteraction(
            12,
            12,
            ToolKind.Arrow,
            new Point(1, 1));

        state.Move(EditorKeyboardDirection.Right);
        state.Move(EditorKeyboardDirection.Down, accelerated: true);
        Assert.Equal(new Point(2, 11), state.Cursor);

        state.Move(EditorKeyboardDirection.Left, accelerated: true);
        state.Move(EditorKeyboardDirection.Up, accelerated: true);
        Assert.Equal(new Point(0, 1), state.Cursor);

        state.Move(EditorKeyboardDirection.Up, accelerated: true);
        state.Move(EditorKeyboardDirection.Right, accelerated: true);
        state.Move(EditorKeyboardDirection.Right, accelerated: true);
        state.Move(EditorKeyboardDirection.Down, accelerated: true);
        state.Move(EditorKeyboardDirection.Down, accelerated: true);
        Assert.Equal(new Point(12, 12), state.Cursor);
    }

    [Fact]
    public void MoveTo_ClampsPointerHandoffAndDoesNotStartGesture()
    {
        var state = new EditorKeyboardInteraction(
            12,
            8,
            ToolKind.Arrow);

        state.MoveTo(new Point(30, -4));

        Assert.Equal(new Point(12, 0), state.Cursor);
        Assert.False(state.IsGestureActive);
    }

    [Theory]
    [InlineData(ToolKind.Select, (int)EditorKeyboardToolFamily.Selection)]
    [InlineData(ToolKind.Text, (int)EditorKeyboardToolFamily.Point)]
    [InlineData(ToolKind.Step, (int)EditorKeyboardToolFamily.Point)]
    [InlineData(ToolKind.Arrow, (int)EditorKeyboardToolFamily.TwoPointShape)]
    [InlineData(ToolKind.Line, (int)EditorKeyboardToolFamily.TwoPointShape)]
    [InlineData(ToolKind.Rect, (int)EditorKeyboardToolFamily.TwoPointShape)]
    [InlineData(ToolKind.Ellipse, (int)EditorKeyboardToolFamily.TwoPointShape)]
    [InlineData(ToolKind.Blur, (int)EditorKeyboardToolFamily.TwoPointRegion)]
    [InlineData(ToolKind.Pixelate, (int)EditorKeyboardToolFamily.TwoPointRegion)]
    [InlineData(ToolKind.Spotlight, (int)EditorKeyboardToolFamily.TwoPointRegion)]
    [InlineData(ToolKind.Crop, (int)EditorKeyboardToolFamily.TwoPointRegion)]
    [InlineData(ToolKind.Freehand, (int)EditorKeyboardToolFamily.Stroke)]
    [InlineData(ToolKind.Highlighter, (int)EditorKeyboardToolFamily.Stroke)]
    public void Classify_CoversEveryTool(
        ToolKind tool,
        int expected)
    {
        Assert.Equal(
            (EditorKeyboardToolFamily)expected,
            EditorKeyboardInteraction.Classify(tool));
    }

    [Theory]
    [InlineData(ToolKind.Arrow)]
    [InlineData(ToolKind.Line)]
    [InlineData(ToolKind.Rect)]
    [InlineData(ToolKind.Ellipse)]
    public void TwoPointShape_StartsCommitsAndResets(ToolKind tool)
    {
        var state = new EditorKeyboardInteraction(
            100,
            100,
            tool,
            new Point(20, 30));

        EditorKeyboardAction started = state.Activate();
        state.Move(EditorKeyboardDirection.Right, accelerated: true);
        EditorKeyboardAction committed = state.Activate();

        Assert.Equal(EditorKeyboardActionKind.GestureStarted, started.Kind);
        Assert.Equal(new Point(20, 30), started.Start);
        Assert.Equal(EditorKeyboardActionKind.ShapeCommitted, committed.Kind);
        Assert.Equal(new Point(20, 30), committed.Start);
        Assert.Equal(new Point(30, 30), committed.End);
        Assert.False(state.IsGestureActive);
    }

    [Theory]
    [InlineData(ToolKind.Blur)]
    [InlineData(ToolKind.Pixelate)]
    [InlineData(ToolKind.Spotlight)]
    [InlineData(ToolKind.Crop)]
    public void TwoPointRegion_StartsCommitsAndNormalizesReverseGeometry(ToolKind tool)
    {
        var state = new EditorKeyboardInteraction(
            100,
            100,
            tool,
            new Point(50, 50));

        state.Activate();
        state.Move(EditorKeyboardDirection.Left, accelerated: true);
        state.Move(EditorKeyboardDirection.Up, accelerated: true);
        EditorKeyboardAction committed = state.Activate();

        Assert.Equal(EditorKeyboardActionKind.RegionCommitted, committed.Kind);
        Assert.Equal(new Point(50, 50), committed.Start);
        Assert.Equal(new Point(40, 40), committed.End);
        Assert.False(state.IsGestureActive);
    }

    [Fact]
    public void TwoPointShape_RejectsShortGeometryAndKeepsGestureActive()
    {
        var state = new EditorKeyboardInteraction(
            100,
            100,
            ToolKind.Arrow,
            new Point(10, 10));
        state.Activate();
        state.Move(EditorKeyboardDirection.Right);
        state.Move(EditorKeyboardDirection.Right);

        EditorKeyboardAction rejected = state.Activate();

        Assert.Equal(EditorKeyboardActionKind.GeometryTooSmall, rejected.Kind);
        Assert.True(state.IsGestureActive);
        Assert.Equal(
            "Arrow needs at least 3 pixels between start and end.",
            rejected.Announcement);
    }

    [Fact]
    public void TwoPointShape_AcceptsExactMinimumLength()
    {
        var state = new EditorKeyboardInteraction(
            20,
            20,
            ToolKind.Line,
            new Point(5, 5));
        state.Activate();
        state.Move(EditorKeyboardDirection.Right);
        state.Move(EditorKeyboardDirection.Right);
        state.Move(EditorKeyboardDirection.Right);

        EditorKeyboardAction committed = state.Activate();

        Assert.Equal(EditorKeyboardActionKind.ShapeCommitted, committed.Kind);
        Assert.Equal(new Point(5, 5), committed.Start);
        Assert.Equal(new Point(8, 5), committed.End);
    }

    [Fact]
    public void TwoPointRegion_RequiresBothEdgesAndKeepsGestureActiveOnFailure()
    {
        var state = new EditorKeyboardInteraction(
            100,
            100,
            ToolKind.Crop,
            new Point(10, 10));
        state.Activate();
        state.Move(EditorKeyboardDirection.Right, accelerated: true);

        EditorKeyboardAction rejected = state.Activate();

        Assert.Equal(EditorKeyboardActionKind.GeometryTooSmall, rejected.Kind);
        Assert.True(state.IsGestureActive);
        Assert.Equal(
            "Crop needs width and height of at least 4 pixels.",
            rejected.Announcement);
    }

    [Fact]
    public void TwoPointRegion_AcceptsExactMinimumEdges()
    {
        var state = new EditorKeyboardInteraction(
            20,
            20,
            ToolKind.Blur,
            new Point(5, 5));
        state.Activate();
        for (int i = 0; i < 4; i++)
        {
            state.Move(EditorKeyboardDirection.Right);
            state.Move(EditorKeyboardDirection.Down);
        }

        EditorKeyboardAction committed = state.Activate();

        Assert.Equal(EditorKeyboardActionKind.RegionCommitted, committed.Kind);
        Assert.Equal(new Point(5, 5), committed.Start);
        Assert.Equal(new Point(9, 9), committed.End);
    }

    [Fact]
    public void Cancel_DropsTwoPointGestureWithoutChangingCursor()
    {
        var state = new EditorKeyboardInteraction(
            100,
            100,
            ToolKind.Rect,
            new Point(20, 20));
        state.Activate();
        state.Move(EditorKeyboardDirection.Right, accelerated: true);

        EditorKeyboardAction cancelled = state.Cancel();

        Assert.Equal(EditorKeyboardActionKind.GestureCancelled, cancelled.Kind);
        Assert.Equal(new Point(30, 20), state.Cursor);
        Assert.False(state.IsGestureActive);
        Assert.Equal(
            "Rectangle tool. Cursor 30, 20 pixels. Press Enter or Space to set the start point.",
            state.Status);
        Assert.Equal(EditorKeyboardActionKind.None, state.Cancel().Kind);

        EditorKeyboardAction restarted = state.Activate();
        Assert.Equal(EditorKeyboardActionKind.GestureStarted, restarted.Kind);
        Assert.Equal(new Point(30, 20), restarted.Start);
    }

    [Theory]
    [InlineData(ToolKind.Freehand)]
    [InlineData(ToolKind.Highlighter)]
    public void Stroke_CollectsDistinctCursorMovesAndCommitsImmutablePoints(ToolKind tool)
    {
        var state = new EditorKeyboardInteraction(
            100,
            100,
            tool,
            new Point(10, 10));
        state.Activate();
        state.Move(EditorKeyboardDirection.Left, accelerated: true);
        state.Move(EditorKeyboardDirection.Right);
        state.Move(EditorKeyboardDirection.Down);

        EditorKeyboardAction committed = state.Activate();

        Assert.Equal(EditorKeyboardActionKind.StrokeCommitted, committed.Kind);
        IReadOnlyList<Point> points =
            Assert.IsAssignableFrom<IReadOnlyList<Point>>(committed.Points);
        Assert.Equal(
            [new Point(10, 10), new Point(0, 10), new Point(1, 10), new Point(1, 11)],
            points);
        Assert.False(state.IsGestureActive);
        Assert.Equal(
            tool == ToolKind.Freehand
                ? "Pen stroke placed with 4 points."
                : "Highlighter stroke placed with 4 points.",
            committed.Announcement);
    }

    [Fact]
    public void Stroke_RejectsDuplicateBoundaryPointUntilCursorActuallyMoves()
    {
        var state = new EditorKeyboardInteraction(
            10,
            10,
            ToolKind.Highlighter,
            new Point(0, 0));
        state.Activate();
        state.Move(EditorKeyboardDirection.Left);

        EditorKeyboardAction rejected = state.Activate();

        Assert.Equal(EditorKeyboardActionKind.GeometryTooSmall, rejected.Kind);
        Assert.True(state.IsGestureActive);

        state.Move(EditorKeyboardDirection.Right);
        Assert.Equal(
            EditorKeyboardActionKind.StrokeCommitted,
            state.Activate().Kind);
    }

    [Fact]
    public void Cancel_DropsAccumulatedStrokePoints()
    {
        var state = new EditorKeyboardInteraction(
            20,
            20,
            ToolKind.Freehand,
            new Point(5, 5));
        state.Activate();
        state.Move(EditorKeyboardDirection.Right);

        EditorKeyboardAction cancelled = state.Cancel();
        EditorKeyboardAction restarted = state.Activate();
        EditorKeyboardAction rejected = state.Activate();

        Assert.Equal(EditorKeyboardActionKind.GestureCancelled, cancelled.Kind);
        Assert.Equal(EditorKeyboardActionKind.GestureStarted, restarted.Kind);
        Assert.Equal(EditorKeyboardActionKind.GeometryTooSmall, rejected.Kind);
    }

    [Fact]
    public void PointTools_ReturnExactCursorCoordinatesWithoutStartingGesture()
    {
        var text = new EditorKeyboardInteraction(
            80,
            60,
            ToolKind.Text,
            new Point(17.5, 22.25));
        var step = new EditorKeyboardInteraction(
            80,
            60,
            ToolKind.Step,
            new Point(17.5, 22.25));

        EditorKeyboardAction textAction = text.Activate();
        EditorKeyboardAction stepAction = step.Activate();

        Assert.Equal(EditorKeyboardActionKind.TextRequested, textAction.Kind);
        Assert.Equal(new Point(17.5, 22.25), textAction.Start);
        Assert.Equal(textAction.Start, textAction.End);
        Assert.Equal(EditorKeyboardActionKind.StepRequested, stepAction.Kind);
        Assert.Equal(textAction.Start, stepAction.Start);
        Assert.False(text.IsGestureActive);
        Assert.False(step.IsGestureActive);
    }

    [Fact]
    public void Selection_ReturnsExactCursorAndDoesNotMutateState()
    {
        var state = new EditorKeyboardInteraction(
            80,
            60,
            ToolKind.Select,
            new Point(12, 34));

        EditorKeyboardAction requested = state.Activate();

        Assert.Equal(EditorKeyboardActionKind.SelectionRequested, requested.Kind);
        Assert.Equal(new Point(12, 34), requested.Start);
        Assert.False(state.IsGestureActive);
    }

    [Fact]
    public void SetTool_CancelsActiveGestureAndUsesNewToolForSubsequentAction()
    {
        var state = new EditorKeyboardInteraction(
            80,
            60,
            ToolKind.Arrow,
            new Point(12, 34));
        state.Activate();

        EditorKeyboardAction cancelled = state.SetTool(ToolKind.Text);
        EditorKeyboardAction requested = state.Activate();

        Assert.Equal(EditorKeyboardActionKind.GestureCancelled, cancelled.Kind);
        Assert.Equal(ToolKind.Text, cancelled.Tool);
        Assert.False(state.IsGestureActive);
        Assert.Equal(EditorKeyboardActionKind.TextRequested, requested.Kind);
    }

    [Fact]
    public void StatusStrings_AreStableForIdleAndActiveStates()
    {
        var state = new EditorKeyboardInteraction(
            100,
            50,
            ToolKind.Rect);

        Assert.Equal(
            "Rectangle tool. Cursor 50, 25 pixels. Press Enter or Space to set the start point.",
            state.Status);

        state.Activate();
        state.Move(EditorKeyboardDirection.Right, accelerated: true);

        Assert.Equal(
            "Rectangle tool. Start 50, 25 pixels; Cursor 60, 25 pixels. Press Enter or Space to finish, or Escape to cancel.",
            state.Status);

        state.SetTool(ToolKind.Text);
        Assert.Equal(
            "Text tool. Cursor 60, 25 pixels. Press Enter or Space to place text.",
            state.Status);
    }
}
