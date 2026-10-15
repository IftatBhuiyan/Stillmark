using System.Globalization;
using WinShot.Core;

namespace WinShot.App.Overlays;

internal enum SelectionKeyboardDirection
{
    Left,
    Right,
    Up,
    Down,
}

internal enum SelectionKeyboardActionKind
{
    None,
    DraftStarted,
    DraftCancelled,
    SelectionCommitted,
    GeometryTooSmall,
}

internal readonly record struct SelectionKeyboardAction(
    SelectionKeyboardActionKind Kind,
    PixelRect Selection,
    string Announcement);

/// <summary>
/// UI-free physical-pixel state for creating a capture area from the keyboard.
/// Session/window adapters remain responsible for completion and native input ownership.
/// </summary>
internal sealed class SelectionKeyboardInteraction
{
    internal const int NormalMovePixels = 1;
    internal const int AcceleratedMovePixels = 10;
    internal const int MinimumSelectionEdge = 3;

    private readonly PixelRect _bounds;
    private (int X, int Y)? _anchor;

    internal int CursorX { get; private set; }
    internal int CursorY { get; private set; }
    internal bool IsDraftActive => _anchor is not null;

    internal PixelRect? PreviewSelection =>
        _anchor is { } anchor
            ? PixelRect.FromCorners(anchor.X, anchor.Y, CursorX, CursorY)
            : null;

    internal string Status => BuildStatus();

    internal SelectionKeyboardInteraction(
        PixelRect bounds,
        int initialCursorX,
        int initialCursorY)
    {
        if (bounds.IsEmpty ||
            bounds.Right <= bounds.X ||
            bounds.Bottom <= bounds.Y)
            throw new ArgumentOutOfRangeException(nameof(bounds));

        _bounds = bounds;
        MoveTo(initialCursorX, initialCursorY);
    }

    internal void Move(
        SelectionKeyboardDirection direction,
        bool accelerated)
    {
        int distance = accelerated
            ? AcceleratedMovePixels
            : NormalMovePixels;
        (int deltaX, int deltaY) = direction switch
        {
            SelectionKeyboardDirection.Left => (-distance, 0),
            SelectionKeyboardDirection.Right => (distance, 0),
            SelectionKeyboardDirection.Up => (0, -distance),
            SelectionKeyboardDirection.Down => (0, distance),
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };

        MoveTo(SaturatingAdd(CursorX, deltaX), SaturatingAdd(CursorY, deltaY));
    }

    internal void MoveTo(int x, int y)
    {
        CursorX = Math.Clamp(x, _bounds.X, _bounds.Right - 1);
        CursorY = Math.Clamp(y, _bounds.Y, _bounds.Bottom - 1);
    }

    internal SelectionKeyboardAction StartOrCommit(bool isRepeat = false)
    {
        if (isRepeat)
            return None();

        if (_anchor is null)
        {
            _anchor = (CursorX, CursorY);
            return new SelectionKeyboardAction(
                SelectionKeyboardActionKind.DraftStarted,
                default,
                $"Selection start set at {FormatPoint(CursorX, CursorY)}.");
        }

        PixelRect selection = PreviewSelection ?? default;
        if (selection.Width < MinimumSelectionEdge ||
            selection.Height < MinimumSelectionEdge)
        {
            return new SelectionKeyboardAction(
                SelectionKeyboardActionKind.GeometryTooSmall,
                selection,
                $"Selection needs width and height of at least {MinimumSelectionEdge.ToString(CultureInfo.InvariantCulture)} pixels.");
        }

        _anchor = null;
        return new SelectionKeyboardAction(
            SelectionKeyboardActionKind.SelectionCommitted,
            selection,
            $"Selected area {FormatRect(selection)}.");
    }

    internal SelectionKeyboardAction CancelDraft()
    {
        if (_anchor is null)
            return None();

        _anchor = null;
        return new SelectionKeyboardAction(
            SelectionKeyboardActionKind.DraftCancelled,
            default,
            "Keyboard area selection cancelled.");
    }

    private string BuildStatus()
    {
        if (PreviewSelection is PixelRect selection)
        {
            return $"Selecting from {FormatPoint(selection.X, selection.Y)} to " +
                   $"{FormatPoint(selection.Right, selection.Bottom)}. " +
                   $"Size {selection.Width.ToString(CultureInfo.InvariantCulture)} by " +
                   $"{selection.Height.ToString(CultureInfo.InvariantCulture)} pixels. " +
                   "Press Space or Enter to finish, or Escape to cancel.";
        }

        return $"Selection cursor {FormatPoint(CursorX, CursorY)}. " +
               "Arrow keys move one pixel; hold Shift to move ten pixels. " +
               "Press Space to set the first corner.";
    }

    private SelectionKeyboardAction None() =>
        new(SelectionKeyboardActionKind.None, default, string.Empty);

    private static int SaturatingAdd(int value, int delta)
    {
        long result = (long)value + delta;
        return result > int.MaxValue
            ? int.MaxValue
            : result < int.MinValue
                ? int.MinValue
                : (int)result;
    }

    private static string FormatPoint(int x, int y) =>
        $"{x.ToString(CultureInfo.InvariantCulture)}, " +
        $"{y.ToString(CultureInfo.InvariantCulture)} pixels";

    private static string FormatRect(PixelRect rect) =>
        $"{rect.X.ToString(CultureInfo.InvariantCulture)}, " +
        $"{rect.Y.ToString(CultureInfo.InvariantCulture)}, " +
        $"{rect.Width.ToString(CultureInfo.InvariantCulture)} by " +
        $"{rect.Height.ToString(CultureInfo.InvariantCulture)} pixels";
}
