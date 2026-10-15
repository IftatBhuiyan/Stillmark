using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace WinShot.App;

/// <summary>
/// WinShot's own minimal player for recordings: play/pause, scrub with live preview,
/// playback speed, mute, reveal in folder. Space toggles, arrows seek, Esc closes.
/// </summary>
public partial class VideoPlayerWindow : Window
{
    private const string PlayGlyphText = "";
    private const string PauseGlyphText = "";
    private const string VolumeGlyphText = "";
    private const string MuteGlyphText = "";

    private static readonly double[] Speeds = { 0.5, 1.0, 1.5, 2.0 };

    private readonly string _filePath;
    private readonly DispatcherTimer _clock;
    private int _speedIndex = 1;
    private bool _isPlaying;
    private bool _suppressSeek;

    public VideoPlayerWindow(string filePath)
    {
        InitializeComponent();
        WindowTheming.ApplyDarkTitleBar(this);
        _filePath = filePath;
        Title = Path.GetFileName(filePath);

        PlayGlyph.Text = PauseGlyphText;
        BigPlayGlyph.Text = PlayGlyphText;
        MuteGlyph.Text = VolumeGlyphText;
        EditGlyph.Text = ""; // scissors
        FolderGlyph.Text = "";

        Player.Source = new Uri(filePath);
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();

        PreviewKeyDown += OnKey;
        Play();
    }

    // ---- Playback state ----

    private void Play()
    {
        Player.Play();
        _isPlaying = true;
        PlayGlyph.Text = PauseGlyphText;
        BigPlayOverlay.Visibility = Visibility.Collapsed;
    }

    private void Pause()
    {
        Player.Pause();
        _isPlaying = false;
        PlayGlyph.Text = PlayGlyphText;
        BigPlayOverlay.Visibility = Visibility.Visible;
    }

    private void TogglePlay()
    {
        if (_isPlaying)
            Pause();
        else
            Play();
    }

    private void OnPlayPause(object sender, RoutedEventArgs e) => TogglePlay();

    private void OnVideoSurfaceClick(object sender, MouseButtonEventArgs e) => TogglePlay();

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan)
            SeekBar.Maximum = Math.Max(0.1, Player.NaturalDuration.TimeSpan.TotalSeconds);
        UpdateClock();
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Position = TimeSpan.Zero;
        Pause();
    }

    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        App.LogError(e.ErrorException);
        MessageBox.Show(this,
            "Stillmark could not play this recording. The file may be incomplete, unsupported, or unavailable.",
            "Stillmark Player", MessageBoxButton.OK, MessageBoxImage.Warning);
        Close();
    }

    // ---- Clock & seeking ----

    private void UpdateClock()
    {
        if (SeekBar.IsMouseCaptureWithin)
            return; // user is scrubbing; ValueChanged drives the position
        _suppressSeek = true;
        SeekBar.Value = Player.Position.TotalSeconds;
        _suppressSeek = false;

        TimeText.Text = $"{Format(Player.Position)} / {Format(TimeSpan.FromSeconds(SeekBar.Maximum))}";
    }

    private static string Format(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    private void OnSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSeek)
            return;
        Player.Position = TimeSpan.FromSeconds(e.NewValue);
        TimeText.Text = $"{Format(Player.Position)} / {Format(TimeSpan.FromSeconds(SeekBar.Maximum))}";
    }

    private void SeekBy(double seconds)
    {
        double target = Math.Clamp(Player.Position.TotalSeconds + seconds, 0, SeekBar.Maximum);
        Player.Position = TimeSpan.FromSeconds(target);
        UpdateClock();
    }

    // ---- Secondary controls ----

    private void OnCycleSpeed(object sender, RoutedEventArgs e)
    {
        _speedIndex = (_speedIndex + 1) % Speeds.Length;
        Player.SpeedRatio = Speeds[_speedIndex];
        SpeedText.Text = Speeds[_speedIndex] switch
        {
            0.5 => "½×",
            1.5 => "1.5×",
            2.0 => "2×",
            _ => "1×",
        };
    }

    private void OnToggleMute(object sender, RoutedEventArgs e)
    {
        Player.IsMuted = !Player.IsMuted;
        MuteGlyph.Text = Player.IsMuted ? MuteGlyphText : VolumeGlyphText;
    }

    private void OnOpenEditor(object sender, RoutedEventArgs e)
    {
        Pause();
        new Video.VideoEditorWindow(_filePath, App.CurrentUpdateActivity).Show();
    }

    private void OnRevealFile(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_filePath))
            ShellHelper.RevealInFolder(_filePath);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                TogglePlay();
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Left:
                SeekBy(-5);
                e.Handled = true;
                break;
            case Key.Right:
                SeekBy(5);
                e.Handled = true;
                break;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _clock.Stop();
        Player.Close();
    }
}
