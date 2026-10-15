using WinShot.App.Overlays;
using WinShot.Capture;
using WinShot.Core;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace WinShot.Core.Tests;

public class SelectionKeyboardInteractionTests
{
    [Fact]
    public void Constructor_RejectsEmptyBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SelectionKeyboardInteraction(default, 0, 0));
    }

    [Fact]
    public void Constructor_ClampsCursorAcrossNegativeCoordinateBounds()
    {
        var state = new SelectionKeyboardInteraction(
            new PixelRect(-1920, -400, 1920, 1080),
            int.MaxValue,
            int.MinValue);

        Assert.Equal(-1, state.CursorX);
        Assert.Equal(-400, state.CursorY);
    }

    [Fact]
    public void Move_UsesOneOrTenPixelsAndClampsAllEdges()
    {
        var state = new SelectionKeyboardInteraction(
            new PixelRect(-10, -20, 21, 31),
            0,
            0);

        state.Move(SelectionKeyboardDirection.Left, accelerated: false);
        state.Move(SelectionKeyboardDirection.Up, accelerated: true);
        Assert.Equal((-1, -10), (state.CursorX, state.CursorY));

        state.Move(SelectionKeyboardDirection.Left, accelerated: true);
        state.Move(SelectionKeyboardDirection.Up, accelerated: true);
        Assert.Equal((-10, -20), (state.CursorX, state.CursorY));

        state.Move(SelectionKeyboardDirection.Right, accelerated: true);
        state.Move(SelectionKeyboardDirection.Right, accelerated: true);
        state.Move(SelectionKeyboardDirection.Right, accelerated: true);
        state.Move(SelectionKeyboardDirection.Down, accelerated: true);
        state.Move(SelectionKeyboardDirection.Down, accelerated: true);
        state.Move(SelectionKeyboardDirection.Down, accelerated: true);
        state.Move(SelectionKeyboardDirection.Down, accelerated: true);
        Assert.Equal((10, 10), (state.CursorX, state.CursorY));
    }

    [Fact]
    public void Move_SaturatesBeforeClampingIntegerExtremes()
    {
        var state = new SelectionKeyboardInteraction(
            new PixelRect(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue),
            int.MinValue,
            int.MinValue);

        state.Move(SelectionKeyboardDirection.Left, accelerated: true);
        state.Move(SelectionKeyboardDirection.Up, accelerated: true);

        Assert.Equal(int.MinValue, state.CursorX);
        Assert.Equal(int.MinValue, state.CursorY);
    }

    [Fact]
    public void Space_StartsAndCommitsReverseDirectionSelection()
    {
        var state = new SelectionKeyboardInteraction(
            new PixelRect(-100, -100, 200, 200),
            20,
            30);

        SelectionKeyboardAction started = state.StartOrCommit();
        state.Move(SelectionKeyboardDirection.Left, accelerated: true);
        state.Move(SelectionKeyboardDirection.Up, accelerated: true);
        SelectionKeyboardAction committed = state.StartOrCommit();

        Assert.Equal(SelectionKeyboardActionKind.DraftStarted, started.Kind);
        Assert.Equal(SelectionKeyboardActionKind.SelectionCommitted, committed.Kind);
        Assert.Equal(new PixelRect(10, 20, 10, 10), committed.Selection);
        Assert.False(state.IsDraftActive);
    }

    [Fact]
    public void ExactThreeByThreeSelection_IsAccepted()
    {
        var state = new SelectionKeyboardInteraction(
            new PixelRect(0, 0, 20, 20),
            5,
            5);
        state.StartOrCommit();
        for (int i = 0; i < 3; i++)
        {
            state.Move(SelectionKeyboardDirection.Right, accelerated: false);
            state.Move(SelectionKeyboardDirection.Down, accelerated: false);
        }

        SelectionKeyboardAction committed = state.StartOrCommit();

        Assert.Equal(SelectionKeyboardActionKind.SelectionCommitted, committed.Kind);
        Assert.Equal(new PixelRect(5, 5, 3, 3), committed.Selection);
    }

    [Fact]
    public void TooSmallSelection_IsRejectedAndDraftRemainsActive()
    {
        var state = new SelectionKeyboardInteraction(
            new PixelRect(0, 0, 20, 20),
            5,
            5);
        state.StartOrCommit();
        state.Move(SelectionKeyboardDirection.Right, accelerated: true);
        state.Move(SelectionKeyboardDirection.Down, accelerated: false);

        SelectionKeyboardAction rejected = state.StartOrCommit();

        Assert.Equal(SelectionKeyboardActionKind.GeometryTooSmall, rejected.Kind);
        Assert.Equal(new PixelRect(5, 5, 10, 1), rejected.Selection);
        Assert.True(state.IsDraftActive);
        Assert.Equal(
            "Selection needs width and height of at least 3 pixels.",
            rejected.Announcement);
    }

    [Fact]
    public void RepeatedActivation_IsIgnoredWithoutChangingDraft()
    {
        var state = new SelectionKeyboardInteraction(
            new PixelRect(0, 0, 20, 20),
            5,
            5);

        Assert.Equal(
            SelectionKeyboardActionKind.None,
            state.StartOrCommit(isRepeat: true).Kind);
        Assert.False(state.IsDraftActive);

        state.StartOrCommit();
        state.Move(SelectionKeyboardDirection.Right, accelerated: true);
        state.Move(SelectionKeyboardDirection.Down, accelerated: true);

        Assert.Equal(
            SelectionKeyboardActionKind.None,
            state.StartOrCommit(isRepeat: true).Kind);
        Assert.True(state.IsDraftActive);
    }

    [Fact]
    public void Cancel_DropsDraftAndPreservesCursorForFreshStart()
    {
        var state = new SelectionKeyboardInteraction(
            new PixelRect(0, 0, 100, 100),
            20,
            20);
        state.StartOrCommit();
        state.Move(SelectionKeyboardDirection.Right, accelerated: true);

        SelectionKeyboardAction cancelled = state.CancelDraft();
        SelectionKeyboardAction restarted = state.StartOrCommit();

        Assert.Equal(SelectionKeyboardActionKind.DraftCancelled, cancelled.Kind);
        Assert.Equal("Keyboard area selection cancelled.", cancelled.Announcement);
        Assert.Equal(SelectionKeyboardActionKind.DraftStarted, restarted.Kind);
        Assert.Equal("Selection start set at 30, 20 pixels.", restarted.Announcement);
    }

    [Fact]
    public void StatusStrings_AreStableForIdleAndDraftStates()
    {
        var state = new SelectionKeyboardInteraction(
            new PixelRect(-100, -50, 200, 100),
            -20,
            -10);

        Assert.Equal(
            "Selection cursor -20, -10 pixels. Arrow keys move one pixel; hold Shift to move ten pixels. Press Space to set the first corner.",
            state.Status);

        state.StartOrCommit();
        state.Move(SelectionKeyboardDirection.Right, accelerated: true);
        state.Move(SelectionKeyboardDirection.Down, accelerated: true);

        Assert.Equal(
            "Selecting from -20, -10 pixels to -10, 0 pixels. Size 10 by 10 pixels. Press Space or Enter to finish, or Escape to cancel.",
            state.Status);
    }

    [Fact]
    public void FirstCorner_StatusRetainsItsPhysicalCoordinate()
    {
        var state = new SelectionKeyboardInteraction(
            new PixelRect(-100, -50, 200, 100),
            -20,
            -10);

        state.StartOrCommit();

        Assert.Equal(new PixelRect(-20, -10, 0, 0), state.PreviewSelection);
        Assert.Equal(
            "Selecting from -20, -10 pixels to -20, -10 pixels. Size 0 by 0 pixels. Press Space or Enter to finish, or Escape to cancel.",
            state.Status);
    }

    [Fact]
    public void InteractiveSession_KeyboardAreaUsesExistingSelectionEstablishedPath()
    {
        PixelRect bounds = new(0, 0, 40, 40);
        var session = CreateSession(bounds, interactive: true);
        PixelRect? established = null;
        session.SelectionEstablished += selection => established = selection;

        MoveSessionToBottomRight(session);
        session.HandleKeyboardActivate(isRepeat: false);
        for (int i = 0; i < 4; i++)
        {
            session.HandleKeyboardMove(
                SelectionKeyboardDirection.Left,
                accelerated: true);
            session.HandleKeyboardMove(
                SelectionKeyboardDirection.Up,
                accelerated: true);
        }
        session.HandleKeyboardConfirm(isRepeat: false);

        Assert.True(established.HasValue);
        PixelRect establishedSelection = established.GetValueOrDefault();
        Assert.Equal(establishedSelection, session.Committed);
        Assert.True(establishedSelection.Width >= 3);
        Assert.True(establishedSelection.Height >= 3);
    }

    [Fact]
    public void QuickSession_KeyboardAreaUsesExistingCompletedPath()
    {
        PixelRect bounds = new(0, 0, 40, 40);
        var session = CreateSession(bounds, interactive: false);
        SelectionResult? completed = null;
        session.Completed += result => completed = result;

        MoveSessionToBottomRight(session);
        session.HandleKeyboardActivate(isRepeat: false);
        for (int i = 0; i < 4; i++)
        {
            session.HandleKeyboardMove(
                SelectionKeyboardDirection.Left,
                accelerated: true);
            session.HandleKeyboardMove(
                SelectionKeyboardDirection.Up,
                accelerated: true);
        }
        session.HandleKeyboardActivate(isRepeat: false);

        Assert.NotNull(completed);
        Assert.Null(completed!.Window);
        Assert.True(completed.Rect.Width >= 3);
        Assert.True(completed.Rect.Height >= 3);
    }

    [Fact]
    public void SessionEscape_CancelsDraftBeforeCancellingSession()
    {
        var session = CreateSession(
            new PixelRect(0, 0, 40, 40),
            interactive: true);
        bool completed = false;
        session.Completed += _ => completed = true;
        session.HandleKeyboardActivate(isRepeat: false);

        session.HandleKeyboardEscape();

        Assert.False(completed);
        Assert.False(session.IsKeyboardSelecting);

        session.HandleKeyboardEscape();
        Assert.True(completed);
    }

    [Fact]
    public void Enter_SelectsHoveredWindowThroughExistingCommitPath()
    {
        PixelRect bounds = new(0, 0, 40, 40);
        var window = new WindowInfo(
            new IntPtr(42),
            "Fixture",
            bounds);
        var session = CreateSession(
            bounds,
            interactive: true,
            topWindows: [window]);

        session.HandleKeyboardConfirm(isRepeat: false);

        Assert.Equal(window.Bounds, session.Committed);
        Assert.Same(window, session.CommittedWindow);
    }

    [Fact]
    public void BeginFreshAreaSelection_DiscardsCommittedWindowAndRequiresNewArea()
    {
        PixelRect bounds = new(0, 0, 80, 80);
        var window = new WindowInfo(new IntPtr(42), "Fixture", bounds);
        var session = CreateSession(bounds, interactive: true, topWindows: [window]);
        session.CommitSelection(window.Bounds, window);
        int cleared = 0;
        int established = 0;
        bool completed = false;
        session.SelectionCleared += () => cleared++;
        session.SelectionEstablished += _ => established++;
        session.Completed += _ => completed = true;

        session.BeginFreshAreaSelection();
        session.UpdateCursor(20, 20);
        session.OnMouseDown(20, 20);
        session.OnMouseUp(20, 20);

        Assert.Null(session.Committed);
        Assert.Null(session.CommittedWindow);
        Assert.Null(session.HoveredWindow);
        Assert.Equal(1, cleared);
        Assert.Equal(0, established);
        Assert.False(completed);

        session.OnMouseDown(10, 10);
        session.UpdateCursor(45, 50);
        session.OnMouseUp(45, 50);

        Assert.Equal(new PixelRect(10, 10, 35, 40), session.Committed);
        Assert.Null(session.CommittedWindow);
        Assert.Equal(1, established);
    }

    [Fact]
    public void BeginFreshAreaSelection_CancelsStaleDragAndSupportsKeyboardCommit()
    {
        PixelRect bounds = new(0, 0, 80, 80);
        var session = CreateSession(bounds, interactive: true);
        session.CommitSelection(new PixelRect(10, 10, 30, 30));
        session.OnMouseDown(20, 20);

        session.BeginFreshAreaSelection();
        session.OnMouseUp(60, 60);

        Assert.Null(session.Committed);
        Assert.False(session.IsDragging);

        session.HandleKeyboardActivate(isRepeat: false);
        session.HandleKeyboardMove(SelectionKeyboardDirection.Right, accelerated: true);
        session.HandleKeyboardMove(SelectionKeyboardDirection.Down, accelerated: true);
        session.HandleKeyboardConfirm(isRepeat: false);

        Assert.Equal(10, session.Committed?.Width);
        Assert.Equal(10, session.Committed?.Height);
    }

    private static void MoveSessionToBottomRight(SelectionSession session)
    {
        for (int i = 0; i < 4; i++)
        {
            session.HandleKeyboardMove(
                SelectionKeyboardDirection.Right,
                accelerated: true);
            session.HandleKeyboardMove(
                SelectionKeyboardDirection.Down,
                accelerated: true);
        }
    }

    private static SelectionSession CreateSession(
        PixelRect bounds,
        bool interactive,
        IReadOnlyList<WindowInfo>? topWindows = null)
    {
        var monitor = new MonitorInfo(
            "test",
            bounds,
            bounds,
            true,
            96,
            96);
        return new SelectionSession(
            [monitor],
            bounds,
            CreateBitmap(bounds.Width, bounds.Height),
            topWindows ?? [],
            interactive);
    }

    private static BitmapSource CreateBitmap(int width, int height)
    {
        int stride = width * 4;
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[stride * height],
            stride);
        bitmap.Freeze();
        return bitmap;
    }
}
