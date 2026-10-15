namespace WinShot.Core.Settings;

public enum ImageFormatKind
{
    Png,
    Jpg,
}

public enum AppearanceKind
{
    Dark,
    Light,
}

/// <summary>Backdrop rendered behind a padded window capture.</summary>
public enum WindowBackgroundKind
{
    Transparent,
    Desktop,
    Color,
    Gradient,
}

public sealed class HotkeySettings
{
    public string AreaCapture { get; set; } = "Ctrl+Shift+A";
    public string FullscreenCapture { get; set; } = "Ctrl+Shift+F";
    public string AllMonitorsCapture { get; set; } = "Ctrl+Shift+D";
    public string RepeatLastArea { get; set; } = "Ctrl+Shift+R";
    public string TextCapture { get; set; } = "Ctrl+Shift+O";
    public string RecordScreen { get; set; } = "Ctrl+Shift+E";
    public string AllInOne { get; set; } = "Ctrl+Shift+5";

    public HotkeySettings Clone() => (HotkeySettings)MemberwiseClone();
}

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Version of the persisted settings contract. Zero represents an unversioned legacy profile.
    /// </summary>
    public int SchemaVersion { get; set; }

    public string SaveDirectory { get; set; } = "";
    public string FileNameTemplate { get; set; } = "Relic_Screenshot_{yyyy}-{MM}-{dd}_{HH}-{mm}-{ss}";
    public ImageFormatKind ImageFormat { get; set; } = ImageFormatKind.Png;
    public int JpgQuality { get; set; } = 90;
    public bool CopyToClipboard { get; set; } = true;
    public bool SaveToFile { get; set; } = true;
    public bool ShowNotification { get; set; } = true;
    public bool LaunchAtStartup { get; set; } = true;
    public bool AutomaticallyCheckForUpdates { get; set; } = true;
    public AppearanceKind Appearance { get; set; } = AppearanceKind.Dark;

    /// <summary>False until the startup preference has been migrated. New profiles default on;
    /// migration preserves any older stored false value and later manual opt-outs.</summary>
    public bool HasConfiguredStartup { get; set; }

    /// <summary>False until we've told the user once that WinShot keeps running in the system tray
    /// (so closing the window / a background launch doesn't feel like the app quit).</summary>
    public bool HasShownTrayHint { get; set; }

    public HotkeySettings Hotkeys { get; set; } = new();

    /// <summary>Show the floating quick-access card after each capture (instead of a balloon).</summary>
    public bool QuickAccessEnabled { get; set; } = true;

    /// <summary>Seconds before a quick-access card dismisses itself; 0 = stay until closed.</summary>
    public int QuickAccessAutoHideSeconds { get; set; }

    /// <summary>Round the corners of window captures (matches Windows 11 window shape).</summary>
    public bool WindowRoundedCorners { get; set; } = true;

    /// <summary>Render window captures on a transparent margin with a drop shadow.</summary>
    public bool WindowShadow { get; set; } = true;

    /// <summary>Backdrop placed behind a padded window capture (desktop wallpaper, a color, a gradient, or none).</summary>
    public WindowBackgroundKind WindowBackground { get; set; } = WindowBackgroundKind.Transparent;

    /// <summary>Solid backdrop color (#AARRGGBB) when <see cref="WindowBackground"/> is Color.</summary>
    public string WindowBackgroundColor { get; set; } = "#FF2E3A59";

    /// <summary>Index into the built-in gradient presets when <see cref="WindowBackground"/> is Gradient.</summary>
    public int WindowGradientIndex { get; set; }

    /// <summary>Apply the window presentation treatment to regular area/display captures too.</summary>
    public bool ApplyWindowPresentationToAreaCaptures { get; set; }

    /// <summary>Last area selection as [x, y, width, height] physical pixels, for repeat capture.</summary>
    public int[]? LastArea { get; set; }

    public int RecordingFps { get; set; } = 30;
    public bool RecordSystemAudio { get; set; } = true;
    public bool RecordMicrophone { get; set; }

    // General
    public bool PlaySound { get; set; }
    public bool HideDesktopIconsWhileCapturing { get; set; }

    /// <summary>When false, captures you didn't save to a file are NOT copied into the browsable
    /// History folder (a short-lived temp copy still backs quick-access). Default on.</summary>
    public bool KeepHistoryOfUnsavedCaptures { get; set; } = true;

    // After-capture matrix (screenshot column beyond the existing Copy/Save/QAO flags)
    public bool ScreenshotOpenEditor { get; set; }
    public bool ScreenshotPin { get; set; }
    public bool RecordingShowQuickAccess { get; set; } = true;
    public bool RecordingCopyFile { get; set; }
    public bool RecordingOpenPlayer { get; set; }

    // Wallpaper / window screenshots
    public double WindowPadding { get; set; } = 30;

    // Quick access overlay
    public string QuickAccessPosition { get; set; } = "Left";
    public double QuickAccessScale { get; set; } = 1.0;
    public bool QuickAccessCloseAfterDrag { get; set; } = true;

    // Recording extras
    public bool ShowRecordingControls { get; set; } = true;
    public bool RecordCursor { get; set; } = true;
    public bool HighlightClicks { get; set; }

    // Screenshots
    public int TimerSeconds { get; set; } = 5;
    public bool FreezeScreen { get; set; } = true;
    public bool ShowCursorOnScreenshots { get; set; }

    // Recording extras (second wave)
    public bool RecordingSaveToFile { get; set; } = true;
    public bool ShowKeystrokes { get; set; }
    public bool DimWhileRecording { get; set; } = true;
    public bool DisplayRecordingTime { get; set; } = true;

    /// <summary>Audio capture device IDs (ScreenRecorderLib DeviceName). Null/empty = system default.</summary>
    public string? MicrophoneDeviceId { get; set; }
    public string? SystemAudioDeviceId { get; set; }

    public static AppSettings CreateDefault() => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        SaveDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "WinShot"),
    };

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Hotkeys = Hotkeys?.Clone() ?? new HotkeySettings();
        copy.LastArea = LastArea is null ? null : (int[])LastArea.Clone();
        return copy;
    }
}
