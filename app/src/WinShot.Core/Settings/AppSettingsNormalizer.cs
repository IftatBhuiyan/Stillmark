using System.Globalization;
using WinShot.Core.Hotkeys;
using WinShot.Core.Naming;

namespace WinShot.Core.Settings;

/// <summary>
/// Applies the settings contract at persistence boundaries. This protects downstream capture and
/// recording code from corrupt or hand-edited JSON while preserving intentional user choices.
/// </summary>
internal static class AppSettingsNormalizer
{
    private static readonly int[] AutoHideChoices = { 0, 5, 10, 30 };
    private static readonly int[] TimerChoices = { 3, 5, 10 };

    // NTFS permits 255 UTF-16 code units in a filename component. Leave room for the extension
    // and the collision suffix added by FileNameTemplate.GetUniquePath.
    private const int MaxExpandedBaseNameLength = 240;
    private const int MaxHotkeyTextLength = 64;
    private const int MaxWindowsPathLength = 32_767;
    private const int MaxGradientIndex = 5;

    public static AppSettings Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        AppSettings defaults = AppSettings.CreateDefault();
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;

        settings.SaveDirectory = NormalizeDirectory(settings.SaveDirectory, defaults.SaveDirectory);
        settings.FileNameTemplate = NormalizeTemplate(settings.FileNameTemplate, defaults.FileNameTemplate);

        if (!Enum.IsDefined(settings.ImageFormat))
            settings.ImageFormat = defaults.ImageFormat;
        if (!Enum.IsDefined(settings.Appearance))
            settings.Appearance = defaults.Appearance;
        settings.JpgQuality = Math.Clamp(settings.JpgQuality, 1, 100);

        settings.Hotkeys ??= new HotkeySettings();
        NormalizeHotkeys(settings.Hotkeys);

        settings.QuickAccessAutoHideSeconds =
            NormalizeChoice(settings.QuickAccessAutoHideSeconds, AutoHideChoices, defaults.QuickAccessAutoHideSeconds);

        if (!Enum.IsDefined(settings.WindowBackground))
            settings.WindowBackground = defaults.WindowBackground;
        settings.WindowBackgroundColor =
            NormalizeArgbColor(settings.WindowBackgroundColor, defaults.WindowBackgroundColor);
        settings.WindowGradientIndex = Math.Clamp(settings.WindowGradientIndex, 0, MaxGradientIndex);
        settings.LastArea = NormalizeLastArea(settings.LastArea);

        // Match the current two-choice UI and its prior effective behavior for hand-edited values.
        settings.RecordingFps = settings.RecordingFps >= 60 ? 60 : 30;
        settings.WindowPadding = ClampFinite(settings.WindowPadding, 8, 200, defaults.WindowPadding);
        settings.QuickAccessPosition =
            string.Equals(settings.QuickAccessPosition, "Right", StringComparison.OrdinalIgnoreCase)
                ? "Right"
                : string.Equals(settings.QuickAccessPosition, "Left", StringComparison.OrdinalIgnoreCase)
                    ? "Left"
                    : defaults.QuickAccessPosition;
        settings.QuickAccessScale = ClampFinite(settings.QuickAccessScale, 0.7, 1.4, defaults.QuickAccessScale);
        settings.TimerSeconds = NormalizeChoice(settings.TimerSeconds, TimerChoices, defaults.TimerSeconds);

        return settings;
    }

    private static string NormalizeDirectory(string? directory, string fallback)
    {
        string value = directory?.Trim() ?? "";
        if (value.Length == 0 || value.Length > MaxWindowsPathLength || HasInvalidWindowsPathCharacter(value))
            return fallback;

        try
        {
            // Validate syntax only. Do not require existence, resolve the returned path, or contact
            // a UNC host: users may intentionally select offline/removable/network locations.
            _ = Path.GetFullPath(value);
            return value;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return fallback;
        }
    }

    private static bool HasInvalidWindowsPathCharacter(string value)
    {
        bool hasExtendedPathPrefix = value.StartsWith(@"\\?\", StringComparison.Ordinal);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character < ' ' || character is '"' or '<' or '>' or '|' or '*'
                || (character == '?' && !(hasExtendedPathPrefix && index == 2)))
            {
                return true;
            }
        }
        return false;
    }

    private static string NormalizeTemplate(string? template, string fallback)
    {
        string value = template?.Trim() ?? "";
        if (value.Length == 0 || value.Length > MaxWindowsPathLength)
            return fallback;

        try
        {
            string sample = FileNameTemplate.Expand(value, new DateTime(2099, 12, 31, 23, 59, 59, 999));
            if (sample.Length == 0 || sample.Length > MaxExpandedBaseNameLength || IsReservedDeviceName(sample))
                return fallback;
            return value;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return fallback;
        }
    }

    private static bool IsReservedDeviceName(string name)
    {
        string fileName = Path.GetFileName(name).TrimEnd(' ', '.');
        int firstDot = fileName.IndexOf('.');
        string stem = firstDot >= 0 ? fileName[..firstDot] : fileName;
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (stem.Length != 4)
            return false;

        string prefix = stem[..3];
        char suffix = stem[3];
        return suffix is >= '1' and <= '9'
            && (prefix.Equals("COM", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private static void NormalizeHotkeys(HotkeySettings hotkeys)
    {
        hotkeys.AreaCapture = NormalizeHotkey(hotkeys.AreaCapture);
        hotkeys.FullscreenCapture = NormalizeHotkey(hotkeys.FullscreenCapture);
        hotkeys.AllMonitorsCapture = NormalizeHotkey(hotkeys.AllMonitorsCapture);
        hotkeys.RepeatLastArea = NormalizeHotkey(hotkeys.RepeatLastArea);
        hotkeys.TextCapture = NormalizeHotkey(hotkeys.TextCapture);
        hotkeys.RecordScreen = NormalizeHotkey(hotkeys.RecordScreen);
        hotkeys.AllInOne = NormalizeHotkey(hotkeys.AllInOne);
    }

    private static string NormalizeHotkey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        string trimmed = value.Trim();
        return trimmed.Length <= MaxHotkeyTextLength && HotkeyGesture.TryParse(trimmed, out var gesture)
            ? gesture.ToString()
            : "";
    }

    private static string NormalizeArgbColor(string? value, string fallback)
    {
        string trimmed = value?.Trim() ?? "";
        return trimmed.Length == 9
            && trimmed[0] == '#'
            && uint.TryParse(trimmed.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
                ? trimmed.ToUpperInvariant()
                : fallback;
    }

    private static int[]? NormalizeLastArea(int[]? area)
    {
        if (area is not { Length: 4 } || area[2] <= 0 || area[3] <= 0)
            return null;

        long right = (long)area[0] + area[2];
        long bottom = (long)area[1] + area[3];
        return right <= int.MaxValue && bottom <= int.MaxValue
            ? (int[])area.Clone()
            : null;
    }

    private static int NormalizeChoice(int value, int[] choices, int fallback) =>
        Array.IndexOf(choices, value) >= 0 ? value : fallback;

    private static double ClampFinite(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
