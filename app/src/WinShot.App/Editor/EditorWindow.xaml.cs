using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WinShot.Core.Settings;

namespace WinShot.App.Editor;

public partial class EditorWindow : Window
{
    private static readonly (ToolKind Tool, string Glyph, string Name)[] Tools =
    {
        (ToolKind.Select, "", "Select and move (V)"),
        (ToolKind.Arrow, "", "Arrow (A)"),
        (ToolKind.Line, "", "Line (L)"),
        (ToolKind.Rect, "", "Rectangle (R)"),
        (ToolKind.Ellipse, "", "Ellipse (O)"),
        (ToolKind.Freehand, "", "Pen (P)"),
        (ToolKind.Highlighter, "", "Highlighter (H)"),
        (ToolKind.Text, "", "Text (T)"),
        (ToolKind.Step, "", "Number steps (N)"),
        (ToolKind.Blur, "", "Blur (B)"),
        (ToolKind.Pixelate, "", "Pixelate (X)"),
        (ToolKind.Spotlight, "", "Spotlight (S)"),
        (ToolKind.Crop, "", "Crop (C)"),
    };

    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xFF, 0x45, 0x3A), // red
        Color.FromRgb(0xFF, 0x9F, 0x0A), // orange
        Color.FromRgb(0xFF, 0xD6, 0x0A), // yellow
        Color.FromRgb(0x32, 0xD7, 0x4B), // green
        Color.FromRgb(0x4D, 0x8D, 0xFF), // blue
        Color.FromRgb(0xFF, 0xFF, 0xFF), // white
        Color.FromRgb(0x1C, 0x1C, 0x1E), // black
    };

    private static readonly (double Thickness, double FontSize, double Dot)[] Sizes =
    {
        (2.0, 16, 5),
        (3.5, 22, 7),
        (6.0, 30, 10),
    };

    private readonly SettingsManager _settings;
    private readonly EditorDocument _document;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly EditorResultPreparationCache _preparedResult = new();
    private readonly EditorResultPreparationCoordinator _resultPreparation = new();
    private readonly Dictionary<ToolKind, ToggleButton> _toolButtons = new();
    private readonly List<MenuItem> _swatchButtons = new();
    private readonly List<MenuItem> _sizeButtons = new();
    private Button _colorButton = null!;
    private Button _sizeButton = null!;
    private TextBlock _toolLabel = null!;
    private int _colorIndex;
    private int _sizeIndex = 1;
    private Button _undoButton = null!;
    private Button _redoButton = null!;
    private Button _deleteButton = null!;

    private static readonly (string Name, Brush? Fill)[] Backdrops = BuildBackdrops();

    private readonly List<ToggleButton> _backdropButtons = new();
    private readonly List<ToggleButton> _paddingButtons = new();
    private ToggleButton _backdropToggle = null!;
    private CheckBox _shadowCheck = null!;

    private TextBox? _activeTextBox;
    private Point _textImagePoint;
    private string? _lastSavedPath;
    private bool _outputInProgress;
    private Task _activeOutputTask = Task.CompletedTask;
    private string? _lastAnnouncedStatus;
    private int _liveRegionAnnouncementVersion;

    private sealed record PreparedEditorTransfer(
        PreparedEditorResult Result,
        EncodedImagePayload Payload);

    private static (string, Brush?)[] BuildBackdrops()
    {
        static Brush Gradient(string from, string to)
        {
            var brush = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString(from),
                (Color)ColorConverter.ConvertFromString(to),
                new Point(0, 0), new Point(1, 1));
            brush.Freeze();
            return brush;
        }

        return new (string, Brush?)[]
        {
            ("None", null),
            ("Ocean", Gradient("#4E7CFF", "#9B59F6")),
            ("Candy", Gradient("#FF6CAB", "#FF9F43")),
            ("Fresh", Gradient("#34D1BF", "#4D8DFF")),
            ("Sunset", Gradient("#FA709A", "#FEE140")),
            ("Slate", Gradient("#3A3F51", "#181C26")),
            ("Paper", Gradient("#E8EAF0", "#FDFDFE")),
        };
    }

    public EditorWindow(BitmapSource image, SettingsManager settings)
    {
        InitializeComponent();
        WindowTheming.ApplyDarkTitleBar(this);
        _settings = settings;
        _document = new EditorDocument(image);
        Canvas.SetDocument(_document);
        Canvas.TextRequested += BeginTextEdit;
        Canvas.KeyboardAnnouncementRequested += SetEditorStatus;
        Canvas.RegionEffectStatusChanged += SetEditorStatus;
        _document.Changed += OnDocumentChanged;

        BuildToolbar();
        WireBottomBar();
        SelectTool(ToolKind.Select);
        SelectColor(0);
        SelectSize(1);
        UpdateStatus();
        PreviewKeyDown += OnKeyDown;
        Closed += (_, _) =>
        {
            _lifetimeCancellation.Cancel();
            _resultPreparation.Dispose();
            _document.Dispose();
        };
    }

    internal async Task CancelPendingOutputAsync()
    {
        _resultPreparation.CancelCurrent();
        Task activeOutput = _activeOutputTask;
        try
        {
            await activeOutput;
        }
        catch
        {
            // The initiating editor action owns user-facing failure reporting. Session shutdown
            // only needs proof that no output work remains.
        }
    }

    // ---- Toolbar construction ----

    private void BuildToolbar()
    {
        foreach (var (tool, glyph, name) in Tools)
        {
            var button = new ToggleButton
            {
                Style = (Style)FindResource("ToolButton"),
                Content = tool == ToolKind.Select ? MakeSelectionIcon() : MakeGlyph(glyph, 15),
                ToolTip = name,
                Margin = new Thickness(1, 0, 1, 0),
                TabIndex = (int)tool,
            };
            AutomationProperties.SetName(button, AccessibleLabel(name));
            AutomationProperties.SetHelpText(button, $"Select the {AccessibleLabel(name).ToLowerInvariant()} annotation tool. Shortcut: {Shortcut(name)}.");
            button.Click += (_, _) => SelectTool(tool);
            _toolButtons[tool] = button;
            ToolPanel.Children.Add(button);
        }

        _toolLabel = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
        };
        _toolLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        StylePanel.Children.Add(_toolLabel);

        _colorButton = MakeFormattingButton("Annotation color", 20);
        for (int i = 0; i < Palette.Length; i++)
        {
            int index = i;
            string colorName = new[] { "Red", "Orange", "Yellow", "Green", "Blue", "White", "Black" }[i];
            var item = new MenuItem
            {
                Header = colorName,
                IsCheckable = true,
                Icon = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Palette[i]) },
            };
            item.Click += (_, _) => SelectColor(index);
            _swatchButtons.Add(item);
            _colorButton.ContextMenu!.Items.Add(item);
        }
        StylePanel.Children.Add(_colorButton);
        _sizeButton = MakeFormattingButton("Annotation size", 21);
        for (int i = 0; i < Sizes.Length; i++)
        {
            int index = i;
            var item = new MenuItem { IsCheckable = true };
            item.Click += (_, _) => SelectSize(index);
            _sizeButtons.Add(item);
            _sizeButton.ContextMenu!.Items.Add(item);
        }
        StylePanel.Children.Add(_sizeButton);
        _backdropToggle = new ToggleButton
        {
            Style = (Style)FindResource("ToolButton"),
            Content = MakeGlyph("", 15),
            ToolTip = "Background — margin, gradient and shadow around the export",
            Margin = new Thickness(1, 0, 1, 0),
            TabIndex = 40,
        };
        AutomationProperties.SetName(_backdropToggle, "Background options");
        AutomationProperties.SetHelpText(_backdropToggle, "Show or hide export background, margin, and shadow options.");
        _backdropToggle.Click += (_, _) =>
            BackdropPanel.Visibility = _backdropToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UtilityPanel.Children.Add(_backdropToggle);

        _undoButton = MakeActionButton("", "Undo (Ctrl+Z)", 50, PerformUndo);
        _redoButton = MakeActionButton("", "Redo (Ctrl+Y)", 51, PerformRedo);
        _deleteButton = MakeActionButton("", "Delete selection (Del)", 52, DeleteSelection);
        UtilityPanel.Children.Add(_undoButton);
        UtilityPanel.Children.Add(_redoButton);
        UtilityPanel.Children.Add(_deleteButton);
        var saveAsButton = new Button
        {
            Style = (Style)FindResource("TextCommand"),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(6, 0, 0, 0),
            Content = "Save as…",
            ToolTip = "Choose where to save the result",
            TabIndex = 102,
        };
        AutomationProperties.SetName(saveAsButton, "Save as");
        AutomationProperties.SetHelpText(saveAsButton, "Choose a destination and image format for the edited image.");
        saveAsButton.Click += (_, _) => SaveAsResult();
        ActionPanel.Children.Add(saveAsButton);

        var doneButton = new Button
        {
            Style = (Style)FindResource("AccentButton"),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(6, 0, 0, 0),
            Content = "Save & copy",
            ToolTip = "Copy + save to your captures folder, then close",
            TabIndex = 103,
        };
        AutomationProperties.SetName(doneButton, "Save and copy");
        AutomationProperties.SetHelpText(doneButton, "Copy and save the edited image to the captures folder, then close the editor.");
        doneButton.Click += (_, _) => DoneResult();
        ActionPanel.Children.Add(doneButton);

        BuildBackdropPanel(); // last: its initial selection fires Changed, which updates the buttons above
    }

    private void BuildBackdropPanel()
    {
        BackdropPanel.Children.Add(new TextBlock
        {
            Text = "Background",
            FontSize = 12,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 10, 0),
        });

        for (int i = 0; i < Backdrops.Length; i++)
        {
            int index = i;
            var (name, fill) = Backdrops[index];
            var button = new ToggleButton
            {
                Style = (Style)FindResource("ToolButton"),
                Width = 26,
                Height = 26,
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                ToolTip = name,
                TabIndex = 70 + index,
                Content = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(2),
                    BorderBrush = Brushes.Transparent,
                    Background = fill ?? (Brush)FindResource("InputBrush"),
                    Child = fill is null
                        ? new TextBlock
                        {
                            Text = "",
                            FontFamily = (FontFamily)FindResource("IconFont"),
                            FontSize = 9,
                            Foreground = (Brush)FindResource("TextSecondaryBrush"),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        }
                        : null,
                },
            };
            AutomationProperties.SetName(button, $"{name} background");
            AutomationProperties.SetHelpText(button, name == "None"
                ? "Export without a background."
                : $"Use the {name} background for the exported image.");
            button.Click += (_, _) => SelectBackdrop(index);
            _backdropButtons.Add(button);
            BackdropPanel.Children.Add(button);
        }

        BackdropPanel.Children.Add(new Rectangle
        {
            Width = 1,
            Margin = new Thickness(10, 2, 10, 2),
            Fill = (Brush)FindResource("ControlBorderBrush"),
        });

        BackdropPanel.Children.Add(new TextBlock
        {
            Text = "Margin",
            FontSize = 12,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });

        double[] paddings = { 32, 56, 88 };
        string[] labels = { "S", "M", "L" };
        for (int i = 0; i < paddings.Length; i++)
        {
            int index = i;
            var button = new ToggleButton
            {
                Style = (Style)FindResource("ToolButton"),
                Width = 28,
                Height = 26,
                Content = new TextBlock { Text = labels[index], FontSize = 12 },
                Margin = new Thickness(1, 0, 1, 0),
                IsChecked = index == 1,
                TabIndex = 80 + index,
            };
            string paddingName = new[] { "Small", "Medium", "Large" }[index];
            AutomationProperties.SetName(button, $"{paddingName} background margin");
            AutomationProperties.SetHelpText(button, $"Use a {paddingName.ToLowerInvariant()} margin around the image.");
            button.Click += (_, _) => SelectPadding(index, paddings[index]);
            _paddingButtons.Add(button);
            BackdropPanel.Children.Add(button);
        }

        _shadowCheck = new CheckBox
        {
            Content = "Shadow",
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            TabIndex = 83,
        };
        AutomationProperties.SetName(_shadowCheck, "Background shadow");
        AutomationProperties.SetHelpText(_shadowCheck, "Add a shadow behind the image when a background is selected.");
        _shadowCheck.Click += (_, _) =>
        {
            bool shadow = _shadowCheck.IsChecked == true;
            if (_document.Backdrop.Shadow == shadow)
                return;
            _document.PushUndo();
            _document.Backdrop.Shadow = shadow;
            _document.NotifyChanged();
        };
        BackdropPanel.Children.Add(_shadowCheck);

        SelectBackdrop(0);
    }

    private void SelectBackdrop(int index)
    {
        Brush? fill = Backdrops[index].Fill;
        if (!ReferenceEquals(_document.Backdrop.Fill, fill))
        {
            _document.PushUndo();
            _document.Backdrop.Fill = fill;
            _document.NotifyChanged();
        }
        for (int i = 0; i < _backdropButtons.Count; i++)
        {
            _backdropButtons[i].IsChecked = i == index;
            if (_backdropButtons[i].Content is Border ring)
                ring.BorderBrush = i == index ? Brushes.White : Brushes.Transparent;
        }
    }

    private void SelectPadding(int index, double padding)
    {
        if (Math.Abs(_document.Backdrop.Padding - padding) > 0.001)
        {
            _document.PushUndo();
            _document.Backdrop.Padding = padding;
            _document.NotifyChanged();
        }
        for (int i = 0; i < _paddingButtons.Count; i++)
            _paddingButtons[i].IsChecked = i == index;
    }

    private Button MakeActionButton(string glyph, string tooltip, int tabIndex, Action onClick)
    {
        var button = new Button
        {
            Style = (Style)FindResource("ToolbarButton"),
            Content = MakeGlyph(glyph, 14),
            ToolTip = tooltip,
            Margin = new Thickness(1, 0, 1, 0),
            TabIndex = tabIndex,
        };
        AutomationProperties.SetName(button, AccessibleLabel(tooltip));
        AutomationProperties.SetHelpText(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static FrameworkElement MakeSelectionIcon()
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 2,1 L 2,13 L 5.5,10 L 8,15 L 10,14 L 7.5,9 L 12,9 Z"),
            Width = 15,
            Height = 16,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.2,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        icon.SetResourceReference(Shape.StrokeProperty, "TextPrimaryBrush");
        return icon;
    }

    private static TextBlock MakeGlyph(string glyph, double size)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = size,
        };
        AutomationProperties.SetName(text, string.Empty);
        return text;
    }

    private static string AccessibleLabel(string text)
    {
        int shortcutStart = text.LastIndexOf(" (", StringComparison.Ordinal);
        return shortcutStart > 0 ? text[..shortcutStart] : text;
    }

    private static string Shortcut(string text)
    {
        int shortcutStart = text.LastIndexOf(" (", StringComparison.Ordinal);
        return shortcutStart > 0 && text.EndsWith(')')
            ? text[(shortcutStart + 2)..^1]
            : string.Empty;
    }

    // ---- Toolbar state ----

    private void SelectTool(ToolKind tool)
    {
        CommitTextEdit();
        Canvas.ActiveTool = tool;
        UpdateFormatting();
        foreach (var (kind, button) in _toolButtons)
            button.IsChecked = kind == tool;
        if (tool != ToolKind.Select && _document.Selected is not null)
        {
            _document.Selected = null;
            _document.NotifyChanged();
        }
        UpdateStatus();
    }

    private Button MakeFormattingButton(string name, int tabIndex)
    {
        var button = new Button
        {
            Style = (Style)FindResource("TextCommand"),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 8, 0),
            TabIndex = tabIndex,
            ContextMenu = new ContextMenu
            {
                Style = (Style)FindResource("FormattingMenu"),
                Background = (Brush)FindResource("CardBrush"),
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                FontFamily = FontFamily,
                FontSize = 13,
            },
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) =>
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        };
        return button;
    }

    private void UpdateFormatting()
    {
        ToolKind tool = Canvas.ActiveTool;
        // Only expose options that affect this tool. Region effects and selection do not
        // consume the drawing color or stroke width.
        bool hasColor = tool is ToolKind.Arrow or ToolKind.Line or ToolKind.Rect or ToolKind.Ellipse
            or ToolKind.Freehand or ToolKind.Highlighter or ToolKind.Text or ToolKind.Step;
        bool hasSize = hasColor && tool != ToolKind.Step;
        StylePanel.Visibility = hasColor ? Visibility.Visible : Visibility.Collapsed;
        _sizeButton.Visibility = hasSize ? Visibility.Visible : Visibility.Collapsed;
        _toolLabel.Text = AccessibleLabel(_toolButtons[tool].ToolTip?.ToString() ?? tool.ToString());
        for (int i = 0; i < Sizes.Length; i++)
        {
            double value = tool == ToolKind.Text ? Sizes[i].FontSize
                : tool == ToolKind.Highlighter ? Sizes[i].Thickness * 4.5 : Sizes[i].Thickness;
            _sizeButtons[i].Header = $"{value:0.##} px";
        }
        _sizeButton.Content = $"{_sizeButtons[_sizeIndex].Header}  ⌄";
        AutomationProperties.SetName(_sizeButton, $"{(tool == ToolKind.Text ? "Text size" : "Stroke width")}, {_sizeButtons[_sizeIndex].Header}");
    }

    private void SelectColor(int index)
    {
        _colorIndex = index;
        Canvas.CurrentColor = Palette[index];
        for (int i = 0; i < _swatchButtons.Count; i++)
            _swatchButtons[i].IsChecked = i == index;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Ellipse
        {
            Width = 9, Height = 9, Fill = new SolidColorBrush(Palette[index]),
            Stroke = (Brush)FindResource("TextSecondaryBrush"), StrokeThickness = 0.5,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0),
        });
        content.Children.Add(new TextBlock { Text = "Color  ⌄" });
        _colorButton.Content = content;
        AutomationProperties.SetName(_colorButton, $"Annotation color, {_swatchButtons[index].Header}");
        if (_activeTextBox is not null)
            _activeTextBox.Foreground = new SolidColorBrush(Palette[index]);
    }

    private void SelectSize(int index)
    {
        _sizeIndex = index;
        Canvas.CurrentThickness = Sizes[index].Thickness;
        Canvas.CurrentFontSize = Sizes[index].FontSize;
        for (int i = 0; i < _sizeButtons.Count; i++)
            _sizeButtons[i].IsChecked = i == index;
        UpdateFormatting();
    }
    private void OnDocumentChanged()
    {
        _resultPreparation.CancelCurrent();
        _preparedResult.Invalidate();
        if (_undoButton is null || _redoButton is null)
            return; // toolbar still under construction
        _undoButton.IsEnabled = _document.CanUndo;
        _redoButton.IsEnabled = _document.CanRedo;
        _deleteButton.IsEnabled = _document.Selected is not null;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusLeft.Text = $"{_document.Background.PixelWidth} × {_document.Background.PixelHeight} px" +
                          $"  ·  {_document.Annotations.Count} annotation{(_document.Annotations.Count == 1 ? "" : "s")}";
        SetEditorStatus(_lastSavedPath is null
            ? string.Empty
            : "Saved " + System.IO.Path.GetFileName(_lastSavedPath));
        AutomationProperties.SetItemStatus(
            Canvas,
            $"{AccessibleLabel(_toolButtons[Canvas.ActiveTool].ToolTip?.ToString() ?? Canvas.ActiveTool.ToString())} tool selected; {_document.Annotations.Count} annotations.");
    }

    private void SetEditorStatus(string status)
    {
        if (string.Equals(StatusRight.Text, status, StringComparison.Ordinal))
            return;

        StatusRight.Text = status;
        if (!IsLoaded ||
            string.Equals(status, _lastAnnouncedStatus, StringComparison.Ordinal) ||
            Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _lastAnnouncedStatus = status;
        int version = ++_liveRegionAnnouncementVersion;
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                if (version != _liveRegionAnnouncementVersion ||
                    !StatusRight.IsVisible)
                {
                    return;
                }

                AutomationPeer? peer =
                    UIElementAutomationPeer.FromElement(StatusRight)
                    ?? UIElementAutomationPeer.CreatePeerForElement(StatusRight);
                peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            },
            System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---- Text editing ----

    private void BeginTextEdit(Point imagePoint)
    {
        CommitTextEdit();
        _textImagePoint = imagePoint;

        var box = new TextBox
        {
            MinWidth = 60,
            FontSize = Math.Max(11, Canvas.CurrentFontSize * Canvas.ViewScale),
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Canvas.CurrentColor),
            AcceptsReturn = true,
        };
        AutomationProperties.SetName(box, "Annotation text");
        AutomationProperties.SetHelpText(box, "Type annotation text. Enter accepts, Shift+Enter adds a line, and Escape cancels.");
        var viewPoint = Canvas.ImageToView(imagePoint);
        System.Windows.Controls.Canvas.SetLeft(box, viewPoint.X);
        System.Windows.Controls.Canvas.SetTop(box, viewPoint.Y);

        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                CommitTextEdit();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelTextEdit();
            }
        };
        box.LostKeyboardFocus += (_, _) => CommitTextEdit();

        _activeTextBox = box;
        TextEditLayer.IsHitTestVisible = true;
        TextEditLayer.Children.Add(box);
        box.Focus();
    }

    private void CommitTextEdit()
    {
        if (_activeTextBox is null)
            return;
        string text = _activeTextBox.Text;
        RemoveTextBox();
        if (!string.IsNullOrWhiteSpace(text))
        {
            _document.Add(new TextAnnotation
            {
                Position = _textImagePoint,
                Text = text.TrimEnd(),
                Color = Canvas.CurrentColor,
                FontSize = Canvas.CurrentFontSize,
            });
        }
    }

    private void CancelTextEdit() => RemoveTextBox();

    private void RemoveTextBox()
    {
        if (_activeTextBox is null)
            return;
        var box = _activeTextBox;
        _activeTextBox = null; // clear first: removal fires LostKeyboardFocus re-entrantly
        TextEditLayer.Children.Remove(box);
        TextEditLayer.IsHitTestVisible = false;
    }

    // ---- Bottom bar ----

    private void WireBottomBar()
    {
        BottomCopy.Content = MakeGlyph("", 14);
        BottomPin.Content = MakeGlyph("", 14);
        BottomCopy.Click += (_, _) => CopyResult();
        BottomPin.Click += (_, _) => PinResult();
        DragPill.MouseLeftButtonDown += OnDragPill;
        Canvas.SizeChanged += (_, _) => UpdateZoom();
        Loaded += (_, _) => UpdateZoom();
        _document.Changed += UpdateZoom;
        Canvas.ViewportChanged += UpdateZoom;
    }

    private void UpdateZoom()
    {
        if (ZoomText is not null)
            ZoomText.Text = $"{Canvas.ViewScale * 100:0}%";
    }

    private void PerformUndo()
    {
        CommitTextEdit();
        Canvas.CancelDraft();
        _document.Undo();
    }

    private void PerformRedo()
    {
        CommitTextEdit();
        Canvas.CancelDraft();
        _document.Redo();
    }

    private async void PinResult()
    {
        try
        {
            CommitTextEdit();
            var image = await TryRenderExactOutputAsync();
            if (image is null)
                return;
            var monitor = WinShot.Capture.MonitorService.GetMonitors().First(m => m.IsPrimary);
            var rect = new WinShot.Core.PixelRect(
                monitor.Bounds.X + Math.Max(0, (monitor.Bounds.Width - image.PixelWidth) / 2),
                monitor.Bounds.Y + Math.Max(0, (monitor.Bounds.Height - image.PixelHeight) / 2),
                image.PixelWidth, image.PixelHeight);
            new PinWindow(image, rect, _settings).Show();
            SetEditorStatus("Pinned to screen");
        }
        catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowEditorOutputFailure();
        }
    }

    private async void OnDragPill(object sender, MouseEventArgs e)
    {
        try
        {
            e.Handled = true;
            if (_outputInProgress)
            {
                SetEditorStatus("An editor output is already being prepared");
                return;
            }

            CommitTextEdit();
            PreparedEditorResult? prepared = GetReadyPreparedResult();
            if (prepared is null)
            {
                await PrepareDragResultAsync();
                prepared = GetReadyPreparedResult();
                if (prepared is null)
                    return;
            }

            var data = new DataObject();
            data.SetFileDropList(
                new System.Collections.Specialized.StringCollection
                {
                    prepared.FilePath,
                });
            DragDrop.DoDragDrop(DragPill, data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowEditorOutputFailure();
        }
    }

    private async void SaveAsResult()
    {
        if (_outputInProgress)
        {
            SetEditorStatus("An editor output is already being prepared");
            return;
        }

        CommitTextEdit();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
            FileName = "Relic_Screenshot_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"),
            InitialDirectory = _settings.Current.SaveDirectory,
            AddExtension = true,
            CheckPathExists = true,
            OverwritePrompt = false,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        string destinationPath = System.IO.Path.GetFullPath(dialog.FileName);
        bool saveJpeg = dialog.FilterIndex == 2;
        bool replaceExisting = false;
        if (File.Exists(destinationPath))
        {
            MessageBoxResult overwrite = MessageBox.Show(
                this,
                $"A file named “{System.IO.Path.GetFileName(destinationPath)}” already exists. Replace it?",
                "Replace existing image?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (overwrite != MessageBoxResult.Yes)
                return;
            replaceExisting = true;
        }
        try
        {
            await StartExactOutputAsync(
                "Preparing image for Save As...",
                async (image, preparation) =>
                {
                    using ImageSaveService.PreparedImageWrite prepared =
                        saveJpeg
                            ? await ImageSaveService.PrepareExactJpegAsync(
                                image,
                                destinationPath,
                                quality: 90,
                                replaceExisting,
                                preparation.Token)
                            : await PrepareExactPngAsync(
                                image,
                                destinationPath,
                                replaceExisting,
                                preparation);
                    preparation.ThrowIfCannotPublish(_document.Revision);
                    _lastSavedPath = CommitPreparedWrite(preparation, prepared);
                    App.RecordSavedEditorImage(_lastSavedPath, image);
                    UpdateStatus();
                });
        }
        catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            SetEditorStatus("Save failed — check the destination and available disk space");
            MessageBox.Show(this,
                "Stillmark could not save this image. Check the destination and available disk space, then try again.",
                "Stillmark", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void DoneResult()
    {
        CommitTextEdit();
        AppSettings settingsSnapshot = _settings.Current.Clone();
        DateTime timestamp = DateTime.Now;
        string? savedPath = null;
        try
        {
            await StartExactOutputAsync(
                "Preparing final image...",
                async (image, preparation) =>
                {
                    EncodedImagePayload payload =
                        await StillImageEncodingService.EncodePngAsync(
                            image,
                            preparation.Token);
                    preparation.ThrowIfCannotPublish(_document.Revision);
                    using ImageSaveService.PreparedImageWrite prepared =
                        await ImageSaveService.PrepareAsync(
                            payload,
                            settingsSnapshot,
                            timestamp,
                            preparation.Token);
                    preparation.ThrowIfCannotPublish(_document.Revision);

                    long currentRevision = _document.Revision;
                    if (!preparation.TryPublish(
                            currentRevision,
                            () => savedPath = prepared.Commit()))
                    {
                        preparation.ThrowIfCannotPublish(currentRevision);
                        throw new OperationCanceledException(preparation.Token);
                    }

                    _lastSavedPath = savedPath;
                    App.RecordSavedEditorImage(savedPath!, image);
                    ClipboardService.SetImage(payload, savedPath!);
                    Close();
                });
        }
        catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            SetEditorStatus(savedPath is null
                ? "Finish failed — the image was not delivered"
                : $"Saved {System.IO.Path.GetFileName(savedPath)}, but copying failed");
            MessageBox.Show(this,
                savedPath is null
                    ? "Stillmark could not finish this image. Try saving to another destination."
                    : "The image was saved, but Stillmark could not copy it. The editor will stay open so you can try again.",
                "Stillmark", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- Actions ----

    private void DeleteSelection()
    {
        if (_document.Selected is Annotation selected)
            _document.Delete(selected);
    }

    private void CopyResult()
    {
        if (_outputInProgress)
        {
            SetEditorStatus("An editor output is already being prepared");
            return;
        }

        CommitTextEdit();
        _outputInProgress = true;
        long expectedRevision = _document.Revision;
        EditorResultPreparationLease preparation =
            _resultPreparation.Begin(expectedRevision);
        bool restoreCanvasFocus = Canvas.IsKeyboardFocusWithin;
        Canvas.IsEnabled = false;
        Task operation = CopyResultCoreAsync(
            preparation,
            restoreCanvasFocus);
        _activeOutputTask = operation;
    }

    private async Task CopyResultCoreAsync(
        EditorResultPreparationLease preparation,
        bool restoreCanvasFocus)
    {
        try
        {
            PreparedEditorTransfer? transfer =
                await PrepareResultFileCoreAsync(
                    preparation,
                    "Preparing result for copy...");
            if (transfer is null)
                return;

            ClipboardService.SetImage(
                transfer.Payload,
                transfer.Result.FilePath);
            SetEditorStatus("Copied · ready to paste");
        }
        catch (EditorDocumentChangedDuringOutputException)
        {
            SetEditorStatus("The image changed — try the action again");
        }
        catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            SetEditorStatus("The image changed — try the action again");
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowEditorOutputFailure();
        }
        finally
        {
            preparation.Dispose();
            _outputInProgress = false;
            if (IsVisible && !Dispatcher.HasShutdownStarted)
            {
                Canvas.IsEnabled = true;
                if (restoreCanvasFocus && IsActive)
                    Canvas.Focus();
            }
        }
    }

    private async void SaveResult()
    {
        CommitTextEdit();
        AppSettings settingsSnapshot = _settings.Current.Clone();
        DateTime timestamp = DateTime.Now;
        try
        {
            await StartExactOutputAsync(
                "Preparing image to save...",
                async (image, preparation) =>
                {
                    EncodedImagePayload payload =
                        await StillImageEncodingService.EncodePngAsync(
                            image,
                            preparation.Token);
                    preparation.ThrowIfCannotPublish(_document.Revision);
                    using ImageSaveService.PreparedImageWrite prepared =
                        await ImageSaveService.PrepareAsync(
                            payload,
                            settingsSnapshot,
                            timestamp,
                            preparation.Token);
                    preparation.ThrowIfCannotPublish(_document.Revision);
                    _lastSavedPath = CommitPreparedWrite(preparation, prepared);
                    App.RecordSavedEditorImage(_lastSavedPath, image);
                    UpdateStatus();
                });
        }
        catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            SetEditorStatus("Save failed — check the destination and available disk space");
            MessageBox.Show(this,
                "Stillmark could not save this image. Check the destination and available disk space, then try again.",
                "Stillmark", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private Task<bool> StartExactOutputAsync(
        string preparingStatus,
        Func<BitmapSource, EditorResultPreparationLease, Task> deliverAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preparingStatus);
        ArgumentNullException.ThrowIfNull(deliverAsync);
        if (_outputInProgress)
        {
            SetEditorStatus("An editor output is already being prepared");
            return Task.FromResult(false);
        }

        _outputInProgress = true;
        long expectedRevision = _document.Revision;
        EditorResultPreparationLease preparation =
            _resultPreparation.Begin(expectedRevision);
        bool restoreCanvasFocus = Canvas.IsKeyboardFocusWithin;
        Canvas.IsEnabled = false;
        Task<bool> operation = RunExactOutputCoreAsync(
            preparingStatus,
            deliverAsync,
            preparation,
            restoreCanvasFocus);
        _activeOutputTask = operation;
        return operation;
    }

    private async Task<bool> RunExactOutputCoreAsync(
        string preparingStatus,
        Func<BitmapSource, EditorResultPreparationLease, Task> deliverAsync,
        EditorResultPreparationLease preparation,
        bool restoreCanvasFocus)
    {
        try
        {
            SetEditorStatus(preparingStatus);
            await System.Windows.Threading.Dispatcher.Yield(
                System.Windows.Threading.DispatcherPriority.Background);
            preparation.ThrowIfCannotPublish(_document.Revision);
            BitmapSource image = await _document.RenderExportAsync(
                preparation.ExpectedRevision,
                preparation.Token);
            preparation.ThrowIfCannotPublish(_document.Revision);
            await deliverAsync(image, preparation);
            return true;
        }
        catch (EditorDocumentChangedDuringOutputException)
        {
            SetEditorStatus("The image changed — try the action again");
            return false;
        }
        catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            SetEditorStatus("The image changed or the session interrupted output — try again");
            return false;
        }
        finally
        {
            preparation.Dispose();
            _outputInProgress = false;
            if (IsVisible && !Dispatcher.HasShutdownStarted)
            {
                Canvas.IsEnabled = true;
                if (restoreCanvasFocus && IsActive)
                    Canvas.Focus();
            }
        }
    }

    private static async Task<ImageSaveService.PreparedImageWrite> PrepareExactPngAsync(
        BitmapSource image,
        string destinationPath,
        bool replaceExisting,
        EditorResultPreparationLease preparation)
    {
        EncodedImagePayload payload =
            await StillImageEncodingService.EncodePngAsync(
                image,
                preparation.Token);
        return await ImageSaveService.PrepareExactPngAsync(
            payload,
            destinationPath,
            replaceExisting,
            preparation.Token);
    }

    private string CommitPreparedWrite(
        EditorResultPreparationLease preparation,
        ImageSaveService.PreparedImageWrite prepared)
    {
        string? committedPath = null;
        long currentRevision = _document.Revision;
        if (!preparation.TryPublish(
                currentRevision,
                () => committedPath = prepared.Commit()))
        {
            preparation.ThrowIfCannotPublish(currentRevision);
            throw new OperationCanceledException(preparation.Token);
        }

        return committedPath!;
    }

    private Task<BitmapSource?> TryRenderExactOutputAsync()
    {
        if (_outputInProgress)
        {
            SetEditorStatus("An editor output is already being prepared");
            return Task.FromResult<BitmapSource?>(null);
        }

        _outputInProgress = true;
        long expectedRevision = _document.Revision;
        EditorResultPreparationLease preparation =
            _resultPreparation.Begin(expectedRevision);
        bool restoreCanvasFocus = Canvas.IsKeyboardFocusWithin;
        Canvas.IsEnabled = false;
        Task<BitmapSource?> operation = RenderExactOutputCoreAsync(
            preparation,
            restoreCanvasFocus);
        _activeOutputTask = operation;
        return operation;
    }

    private async Task<BitmapSource?> RenderExactOutputCoreAsync(
        EditorResultPreparationLease preparation,
        bool restoreCanvasFocus)
    {
        try
        {
            SetEditorStatus("Preparing region effects...");
            BitmapSource image = await _document.RenderExportAsync(
                preparation.ExpectedRevision,
                preparation.Token);
            preparation.ThrowIfCannotPublish(_document.Revision);
            return image;
        }
        catch (EditorDocumentChangedDuringOutputException)
        {
            SetEditorStatus("The image changed — try the action again");
            return null;
        }
        catch (OperationCanceledException)
            when (!_lifetimeCancellation.IsCancellationRequested)
        {
            SetEditorStatus("The image changed or the session interrupted output — try again");
            return null;
        }
        finally
        {
            preparation.Dispose();
            _outputInProgress = false;
            if (IsVisible && !Dispatcher.HasShutdownStarted)
            {
                Canvas.IsEnabled = true;
                if (restoreCanvasFocus && IsActive)
                    Canvas.Focus();
            }
        }
    }

    private async Task PrepareDragResultAsync()
    {
        try
        {
            PreparedEditorResult? prepared = GetReadyPreparedResult();
            if (prepared is null)
            {
                PreparedEditorTransfer? transfer =
                    await TryPrepareResultFileAsync("Preparing result for drag...");
                if (transfer is not null)
                    prepared = GetReadyPreparedResult();
            }
            if (prepared is not null)
                SetEditorStatus("Drag is ready");
        }
        catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowEditorOutputFailure();
        }
    }

    private PreparedEditorResult? GetReadyPreparedResult()
    {
        if (!_preparedResult.TryGet(_document.Revision, out PreparedEditorResult? prepared) ||
            prepared is null)
        {
            return null;
        }

        if (File.Exists(prepared.FilePath))
            return prepared;

        _preparedResult.Invalidate();
        return null;
    }

    private Task<PreparedEditorTransfer?> TryPrepareResultFileAsync(
        string preparingStatus)
    {
        if (_outputInProgress)
        {
            SetEditorStatus("An editor output is already being prepared");
            return Task.FromResult<PreparedEditorTransfer?>(null);
        }

        _outputInProgress = true;
        long expectedRevision = _document.Revision;
        EditorResultPreparationLease preparation =
            _resultPreparation.Begin(expectedRevision);
        bool restoreCanvasFocus = Canvas.IsKeyboardFocusWithin;
        Canvas.IsEnabled = false;
        Task<PreparedEditorTransfer?> operation =
            PrepareResultFileWithLifecycleAsync(
                preparation,
                preparingStatus,
                restoreCanvasFocus);
        _activeOutputTask = operation;
        return operation;
    }

    private async Task<PreparedEditorTransfer?> PrepareResultFileWithLifecycleAsync(
        EditorResultPreparationLease preparation,
        string preparingStatus,
        bool restoreCanvasFocus)
    {
        try
        {
            return await PrepareResultFileCoreAsync(
                preparation,
                preparingStatus);
        }
        catch (EditorDocumentChangedDuringOutputException)
        {
            SetEditorStatus("The image changed — try the action again");
            return null;
        }
        catch (OperationCanceledException)
            when (!_lifetimeCancellation.IsCancellationRequested)
        {
            SetEditorStatus("The image changed — try the action again");
            return null;
        }
        finally
        {
            preparation.Dispose();
            _outputInProgress = false;
            if (IsVisible && !Dispatcher.HasShutdownStarted)
            {
                Canvas.IsEnabled = true;
                if (restoreCanvasFocus && IsActive)
                    Canvas.Focus();
            }
        }
    }

    private async Task<PreparedEditorTransfer?> PrepareResultFileCoreAsync(
        EditorResultPreparationLease preparation,
        string preparingStatus)
    {
        SetEditorStatus(preparingStatus);
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.Background);
        preparation.ThrowIfCannotPublish(_document.Revision);

        BitmapSource image = await _document.RenderExportAsync(
            preparation.ExpectedRevision,
            preparation.Token);
        preparation.ThrowIfCannotPublish(_document.Revision);
        EncodedImagePayload payload =
            await StillImageEncodingService.EncodePngAsync(
                image,
                preparation.Token);
        preparation.ThrowIfCannotPublish(_document.Revision);
        await Task.Run(
            () => OwnedTransientFileRetention.PurgeClipboard(DateTime.UtcNow),
            preparation.Token);
        preparation.ThrowIfCannotPublish(_document.Revision);
        using ImageSaveService.PreparedImageWrite staged =
            await ImageSaveService.PreparePngCopyAsync(
                payload,
                OwnedTransientFileRetention.ClipboardDirectory,
                $"Relic_Screenshot_edit_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}",
                preparation.Token);
        preparation.ThrowIfCannotPublish(_document.Revision);

        PreparedEditorResult? prepared = null;
        string? path = null;
        long currentRevision = _document.Revision;
        if (!preparation.TryPublish(
                currentRevision,
                () =>
                {
                    path = staged.Commit();
                    try
                    {
                        if (!_preparedResult.TryCommit(
                                preparation.ExpectedRevision,
                                currentRevision,
                                path,
                                out prepared))
                        {
                            throw new EditorDocumentChangedDuringOutputException();
                        }
                    }
                    catch
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch (IOException)
                        {
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }
                        throw;
                    }
                }))
        {
            preparation.ThrowIfCannotPublish(currentRevision);
            throw new OperationCanceledException(preparation.Token);
        }

        return new PreparedEditorTransfer(prepared!, payload);
    }

    private void ShowEditorOutputFailure()
    {
        if (!IsVisible)
            return;

        MessageBox.Show(
            this,
            "Stillmark could not prepare or deliver this editor output. Try again, reduce a large blur area, or save to another destination.",
            "Stillmark",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    // ---- Keyboard ----

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_activeTextBox is not null)
            return; // typing text — leave shortcuts alone (its own handler covers Enter/Esc)

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Z when ctrl:
                PerformUndo();
                e.Handled = true;
                break;
            case Key.Y when ctrl:
                PerformRedo();
                e.Handled = true;
                break;
            case Key.C when ctrl:
                CopyResult();
                e.Handled = true;
                break;
            case Key.S when ctrl:
                SaveResult();
                e.Handled = true;
                break;
            case Key.Delete or Key.Back:
                DeleteSelection();
                e.Handled = true;
                break;
            case Key.Escape:
                if (Canvas.IsKeyboardFocused)
                    return;
                Canvas.CancelDraft();
                if (_document.Selected is not null)
                {
                    _document.Selected = null;
                    _document.NotifyChanged();
                }
                e.Handled = true;
                break;
            case Key.V: SelectTool(ToolKind.Select); break;
            case Key.A when !ctrl: SelectTool(ToolKind.Arrow); break;
            case Key.L: SelectTool(ToolKind.Line); break;
            case Key.R: SelectTool(ToolKind.Rect); break;
            case Key.O: SelectTool(ToolKind.Ellipse); break;
            case Key.P: SelectTool(ToolKind.Freehand); break;
            case Key.H: SelectTool(ToolKind.Highlighter); break;
            case Key.T: SelectTool(ToolKind.Text); break;
            case Key.N: SelectTool(ToolKind.Step); break;
            case Key.B: SelectTool(ToolKind.Blur); break;
            case Key.X: SelectTool(ToolKind.Pixelate); break;
            case Key.S when !ctrl: SelectTool(ToolKind.Spotlight); break;
            case Key.C when !ctrl: SelectTool(ToolKind.Crop); break;
        }
    }
}
