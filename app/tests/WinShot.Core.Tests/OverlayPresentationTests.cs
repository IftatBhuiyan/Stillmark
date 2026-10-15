using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using WinShot.App;
using WinShot.App.Overlays;
using WinShot.App.QuickAccess;
using WinShot.App.Scrolling;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class OverlayPresentationTests
{
    [Fact]
    public void ToolbarGlyphWithoutSemanticColorInheritsControlForeground()
    {
        RunOnStaThread(() =>
        {
            TextBlock glyph = AllInOneToolbar.CreateToolbarGlyph("glyph");
            Assert.Same(
                DependencyProperty.UnsetValue,
                glyph.ReadLocalValue(TextBlock.ForegroundProperty));

            var owner = new Button
            {
                Foreground = Brushes.White,
                Content = glyph,
            };
            Assert.Same(Brushes.White, glyph.Foreground);

            owner.Foreground = Brushes.Orange;
            Assert.Same(Brushes.Orange, glyph.Foreground);
        });
    }

    [Fact]
    public void ToolbarGlyphWithSemanticColorKeepsExplicitForeground()
    {
        RunOnStaThread(() =>
        {
            TextBlock glyph = AllInOneToolbar.CreateToolbarGlyph("glyph", Brushes.Red);
            var owner = new Button
            {
                Foreground = Brushes.White,
                Content = glyph,
            };

            Assert.Same(Brushes.Red, glyph.Foreground);
            owner.Foreground = Brushes.Orange;
            Assert.Same(Brushes.Red, glyph.Foreground);
        });
    }

    [Fact]
    public void ScrollingCaptureDimsOutsideSelectedRegion()
    {
        Assert.True(ScrollingCaptureController.DimOutsideSelectedRegion);
    }

    [Fact]
    public void QuickAccessCloseChromeIsOpaqueAndContrastsWithWhiteGlyph()
    {
        RunOnStaThread(() =>
        {
            var button = new Button();
            Border chrome = QuickAccessWindow.CreateCloseButtonChrome(button);
            var background = Assert.IsType<SolidColorBrush>(chrome.Background);

            Assert.Equal(byte.MaxValue, background.Color.A);
            Assert.True(
                ContrastRatio(Colors.White, background.Color) >= 4.5,
                "The close glyph must remain legible independently of the captured image colors.");
            Assert.Same(button, chrome.Child);
        });
    }

    [Theory]
    [InlineData(0.7)]
    [InlineData(1.0)]
    [InlineData(1.4)]
    public void SharedQuickAccessPresenterUsesProductionGeometry(double scale)
    {
        RunOnStaThread(() =>
        {
            QuickAccessCardPresenter presenter = CreateQuickAccessPresenter(
                isRecording: false,
                previewMode: false,
                scale);

            Assert.Equal(264 * scale, presenter.Thumbnail.Width, precision: 6);
            Assert.Equal(166 * scale, presenter.Thumbnail.Height, precision: 6);
            Assert.Equal(42, presenter.ActionBar.Height);
            Assert.Equal(28, presenter.CloseChrome.Width);
            Button closeButton = presenter.CommandButtons[TransientOverlayCommand.Close];
            Assert.All(presenter.ActionButtons.Where(button => !ReferenceEquals(button, closeButton)), button =>
            {
                Assert.Equal(30, button.Width);
                Assert.Equal(30, button.Height);
            });
            Assert.Equal(24, closeButton.Width);
            Assert.Equal(24, closeButton.Height);

            BitmapSource snapshot = presenter.RenderSnapshot();
            Assert.Equal((int)Math.Ceiling(264 * scale + 48), snapshot.PixelWidth);
            Assert.Equal((int)Math.Ceiling(166 * scale + 48), snapshot.PixelHeight);
            Assert.True(snapshot.IsFrozen);

            presenter.SetCaptureScale(scale == 1.4 ? 0.7 : 1.4);
            Assert.All(
                presenter.ActionButtons.Where(button => !ReferenceEquals(button, closeButton)),
                button => Assert.Equal(30, button.Width));
            Assert.Equal(24, closeButton.Width);
        });
    }

    [Fact]
    public void SharedQuickAccessPresenterKeepsImageAndRecordingActionsExact()
    {
        RunOnStaThread(() =>
        {
            QuickAccessCardPresenter image = CreateQuickAccessPresenter(
                isRecording: false,
                previewMode: false,
                scale: 1);
            Assert.Equal(
                new[]
                {
                    TransientOverlayCommand.Copy,
                    TransientOverlayCommand.Annotate,
                    TransientOverlayCommand.Pin,
                    TransientOverlayCommand.Save,
                    TransientOverlayCommand.Open,
                    TransientOverlayCommand.Close,
                },
                image.CommandButtons.Keys);
            var brush = Assert.IsType<ImageBrush>(image.Thumbnail.Background);
            Assert.Equal(Stretch.UniformToFill, brush.Stretch);
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.IsType<TextBlock>(image.CommandButtons[TransientOverlayCommand.Annotate].Content).Text));

            QuickAccessCardPresenter recording = CreateQuickAccessPresenter(
                isRecording: true,
                previewMode: false,
                scale: 1);
            Assert.Equal(
                new[]
                {
                    TransientOverlayCommand.Copy,
                    TransientOverlayCommand.Save,
                    TransientOverlayCommand.Open,
                    TransientOverlayCommand.Close,
                },
                recording.CommandButtons.Keys);
        });
    }

    [Fact]
    public void SettingsQuickAccessPresenterIsPassiveAndShowsProductionHoverChrome()
    {
        RunOnStaThread(() =>
        {
            QuickAccessCardPresenter presenter = CreateQuickAccessPresenter(
                isRecording: false,
                previewMode: true,
                scale: 1);
            presenter.SetInteractionChromeVisible(visible: true, animate: false);

            Assert.False(presenter.Root.IsHitTestVisible);
            Assert.Equal(1, presenter.HoverLayer.Opacity);
            Assert.All(presenter.ActionButtons, button =>
            {
                Assert.False(button.IsTabStop);
                Assert.False(button.Focusable);
            });

            string productionSource = File.ReadAllText(
                FindRepositoryFile("src", "WinShot.App", "QuickAccess", "QuickAccessWindow.cs"));
            Assert.Contains("new QuickAccessCardPresenter", productionSource, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void OcrToolbarEntryIsAOneShotButtonAction()
    {
        var constructor = Assert.Single(
            typeof(AllInOneToolbar).GetConstructors(),
            candidate => candidate.GetParameters().Length == 8);

        Assert.Equal(typeof(Action), constructor.GetParameters()[4].ParameterType);
        Assert.Equal(
            typeof(Button),
            typeof(AllInOneToolbar)
                .GetProperty(
                    "OcrButton",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .PropertyType);
    }

    [Fact]
    public void OcrProgressSurfaceIsPrivateAccessibleAndIndeterminate()
    {
        RunOnStaThread(() =>
        {
            var progress = new OcrProgressWindow(new WinShot.Core.PixelRect(10, 20, 100, 80));

            Assert.False(progress.ShowActivated);
            Assert.False(progress.ShowInTaskbar);
            Assert.True(progress.Topmost);
            Assert.True(progress.ProgressIndicator.IsIndeterminate);
            Assert.Equal("Recognizing text…", progress.StatusText.Text);
            Assert.Equal(
                AutomationLiveSetting.Polite,
                AutomationProperties.GetLiveSetting(progress.StatusText));
            Assert.Equal(
                "Text recognition in progress",
                AutomationProperties.GetName(progress));
            Assert.DoesNotContain("clipboard", progress.StatusText.Text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void OcrResultDeliveryMarshalsThreadAffineWorkToTheUiDispatcher()
    {
        string source = File.ReadAllText(
            FindRepositoryFile("src", "WinShot.App", "CaptureController.cs"));

        Assert.Contains("await InvokeOnUiAsync", source, StringComparison.Ordinal);
        Assert.Contains("ClipboardService.SetText(text, sensitive: true)", source, StringComparison.Ordinal);
        Assert.Contains("await CloseOcrProgressAsync", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(100, 100, 200, 100, 0, 0, 800, 600, 250, 82, 10, 75, 210)]
    [InlineData(100, 520, 200, 60, 0, 0, 800, 600, 250, 82, 10, 75, 428)]
    [InlineData(-900, -300, 300, 280, -1000, -400, 1000, 700, 250, 82, 10, -875, -10)]
    public void OcrProgressPlacementStaysInWorkAreaAndPrefersOutsideSelection(
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        int workX,
        int workY,
        int workWidth,
        int workHeight,
        int progressWidth,
        int progressHeight,
        int gap,
        int expectedX,
        int expectedY)
    {
        var placement = OcrProgressWindow.CalculatePlacement(
            new WinShot.Core.PixelRect(sourceX, sourceY, sourceWidth, sourceHeight),
            new WinShot.Core.PixelRect(workX, workY, workWidth, workHeight),
            progressWidth,
            progressHeight,
            gap);

        Assert.Equal(new WinShot.Core.PixelRect(expectedX, expectedY, progressWidth, progressHeight), placement);
    }

    [Fact]
    public void GeneralAppearanceAndStartupUseTheSharedGroupedRowSystem()
    {
        XDocument document = XDocument.Load(FindRepositoryFile("src", "WinShot.App", "SettingsWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement group = Assert.Single(
            document.Descendants(presentation + "StackPanel"),
            element => (string?)element.Attribute(x + "Name") == "StartupSettingGroup");
        XElement status = Assert.Single(
            group.Elements(presentation + "TextBlock"),
            element => (string?)element.Attribute(x + "Name") == "StartupStatusText");
        XElement theme = Assert.Single(
            document.Descendants(presentation + "CheckBox"),
            element => (string?)element.Attribute(x + "Name") == "ThemeCheck");

        Assert.Contains("SettingsRow", theme.Parent?.Attribute("Style")?.Value ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("SettingsRow", group.Parent?.Attribute("Style")?.Value ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("0,5,54,0", (string?)status.Attribute("Margin"));
    }

    [Fact]
    public void WindowPresentationPaneExposesRegularCaptureOption()
    {
        XDocument document = XDocument.Load(FindRepositoryFile("src", "WinShot.App", "SettingsWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement option = Assert.Single(
            document.Descendants(presentation + "CheckBox"),
            element => (string?)element.Attribute(x + "Name") == "ApplyAreaPresentationCheck");

        Assert.Equal(
            "Apply padding and backdrop to regular captures",
            option.Attributes().Single(attribute =>
                attribute.Name.LocalName.Contains("Name", StringComparison.Ordinal) &&
                attribute.Name.Namespace != x).Value);
        Assert.Contains(
            "area, display, repeat, timed, and scrolling captures",
            option.Attributes().Single(attribute => attribute.Name.LocalName.Contains("HelpText", StringComparison.Ordinal)).Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowPaneTogglesStayVisibleWhenOffAndPreviewUsesCaptureRenderer()
    {
        string theme = File.ReadAllText(
            FindRepositoryFile("src", "WinShot.App", "Theme.xaml"));
        string settings = File.ReadAllText(
            FindRepositoryFile("src", "WinShot.App", "SettingsWindow.xaml"));
        string code = File.ReadAllText(
            FindRepositoryFile("src", "WinShot.App", "SettingsWindow.xaml.cs"));
        string beautifier = File.ReadAllText(
            FindRepositoryFile("src", "WinShot.App", "ImageBeautifier.cs"));

        Assert.Contains("x:Key=\"ToggleSwitch\"", theme, StringComparison.Ordinal);
        Assert.Contains("Background=\"{DynamicResource SurfaceRaisedBrush}\"", theme, StringComparison.Ordinal);
        Assert.Contains("BorderBrush=\"{DynamicResource BorderStrongBrush}\"", theme, StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment\" Value=\"Stretch\"", settings, StringComparison.Ordinal);
        Assert.Contains("ImageBeautifier.BeautifyWindowCapture", code, StringComparison.Ordinal);
        Assert.Contains("CreatePreviewWindowImage", code, StringComparison.Ordinal);
        Assert.Contains("ResolvePreviewBackdrop", code, StringComparison.Ordinal);
        Assert.Contains("paddingDip > 0", beautifier, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPreviewsTrackWindowAndOverlayPresentationChoices()
    {
        XDocument document = XDocument.Load(FindRepositoryFile("src", "WinShot.App", "SettingsWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement wallpaper = Assert.Single(
            document.Descendants(),
            element => (string?)element.Attribute(x + "Name") == "PaneWallpaper");
        Assert.Equal("Disabled", (string?)wallpaper.Attribute("HorizontalScrollBarVisibility"));
        foreach (string name in new[]
                 {
                     "PositionBox",
                     "SizeSlider",
                     "AutoHideBox",
                     "OverlayPreviewTile",
                     "OverlayPreviewCardHost",
                     "OverlayPreviewCaption",
                     "CloseAfterDragCheck",
                 })
        {
            Assert.NotNull(wallpaper.Descendants().SingleOrDefault(
                element => (string?)element.Attribute(x + "Name") == name));
        }
        Assert.Null(document.Descendants().SingleOrDefault(
            element => (string?)element.Attribute(x + "Name") == "TabQuickAccess"));
        Assert.Null(document.Descendants().SingleOrDefault(
            element => (string?)element.Attribute(x + "Name") == "PaneQuickAccess"));
        Assert.DoesNotContain(
            document.Descendants().Attributes(x + "Name"),
            attribute => attribute.Value.StartsWith("Legacy", StringComparison.Ordinal));

        string[] expectedTabs =
        {
            "TabGeneral",
            "TabWallpaper",
            "TabShortcuts",
            "TabRecording",
            "TabScreenshots",
            "TabAbout",
        };
        Assert.Equal(
            expectedTabs,
            document.Descendants(presentation + "ToggleButton")
                .Select(element => (string?)element.Attribute(x + "Name"))
                .Where(name => name is not null && name.StartsWith("Tab", StringComparison.Ordinal))
                .Cast<string>());

        string source = File.ReadAllText(
            FindRepositoryFile("src", "WinShot.App", "SettingsWindow.xaml.cs"));
        Assert.Contains("RenderWallpaperPreview", source, StringComparison.Ordinal);
        Assert.Contains("RenderOverlayPreview", source, StringComparison.Ordinal);
        Assert.Contains("new QuickAccessCardPresenter", source, StringComparison.Ordinal);
        Assert.Contains("SetCaptureScale(scale)", source, StringComparison.Ordinal);
        Assert.Contains("RenderSnapshot()", source, StringComparison.Ordinal);
        Assert.Contains("OverlayPreviewCaption.Text", source, StringComparison.Ordinal);
        Assert.Contains("OverlayPreviewDesktop.Background", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorCanvasZoomUsesWheelInputAndNotifiesTheWindow()
    {
        string source = File.ReadAllText(
            FindRepositoryFile("src", "WinShot.App", "Editor", "EditorCanvas.cs"));

        Assert.Contains("OnMouseWheel", source, StringComparison.Ordinal);
        Assert.Contains("Math.Pow(1.1", source, StringComparison.Ordinal);
        Assert.Contains("ViewportChanged", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RegularCapturePresentationOptionRoutesThroughCentralDelivery()
    {
        string source = File.ReadAllText(
            FindRepositoryFile("src", "WinShot.App", "CaptureController.cs"));

        Assert.Contains(
            "settings.ApplyWindowPresentationToAreaCaptures",
            source,
            StringComparison.Ordinal);
        Assert.Contains("ApplyCapturePresentationAsync", source, StringComparison.Ordinal);
        Assert.Contains("isWindowCapture", source, StringComparison.Ordinal);
    }

    private static QuickAccessCardPresenter CreateQuickAccessPresenter(
        bool isRecording,
        bool previewMode,
        double scale)
    {
        var owner = new Border();
        var buttonStyle = new Style(typeof(Button));
        buttonStyle.Setters.Add(new Setter(FrameworkElement.WidthProperty, 30d));
        buttonStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 30d));
        owner.Resources["OverlayIconButton"] = buttonStyle;

        RoutedEventHandler passiveHandler = (_, e) => e.Handled = true;
        var handlers = new Dictionary<TransientOverlayCommand, RoutedEventHandler>
        {
            [TransientOverlayCommand.Copy] = passiveHandler,
            [TransientOverlayCommand.Annotate] = passiveHandler,
            [TransientOverlayCommand.Pin] = passiveHandler,
            [TransientOverlayCommand.Save] = passiveHandler,
            [TransientOverlayCommand.Open] = passiveHandler,
            [TransientOverlayCommand.Close] = passiveHandler,
        };
        var drawing = new DrawingImage(
            new GeometryDrawing(Brushes.CornflowerBlue, null, new RectangleGeometry(new Rect(0, 0, 4, 4))));

        return new QuickAccessCardPresenter(
            owner,
            drawing,
            "Representative capture",
            isRecording,
            isSaved: false,
            scale,
            handlers,
            previewMode);
    }

    private static double ContrastRatio(Color first, Color second)
    {
        double firstLuminance = RelativeLuminance(first);
        double secondLuminance = RelativeLuminance(second);
        double lighter = Math.Max(firstLuminance, secondLuminance);
        double darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
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

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. relativeParts]);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file '{Path.Combine(relativeParts)}'.");
    }
}
