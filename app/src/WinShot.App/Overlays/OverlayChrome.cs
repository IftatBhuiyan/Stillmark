using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace WinShot.App.Overlays;

/// <summary>
/// Crispness helpers for transparent overlay windows. Transparent (layered) windows
/// disable ClearType and WPF's "Ideal" text mode softens small UI text. These helpers
/// snap to whole pixels, re-enable ClearType on opaque cards, and keep a card's drop
/// shadow on a sibling layer — a shadow Effect placed on the content itself would
/// rasterize the text through an intermediate texture and lose ClearType.
/// </summary>
public static class OverlayChrome
{
    internal static bool AnimationsEnabled =>
        SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    public static void MakeCrisp(Window window)
    {
        window.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(window, TextRenderingMode.ClearType);
    }

    public static void EnableClearType(DependencyObject opaqueElement)
    {
        RenderOptions.SetClearTypeHint(opaqueElement, ClearTypeHint.Enabled);
    }

    /// <summary>
    /// An opaque card with its drop shadow on a separate underlay so the card's text
    /// renders with crisp ClearType (never through the shadow's effect texture).
    /// </summary>
    public static FrameworkElement ShadowedCard(
        UIElement content, double cornerRadius, Brush background, Brush border, Thickness padding,
        double blur = 18, double depth = 4, double shadowOpacity = 0.55)
    {
        var shadowLayer = new Border
        {
            CornerRadius = new CornerRadius(cornerRadius),
            Background = background,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Effect = new DropShadowEffect
            {
                BlurRadius = blur,
                ShadowDepth = depth,
                Direction = 270,
                Color = Colors.Black,
                Opacity = shadowOpacity,
            },
        };
        var card = new Border
        {
            CornerRadius = new CornerRadius(cornerRadius),
            Background = background,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Padding = padding,
            Child = content,
        };
        EnableClearType(card);

        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(shadowLayer);
        grid.Children.Add(card);
        return grid;
    }
}
