using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using WinShot.App.Overlays;

namespace WinShot.App.QuickAccess;

/// <summary>
/// Builds the production Quick Access card visual shared by the floating card and Settings preview.
/// Window lifetime, actions, placement, and capture drag behavior remain owned by
/// <see cref="QuickAccessWindow"/>.
/// </summary>
internal sealed class QuickAccessCardPresenter
{
    internal const double ShadowMargin = 18;
    internal const double ThumbnailWidth = 264;
    internal const double ThumbnailHeight = 166;
    internal const double ActionBarHeight = 42;
    internal const double CloseChromeSize = 28;
    private readonly TextBlock _feedbackText = new()
    {
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly Border _feedbackChrome;

    internal QuickAccessCardPresenter(
        FrameworkElement resourceOwner,
        ImageSource image,
        string summary,
        bool isRecording,
        bool isSaved,
        double scale,
        IReadOnlyDictionary<TransientOverlayCommand, RoutedEventHandler> handlers,
        bool previewMode = false)
    {
        ArgumentNullException.ThrowIfNull(resourceOwner);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(handlers);

        Thumbnail = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new ImageBrush(image)
            {
                // Fill the preview at every aspect ratio without distorting the screenshot.
                // Only the thumbnail is cropped; editing, saving and copying use the full image.
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
            },
            Cursor = previewMode ? null : System.Windows.Input.Cursors.Hand,
            ToolTip = previewMode ? null : summary,
        };
        SetCaptureScale(scale);
        RenderOptions.SetBitmapScalingMode(Thumbnail, BitmapScalingMode.HighQuality);

        ActionBar = new Border
        {
            Height = ActionBarHeight,
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(0, 0, 10, 10),
            Background = new LinearGradientBrush(
                Color.FromArgb(0x00, 0x00, 0x00, 0x00),
                Color.FromArgb(0xD8, 0x00, 0x00, 0x00),
                90),
            IsHitTestVisible = false,
        };

        SaveGlyph = MakeGlyph(isSaved ? "\uE838" : "\uE74E");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 6),
        };
        KeyboardNavigation.SetTabNavigation(buttons, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(buttons, KeyboardNavigationMode.Cycle);
        AutomationProperties.SetName(buttons, "Quick Access actions");

        buttons.Children.Add(MakeIconButton(resourceOwner, MakeGlyph("\uE8C8"),
            TransientOverlayCommand.Copy, isRecording ? "Copy recording file" : "Copy screenshot",
            "Ctrl+C", handlers, previewMode));
        if (!isRecording)
        {
            buttons.Children.Add(MakeIconButton(resourceOwner, MakeGlyph("\uE70F"),
                TransientOverlayCommand.Annotate, "Edit screenshot", "Ctrl+E", handlers, previewMode));
            buttons.Children.Add(MakeIconButton(resourceOwner, MakeGlyph("\uE718"),
                TransientOverlayCommand.Pin, "Pin to screen", "Ctrl+P", handlers, previewMode));
        }

        buttons.Children.Add(MakeIconButton(resourceOwner, SaveGlyph,
            TransientOverlayCommand.Save,
            isSaved ? "Show in folder" : "Save to captures folder",
            "Ctrl+S", handlers, previewMode));
        buttons.Children.Add(MakeIconButton(resourceOwner, MakeGlyph("\uE8A7"),
            TransientOverlayCommand.Open, isRecording ? "Play recording" : "Open in default app", "Ctrl+O", handlers, previewMode));

        Button closeButton = MakeIconButton(resourceOwner, MakeGlyph("\uE711", 10),
            TransientOverlayCommand.Close, "Dismiss Quick Access", "Esc", handlers, previewMode);
        closeButton.Width = closeButton.Height = 24;
        closeButton.HorizontalAlignment = HorizontalAlignment.Center;
        closeButton.VerticalAlignment = VerticalAlignment.Center;

        HoverLayer = new Grid { Opacity = 0, IsHitTestVisible = false };
        CloseChrome = CreateCloseButtonChrome(closeButton);
        HoverLayer.Children.Add(ActionBar);
        HoverLayer.Children.Add(buttons);
        HoverLayer.Children.Add(CloseChrome);

        var content = new Grid();
        KeyboardNavigation.SetTabNavigation(content, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(content, KeyboardNavigationMode.Cycle);
        content.Children.Add(Thumbnail);
        content.Children.Add(HoverLayer);
        AutomationProperties.SetLiveSetting(_feedbackText, AutomationLiveSetting.Polite);
        _feedbackChrome = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEB, 0x20, 0x22, 0x24)),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(8, 8, 40, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = _feedbackText,
        };
        content.Children.Add(_feedbackChrome);

        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(5),
            Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x20, 0x22, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 5,
                Direction = 270,
                Opacity = 0.55,
            },
            Child = content,
        };

        Root = new Border
        {
            Padding = new Thickness(ShadowMargin),
            IsHitTestVisible = !previewMode,
            Focusable = false,
            Child = card,
        };
    }

    internal Border Root { get; }

    internal Border Thumbnail { get; }

    internal Grid HoverLayer { get; }

    internal Border ActionBar { get; }

    internal Border CloseChrome { get; }

    internal TextBlock SaveGlyph { get; }

    internal List<Button> ActionButtons { get; } = [];

    internal Dictionary<TransientOverlayCommand, Button> CommandButtons { get; } = [];

    internal void SetImage(ImageSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ((ImageBrush)Thumbnail.Background).ImageSource = image;
    }

    internal void SetCaptureScale(double scale)
    {
        double boundedScale = Math.Clamp(scale, 0.7, 1.5);
        Thumbnail.Width = ThumbnailWidth * boundedScale;
        Thumbnail.Height = ThumbnailHeight * boundedScale;
        _feedbackText.MaxWidth = Math.Max(100, Thumbnail.Width - 70);
    }

    internal void ShowFeedback(string? message)
    {
        _feedbackText.Text = message ?? "";
        AutomationProperties.SetName(_feedbackText, _feedbackText.Text);
        _feedbackChrome.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrEmpty(message))
        {
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(_feedbackText)
                ?? System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(_feedbackText);
            peer?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
        }
    }

    internal void SetInteractionChromeVisible(bool visible, bool animate = true)
    {
        HoverLayer.IsHitTestVisible = visible && Root.IsHitTestVisible;
        if (!animate || !OverlayChrome.AnimationsEnabled)
        {
            HoverLayer.BeginAnimation(UIElement.OpacityProperty, null);
            HoverLayer.Opacity = visible ? 1 : 0;
            return;
        }

        HoverLayer.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(140)));
    }

    internal BitmapSource RenderSnapshot()
    {
        Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = Root.DesiredSize;
        Root.Arrange(new Rect(new Point(), size));
        Root.UpdateLayout();

        int width = Math.Max(1, (int)Math.Ceiling(size.Width));
        int height = Math.Max(1, (int)Math.Ceiling(size.Height));
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(Root);
        bitmap.Freeze();
        return bitmap;
    }

    internal static Border CreateCloseButtonChrome(Button closeButton)
    {
        ArgumentNullException.ThrowIfNull(closeButton);
        return new Border
        {
            Width = CloseChromeSize,
            Height = CloseChromeSize,
            CornerRadius = new CornerRadius(CloseChromeSize / 2),
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 5, 0),
            SnapsToDevicePixels = true,
            Child = closeButton,
        };
    }

    private static TextBlock MakeGlyph(string glyph, double size = 13)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = size,
            Foreground = Brushes.White,
        };
        AutomationProperties.SetName(text, string.Empty);
        return text;
    }

    private Button MakeIconButton(
        FrameworkElement resourceOwner,
        TextBlock glyph,
        TransientOverlayCommand command,
        string name,
        string acceleratorKey,
        IReadOnlyDictionary<TransientOverlayCommand, RoutedEventHandler> handlers,
        bool previewMode)
    {
        if (!handlers.TryGetValue(command, out RoutedEventHandler? handler))
            throw new ArgumentException($"Missing Quick Access handler for {command}.", nameof(handlers));

        var button = new Button
        {
            Style = (Style)resourceOwner.FindResource("OverlayIconButton"),
            Content = glyph,
            ToolTip = previewMode ? null : $"{name} ({acceleratorKey})",
            IsTabStop = !previewMode,
            Focusable = !previewMode,
            TabIndex = ActionButtons.Count,
        };
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetHelpText(
            button,
            $"{name}. Press Enter or Space to activate. Shortcut: {acceleratorKey}.");
        AutomationProperties.SetAcceleratorKey(button, acceleratorKey);
        button.Click += handler;
        ActionButtons.Add(button);
        CommandButtons.Add(command, button);
        return button;
    }
}
