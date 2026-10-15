using System.Runtime.InteropServices;
using System.Globalization;
using System.Windows.Media.Imaging;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Interop;

namespace WinShot.App.Overlays;

public enum SelectionHitZone
{
    Outside,
    Inside,
    CornerNW,
    CornerNE,
    CornerSW,
    CornerSE,
}

/// <summary>
/// Coordinates one selection interaction across all monitors. In quick mode a drag or
/// window click completes immediately. In interactive (all-in-one) mode the selection
/// stays on screen and can be moved/resized while a toolbar offers actions.
/// All coordinates are physical pixels in virtual-screen space.
/// </summary>
public sealed class SelectionSession
{
    private const int DragThresholdPx = 4;
    private const int HandleHitPx = 14;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;
    private const uint VkEscape = 0x1B;
    private const uint VkEnter = 0x0D;
    private const uint VkSpace = 0x20;
    private const uint VkLeft = 0x25;
    private const uint VkUp = 0x26;
    private const uint VkRight = 0x27;
    private const uint VkDown = 0x28;

    private enum DragKind { None, New, Move, Resize }

    private readonly List<SelectionOverlayWindow> _windows = new();
    private readonly SelectionKeyboardInteraction _keyboard;
    private readonly HashSet<uint> _handledHookKeys = new();
    private DragKind _drag = DragKind.None;
    private int _anchorX, _anchorY;          // fixed corner (New/Resize) or grab point (Move)
    private PixelRect _moveOrigin;
    private bool _mouseDown;
    private bool _finished;
    private bool _areaSelectionOnly;

    public IReadOnlyList<MonitorInfo> Monitors { get; }
    public PixelRect VirtualBounds { get; }
    public BitmapSource FrozenScreen { get; }
    public IReadOnlyList<WindowInfo> TopWindows { get; }
    public bool Interactive { get; }

    /// <summary>When false the overlay is see-through (the live desktop shows during selection)
    /// instead of painting the frozen snapshot; the final image is then grabbed live.</summary>
    public bool ShowFrozen { get; }

    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool IsDragging => _drag != DragKind.None && _mouseDown;
    internal bool IsKeyboardSelecting => _keyboard.IsDraftActive;
    public WindowInfo? HoveredWindow { get; private set; }

    /// <summary>Established selection in interactive mode (null while picking).</summary>
    public PixelRect? Committed { get; private set; }

    /// <summary>
    /// The window represented by <see cref="Committed"/>, when the selection came from window
    /// picking and has not subsequently been moved or resized.
    /// </summary>
    public WindowInfo? CommittedWindow { get; private set; }

    /// <summary>True when the custom crosshair pointer should be drawn.</summary>
    public bool PointerVisible =>
        !Interactive ||
        Committed is null ||
        _drag == DragKind.New ||
        _keyboard.IsDraftActive;

    internal string KeyboardStatus
    {
        get
        {
            if (_keyboard.IsDraftActive)
                return _keyboard.Status;
            if (Committed is PixelRect committed)
            {
                return FormattableString.Invariant(
                    $"Selected area {committed.X}, {committed.Y}, {committed.Width} by {committed.Height} pixels. Press Enter to confirm, or Space to start a new area.");
            }
            return _keyboard.Status;
        }
    }

    /// <summary>Quick mode result; interactive mode always completes with null (actions fire separately).</summary>
    public event Action<SelectionResult?>? Completed;

    public event Action<PixelRect>? SelectionEstablished;
    public event Action<PixelRect>? SelectionChanged;
    public event Action? SelectionCleared;
    public event Action? ConfirmRequested;

    public SelectionSession(
        IReadOnlyList<MonitorInfo> monitors,
        PixelRect virtualBounds,
        BitmapSource frozenScreen,
        IReadOnlyList<WindowInfo> topWindows,
        bool interactive = false,
        bool showFrozen = true)
    {
        Monitors = monitors;
        VirtualBounds = virtualBounds;
        FrozenScreen = frozenScreen;
        TopWindows = topWindows;
        Interactive = interactive;
        ShowFrozen = showFrozen;
        (CursorX, CursorY) = MonitorService.GetCursorPosition();
        _keyboard = new SelectionKeyboardInteraction(
            virtualBounds,
            CursorX,
            CursorY);
        CursorX = _keyboard.CursorX;
        CursorY = _keyboard.CursorY;
        HoveredWindow = WindowService.HitTest(topWindows, CursorX, CursorY);
    }

    public PixelRect? SelectionRect
    {
        get
        {
            if (_keyboard.PreviewSelection is PixelRect keyboardSelection)
                return keyboardSelection;
            if (_drag == DragKind.New && _mouseDown)
                return PixelRect.FromCorners(_anchorX, _anchorY, CursorX, CursorY).Intersect(VirtualBounds);
            return Committed;
        }
    }

    public void Show()
    {
        foreach (var monitor in Monitors)
            _windows.Add(new SelectionOverlayWindow(this, monitor));
        foreach (var window in _windows)
            window.Show();

        if (ShowFrozen)
        {
            // Frozen snapshot: safe to take focus so WPF handles Esc/Enter directly.
            var focusTarget = _windows.FirstOrDefault(w => w.Monitor.Bounds.Contains(CursorX, CursorY)) ?? _windows[0];
            focusTarget.Activate();
        }
        else
        {
            // See-through (freeze-off): never steal focus, so a focus-sensitive foreground window
            // (e.g. a launcher like Raycast) stays open and is included in the live grab. The
            // overlays are marked WS_EX_NOACTIVATE; keyboard cancel/confirm comes via a global hook.
            InstallKeyHook();
        }
    }

    // ---- Freeze-off keyboard hook without activating the foreground overlay ----

    private NativeMethods.LowLevelKeyboardProc? _keyHookProc; // kept alive for the hook's lifetime
    private IntPtr _keyHook;

    private void InstallKeyHook()
    {
        _keyHookProc = KeyHookProc;
        _keyHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL, _keyHookProc, NativeMethods.GetModuleHandle(null), 0);
        if (_keyHook == IntPtr.Zero)
        {
            // Selection keys won't reach us without the hook; log so a silent failure is diagnosable.
            App.LogError(new InvalidOperationException(
                $"SetWindowsHookEx (selection) failed, error {Marshal.GetLastWin32Error()}"));
            _keyHookProc = null;
        }
    }

    private IntPtr KeyHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return NativeMethods.CallNextHookEx(_keyHook, nCode, wParam, lParam);

        int message = (int)wParam;
        uint vk = (uint)Marshal.ReadInt32(lParam);
        if (message is WmKeyUp or WmSysKeyUp)
        {
            if (_handledHookKeys.Remove(vk))
                return new IntPtr(1);
            return NativeMethods.CallNextHookEx(_keyHook, nCode, wParam, lParam);
        }

        if (message is not NativeMethods.WM_KEYDOWN and not NativeMethods.WM_SYSKEYDOWN)
            return NativeMethods.CallNextHookEx(_keyHook, nCode, wParam, lParam);

        bool isEscape = vk == VkEscape;
        bool isSelectionKey =
            isEscape ||
            vk is VkEnter or VkSpace or VkLeft or VkUp or VkRight or VkDown;
        if (!isSelectionKey || (!isEscape && HasUnsupportedKeyboardModifier()))
            return NativeMethods.CallNextHookEx(_keyHook, nCode, wParam, lParam);

        bool isRepeat = !_handledHookKeys.Add(vk);
        bool accelerated =
            (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            switch (vk)
            {
                case VkEscape when !isRepeat:
                    HandleKeyboardEscape();
                    break;
                case VkEnter:
                    HandleKeyboardConfirm(isRepeat);
                    break;
                case VkSpace:
                    HandleKeyboardActivate(isRepeat);
                    break;
                case VkLeft:
                    HandleKeyboardMove(SelectionKeyboardDirection.Left, accelerated);
                    break;
                case VkUp:
                    HandleKeyboardMove(SelectionKeyboardDirection.Up, accelerated);
                    break;
                case VkRight:
                    HandleKeyboardMove(SelectionKeyboardDirection.Right, accelerated);
                    break;
                case VkDown:
                    HandleKeyboardMove(SelectionKeyboardDirection.Down, accelerated);
                    break;
            }
        }));
        return new IntPtr(1);
    }

    private static bool HasUnsupportedKeyboardModifier() =>
        IsKeyDown(NativeMethods.VK_CONTROL) ||
        IsKeyDown(NativeMethods.VK_MENU) ||
        IsKeyDown(NativeMethods.VK_LWIN) ||
        IsKeyDown(NativeMethods.VK_RWIN);

    private static bool IsKeyDown(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    internal void HandleKeyboardMove(
        SelectionKeyboardDirection direction,
        bool accelerated)
    {
        if (_finished)
            return;

        _keyboard.Move(direction, accelerated);
        SyncCursorFromKeyboard();
        HoveredWindow = _keyboard.IsDraftActive
            ? null
            : (!_areaSelectionOnly && (!Interactive || Committed is null))
                ? WindowService.HitTest(TopWindows, CursorX, CursorY)
                : null;
        Repaint();
    }

    internal void HandleKeyboardActivate(bool isRepeat)
    {
        if (_finished)
            return;

        ApplyKeyboardAction(_keyboard.StartOrCommit(isRepeat));
    }

    internal void HandleKeyboardConfirm(bool isRepeat)
    {
        if (_finished || isRepeat)
            return;

        if (_keyboard.IsDraftActive)
        {
            ApplyKeyboardAction(_keyboard.StartOrCommit());
            return;
        }

        if (Committed is not null)
        {
            RequestConfirm();
            return;
        }

        if (HoveredWindow is not WindowInfo window)
            return;

        PixelRect selection = window.Bounds.Intersect(VirtualBounds);
        if (Interactive)
            CommitSelection(selection, window);
        else
            Finish(new SelectionResult(selection, window));
    }

    internal void HandleKeyboardEscape()
    {
        if (_finished)
            return;

        SelectionKeyboardAction action = _keyboard.CancelDraft();
        if (action.Kind == SelectionKeyboardActionKind.DraftCancelled)
        {
            HoveredWindow = (!_areaSelectionOnly && (!Interactive || Committed is null))
                ? WindowService.HitTest(TopWindows, CursorX, CursorY)
                : null;
            Repaint();
            return;
        }

        Cancel();
    }

    private void ApplyKeyboardAction(SelectionKeyboardAction action)
    {
        switch (action.Kind)
        {
            case SelectionKeyboardActionKind.None:
            case SelectionKeyboardActionKind.GeometryTooSmall:
                Repaint();
                return;
            case SelectionKeyboardActionKind.DraftStarted:
                HoveredWindow = null;
                if (Interactive && Committed is not null)
                    ClearSelection();
                Repaint();
                return;
            case SelectionKeyboardActionKind.SelectionCommitted:
                if (Interactive)
                    CommitSelection(action.Selection);
                else
                    Finish(new SelectionResult(action.Selection, null));
                return;
            case SelectionKeyboardActionKind.DraftCancelled:
                Repaint();
                return;
        }
    }

    private void SyncCursorFromKeyboard()
    {
        CursorX = _keyboard.CursorX;
        CursorY = _keyboard.CursorY;
    }

    private void UsePointerInput(int px, int py)
    {
        _keyboard.CancelDraft();
        _keyboard.MoveTo(px, py);
        SyncCursorFromKeyboard();
    }

    private void RemoveKeyHook()
    {
        if (_keyHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyHook);
            _keyHook = IntPtr.Zero;
        }
        _keyHookProc = null;
        _handledHookKeys.Clear();
    }

    // ---- Interactive selection management ----

    public void CommitSelection(PixelRect rect, WindowInfo? window = null)
    {
        _keyboard.CancelDraft();
        Committed = rect.Intersect(VirtualBounds);
        CommittedWindow = window;
        _areaSelectionOnly = false;
        HoveredWindow = null;
        SelectionEstablished?.Invoke(Committed.Value);
        Repaint();
    }

    public void ClearSelection()
    {
        Committed = null;
        CommittedWindow = null;
        SelectionCleared?.Invoke();
        Repaint();
    }

    /// <summary>
    /// Discards the current interactive selection and any in-flight pointer/keyboard draft so the
    /// next committed result must be a newly drawn area rather than a hovered window or stale drag.
    /// </summary>
    public void BeginFreshAreaSelection()
    {
        if (_finished)
            return;

        _keyboard.CancelDraft();
        _mouseDown = false;
        _drag = DragKind.None;
        _areaSelectionOnly = true;
        Committed = null;
        CommittedWindow = null;
        HoveredWindow = null;
        SelectionCleared?.Invoke();
        Repaint();

        if (ShowFrozen && _windows.Count > 0)
        {
            var focusTarget = _windows.FirstOrDefault(
                window => window.Monitor.Bounds.Contains(CursorX, CursorY)) ?? _windows[0];
            focusTarget.Activate();
        }
    }

    public void RequestConfirm()
    {
        if (Committed is not null)
            ConfirmRequested?.Invoke();
    }

    public SelectionHitZone HitTestSelection(int px, int py)
    {
        if (Committed is not PixelRect r)
            return SelectionHitZone.Outside;

        bool Near(int x, int y) => Math.Abs(px - x) <= HandleHitPx && Math.Abs(py - y) <= HandleHitPx;
        if (Near(r.X, r.Y)) return SelectionHitZone.CornerNW;
        if (Near(r.Right, r.Y)) return SelectionHitZone.CornerNE;
        if (Near(r.X, r.Bottom)) return SelectionHitZone.CornerSW;
        if (Near(r.Right, r.Bottom)) return SelectionHitZone.CornerSE;
        return r.Contains(px, py) ? SelectionHitZone.Inside : SelectionHitZone.Outside;
    }

    // ---- Mouse input from overlay windows ----

    public void UpdateCursor(int px, int py)
    {
        UsePointerInput(px, py);
        px = CursorX;
        py = CursorY;

        if (_mouseDown)
        {
            if (_drag == DragKind.None &&
                (Math.Abs(px - _anchorX) >= DragThresholdPx || Math.Abs(py - _anchorY) >= DragThresholdPx))
            {
                _drag = DragKind.New;
                if (Interactive && Committed is not null)
                    ClearSelection();
            }
            if (_drag == DragKind.Move && Committed is not null)
            {
                var moved = new PixelRect(
                    _moveOrigin.X + (px - _anchorX), _moveOrigin.Y + (py - _anchorY),
                    _moveOrigin.Width, _moveOrigin.Height);
                Committed = ClampToVirtual(moved);
                SelectionChanged?.Invoke(Committed.Value);
            }
            else if (_drag == DragKind.Resize)
            {
                Committed = PixelRect.FromCorners(_anchorX, _anchorY, px, py).Intersect(VirtualBounds);
                SelectionChanged?.Invoke(Committed.Value);
            }
        }
        else
        {
            HoveredWindow = (!_areaSelectionOnly && (!Interactive || Committed is null))
                ? WindowService.HitTest(TopWindows, px, py)
                : null;
        }
        Repaint();
    }

    public void OnMouseDown(int px, int py)
    {
        UsePointerInput(px, py);
        px = CursorX;
        py = CursorY;
        _mouseDown = true;

        if (Interactive && Committed is PixelRect r)
        {
            switch (HitTestSelection(px, py))
            {
                case SelectionHitZone.CornerNW: BeginResize(r.Right, r.Bottom); return;
                case SelectionHitZone.CornerNE: BeginResize(r.X, r.Bottom); return;
                case SelectionHitZone.CornerSW: BeginResize(r.Right, r.Y); return;
                case SelectionHitZone.CornerSE: BeginResize(r.X, r.Y); return;
                case SelectionHitZone.Inside:
                    _drag = DragKind.Move;
                    CommittedWindow = null;
                    _anchorX = px;
                    _anchorY = py;
                    _moveOrigin = r;
                    return;
            }
        }
        // Potential new drag (or a plain click); anchor here.
        _drag = DragKind.None;
        _anchorX = px;
        _anchorY = py;
    }

    private void BeginResize(int fixedX, int fixedY)
    {
        _drag = DragKind.Resize;
        CommittedWindow = null;
        _anchorX = fixedX;
        _anchorY = fixedY;
    }

    public void OnMouseUp(int px, int py)
    {
        if (!_mouseDown)
            return;
        _mouseDown = false;
        UsePointerInput(px, py);
        px = CursorX;
        py = CursorY;
        var kind = _drag;
        _drag = DragKind.None;

        if (!Interactive)
        {
            if (kind == DragKind.New)
            {
                var rect = PixelRect.FromCorners(_anchorX, _anchorY, px, py).Intersect(VirtualBounds);
                Finish(rect is { Width: >= 3, Height: >= 3 }
                    ? new SelectionResult(rect, null)
                    : null);
            }
            else if (HoveredWindow is WindowInfo window)
            {
                Finish(new SelectionResult(window.Bounds.Intersect(VirtualBounds), window));
            }
            else
            {
                Finish(null);
            }
            return;
        }

        // Interactive mode
        switch (kind)
        {
            case DragKind.New:
                var newRect = PixelRect.FromCorners(_anchorX, _anchorY, px, py).Intersect(VirtualBounds);
                if (newRect is { Width: >= 3, Height: >= 3 })
                    CommitSelection(newRect);
                break;

            case DragKind.Move:
            case DragKind.Resize:
                if (Committed is PixelRect r)
                    SelectionEstablished?.Invoke(r); // finalize position for the toolbar
                break;

            default: // plain click
                if (Committed is null && HoveredWindow is WindowInfo w)
                    CommitSelection(w.Bounds.Intersect(VirtualBounds), w);
                break;
        }
        Repaint();
    }

    private PixelRect ClampToVirtual(PixelRect r)
    {
        int x = Math.Clamp(r.X, VirtualBounds.X, Math.Max(VirtualBounds.X, VirtualBounds.Right - r.Width));
        int y = Math.Clamp(r.Y, VirtualBounds.Y, Math.Max(VirtualBounds.Y, VirtualBounds.Bottom - r.Height));
        return new PixelRect(x, y, r.Width, r.Height);
    }

    public void Cancel() => Finish(null);

    private void Finish(SelectionResult? result)
    {
        if (_finished)
            return;
        _finished = true;
        RemoveKeyHook();
        foreach (var window in _windows)
            window.CloseSafe();
        Completed?.Invoke(result);
    }

    private void Repaint()
    {
        foreach (var window in _windows)
            window.RepaintOverlay();
    }
}
