using System.Windows.Interop;
using WinShot.Core.Hotkeys;
using WinShot.Interop;

namespace WinShot.App;

/// <summary>Registers global hotkeys against a hidden message-only window.</summary>
public sealed class HotkeyService : IDisposable
{
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("WinShot.Hotkeys")
        {
            WindowStyle = 0,
            ParentWindow = HWND_MESSAGE,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public bool TryRegister(HotkeyGesture gesture, Action action)
    {
        int id = _nextId++;
        if (!NativeMethods.RegisterHotKey(_source.Handle, id,
                (uint)gesture.Modifiers | NativeMethods.MOD_NOREPEAT, gesture.VirtualKey))
            return false;
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (int id in _actions.Keys)
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _actions.TryGetValue((int)wParam, out Action? action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                App.LogError(ex); // a throwing hotkey must not become a silent dead key
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}
