using System.Windows.Controls;
using System.Windows.Input;
using WinShot.Core.Hotkeys;

namespace WinShot.App;

/// <summary>
/// Read-only text box that records a hotkey from an actual key press.
/// Backspace or Delete clears it (disables the hotkey).
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
    }

    /// <summary>The gesture as text ("Ctrl+Shift+A"), or "" when unset.</summary>
    public string GestureText
    {
        get => Text;
        set => Text = value;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Let focus navigation keep working.
        if (key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
            return;

        e.Handled = true;

        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            GestureText = "";
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.Escape)
            return;

        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyModifiers.Win;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (HotkeyGesture.TryCreate(modifiers, vk, out var gesture))
            GestureText = gesture.ToString();
    }
}
