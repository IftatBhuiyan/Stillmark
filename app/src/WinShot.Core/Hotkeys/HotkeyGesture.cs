namespace WinShot.Core.Hotkeys;

/// <summary>Modifier flags matching the Win32 MOD_* values used by RegisterHotKey.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>A parsed global hotkey (modifiers + virtual key), serialized as e.g. "Ctrl+Shift+A".</summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private static readonly Dictionary<string, uint> NameToVk = BuildNameMap();
    private static readonly Dictionary<uint, string> VkToName =
        NameToVk.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

    private static Dictionary<string, uint> BuildNameMap()
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        for (char c = 'A'; c <= 'Z'; c++)
            map[c.ToString()] = (uint)c;
        for (char c = '0'; c <= '9'; c++)
            map[c.ToString()] = (uint)c;
        for (int i = 1; i <= 24; i++)
            map["F" + i] = (uint)(0x70 + i - 1);
        map["Space"] = 0x20;
        map["PageUp"] = 0x21;
        map["PageDown"] = 0x22;
        map["End"] = 0x23;
        map["Home"] = 0x24;
        map["Left"] = 0x25;
        map["Up"] = 0x26;
        map["Right"] = 0x27;
        map["Down"] = 0x28;
        map["PrintScreen"] = 0x2C;
        map["Insert"] = 0x2D;
        map["Delete"] = 0x2E;
        return map;
    }

    /// <summary>Keys that are acceptable as a hotkey without any modifier.</summary>
    private static bool IsStandaloneKey(uint vk) =>
        (vk >= 0x70 && vk <= 0x87) || vk == 0x2C; // F1-F24, PrintScreen

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = null!;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var modifiers = HotkeyModifiers.None;
        uint vk = 0;
        bool keyFound = false;

        foreach (string rawPart in text.Split('+'))
        {
            string part = rawPart.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "win" or "windows":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                default:
                    if (keyFound || !NameToVk.TryGetValue(part, out vk))
                        return false;
                    keyFound = true;
                    break;
            }
        }

        if (!keyFound || (modifiers == HotkeyModifiers.None && !IsStandaloneKey(vk)))
            return false;

        gesture = new HotkeyGesture(modifiers, vk);
        return true;
    }

    public static bool TryCreate(HotkeyModifiers modifiers, uint vk, out HotkeyGesture gesture)
    {
        gesture = null!;
        if (!VkToName.ContainsKey(vk))
            return false;
        if (modifiers == HotkeyModifiers.None && !IsStandaloneKey(vk))
            return false;
        gesture = new HotkeyGesture(modifiers, vk);
        return true;
    }

    public static bool TryGetKeyName(uint vk, out string name) => VkToName.TryGetValue(vk, out name!);

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(VkToName.TryGetValue(VirtualKey, out string? name) ? name : $"0x{VirtualKey:X2}");
        return string.Join("+", parts);
    }
}
