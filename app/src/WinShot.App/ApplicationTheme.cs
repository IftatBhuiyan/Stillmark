using System.Windows;
using System.Windows.Media;
using WinShot.Core.Settings;

namespace WinShot.App;

internal static class ApplicationTheme
{
    private static readonly IReadOnlyDictionary<string, Color> DarkPalette = CreateDarkPalette();
    private static readonly IReadOnlyDictionary<string, Color> LightPalette = CreateLightPalette();

    internal static AppearanceKind Current { get; private set; } = AppearanceKind.Dark;

    internal static void Apply(AppearanceKind appearance)
    {
        if (!Enum.IsDefined(appearance))
            appearance = AppearanceKind.Dark;

        Current = appearance;
        Application? application = Application.Current;
        if (application is null)
            return;

        ApplyPalette(application.Resources, appearance);
        foreach (Window window in application.Windows)
        {
            WindowTheming.ApplyTitleBar(window);
            window.InvalidateVisual();
        }
    }

    internal static void ApplyPalette(ResourceDictionary resources, AppearanceKind appearance)
    {
        IReadOnlyDictionary<string, Color> palette = appearance == AppearanceKind.Light
            ? LightPalette
            : DarkPalette;
        foreach ((string key, Color color) in palette)
        {
            ResourceDictionary? owner = FindOwner(resources, key);
            if (owner is null)
                continue;

            if (owner[key] is SolidColorBrush brush && !brush.IsFrozen)
            {
                brush.Color = color;
            }
            else
            {
                owner[key] = new SolidColorBrush(color);
            }
        }
    }

    internal static IReadOnlyDictionary<string, Color> PaletteFor(AppearanceKind appearance) =>
        appearance == AppearanceKind.Light ? LightPalette : DarkPalette;

    private static ResourceDictionary? FindOwner(ResourceDictionary resources, string key)
    {
        if (resources.Contains(key))
            return resources;

        for (int index = resources.MergedDictionaries.Count - 1; index >= 0; index--)
        {
            ResourceDictionary? owner = FindOwner(resources.MergedDictionaries[index], key);
            if (owner is not null)
                return owner;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, Color> CreateDarkPalette() =>
        Palette(
            window: "#FF202224",
            card: "#FF252729",
            elevated: "#FF303336",
            hover: "#FF393D40",
            pressed: "#FF292C2F",
            input: "#FF1C1E20",
            controlBorder: "#FF53595F",
            controlBorderHover: "#FF79828A",
            cardBorder: "#0FFFFFFF",
            textPrimary: "#FFF0F1F2",
            textSecondary: "#FFADB2B7",
            canvas: "#FF202224",
            surface: "#FF202224",
            surfaceRaised: "#FF2B2E31",
            surfaceHover: "#FF303437",
            borderSoft: "#0FFFFFFF",
            borderStrong: "#28FFFFFF",
            textTertiary: "#FF92999F",
            focus: "#FF9EC5E8",
            success: "#FF62C98D");

    private static IReadOnlyDictionary<string, Color> CreateLightPalette() =>
        Palette(
            window: "#FFF7F7F9",
            card: "#FFFFFFFF",
            elevated: "#FFF0F0F4",
            hover: "#FFE8E8EE",
            pressed: "#FFDDDDE5",
            input: "#FFFFFFFF",
            controlBorder: "#FFD0D0D9",
            controlBorderHover: "#FFB5B5C1",
            cardBorder: "#18000000",
            textPrimary: "#FF18181D",
            textSecondary: "#FF5F616B",
            canvas: "#FFF7F7F9",
            surface: "#FFFFFFFF",
            surfaceRaised: "#FFF0F0F4",
            surfaceHover: "#FFE8E8EE",
            borderSoft: "#18000000",
            borderStrong: "#2B000000",
            textTertiary: "#FF747680",
            focus: "#FF28649A",
            success: "#FF168653");

    private static IReadOnlyDictionary<string, Color> Palette(
        string window,
        string card,
        string elevated,
        string hover,
        string pressed,
        string input,
        string controlBorder,
        string controlBorderHover,
        string cardBorder,
        string textPrimary,
        string textSecondary,
        string canvas,
        string surface,
        string surfaceRaised,
        string surfaceHover,
        string borderSoft,
        string borderStrong,
        string textTertiary,
        string focus,
        string success)
    {
        static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);

        return new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            ["WindowBackgroundBrush"] = Parse(window),
            ["CardBrush"] = Parse(card),
            ["ElevatedBrush"] = Parse(elevated),
            ["HoverBrush"] = Parse(hover),
            ["PressedBrush"] = Parse(pressed),
            ["InputBrush"] = Parse(input),
            ["ControlBorderBrush"] = Parse(controlBorder),
            ["ControlBorderHoverBrush"] = Parse(controlBorderHover),
            ["CardBorderBrush"] = Parse(cardBorder),
            ["TextPrimaryBrush"] = Parse(textPrimary),
            ["TextSecondaryBrush"] = Parse(textSecondary),
            ["AccentBrush"] = Parse("#FF626E78"),
            ["AccentHoverBrush"] = Parse(hover),
            ["AccentPressedBrush"] = Parse(pressed),
            ["AccentSubtleBrush"] = Parse(elevated),
            ["CanvasBrush"] = Parse(canvas),
            ["SurfaceBrush"] = Parse(surface),
            ["SurfaceRaisedBrush"] = Parse(surfaceRaised),
            ["SurfaceHoverBrush"] = Parse(surfaceHover),
            ["BorderSoftBrush"] = Parse(borderSoft),
            ["BorderStrongBrush"] = Parse(borderStrong),
            ["TextTertiaryBrush"] = Parse(textTertiary),
            ["QuietAccentBrush"] = Parse(textPrimary),
            ["QuietAccentHoverBrush"] = Parse(hover),
            ["QuietAccentSubtleBrush"] = Parse(elevated),
            ["FocusBrush"] = Parse(focus),
            ["RecordBrush"] = Parse("#FFFF646D"),
            ["RecordSubtleBrush"] = Parse("#28FF646D"),
            ["SuccessBrush"] = Parse(success),
            ["DangerBrush"] = Parse("#FFE5484D"),
        };
    }
}
