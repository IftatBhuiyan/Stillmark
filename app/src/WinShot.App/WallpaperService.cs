using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.Core.Settings;
using WinShot.Interop;

namespace WinShot.App;

/// <summary>
/// Resolves the backdrop for window screenshots and supplies the color / gradient presets
/// shared by the settings UI and the beautifier.
/// </summary>
public static class WallpaperService
{
    /// <summary>Solid-color presets offered in the Wallpaper settings (stored as #AARRGGBB).</summary>
    public static readonly IReadOnlyList<Color> ColorPresets = new[]
    {
        Color.FromRgb(0x2E, 0x3A, 0x59), // deep blue
        Color.FromRgb(0x1E, 0x1E, 0x24), // near-black
        Color.FromRgb(0xF2, 0xF3, 0xF5), // soft white
        Color.FromRgb(0xE8, 0x5D, 0x4E), // coral
        Color.FromRgb(0x37, 0x8E, 0x6E), // green
        Color.FromRgb(0x8A, 0x5C, 0xD1), // purple
        Color.FromRgb(0xF5, 0xA6, 0x23), // amber
        Color.FromRgb(0x22, 0x66, 0xAA), // azure
    };

    /// <summary>Two-stop linear gradient presets (top-left → bottom-right).</summary>
    public static readonly IReadOnlyList<(Color From, Color To)> GradientPresets = new[]
    {
        (Color.FromRgb(0x6D, 0x5B, 0xF8), Color.FromRgb(0xC0, 0x4C, 0xF0)), // indigo → magenta
        (Color.FromRgb(0xFF, 0x8A, 0x5B), Color.FromRgb(0xFF, 0x4D, 0x6D)), // orange → pink
        (Color.FromRgb(0x2A, 0xF5, 0x98), Color.FromRgb(0x08, 0xAE, 0xEA)), // mint → sky
        (Color.FromRgb(0x30, 0x3A, 0x52), Color.FromRgb(0x16, 0x18, 0x22)), // slate → charcoal
        (Color.FromRgb(0xFB, 0xC2, 0xEB), Color.FromRgb(0xA6, 0xC1, 0xEE)), // blush → periwinkle
        (Color.FromRgb(0xF0, 0xC2, 0x7B), Color.FromRgb(0x4B, 0x13, 0x08)), // sand → mahogany
    };

    public static SolidColorBrush ColorBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static LinearGradientBrush GradientBrush(int index)
    {
        var (from, to) = GradientPresets[Math.Clamp(index, 0, GradientPresets.Count - 1)];
        var brush = new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>The backdrop brush for the current settings, or null for a transparent margin.</summary>
    public static Brush? ResolveBackground(AppSettings s) => s.WindowBackground switch
    {
        WindowBackgroundKind.Color => ColorBrush(ParseColor(s.WindowBackgroundColor)),
        WindowBackgroundKind.Gradient => GradientBrush(s.WindowGradientIndex),
        WindowBackgroundKind.Desktop => DesktopWallpaperBrush(),
        _ => null,
    };

    public static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return ColorPresets[0];
        }
    }

    /// <summary>The current desktop wallpaper as a fill brush (cropped to fit), or a solid fallback.</summary>
    private static Brush DesktopWallpaperBrush()
    {
        try
        {
            var sb = new StringBuilder(520);
            if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0)
                && sb.Length > 0 && File.Exists(sb.ToString()))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.UriSource = new Uri(sb.ToString());
                image.EndInit();
                image.Freeze();

                var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                brush.Freeze();
                return brush;
            }
        }
        catch
        {
            // Fall through to a neutral solid backdrop.
        }
        return ColorBrush(ColorPresets[0]);
    }
}
