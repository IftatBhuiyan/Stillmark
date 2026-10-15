using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinShot.Core.Update;

namespace WinShot.App.Video;

/// <summary>
/// Trim / resize / re-encode window for recordings: preview with playback locked to the
/// trimmed range, drag-handle trim strip, quality + dimensions + audio options, live
/// file-size estimate, and conversion via the OS transcoder.
/// </summary>
public partial class VideoEditorWindow : Window
{
    private const string PlayGlyphText = "";
    private const string PauseGlyphText = "";
    private const double MinTrimFraction = 0.02;

    private readonly string _filePath;
    private readonly DispatcherTimer _clock;
    private readonly VideoOperationCoordinator _operations;

    private TimeSpan _duration = TimeSpan.Zero;
    private int _sourceWidth = 1920;
    private int _sourceHeight = 1080;
    private double _trimStartF;
    private double _trimEndF = 1.0;
    private bool _isPlaying;
    private bool _updatingSizeBoxes;
    private string? _completedOutput;

    // Trim strip visuals
    private Border _track = null!;
    private Border _range = null!;
    private Thumb _startThumb = null!;
    private Thumb _endThumb = null!;
    private Rectangle _playhead = null!;

    public VideoEditorWindow(
        string filePath,
        IUpdateActivityCoordinator updateActivity)
    {
        InitializeComponent();
        _operations = new VideoOperationCoordinator(updateActivity);
        WindowTheming.ApplyDarkTitleBar(this);
        _filePath = filePath;
        Title = "Edit — " + System.IO.Path.GetFileName(filePath);

        PlayGlyph.Text = PlayGlyphText;
        BigPlayGlyph.Text = PlayGlyphText;

        BuildTrimStrip();
        Player.Source = new Uri(filePath);
        Player.Play();   // start loading
        Player.Pause();

        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _clock.Tick += (_, _) => OnClockTick();
        _clock.Start();

        Loaded += (_, _) => PlayButton.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && CanUseWindowPlaybackShortcut())
            {
                TogglePlay();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelOrClose();
                e.Handled = true;
            }
        };
    }

    private static bool CanUseWindowPlaybackShortcut() =>
        Keyboard.FocusedElement is not (ButtonBase or RangeBase or TextBoxBase or Selector or Thumb);

    // ---- Trim strip ----

    private void BuildTrimStrip()
    {
        _track = new Border
        {
            Height = 42,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
        };
        _range = new Border
        {
            Height = 42,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x2E, 0x4D, 0x8D, 0xFF)),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(2),
        };
        _playhead = new Rectangle
        {
            Width = 2,
            Height = 34,
            Fill = Brushes.White,
            RadiusX = 1,
            RadiusY = 1,
        };
        _startThumb = MakeTrimThumb(isStart: true);
        _endThumb = MakeTrimThumb(isStart: false);

        TrimCanvas.Children.Add(_track);
        TrimCanvas.Children.Add(_range);
        TrimCanvas.Children.Add(_playhead);
        TrimCanvas.Children.Add(_startThumb);
        TrimCanvas.Children.Add(_endThumb);
        Canvas.SetTop(_playhead, 4);
    }

    private Thumb MakeTrimThumb(bool isStart)
    {
        var thumb = new Thumb
        {
            Width = 12,
            Height = 42,
            Cursor = Cursors.SizeWE,
            Focusable = true,
            IsTabStop = true,
            TabIndex = isStart ? 1 : 2,
            Template = BuildThumbTemplate(),
        };
        AutomationProperties.SetName(thumb, isStart ? "Trim start" : "Trim end");
        AutomationProperties.SetHelpText(
            thumb,
            "Use the arrow keys to adjust by one second, Shift plus an arrow for five seconds, and Home or End to move to a boundary.");
        AutomationProperties.SetLiveSetting(thumb, AutomationLiveSetting.Polite);
        thumb.DragDelta += (_, e) =>
        {
            double width = Math.Max(1, TrimCanvas.ActualWidth);
            double delta = e.HorizontalChange / width;
            if (isStart)
                _trimStartF = Math.Clamp(_trimStartF + delta, 0, _trimEndF - MinTrimFraction);
            else
                _trimEndF = Math.Clamp(_trimEndF + delta, _trimStartF + MinTrimFraction, 1);
            LayoutTrim();
            SeekToFraction(isStart ? _trimStartF : _trimEndF);
            UpdateEstimate();
        };
        thumb.PreviewKeyDown += (_, e) => OnTrimThumbKeyDown(isStart, e);
        return thumb;
    }

    private void OnTrimThumbKeyDown(bool isStart, KeyEventArgs e)
    {
        double seconds = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1;
        double step = _duration > TimeSpan.Zero
            ? Math.Min(1, seconds / _duration.TotalSeconds)
            : (seconds > 1 ? 0.05 : 0.01);
        double current = isStart ? _trimStartF : _trimEndF;
        double target = e.Key switch
        {
            Key.Left or Key.Down => current - step,
            Key.Right or Key.Up => current + step,
            Key.Home => isStart ? 0 : _trimStartF + MinTrimFraction,
            Key.End => isStart ? _trimEndF - MinTrimFraction : 1,
            _ => double.NaN,
        };
        if (double.IsNaN(target))
            return;

        if (isStart)
            _trimStartF = Math.Clamp(target, 0, _trimEndF - MinTrimFraction);
        else
            _trimEndF = Math.Clamp(target, _trimStartF + MinTrimFraction, 1);

        LayoutTrim();
        SeekToFraction(isStart ? _trimStartF : _trimEndF);
        UpdateEstimate();
        e.Handled = true;
    }

    private static ControlTemplate BuildThumbTemplate()
    {
        var template = new ControlTemplate(typeof(Thumb))
        {
            VisualTree = BuildThumbVisual(),
        };
        var focusTrigger = new Trigger
        {
            Property = UIElement.IsKeyboardFocusedProperty,
            Value = true,
        };
        focusTrigger.Setters.Add(
            new Setter(
                Border.BorderBrushProperty,
                SystemColors.HighlightBrush,
                "TrimThumbBorder"));
        focusTrigger.Setters.Add(
            new Setter(Border.BorderThicknessProperty, new Thickness(2), "TrimThumbBorder"));
        template.Triggers.Add(focusTrigger);
        return template;
    }

    private static FrameworkElementFactory BuildThumbVisual()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "TrimThumbBorder";
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x4D, 0x8D, 0xFF)));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00)));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));

        var grip = new FrameworkElementFactory(typeof(Rectangle));
        grip.SetValue(Rectangle.WidthProperty, 2.0);
        grip.SetValue(Rectangle.HeightProperty, 14.0);
        grip.SetValue(Rectangle.FillProperty, new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)));
        grip.SetValue(Rectangle.RadiusXProperty, 1.0);
        grip.SetValue(Rectangle.RadiusYProperty, 1.0);
        grip.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        grip.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(grip);
        return border;
    }

    private void OnTrimCanvasSized(object sender, SizeChangedEventArgs e) => LayoutTrim();

    private void LayoutTrim()
    {
        double width = TrimCanvas.ActualWidth;
        if (width <= 0)
            return;
        _track.Width = width;
        Canvas.SetLeft(_track, 0);
        Canvas.SetTop(_track, 0);

        double startX = _trimStartF * width;
        double endX = _trimEndF * width;
        _range.Width = Math.Max(4, endX - startX);
        Canvas.SetLeft(_range, startX);
        Canvas.SetTop(_range, 0);

        Canvas.SetLeft(_startThumb, startX - _startThumb.Width / 2);
        Canvas.SetLeft(_endThumb, endX - _endThumb.Width / 2);
        Canvas.SetTop(_startThumb, 0);
        Canvas.SetTop(_endThumb, 0);

        UpdateTrimText();
        UpdateTrimAccessibility();
        PositionPlayhead();
    }

    private void PositionPlayhead()
    {
        if (_duration <= TimeSpan.Zero)
            return;
        double fraction = Math.Clamp(Player.Position.TotalSeconds / _duration.TotalSeconds, 0, 1);
        Canvas.SetLeft(_playhead, fraction * TrimCanvas.ActualWidth - 1);
    }

    private void UpdateTrimText()
    {
        if (_duration <= TimeSpan.Zero)
        {
            TrimRangeText.Text = "";
            return;
        }
        TimeSpan start = TimeSpan.FromSeconds(_trimStartF * _duration.TotalSeconds);
        TimeSpan end = TimeSpan.FromSeconds(_trimEndF * _duration.TotalSeconds);
        TrimRangeText.Text = $"{Format(start)} – {Format(end)}  ({Format(end - start)})";
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss\.f");

    private void UpdateTrimAccessibility()
    {
        if (_duration <= TimeSpan.Zero)
        {
            AutomationProperties.SetItemStatus(_startThumb, "Start time unavailable until the video is loaded.");
            AutomationProperties.SetItemStatus(_endThumb, "End time unavailable until the video is loaded.");
            return;
        }

        TimeSpan start = TimeSpan.FromSeconds(_trimStartF * _duration.TotalSeconds);
        TimeSpan end = TimeSpan.FromSeconds(_trimEndF * _duration.TotalSeconds);
        AutomationProperties.SetItemStatus(_startThumb, $"Start time {Format(start)}.");
        AutomationProperties.SetItemStatus(_endThumb, $"End time {Format(end)}.");
    }

    private void OnResetTrim(object sender, RoutedEventArgs e)
    {
        _trimStartF = 0;
        _trimEndF = 1;
        LayoutTrim();
        UpdateEstimate();
    }

    // ---- Playback ----

    private void SeekToFraction(double fraction)
    {
        if (_duration > TimeSpan.Zero)
            Player.Position = TimeSpan.FromSeconds(fraction * _duration.TotalSeconds);
        PositionPlayhead();
    }

    private void OnClockTick()
    {
        if (!_isPlaying || _duration <= TimeSpan.Zero)
            return;
        PositionPlayhead();
        if (Player.Position.TotalSeconds >= _trimEndF * _duration.TotalSeconds)
        {
            Pause();
            SeekToFraction(_trimStartF);
        }
    }

    private void TogglePlay()
    {
        if (_isPlaying)
        {
            Pause();
            return;
        }
        if (_duration > TimeSpan.Zero &&
            (Player.Position.TotalSeconds >= _trimEndF * _duration.TotalSeconds - 0.05 ||
             Player.Position.TotalSeconds < _trimStartF * _duration.TotalSeconds))
        {
            SeekToFraction(_trimStartF);
        }
        Player.Play();
        _isPlaying = true;
        PlayGlyph.Text = PauseGlyphText;
        BigPlayOverlay.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(PlayButton, "Pause preview");
    }

    private void Pause()
    {
        Player.Pause();
        _isPlaying = false;
        PlayGlyph.Text = PlayGlyphText;
        BigPlayOverlay.Visibility = Visibility.Visible;
        AutomationProperties.SetName(PlayButton, "Play preview");
    }

    private void OnPlayPause(object sender, RoutedEventArgs e) => TogglePlay();
    private void OnPreviewClick(object sender, MouseButtonEventArgs e) => TogglePlay();
    private void OnMediaEnded(object sender, RoutedEventArgs e) => Pause();

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan)
            _duration = Player.NaturalDuration.TimeSpan;
        _sourceWidth = Math.Max(2, Player.NaturalVideoWidth);
        _sourceHeight = Math.Max(2, Player.NaturalVideoHeight);
        Pause();

        DimensionsBox.Items.Clear();
        DimensionsBox.Items.Add($"{_sourceWidth} × {_sourceHeight}  (Original)");
        DimensionsBox.Items.Add("75%");
        DimensionsBox.Items.Add("50%");
        DimensionsBox.Items.Add("25%");
        DimensionsBox.Items.Add("Custom");
        DimensionsBox.SelectedIndex = 0;

        LayoutTrim();
        UpdateEstimate();
    }

    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        App.LogError(e.ErrorException);
        MessageBox.Show(this,
            "Stillmark could not open this recording. The file may be incomplete, unsupported, " +
            "or unavailable. Check diagnostics for technical details.",
            "Stillmark", MessageBoxButton.OK, MessageBoxImage.Warning);
        Close();
    }

    // ---- Settings ----

    private void OnDimensionPreset(object sender, SelectionChangedEventArgs e)
    {
        double factor = DimensionsBox.SelectedIndex switch
        {
            1 => 0.75,
            2 => 0.5,
            3 => 0.25,
            _ => 1.0,
        };
        if (DimensionsBox.SelectedIndex is >= 0 and <= 3)
        {
            _updatingSizeBoxes = true;
            WidthBox.Text = Math.Max(2, (int)(_sourceWidth * factor)).ToString();
            HeightBox.Text = Math.Max(2, (int)(_sourceHeight * factor)).ToString();
            _updatingSizeBoxes = false;
        }
        UpdateEstimate();
    }

    private void OnSizeBoxChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingSizeBoxes || !IsLoaded)
            return;
        _updatingSizeBoxes = true;
        double aspect = _sourceHeight == 0 ? 1 : (double)_sourceWidth / _sourceHeight;
        if (ReferenceEquals(sender, WidthBox) && int.TryParse(WidthBox.Text, out int w) && w > 0)
            HeightBox.Text = Math.Max(2, (int)Math.Round(w / aspect)).ToString();
        else if (ReferenceEquals(sender, HeightBox) && int.TryParse(HeightBox.Text, out int h) && h > 0)
            WidthBox.Text = Math.Max(2, (int)Math.Round(h * aspect)).ToString();
        if (DimensionsBox.Items.Count > 4)
            DimensionsBox.SelectedIndex = 4; // Custom
        _updatingSizeBoxes = false;
        UpdateEstimate();
    }

    private void OnAnythingChanged(object sender, RoutedEventArgs e) => UpdateEstimate();

    private void OnQualityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityLabel is not null)
        {
            QualityLabel.Text = QualitySlider.Value switch
            {
                >= 0.8 => "High",
                >= 0.5 => "Medium",
                _ => "Low",
            };
            AutomationProperties.SetItemStatus(
                QualitySlider,
                $"{QualityLabel.Text}, {QualitySlider.Value:P0}.");
        }
        UpdateEstimate();
    }

    private VideoConvertOptions BuildOptions()
    {
        int width = int.TryParse(WidthBox.Text, out int w) && w >= 2 ? w : _sourceWidth;
        int height = int.TryParse(HeightBox.Text, out int h) && h >= 2 ? h : _sourceHeight;
        var audio = AudioMute.IsChecked == true ? AudioConvertMode.Mute
            : AudioMono.IsChecked == true ? AudioConvertMode.Mono
            : AudioConvertMode.Keep;
        return new VideoConvertOptions(
            TimeSpan.FromSeconds(_trimStartF * _duration.TotalSeconds),
            _trimEndF >= 0.999 ? TimeSpan.Zero : TimeSpan.FromSeconds(_trimEndF * _duration.TotalSeconds),
            width, height, QualitySlider.Value, audio);
    }

    private void UpdateEstimate()
    {
        if (!IsLoaded || _duration <= TimeSpan.Zero || EstimateText is null)
            return;
        long bytes = VideoTranscodeService.EstimateBytes(_duration, BuildOptions());
        EstimateText.Text = $"Estimated file size: ~{bytes / 1024.0 / 1024.0:0.#} MB";
    }

    // ---- Convert ----

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_completedOutput is not null)
        {
            try
            {
                ShellHelper.RevealInFolder(_completedOutput);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                EstimateText.Text = "Couldn't open the folder. Your exported video is still saved.";
            }
            return;
        }
        CancelOrClose();
    }

    private async void OnConvert(object sender, RoutedEventArgs e)
    {
        if (_completedOutput is not null)
        {
            Close();
            return;
        }

        if (!_operations.TryBegin(out VideoOperationLease? operation))
        {
            EstimateText.Text = "Finish or cancel active work before updating, then try again.";
            AutomationProperties.SetName(
                EstimateText,
                "Video conversion unavailable while an update is being prepared");
            return;
        }

        using (operation)
        {
        _completedOutput = null;
        Pause();
        ConvertButton.Content = "Restart";
        ConvertButton.IsEnabled = true;
        AutomationProperties.SetName(ConvertButton, "Restart export");
        CancelButton.Content = "Cancel export";
        CancelButton.IsEnabled = true;
        AutomationProperties.SetName(CancelButton, "Cancel export");
        ConvertProgress.Visibility = Visibility.Visible;
        ConvertProgress.Value = 0;
        EstimateText.Text = "Exporting…";

        try
        {
            var progress = new Progress<double>(p =>
                operation.TryPublish(() => ConvertProgress.Value = Math.Clamp(p, 0, 1)));
            string output = await VideoTranscodeService.ConvertAsync(
                _filePath, BuildOptions(), progress, operation.Token);

            bool accepted = operation.TryPublish(() =>
            {
                _completedOutput = output;
                EstimateText.Text = "Saved as " + System.IO.Path.GetFileName(output);
                ConvertButton.Content = "Done";
                AutomationProperties.SetName(ConvertButton, "Close video editor");
                CancelButton.Content = "Show in folder";
                AutomationProperties.SetName(CancelButton, "Show exported video in folder");
            });
            if (!accepted)
            {
                await VideoOutputOwnership.TryDeleteStaleOutputAsync(_filePath, output);
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            // Cancellation and supersession are expected control flow; the service cleans staging.
        }
        catch (Exception ex)
        {
            operation.TryPublish(() =>
            {
                App.LogError(ex);
                EstimateText.Text = "Export failed. Your original recording was kept.";
            });
        }
        finally
        {
            operation.TryFinalize(() =>
            {
                ConvertProgress.Visibility = Visibility.Collapsed;
                ConvertProgress.Value = 0;
                CancelButton.IsEnabled = true;

                if (operation.IsCancellationRequested)
                {
                    _completedOutput = null;
                    EstimateText.Text = "Export canceled. Your original recording was kept.";
                    ConvertButton.Content = "Export video";
                    AutomationProperties.SetName(ConvertButton, "Export video");
                    CancelButton.Content = "Cancel";
                    AutomationProperties.SetName(CancelButton, "Cancel and close video editor");
                }
                else if (_completedOutput is null)
                {
                    ConvertButton.Content = "Export video";
                    AutomationProperties.SetName(ConvertButton, "Export video");
                    CancelButton.Content = "Cancel";
                    AutomationProperties.SetName(CancelButton, "Cancel and close video editor");
                }
            });
        }
        }
    }

    private void CancelOrClose()
    {
        if (_operations.HasOperation)
        {
            EstimateText.Text = "Canceling…";
            CancelButton.IsEnabled = false;
            AutomationProperties.SetName(CancelButton, "Canceling export");
            _operations.CancelCurrent();
        }
        else
        {
            Close();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
            _operations.Dispose();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _operations.Dispose();
        _clock.Stop();
        Player.Close();
    }
}
