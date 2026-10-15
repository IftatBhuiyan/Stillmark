using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using WinShot.App.Lifecycle;
using WinShot.App.Overlays;
using WinShot.App.QuickAccess;
using WinShot.Core.Naming;
using WinShot.Core.Settings;
using WinShot.Core.Startup;

namespace WinShot.App;

/// <summary>Auto-saving preferences window — every change persists immediately, no Save button.</summary>
public partial class SettingsWindow : System.Windows.Controls.UserControl, IDisposable
{
    private static readonly int[] AutoHideChoices = { 0, 5, 10, 30 };
    private static readonly int[] TimerChoices = { 3, 5, 10 };
    private static readonly string[] ColorPresetNames =
    {
        "Deep blue",
        "Near black",
        "Soft white",
        "Coral",
        "Green",
        "Purple",
        "Amber",
        "Azure",
    };
    private static readonly string[] GradientPresetNames =
    {
        "Indigo to magenta",
        "Orange to pink",
        "Mint to sky",
        "Slate to charcoal",
        "Blush to periwinkle",
        "Sand to mahogany",
    };

    private readonly SettingsManager _settings;
    private readonly IApplicationStartupSettingsController _startupSettings;
    private readonly Action _onHotkeysSaved;
    private readonly Action<ApplicationStartupSettingsResult, bool>
        _onStartupResult;
    private readonly CancellationTokenSource _windowLifetime = new();
    private QuickAccessCardPresenter? _quickAccessPreviewPresenter;
    private BitmapSource? _quickAccessPreviewImage;
    private bool _loaded;
    private bool _settingsSaveFailureNotified;
    private bool _startupOperationInProgress;

    internal SettingsWindow(
        SettingsManager settings,
        IApplicationStartupSettingsController startupSettings,
        Action onHotkeysSaved,
        Action<ApplicationStartupSettingsResult, bool> onStartupResult)
    {
        InitializeComponent();
        ApplicationTheme.Apply(settings.Current.Appearance);
        _settings = settings;
        _startupSettings = startupSettings;
        _onHotkeysSaved = onHotkeysSaved;
        _onStartupResult = onStartupResult;

        BuildWallpaperControls();
        BuildQuickAccessPreview();
        RenderSettings(settings.Current);
        _loaded = true;
        StartupCheck.IsThreeState = true;
        StartupCheck.IsChecked = null;
        StartupCheck.IsEnabled = false;
        SetStartupStatus("Checking Windows startup status...");
        Loaded += OnLoaded;
    }

    private void RenderSettings(
        AppSettings settings,
        bool includeStartup = true)
    {
        bool wasLoaded = _loaded;
        _loaded = false;
        try
        {
            // General
            ThemeCheck.IsChecked = settings.Appearance == AppearanceKind.Light;
            AutomationProperties.SetItemStatus(
                ThemeCheck,
                settings.Appearance == AppearanceKind.Light ? "Light" : "Dark");
            if (includeStartup)
                StartupCheck.IsChecked = settings.LaunchAtStartup;
            PlaySoundCheck.IsChecked = settings.PlaySound;
            AutomaticUpdatesCheck.IsChecked = settings.AutomaticallyCheckForUpdates;
            SaveFolderBox.Text = settings.SaveDirectory;
            HideIconsCheck.IsChecked = settings.HideDesktopIconsWhileCapturing;
            KeepHistoryCheck.IsChecked = settings.KeepHistoryOfUnsavedCaptures;
            QuickAccessCheck.IsChecked = settings.QuickAccessEnabled;
            RecQaoCheck.IsChecked = settings.RecordingShowQuickAccess;
            ClipboardCheck.IsChecked = settings.CopyToClipboard;
            RecCopyFileCheck.IsChecked = settings.RecordingCopyFile;
            SaveFileCheck.IsChecked = settings.SaveToFile;
            RecSaveFileCheck.IsChecked = settings.RecordingSaveToFile;
            EditorAfterCheck.IsChecked = settings.ScreenshotOpenEditor;
            PinAfterCheck.IsChecked = settings.ScreenshotPin;
            RecOpenPlayerCheck.IsChecked = settings.RecordingOpenPlayer;
            NotifyCheck.IsChecked = settings.ShowNotification;

            // Wallpaper / window screenshots
            RoundedCheck.IsChecked = settings.WindowRoundedCorners;
            ShadowCheck.IsChecked = settings.WindowShadow;
            ApplyAreaPresentationCheck.IsChecked = settings.ApplyWindowPresentationToAreaCaptures;
            PaddingSlider.Value = Math.Clamp(settings.WindowPadding, 8, 200);

            // Shortcuts
            AllInOneHotkeyBox.GestureText = settings.Hotkeys.AllInOne;
            AreaHotkeyBox.GestureText = settings.Hotkeys.AreaCapture;
            FullscreenHotkeyBox.GestureText = settings.Hotkeys.FullscreenCapture;
            AllMonitorsHotkeyBox.GestureText = settings.Hotkeys.AllMonitorsCapture;
            RepeatHotkeyBox.GestureText = settings.Hotkeys.RepeatLastArea;
            OcrHotkeyBox.GestureText = settings.Hotkeys.TextCapture;
            RecordHotkeyBox.GestureText = settings.Hotkeys.RecordScreen;

            // Quick access overlay
            PositionBox.SelectedIndex =
                settings.QuickAccessPosition == "Right" ? 1 : 0;
            SizeSlider.Value = Math.Clamp(settings.QuickAccessScale, 0.7, 1.4);
            int autoHideIndex = Array.IndexOf(
                AutoHideChoices,
                settings.QuickAccessAutoHideSeconds);
            AutoHideBox.SelectedIndex =
                autoHideIndex >= 0 ? autoHideIndex : 0;
            CloseAfterDragCheck.IsChecked = settings.QuickAccessCloseAfterDrag;

            // Recording
            ShowControlsCheck.IsChecked = settings.ShowRecordingControls;
            DisplayTimeCheck.IsChecked = settings.DisplayRecordingTime;
            DimScreenCheck.IsChecked = settings.DimWhileRecording;
            RecordCursorCheck.IsChecked = settings.RecordCursor;
            HighlightClicksCheck.IsChecked = settings.HighlightClicks;
            ShowKeystrokesCheck.IsChecked = settings.ShowKeystrokes;
            FpsBox.SelectedIndex = settings.RecordingFps >= 60 ? 1 : 0;
            SystemAudioCheck.IsChecked = settings.RecordSystemAudio;
            MicrophoneCheck.IsChecked = settings.RecordMicrophone;

            // Screenshots
            FormatBox.SelectedIndex =
                settings.ImageFormat == ImageFormatKind.Jpg ? 1 : 0;
            TemplateBox.Text = settings.FileNameTemplate;
            int timerIndex = Array.IndexOf(
                TimerChoices,
                settings.TimerSeconds);
            TimerBox.SelectedIndex = timerIndex >= 0 ? timerIndex : 1;
            FreezeScreenCheck.IsChecked = settings.FreezeScreen;
            ShowCursorCheck.IsChecked = settings.ShowCursorOnScreenshots;

            // Wallpaper backdrop selector
            _bgKind = settings.WindowBackground;
            _bgColor = WallpaperService.ParseColor(
                settings.WindowBackgroundColor);
            _bgGradient = Math.Clamp(
                settings.WindowGradientIndex,
                0,
                WallpaperService.GradientPresets.Count - 1);
            RefreshWallpaperUi();
            RenderOverlayPreview();
            RenderFileNamePreview();
        }
        finally
        {
            _loaded = wasLoaded;
        }
    }

    /// <summary>Clones current settings, applies a mutation, persists, and optionally runs a scoped callback.</summary>
    private void Apply(Action<AppSettings> mutate, Action? afterSave = null)
    {
        if (!_loaded)
            return;
        var updated = _settings.Current.Clone();
        mutate(updated);

        // A screenshot must land somewhere; if the user turned both off, keep clipboard on.
        if (!updated.CopyToClipboard && !updated.SaveToFile)
        {
            updated.CopyToClipboard = true;
            ClipboardCheck.IsChecked = true;
        }

        SettingsSaveResult result = _settings.Update(updated);
        if (!result.Succeeded)
        {
            RenderSettings(_settings.Current, includeStartup: false);
            ShowSettingsSaveFailureOnce(result.Status);
            return;
        }

        afterSave?.Invoke();
    }

    private void ShowSettingsSaveFailureOnce(SettingsSaveStatus status)
    {
        if (_settingsSaveFailureNotified)
            return;

        _settingsSaveFailureNotified = true;
        string message = status == SettingsSaveStatus.ReadOnly
            ? "These settings were created by a newer Stillmark version and cannot be changed here."
            : "Stillmark could not save this change. Your previous saved settings remain active.";
        MessageBox.Show(
            Window.GetWindow(this),
            message,
            "Settings were not saved",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    // ---- Change handlers (auto-save) ----

    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;

        if (ReferenceEquals(sender, StartupCheck))
        {
            await SetStartupPreferenceAsync(StartupCheck.IsChecked == true);
            return;
        }

        bool themeChanged = ReferenceEquals(sender, ThemeCheck);
        Apply(s =>
        {
            s.Appearance = ThemeCheck.IsChecked == true
                ? AppearanceKind.Light
                : AppearanceKind.Dark;
            s.PlaySound = PlaySoundCheck.IsChecked == true;
            s.HideDesktopIconsWhileCapturing = HideIconsCheck.IsChecked == true;
            s.KeepHistoryOfUnsavedCaptures = KeepHistoryCheck.IsChecked == true;
            s.QuickAccessEnabled = QuickAccessCheck.IsChecked == true;
            s.RecordingShowQuickAccess = RecQaoCheck.IsChecked == true;
            s.CopyToClipboard = ClipboardCheck.IsChecked == true;
            s.RecordingCopyFile = RecCopyFileCheck.IsChecked == true;
            s.SaveToFile = SaveFileCheck.IsChecked == true;
            s.RecordingSaveToFile = RecSaveFileCheck.IsChecked == true;
            s.ScreenshotOpenEditor = EditorAfterCheck.IsChecked == true;
            s.ScreenshotPin = PinAfterCheck.IsChecked == true;
            s.RecordingOpenPlayer = RecOpenPlayerCheck.IsChecked == true;
            s.ShowNotification = NotifyCheck.IsChecked == true;
            s.WindowRoundedCorners = RoundedCheck.IsChecked == true;
            s.WindowShadow = ShadowCheck.IsChecked == true;
            s.ApplyWindowPresentationToAreaCaptures = ApplyAreaPresentationCheck.IsChecked == true;
            s.QuickAccessCloseAfterDrag = CloseAfterDragCheck.IsChecked == true;
            s.ShowRecordingControls = ShowControlsCheck.IsChecked == true;
            s.DisplayRecordingTime = DisplayTimeCheck.IsChecked == true;
            s.DimWhileRecording = DimScreenCheck.IsChecked == true;
            s.RecordCursor = RecordCursorCheck.IsChecked == true;
            s.HighlightClicks = HighlightClicksCheck.IsChecked == true;
            s.ShowKeystrokes = ShowKeystrokesCheck.IsChecked == true;
            s.RecordSystemAudio = SystemAudioCheck.IsChecked == true;
            s.RecordMicrophone = MicrophoneCheck.IsChecked == true;
            s.FreezeScreen = FreezeScreenCheck.IsChecked == true;
            s.ShowCursorOnScreenshots = ShowCursorCheck.IsChecked == true;
        }, themeChanged ? ApplyThemeChange : null);

        if (_loaded && (ReferenceEquals(sender, ShadowCheck) || ReferenceEquals(sender, RoundedCheck)))
            RenderWallpaperPreview();
        if (_loaded && ReferenceEquals(sender, CloseAfterDragCheck))
            RenderOverlayPreview();
    }

    private void ApplyThemeChange()
    {
        ApplicationTheme.Apply(_settings.Current.Appearance);
        AutomationProperties.SetItemStatus(
            ThemeCheck,
            _settings.Current.Appearance == AppearanceKind.Light ? "Light" : "Dark");
        ThemeCheck.ApplyTemplate();
        FrameworkElement pulseTarget =
            ThemeCheck.Template.FindName("Track", ThemeCheck) as FrameworkElement
            ?? ThemeCheck;
        _ = ThemePulseFeedback.TryPulse(
            pulseTarget,
            SystemParameters.ClientAreaAnimation,
            SystemParameters.HighContrast);
        RenderWallpaperPreview();
        RenderOverlayPreview();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshStartupPreferenceAsync();
    }

    public void Dispose()
    {
        _windowLifetime.Cancel();
        _updateBackend?.Dispose();
        _windowLifetime.Dispose();
    }

    private async Task RefreshStartupPreferenceAsync()
    {
        if (_startupOperationInProgress)
            return;

        _startupOperationInProgress = true;
        StartupCheck.IsEnabled = false;
        try
        {
            ApplicationStartupSettingsResult result =
                await _startupSettings.RefreshAsync(_windowLifetime.Token);
            RenderStartupResult(result);
            _onStartupResult(result, false);
        }
        catch (OperationCanceledException)
            when (_windowLifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ApplicationStartupSettingsResult result =
                UnexpectedStartupFailure(ex);
            RenderStartupResult(result);
            _onStartupResult(result, false);
        }
        finally
        {
            _startupOperationInProgress = false;
        }
    }

    private async Task SetStartupPreferenceAsync(bool requestedEnabled)
    {
        if (_startupOperationInProgress)
            return;

        _startupOperationInProgress = true;
        StartupCheck.IsEnabled = false;
        SetStartupStatus("Updating Windows startup status...");
        try
        {
            ApplicationStartupSettingsResult result =
                await _startupSettings.SetEnabledAsync(
                    requestedEnabled,
                    _windowLifetime.Token);
            RenderStartupResult(result);
            _onStartupResult(result, true);
            if (result.SettingsSaveStatus is { } saveStatus
                && saveStatus != SettingsSaveStatus.Saved)
            {
                ShowSettingsSaveFailureOnce(saveStatus);
            }
        }
        catch (OperationCanceledException)
            when (_windowLifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ApplicationStartupSettingsResult result =
                UnexpectedStartupFailure(ex);
            RenderStartupResult(result);
            _onStartupResult(result, true);
        }
        finally
        {
            _startupOperationInProgress = false;
        }
    }

    private void RenderStartupResult(
        ApplicationStartupSettingsResult result)
    {
        bool wasLoaded = _loaded;
        _loaded = false;
        try
        {
            StartupCheck.IsThreeState = result.Presentation.IsChecked is null;
            StartupCheck.IsChecked = result.Presentation.IsChecked;
            StartupCheck.IsEnabled =
                result.Presentation.CanToggle && !_settings.IsReadOnly;
            SetStartupStatus(StartupStatusMessage(result));
        }
        finally
        {
            _loaded = wasLoaded;
        }
    }

    private void SetStartupStatus(string? message)
    {
        StartupStatusText.Text = message ?? string.Empty;
        StartupStatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static string? StartupStatusMessage(
        ApplicationStartupSettingsResult result)
    {
        if (result.SettingsSaveStatus == SettingsSaveStatus.Failed)
            return "This preference could not be saved. The previous saved preference remains active.";
        if (result.SettingsSaveStatus == SettingsSaveStatus.ReadOnly)
            return "These settings were created by a newer Stillmark version and are read-only here.";
        if (result.SynchronizationResult is { RequiresUserAttention: true })
            return "Windows could not apply this preference. Review Windows Settings > Apps > Startup.";

        return result.Presentation.Attention switch
        {
            PackagedStartupAttention.EnableInWindowsSettings =>
                "Re-enable Stillmark in Windows Settings > Apps > Startup.",
            PackagedStartupAttention.ManagedByAdministrator =>
                "Startup is managed by your administrator.",
            PackagedStartupAttention.StatusUnavailable =>
                "Startup status is unavailable for this installation.",
            _ => null,
        };
    }

    private static ApplicationStartupSettingsResult UnexpectedStartupFailure(
        Exception failure) =>
        new(
            PackagedStartupTaskPolicy.Present(
                PackagedStartupRegistrationState.Unavailable),
            SettingsSaveStatus: null,
            new StartupSynchronizationResult(
                StartupSynchronizationStatus.RegistryAccessFailed,
                ApplicationPathKind.Development,
                failure));

    private void OnComboChanged(object sender, SelectionChangedEventArgs e)
    {
        Apply(s =>
        {
            s.QuickAccessPosition = PositionBox.SelectedIndex == 1 ? "Right" : "Left";
            s.QuickAccessAutoHideSeconds = AutoHideChoices[Math.Max(0, AutoHideBox.SelectedIndex)];
            s.RecordingFps = FpsBox.SelectedIndex == 1 ? 60 : 30;
            s.ImageFormat = FormatBox.SelectedIndex == 1 ? ImageFormatKind.Jpg : ImageFormatKind.Png;
            s.TimerSeconds = TimerChoices[Math.Max(0, TimerBox.SelectedIndex)];
        });
        if (_loaded && (ReferenceEquals(sender, PositionBox) || ReferenceEquals(sender, AutoHideBox)))
            RenderOverlayPreview();
        if (ReferenceEquals(sender, FormatBox))
            RenderFileNamePreview();
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Apply(s =>
        {
            s.WindowPadding = Math.Round(PaddingSlider.Value);
            s.QuickAccessScale = Math.Round(SizeSlider.Value, 2);
        });
        if (_loaded && ReferenceEquals(sender, PaddingSlider))
            RenderWallpaperPreview();
        if (_loaded && ReferenceEquals(sender, SizeSlider))
            RenderOverlayPreview();
    }

    private void BuildQuickAccessPreview()
    {
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
        _quickAccessPreviewImage = CreatePreviewWindowImage(dark: true);
        _quickAccessPreviewPresenter = new QuickAccessCardPresenter(
            this,
            _quickAccessPreviewImage,
            "Representative window capture",
            isRecording: false,
            isSaved: false,
            scale: 1,
            handlers,
            previewMode: true);
        _quickAccessPreviewPresenter.SetInteractionChromeVisible(visible: true, animate: false);
    }

    /// <summary>Live production card using the current position and capture-size settings.</summary>
    private void RenderOverlayPreview()
    {
        if (_quickAccessPreviewPresenter is null || OverlayPreviewCardHost is null)
            return;

        double scale = Math.Clamp(SizeSlider?.Value ?? 1, 0.7, 1.4);
        OverlaySizeValueText.Text = $"{Math.Round(scale * 100):0}%";
        _quickAccessPreviewPresenter.SetCaptureScale(scale);
        if (_quickAccessPreviewImage is not null)
            _quickAccessPreviewPresenter.SetImage(_quickAccessPreviewImage);
        OverlayPreviewCardHost.Content = new Image
        {
            Source = _quickAccessPreviewPresenter.RenderSnapshot(),
            Stretch = Stretch.None,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
        };
        OverlayPreviewCardHost.HorizontalAlignment = PositionBox?.SelectedIndex == 1
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;

        string position = PositionBox?.SelectedIndex == 1 ? "Bottom right" : "Bottom left";
        OverlayPositionValueText.Text = $"{position} · production card";
        string size = $"{Math.Round(scale * 100):0}%";
        string close = AutoHideBox?.SelectedIndex switch
        {
            1 => "After 5 seconds",
            2 => "After 10 seconds",
            3 => "After 30 seconds",
            _ => "When I close it",
        };
        string dragged = CloseAfterDragCheck?.IsChecked == true ? " · closes after drag" : string.Empty;
        OverlayPreviewCaption.Text = $"{position} · {size} · {close}{dragged}";
        OverlayPreviewDesktop.Background = _bgKind == WindowBackgroundKind.Transparent
            ? CheckerboardBrush()
            : ResolvePreviewBackdrop();
    }

    // ---- Wallpaper backdrop ----

    private WindowBackgroundKind _bgKind;
    private Color _bgColor;
    private int _bgGradient;
    private readonly Dictionary<WindowBackgroundKind, RadioButton> _radios = new();
    private readonly List<RadioButton> _colorSwatches = new();
    private readonly List<RadioButton> _gradientSwatches = new();

    private void BuildWallpaperControls()
    {
        foreach (var (kind, label, sub) in new[]
                 {
                     (WindowBackgroundKind.Desktop, "Desktop wallpaper", "Your current desktop background"),
                     (WindowBackgroundKind.Color, "Plain color", "A solid backdrop"),
                     (WindowBackgroundKind.Gradient, "Gradient", "A two-tone gradient"),
                     (WindowBackgroundKind.Transparent, "Transparent", "No backdrop — a transparent margin"),
                 })
            BgOptionsHost.Children.Add(MakeRadioRow(kind, label, sub));

        for (int i = 0; i < WallpaperService.ColorPresets.Count; i++)
        {
            var color = WallpaperService.ColorPresets[i];
            string name = i < ColorPresetNames.Length ? ColorPresetNames[i] : $"Color {i + 1}";
            var swatch = MakeSwatch(WallpaperService.ColorBrush(color), "WindowBackgroundColor", $"{name} background color");
            swatch.Click += (_, _) => SelectColor(color);
            _colorSwatches.Add(swatch);
            ColorSwatches.Children.Add(swatch);
        }

        for (int i = 0; i < WallpaperService.GradientPresets.Count; i++)
        {
            int index = i;
            string name = i < GradientPresetNames.Length ? GradientPresetNames[i] : $"Gradient {i + 1}";
            var swatch = MakeSwatch(WallpaperService.GradientBrush(i), "WindowBackgroundGradient", $"{name} background gradient");
            swatch.Click += (_, _) => SelectGradient(index);
            _gradientSwatches.Add(swatch);
            GradientSwatches.Children.Add(swatch);
        }

        RefreshWallpaperUi();
    }

    private RadioButton MakeRadioRow(WindowBackgroundKind kind, string label, string subtitle)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = label, FontSize = 13 };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        text.Children.Add(title);
        var detail = new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Margin = new Thickness(0, 1, 0, 0),
        };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        text.Children.Add(detail);

        var row = new RadioButton
        {
            GroupName = "WindowBackgroundType",
            Content = text,
            Margin = new Thickness(0, 0, 0, 14),
            Cursor = Cursors.Hand,
        };
        AutomationProperties.SetName(row, label);
        AutomationProperties.SetHelpText(row, subtitle);
        row.Click += (_, _) => SetBackgroundKind(kind);
        _radios[kind] = row;
        return row;
    }

    private RadioButton MakeSwatch(Brush fill, string groupName, string automationName)
    {
        var swatch = new RadioButton
        {
            Style = (Style)FindResource("SwatchRadio"),
            GroupName = groupName,
            Background = fill,
            Cursor = Cursors.Hand,
        };
        AutomationProperties.SetName(swatch, automationName);
        AutomationProperties.SetHelpText(swatch, "Use arrow keys to move between choices and Space to select.");
        return swatch;
    }

    private void SetBackgroundKind(WindowBackgroundKind kind)
    {
        _bgKind = kind;
        RefreshWallpaperUi();
        SaveWallpaper();
    }

    private void SelectColor(Color color)
    {
        _bgColor = color;
        _bgKind = WindowBackgroundKind.Color;
        RefreshWallpaperUi();
        SaveWallpaper();
    }

    private void SelectGradient(int index)
    {
        _bgGradient = index;
        _bgKind = WindowBackgroundKind.Gradient;
        RefreshWallpaperUi();
        SaveWallpaper();
    }

    private void RefreshWallpaperUi()
    {
        foreach (var (kind, radio) in _radios)
            radio.IsChecked = kind == _bgKind;

        ColorSwatches.Visibility = _bgKind == WindowBackgroundKind.Color ? Visibility.Visible : Visibility.Collapsed;
        GradientSwatches.Visibility = _bgKind == WindowBackgroundKind.Gradient ? Visibility.Visible : Visibility.Collapsed;

        for (int i = 0; i < _colorSwatches.Count; i++)
            _colorSwatches[i].IsChecked = WallpaperService.ColorPresets[i] == _bgColor;
        for (int i = 0; i < _gradientSwatches.Count; i++)
            _gradientSwatches[i].IsChecked = i == _bgGradient;

        RenderWallpaperPreview();
        RenderOverlayPreview();
    }

    /// <summary>Live preview using the same presentation renderer as delivered captures.</summary>
    private void RenderWallpaperPreview()
    {
        if (PreviewTile is null)
            return;

        bool transparent = _bgKind == WindowBackgroundKind.Transparent;
        Brush? backdrop = transparent ? null : ResolvePreviewBackdrop();

        double padValue = PaddingSlider?.Value ?? 30;
        bool shadow = ShadowCheck?.IsChecked == true;
        bool rounded = RoundedCheck?.IsChecked != false;
        PaddingValueText.Text = $"{Math.Round(padValue):0} px";
        string backdropName = _bgKind switch
        {
            WindowBackgroundKind.Desktop => "Desktop wallpaper",
            WindowBackgroundKind.Color => "Plain color",
            WindowBackgroundKind.Gradient => "Gradient",
            _ => "Transparent",
        };
        string effects = rounded && shadow
            ? "Rounded + shadow"
            : rounded
                ? "Rounded"
                : shadow
                    ? "Shadow"
                    : "No effects";
        PreviewCaption.Text = $"{backdropName} · {Math.Round(padValue):0} px · {effects}";
        BitmapSource rendered = ImageBeautifier.BeautifyWindowCapture(
            CreatePreviewWindowImage(ThemeCheck?.IsChecked != true),
            scale: 1,
            rounded,
            shadow,
            padValue,
            backdrop);
        _quickAccessPreviewImage = rendered;
        _quickAccessPreviewPresenter?.SetImage(rendered);

        PreviewTile.Background = transparent ? CheckerboardBrush() : Brushes.Transparent;
        PreviewTile.Child = new Image
        {
            Source = rendered,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(8),
        };
    }

    private static BitmapSource CreatePreviewWindowImage(bool dark)
    {
        const int width = 280;
        const int height = 180;
        Color body = dark ? Color.FromRgb(0x20, 0x20, 0x24) : Color.FromRgb(0xF8, 0xF8, 0xFA);
        Color title = dark ? Color.FromRgb(0x18, 0x18, 0x1B) : Color.FromRgb(0xEC, 0xEC, 0xEF);
        Color sidebar = dark ? Color.FromRgb(0x26, 0x26, 0x2B) : Color.FromRgb(0xF0, 0xF0, 0xF3);
        Color line = dark ? Color.FromRgb(0x3A, 0x3A, 0x42) : Color.FromRgb(0xD8, 0xD8, 0xDE);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(
                new SolidColorBrush(body),
                null,
                new Rect(0, 0, width, height),
                10,
                10);
            dc.DrawRoundedRectangle(
                new SolidColorBrush(title),
                null,
                new Rect(0, 0, width, 30),
                10,
                10);
            dc.DrawRectangle(
                new SolidColorBrush(title),
                null,
                new Rect(0, 15, width, 15));
            dc.DrawRectangle(new SolidColorBrush(sidebar), null, new Rect(0, 30, 76, height - 30));
            dc.DrawRectangle(new SolidColorBrush(line), null, new Rect(75, 30, 1, height - 30));
            dc.DrawRectangle(new SolidColorBrush(line), null, new Rect(0, 29, width, 1));

            var accent = new SolidColorBrush(Color.FromRgb(0x74, 0x68, 0xEE));
            dc.DrawRoundedRectangle(accent, null, new Rect(94, 55, 86, 12), 4, 4);
            for (int i = 0; i < 4; i++)
            {
                double y = 83 + (i * 21);
                double rowWidth = i == 2 ? 128 : 154;
                dc.DrawRoundedRectangle(new SolidColorBrush(line), null, new Rect(94, y, rowWidth, 8), 3, 3);
            }

            var chrome = new SolidColorBrush(dark ? Color.FromRgb(0xB8, 0xB8, 0xC0) : Color.FromRgb(0x5E, 0x5E, 0x66));
            dc.DrawRectangle(chrome, null, new Rect(width - 64, 13, 10, 1));
            dc.DrawRectangle(null, new Pen(chrome, 1), new Rect(width - 39, 9, 8, 8));
            dc.DrawLine(new Pen(chrome, 1), new Point(width - 17, 9), new Point(width - 9, 17));
            dc.DrawLine(new Pen(chrome, 1), new Point(width - 9, 9), new Point(width - 17, 17));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private Brush ResolvePreviewBackdrop()
    {
        var temp = new AppSettings
        {
            WindowBackground = _bgKind,
            WindowBackgroundColor = $"#FF{_bgColor.R:X2}{_bgColor.G:X2}{_bgColor.B:X2}",
            WindowGradientIndex = _bgGradient,
        };
        return WallpaperService.ResolveBackground(temp) ?? Brushes.DimGray;
    }

    private static Brush CheckerboardBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0xCF, 0xCF, 0xD4)), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        var light = new GeometryGroup();
        light.Children.Add(new RectangleGeometry(new Rect(0, 0, 8, 8)));
        light.Children.Add(new RectangleGeometry(new Rect(8, 8, 8, 8)));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF5)), null, light));

        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 16, 16),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    private void SaveWallpaper()
    {
        Apply(s =>
        {
            s.WindowBackground = _bgKind;
            s.WindowBackgroundColor = $"#FF{_bgColor.R:X2}{_bgColor.G:X2}{_bgColor.B:X2}";
            s.WindowGradientIndex = _bgGradient;
        });
    }

    private void OnTemplateChanged(object sender, TextChangedEventArgs e)
    {
        Apply(s => s.FileNameTemplate = string.IsNullOrWhiteSpace(TemplateBox.Text)
            ? AppSettings.CreateDefault().FileNameTemplate
            : TemplateBox.Text.Trim());
        RenderFileNamePreview();
    }

    private void RenderFileNamePreview()
    {
        if (FileNamePreviewText is null || TemplateBox is null)
            return;

        string template = string.IsNullOrWhiteSpace(TemplateBox.Text)
            ? AppSettings.CreateDefault().FileNameTemplate
            : TemplateBox.Text.Trim();
        string extension = FormatBox?.SelectedIndex == 1 ? ".jpg" : ".png";
        FileNamePreviewText.Text = FileNameTemplate.Expand(
            template,
            new DateTime(2026, 8, 4, 14, 32, 8, 216)) + extension;
    }

    private void OnHotkeyChanged(object sender, TextChangedEventArgs e)
    {
        Apply(s =>
        {
            s.Hotkeys.AllInOne = AllInOneHotkeyBox.GestureText;
            s.Hotkeys.AreaCapture = AreaHotkeyBox.GestureText;
            s.Hotkeys.FullscreenCapture = FullscreenHotkeyBox.GestureText;
            s.Hotkeys.AllMonitorsCapture = AllMonitorsHotkeyBox.GestureText;
            s.Hotkeys.RepeatLastArea = RepeatHotkeyBox.GestureText;
            s.Hotkeys.TextCapture = OcrHotkeyBox.GestureText;
            s.Hotkeys.RecordScreen = RecordHotkeyBox.GestureText;
        }, afterSave: _onHotkeysSaved);
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        SelectTab((ToggleButton)sender);
    }

    private void OnTabKeyDown(object sender, KeyEventArgs e)
    {
        var tabs = GetSettingsTabs();
        int current = sender is ToggleButton currentTab ? Array.IndexOf(tabs, currentTab) : -1;
        int target = e.Key switch
        {
            Key.Left or Key.Up => (current - 1 + tabs.Length) % tabs.Length,
            Key.Right or Key.Down => (current + 1) % tabs.Length,
            Key.Home => 0,
            Key.End => tabs.Length - 1,
            _ => -1,
        };
        if (current < 0 || target < 0)
            return;

        tabs[target].Focus();
        SelectTab(tabs[target]);
        e.Handled = true;
    }

    private ToggleButton[] GetSettingsTabs() =>
        new[] { TabGeneral, TabWallpaper, TabShortcuts, TabRecording, TabScreenshots, TabAbout };

    private void SelectTab(ToggleButton clicked)
    {
        var tabs = GetSettingsTabs();
        foreach (var tab in tabs)
            tab.IsChecked = tab == clicked;

        string tag = (string)clicked.Tag;
        PaneGeneral.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
        PaneWallpaper.Visibility = tag == "Wallpaper" ? Visibility.Visible : Visibility.Collapsed;
        PaneShortcuts.Visibility = tag == "Shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        PaneRecording.Visibility = tag == "Recording" ? Visibility.Visible : Visibility.Collapsed;
        PaneScreenshots.Visibility = tag == "Screenshots" ? Visibility.Visible : Visibility.Collapsed;
        PaneAbout.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose where Stillmark saves captures",
            SelectedPath = SaveFolderBox.Text,
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SaveFolderBox.Text = dialog.SelectedPath;
            Apply(s => s.SaveDirectory = dialog.SelectedPath);
        }
    }

    private void OnSaveFolderChanged(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SaveFolderBox.Text))
            Apply(s => s.SaveDirectory = SaveFolderBox.Text.Trim());
    }
}
