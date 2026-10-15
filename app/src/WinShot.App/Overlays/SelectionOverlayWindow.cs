using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.Capture;
using WinShot.Interop;

namespace WinShot.App.Overlays;

/// <summary>
/// Borderless topmost window covering exactly one monitor during selection.
/// Displays the frozen screen so the capture reflects the moment the hotkey was pressed.
/// </summary>
public sealed class SelectionOverlayWindow : Window
{
    private readonly SelectionSession _session;
    private readonly SelectionRenderer _renderer;

    public MonitorInfo Monitor { get; }

    public SelectionOverlayWindow(SelectionSession session, MonitorInfo monitor)
    {
        _session = session;
        Monitor = monitor;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Cursor = Cursors.None;
        AutomationProperties.SetName(this, "Capture area selection");
        AutomationProperties.SetHelpText(
            this,
            "Arrow keys move the selection cursor one pixel; hold Shift to move ten pixels. " +
            "Space sets or finishes an area. Enter confirms a selected area. " +
            "Escape cancels the current area, then closes selection.");
        AutomationProperties.SetItemStatus(this, session.KeyboardStatus);

        BitmapSource? monitorCrop = null;
        if (session.ShowFrozen)
        {
            Background = Brushes.Black;
            var crop = new CroppedBitmap(session.FrozenScreen, new Int32Rect(
                monitor.Bounds.X - session.VirtualBounds.X,
                monitor.Bounds.Y - session.VirtualBounds.Y,
                monitor.Bounds.Width,
                monitor.Bounds.Height));
            crop.Freeze();
            monitorCrop = crop;
        }
        else
        {
            // See-through overlay: the live desktop shows during selection. An almost-invisible
            // (alpha 1) background keeps the layered window hit-testable — a fully transparent
            // background would let clicks fall through to the windows underneath.
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(0x01, 0x00, 0x00, 0x00));
        }

        _renderer = new SelectionRenderer(session, monitor, monitorCrop);
        var root = new Grid();
        root.Children.Add(_renderer);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            // Position with physical pixels; WPF then adopts this monitor's DPI.
            var hwnd = new WindowInteropHelper(this).Handle;
            if (!_session.ShowFrozen)
            {
                // Freeze-off: never activate on click, so the foreground window keeps focus and stays open.
                int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
                NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                    exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
            }
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                Monitor.Bounds.X, Monitor.Bounds.Y, Monitor.Bounds.Width, Monitor.Bounds.Height,
                NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
        };
    }

    public void RepaintOverlay()
    {
        AutomationProperties.SetItemStatus(this, _session.KeyboardStatus);
        _renderer.InvalidateVisual();
    }

    public void CloseSafe()
    {
        try
        {
            Close();
        }
        catch
        {
            // Window may already be closing during teardown.
        }
    }

    private (int X, int Y) ToPhysical(Point dip)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return (
            Monitor.Bounds.X + (int)Math.Round(dip.X * dpi.DpiScaleX),
            Monitor.Bounds.Y + (int)Math.Round(dip.Y * dpi.DpiScaleY));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (x, y) = ToPhysical(e.GetPosition(this));
        _session.UpdateCursor(x, y);

        if (_session.Interactive && _session.Committed is not null && !_session.IsDragging)
        {
            Cursor = _session.HitTestSelection(x, y) switch
            {
                SelectionHitZone.CornerNW or SelectionHitZone.CornerSE => Cursors.SizeNWSE,
                SelectionHitZone.CornerNE or SelectionHitZone.CornerSW => Cursors.SizeNESW,
                SelectionHitZone.Inside => Cursors.SizeAll,
                _ => Cursors.Arrow,
            };
        }
        else if (Cursor != Cursors.None)
        {
            Cursor = Cursors.None;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        var (x, y) = ToPhysical(e.GetPosition(this));
        _session.OnMouseDown(x, y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
        var (x, y) = ToPhysical(e.GetPosition(this));
        _session.OnMouseUp(x, y);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        _session.Cancel();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        bool unsupportedModifier =
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
            Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ||
            Keyboard.Modifiers.HasFlag(ModifierKeys.Windows);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (!e.IsRepeat)
                _session.HandleKeyboardEscape();
        }
        else if (!unsupportedModifier && e.Key == Key.Enter)
        {
            e.Handled = true;
            _session.HandleKeyboardConfirm(e.IsRepeat);
        }
        else if (!unsupportedModifier && e.Key == Key.Space)
        {
            e.Handled = true;
            _session.HandleKeyboardActivate(e.IsRepeat);
        }
        else if (!unsupportedModifier &&
                 TryMapDirection(e.Key, out SelectionKeyboardDirection direction))
        {
            e.Handled = true;
            _session.HandleKeyboardMove(
                direction,
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }
    }

    private static bool TryMapDirection(
        Key key,
        out SelectionKeyboardDirection direction)
    {
        direction = key switch
        {
            Key.Left => SelectionKeyboardDirection.Left,
            Key.Right => SelectionKeyboardDirection.Right,
            Key.Up => SelectionKeyboardDirection.Up,
            Key.Down => SelectionKeyboardDirection.Down,
            _ => default,
        };
        return key is Key.Left or Key.Right or Key.Up or Key.Down;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _session.Cancel(); // no-op if the session already finished; covers Alt+F4 on one overlay
    }
}
