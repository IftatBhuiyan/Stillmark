using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using WinShot.App.Recording;
using WinShot.Capture;
using WinShot.Core.Settings;

namespace WinShot.App;

/// <summary>System tray icon and menu — the app's main entry point for mouse users.</summary>
public sealed class TrayIconController : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly SettingsManager _settings;
    private readonly ToolStripMenuItem _areaItem;
    private readonly ToolStripMenuItem _fullscreenItem;
    private readonly ToolStripMenuItem _allMonitorsItem;
    private readonly ToolStripMenuItem _repeatItem;
    private readonly ToolStripMenuItem _ocrItem;
    private readonly ToolStripMenuItem _allInOneItem;
    private readonly ToolStripMenuItem _timedItem;
    private readonly ToolStripMenuItem _recordItem;
    private readonly ToolStripMenuItem _stopRecordingItem;
    private readonly ToolStripMenuItem _desktopIconsItem;
    private readonly RecordingController _recording;
    private string? _lastSavedPath;

    public TrayIconController(
        SettingsManager settings,
        CaptureController capture,
        RecordingController recording,
        Action openMainWindow,
        Action openSettings,
        Action openHistory,
        Action openRecordingRecovery,
        Action openDiagnosticSummary,
        Action openDiagnosticFolder,
        Action openLicense,
        Action quit)
    {
        _settings = settings;
        _recording = recording;

        var menu = new ContextMenuStrip
        {
            Renderer = CreateMenuRenderer(settings.Current.Appearance),
            ShowImageMargin = false,
            ForeColor = MenuTextColor(settings.Current.Appearance),
        };

        AddItem(menu, "Open " + ProductIdentity.DisplayName, openMainWindow);
        menu.Items.Add(new ToolStripSeparator());
        _allInOneItem = AddItem(menu, "All-In-One", capture.StartAllInOneCapture);
        _areaItem = AddItem(menu, "Capture Area", capture.StartAreaCapture);
        _fullscreenItem = AddItem(menu, "Capture Fullscreen", () => capture.CaptureFullscreenAtCursor(delayForMenuClose: true));
        _allMonitorsItem = AddItem(menu, "Capture All Monitors", () => capture.CaptureAllMonitors(delayForMenuClose: true));
        _repeatItem = AddItem(menu, "Repeat Last Area", capture.CaptureLastArea);
        _ocrItem = AddItem(menu, "Capture Text (OCR)", capture.StartTextCapture);
        menu.Items.Add(new ToolStripSeparator());
        _recordItem = AddItem(menu, "Record Screen", recording.StartRegionRecording);
        AddItem(menu, "Record Fullscreen", recording.RecordFullscreenAtCursor);
        _stopRecordingItem = AddItem(menu, "Stop Recording", recording.Stop);

        _timedItem = AddItem(menu, "Self-Timer Capture",
            () => capture.StartTimedCapture(Math.Clamp(_settings.Current.TimerSeconds, 1, 60)));

        menu.Items.Add(new ToolStripSeparator());
        _desktopIconsItem = AddItem(menu, "Hide Desktop Icons", DesktopIconsService.Toggle);
        menu.Items.Add(new ToolStripSeparator());
        AddItem(menu, "History...", openHistory);
        AddItem(menu, "Recover Recordings...", openRecordingRecovery);
        AddItem(menu, "Open Captures Folder", OpenCapturesFolder);
        AddItem(menu, "Settings...", openSettings);
        AddItem(menu, "About Stillmark...", openLicense);
        AddItem(menu, "Report an issue...", openDiagnosticSummary);
        var troubleshootingItem = new ToolStripMenuItem("Troubleshooting");
        var diagnosticSummaryItem = new ToolStripMenuItem("Review Diagnostic Summary...");
        diagnosticSummaryItem.Click += (_, _) => openDiagnosticSummary();
        var diagnosticFolderItem = new ToolStripMenuItem("Open Diagnostic Folder");
        diagnosticFolderItem.Click += (_, _) => openDiagnosticFolder();
        troubleshootingItem.DropDownItems.Add(diagnosticSummaryItem);
        troubleshootingItem.DropDownItems.Add(diagnosticFolderItem);
        menu.Items.Add(troubleshootingItem);
        menu.Items.Add(new ToolStripSeparator());
        AddItem(menu, "Quit " + ProductIdentity.DisplayName, quit);

        menu.Opening += (_, _) => RefreshMenuState();

        _icon = new NotifyIcon
        {
            Icon = IconFactory.CreateAppIcon(),
            Text = "Stillmark — capture tool",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                openMainWindow();
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_lastSavedPath is not null && File.Exists(_lastSavedPath))
                ShellHelper.RevealInFolder(_lastSavedPath);
        };
    }

    private static ToolStripMenuItem AddItem(ContextMenuStrip menu, string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => onClick();
        menu.Items.Add(item);
        return item;
    }

    private void RefreshMenuState()
    {
        ContextMenuStrip menu = _icon.ContextMenuStrip!;
        menu.Renderer = CreateMenuRenderer(_settings.Current.Appearance);
        menu.ForeColor = MenuTextColor(_settings.Current.Appearance);
        var hotkeys = _settings.Current.Hotkeys;
        _allInOneItem.ShortcutKeyDisplayString = hotkeys.AllInOne;
        _areaItem.ShortcutKeyDisplayString = hotkeys.AreaCapture;
        _fullscreenItem.ShortcutKeyDisplayString = hotkeys.FullscreenCapture;
        _allMonitorsItem.ShortcutKeyDisplayString = hotkeys.AllMonitorsCapture;
        _repeatItem.ShortcutKeyDisplayString = hotkeys.RepeatLastArea;
        _ocrItem.ShortcutKeyDisplayString = hotkeys.TextCapture;
        _recordItem.ShortcutKeyDisplayString = hotkeys.RecordScreen;
        _timedItem.Text = $"Self-Timer Capture ({_settings.Current.TimerSeconds}s)";
        _recordItem.Enabled = !_recording.IsRecording;
        _stopRecordingItem.Enabled = _recording.IsRecording;
        _repeatItem.Enabled = _settings.Current.LastArea is { Length: 4 };
        _desktopIconsItem.Checked = !DesktopIconsService.AreIconsVisible();
    }

    private static ToolStripRenderer CreateMenuRenderer(AppearanceKind appearance) =>
        SystemInformation.HighContrast ? new ToolStripSystemRenderer() : new DarkMenuRenderer(appearance);

    private static Color MenuTextColor(AppearanceKind appearance) =>
        SystemInformation.HighContrast ? SystemColors.MenuText : appearance == AppearanceKind.Light
            ? Color.FromArgb(0x18, 0x18, 0x1D)
            : Color.FromArgb(0xF2, 0xF2, 0xF6);

    private void OpenCapturesFolder()
    {
        string dir = _settings.Current.SaveDirectory;
        Directory.CreateDirectory(dir);
        ShellHelper.OpenFolder(dir);
    }

    public void ShowBalloon(string title, string message, string? savedPath)
    {
        _lastSavedPath = savedPath;
        _icon.ShowBalloonTip(3000, title, message, ToolTipIcon.None);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

/// <summary>Context-menu renderer matching the selected app appearance.</summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    private readonly bool _light;

    public DarkMenuRenderer(AppearanceKind appearance) : base(new DarkColorTable(appearance))
    {
        _light = appearance == AppearanceKind.Light;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled
            ? (_light ? Color.FromArgb(0x18, 0x18, 0x1D) : Color.FromArgb(0xF2, 0xF2, 0xF6))
            : (_light ? Color.FromArgb(0x8B, 0x8B, 0x95) : Color.FromArgb(0x80, 0x80, 0x8A));
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = _light
            ? Color.FromArgb(0x5F, 0x61, 0x6B)
            : Color.FromArgb(0xA6, 0xA6, 0xB2);
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // A quiet monochrome state indicator, matching the other tool surfaces.
        var bounds = e.ImageRectangle;
        using var brush = new SolidBrush(_light ? Color.FromArgb(0x20, 0x22, 0x24) : Color.FromArgb(0xF0, 0xF1, 0xF2));
        int size = 6;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.FillEllipse(brush,
            bounds.X + (bounds.Width - size) / 2,
            bounds.Y + (bounds.Height - size) / 2,
            size, size);
    }
}

internal sealed class DarkColorTable : ProfessionalColorTable
{
    private readonly Color _background;
    private readonly Color _highlight;
    private readonly Color _border;
    private readonly Color _separator;

    internal DarkColorTable(AppearanceKind appearance)
    {
        bool light = appearance == AppearanceKind.Light;
        _background = light ? Color.White : Color.FromArgb(0x25, 0x27, 0x29);
        _highlight = light ? Color.FromArgb(0xE8, 0xE8, 0xEE) : Color.FromArgb(0x39, 0x3D, 0x40);
        _border = light ? Color.FromArgb(0xD0, 0xD0, 0xD9) : Color.FromArgb(0x3F, 0x43, 0x47);
        _separator = light ? Color.FromArgb(0xDE, 0xDE, 0xE5) : Color.FromArgb(0x3F, 0x43, 0x47);
    }

    public override Color ToolStripDropDownBackground => _background;
    public override Color ImageMarginGradientBegin => _background;
    public override Color ImageMarginGradientMiddle => _background;
    public override Color ImageMarginGradientEnd => _background;
    public override Color MenuBorder => _border;
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => _highlight;
    public override Color MenuItemSelectedGradientBegin => _highlight;
    public override Color MenuItemSelectedGradientEnd => _highlight;
    public override Color MenuItemPressedGradientBegin => _highlight;
    public override Color MenuItemPressedGradientEnd => _highlight;
    public override Color SeparatorDark => _separator;
    public override Color SeparatorLight => _separator;
}
