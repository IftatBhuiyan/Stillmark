using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinShot.App;
using WinShot.Core.Settings;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class ApplicationThemeTests
{
    [Fact]
    public void PaletteMutationPreservesSharedBrushIdentityAcrossThemeChanges()
    {
        RunOnStaThread(() =>
        {
            IReadOnlyDictionary<string, Color> darkPalette =
                ApplicationTheme.PaletteFor(AppearanceKind.Dark);
            var resources = new ResourceDictionary();
            foreach ((string key, Color color) in darkPalette)
                resources[key] = new SolidColorBrush(color);
            resources["OverlayTextSecondaryBrush"] = new SolidColorBrush(
                Color.FromArgb(0xFF, 0xB5, 0xB5, 0xC0));
            var canvas = Assert.IsType<SolidColorBrush>(resources["CanvasBrush"]);
            var primary = Assert.IsType<SolidColorBrush>(resources["TextPrimaryBrush"]);
            var overlay = Assert.IsType<SolidColorBrush>(resources["OverlayTextSecondaryBrush"]);
            Color overlayColor = overlay.Color;

            Assert.False(canvas.IsFrozen);
            ApplicationTheme.ApplyPalette(resources, AppearanceKind.Light);

            Assert.Same(canvas, resources["CanvasBrush"]);
            Assert.Same(primary, resources["TextPrimaryBrush"]);
            Assert.Equal(Color.FromRgb(0xF7, 0xF7, 0xF9), canvas.Color);
            Assert.Equal(Color.FromRgb(0x18, 0x18, 0x1D), primary.Color);
            Assert.Equal(overlayColor, overlay.Color);

            ApplicationTheme.ApplyPalette(resources, AppearanceKind.Dark);
            Assert.Same(canvas, resources["CanvasBrush"]);
            Assert.Equal(Color.FromRgb(0x20, 0x22, 0x24), canvas.Color);
        });
    }

    [Fact]
    public void ThemePaletteBrushesRemainMutableForLiveWindowThemeChanges()
    {
        string theme = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src",
                "WinShot.App",
                "Theme.xaml"));

        Assert.Contains("xmlns:po=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation/options\"", theme, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"TextPrimaryBrush\" po:Freeze=\"False\"", theme, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"SurfaceBrush\" po:Freeze=\"False\"", theme, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((int)AppearanceKind.Dark)]
    [InlineData((int)AppearanceKind.Light)]
    public void PrimaryPaletteTextMeetsNormalTextContrast(int appearanceValue)
    {
        AppearanceKind appearance = (AppearanceKind)appearanceValue;
        IReadOnlyDictionary<string, Color> palette = ApplicationTheme.PaletteFor(appearance);

        Assert.True(
            ContrastRatio(palette["TextPrimaryBrush"], palette["CanvasBrush"]) >= 4.5);
        Assert.True(
            ContrastRatio(palette["TextSecondaryBrush"], palette["SurfaceBrush"]) >= 4.5);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    public void PulsePolicyHonorsReducedMotionAndHighContrast(
        bool clientAreaAnimation,
        bool highContrast,
        bool expected)
    {
        Assert.Equal(
            expected,
            ThemePulseFeedback.ShouldPulse(clientAreaAnimation, highContrast));
    }

    [Fact]
    public void SuppressedPulseDoesNotChangeControlTransform()
    {
        RunOnStaThread(() =>
        {
            var target = new Border();
            Transform before = target.RenderTransform;

            Assert.False(ThemePulseFeedback.TryPulse(target, false, false));
            Assert.Same(before, target.RenderTransform);
        });
    }

    private static double ContrastRatio(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linearize(byte component)
            {
                double value = component / 255.0;
                return value <= 0.04045
                    ? value / 12.92
                    : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linearize(color.R)
                + 0.7152 * Linearize(color.G)
                + 0.0722 * Linearize(color.B);
        }

        double firstLuminance = Luminance(first);
        double secondLuminance = Luminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05)
            / (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA test thread timed out.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "WinShot.sln")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the WinShot repository root.");
    }
}
