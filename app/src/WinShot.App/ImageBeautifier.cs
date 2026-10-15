using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace WinShot.App;

/// <summary>Post-processing that makes window captures look finished: rounded corners and a soft shadow.</summary>
public static class ImageBeautifier
{
    /// <summary>
    /// Returns the capture with Windows-11-style rounded corners and/or a drop shadow, optionally
    /// on a backdrop (<paramref name="background"/> = desktop wallpaper / color / gradient). A null
    /// background keeps the transparent margin. <paramref name="scale"/> is the source monitor's DPI
    /// scale so the treatment is proportionate on high-DPI captures.
    /// </summary>
    public static BitmapSource BeautifyWindowCapture(BitmapSource source, double scale, bool rounded, bool shadow,
        double paddingDip = 30, Brush? background = null)
    {
        bool hasBackground = background is not null;
        bool hasPadding = paddingDip > 0;
        if (!rounded && !shadow && !hasBackground && !hasPadding)
            return source;

        double radius = rounded ? Math.Round(9 * scale) : 0;
        // Padding is an explicit presentation setting, including for transparent backdrops.
        int padding = hasPadding ? (int)Math.Round(Math.Clamp(paddingDip, 8, 200) * scale) : 0;
        int width = source.PixelWidth + padding * 2;
        int height = source.PixelHeight + padding * 2;

        var content = new Border
        {
            Width = source.PixelWidth,
            Height = source.PixelHeight,
            CornerRadius = new CornerRadius(radius),
            Background = new ImageBrush(source) { Stretch = Stretch.Fill },
        };
        if (shadow)
        {
            content.Effect = new DropShadowEffect
            {
                BlurRadius = 24 * scale,
                ShadowDepth = 7 * scale,
                Direction = 270,
                Color = Colors.Black,
                Opacity = 0.42,
            };
        }

        var container = new Grid { Width = width, Height = height };
        if (hasBackground)
            container.Background = background;
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        container.Children.Add(content);

        // Working in 96-DPI units makes 1 layout unit == 1 output pixel.
        container.Measure(new Size(width, height));
        container.Arrange(new Rect(0, 0, width, height));

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(container);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Flattens transparency onto white — for JPEG, which has no alpha channel.</summary>
    public static BitmapSource FlattenToWhite(BitmapSource source)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, source.PixelWidth, source.PixelHeight);
            dc.DrawRectangle(Brushes.White, null, rect);
            dc.DrawImage(source, rect);
        }
        var bitmap = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
