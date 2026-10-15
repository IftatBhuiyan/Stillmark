using System.Reflection;
using System.Text;
using WinShot.Core.Settings;
using Xunit;

namespace WinShot.Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "WinShotTests_" + Guid.NewGuid().ToString("N"));

    private SettingsStore CreateStore() => new(Path.Combine(_dir, "settings.json"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var settings = CreateStore().Load();
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.True(settings.CopyToClipboard);
        Assert.Equal(ImageFormatKind.Png, settings.ImageFormat);
        Assert.False(string.IsNullOrWhiteSpace(settings.SaveDirectory));
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = CreateStore();
        var settings = AppSettings.CreateDefault();
        settings.ImageFormat = ImageFormatKind.Jpg;
        settings.SaveToFile = false;
        settings.Hotkeys.AreaCapture = "Ctrl+Alt+5";
        settings.ApplyWindowPresentationToAreaCaptures = true;
        store.Save(settings);

        var loaded = store.Load();
        Assert.Equal(ImageFormatKind.Jpg, loaded.ImageFormat);
        Assert.False(loaded.SaveToFile);
        Assert.Equal("Ctrl+Alt+5", loaded.Hotkeys.AreaCapture);
        Assert.True(loaded.ApplyWindowPresentationToAreaCaptures);
    }

    [Fact]
    public void Load_CorruptFile_FallsBackToDefaults()
    {
        var store = CreateStore();
        Directory.CreateDirectory(_dir);
        const string corruptJson = "{ not valid json !!";
        File.WriteAllText(store.FilePath, corruptJson);

        var settings = store.Load();
        Assert.True(settings.CopyToClipboard);
        Assert.True(File.Exists(store.FilePath + ".corrupt.json"));
        Assert.Equal(corruptJson, File.ReadAllText(store.FilePath + ".corrupt.json"));
    }

    [Fact]
    public void Load_FileAtMaximumSize_DeserializesNormally()
    {
        var store = CreateStore();
        Directory.CreateDirectory(_dir);
        WriteValidJsonAtExactSize(store.FilePath, SettingsStore.MaximumFileSizeBytes);

        AppSettings settings = store.Load();

        Assert.False(settings.SaveToFile);
        Assert.Equal(SettingsStore.MaximumFileSizeBytes, new FileInfo(store.FilePath).Length);
        Assert.False(File.Exists(store.FilePath + ".corrupt.json"));
    }

    [Fact]
    public void Load_OversizedFile_ReturnsDefaultsAndBoundsEvidence()
    {
        var store = CreateStore();
        Directory.CreateDirectory(_dir);
        using (var source = new FileStream(store.FilePath, FileMode.CreateNew, FileAccess.Write))
            source.SetLength(SettingsStore.MaximumFileSizeBytes + 1L);

        string evidencePath = store.FilePath + ".corrupt.json";
        using (var priorEvidence = new FileStream(evidencePath, FileMode.CreateNew, FileAccess.Write))
            priorEvidence.SetLength(SettingsStore.MaximumFileSizeBytes * 2L);

        AppSettings settings = store.Load();

        Assert.True(settings.CopyToClipboard);
        Assert.Equal(SettingsStore.MaximumFileSizeBytes, new FileInfo(evidencePath).Length);
        Assert.False(File.Exists(evidencePath + ".tmp"));
    }

    [Fact]
    public void ReadBoundedPayload_StreamGrowsBeyondReportedLength_RejectsIt()
    {
        using var growingStream = new LengthUnderreportingStream(
            reportedLength: SettingsStore.MaximumFileSizeBytes - 1,
            readableLength: SettingsStore.MaximumFileSizeBytes + 1L);
        MethodInfo readMethod = typeof(SettingsStore).GetMethod(
            "ReadBoundedPayload",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(
            () => readMethod.Invoke(null, new object[] { growingStream }));

        Assert.IsAssignableFrom<IOException>(exception.InnerException);
        Assert.Equal(SettingsStore.MaximumFileSizeBytes + 1L, growingStream.Position);
    }

    [Fact]
    public void Load_LegacyProfileWithoutNewerProperties_PreservesExistingValues()
    {
        var store = CreateStore();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.FilePath,
            """
            {
              "SaveDirectory": "C:\\LegacyCaptures",
              "CopyToClipboard": false,
              "SaveToFile": true,
              "Hotkeys": {
                "AreaCapture": "F6"
              }
            }
            """);

        AppSettings settings = store.Load();
        AppSettings defaults = AppSettings.CreateDefault();

        Assert.Equal(@"C:\LegacyCaptures", settings.SaveDirectory);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.False(settings.CopyToClipboard);
        Assert.True(settings.SaveToFile);
        Assert.Equal("F6", settings.Hotkeys.AreaCapture);
        Assert.Equal(defaults.Hotkeys.FullscreenCapture, settings.Hotkeys.FullscreenCapture);
        Assert.Equal(defaults.ImageFormat, settings.ImageFormat);
        Assert.False(File.Exists(store.FilePath + ".corrupt.json"));
    }

    [Fact]
    public void Load_NewerSchema_PreservesOriginalAndBlocksOlderWriter()
    {
        var store = CreateStore();
        Directory.CreateDirectory(_dir);
        string original =
            $$"""
            {
              "SchemaVersion": {{AppSettings.CurrentSchemaVersion + 1}},
              "SaveDirectory": "C:\\FutureCaptures",
              "FutureSetting": "must survive rollback"
            }
            """;
        File.WriteAllText(store.FilePath, original);

        AppSettings loaded = store.Load();
        loaded.SaveDirectory = @"C:\OlderBuildAttempt";
        store.Save(loaded);

        Assert.True(store.HasNewerSchema);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(original, File.ReadAllText(store.FilePath));
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public void Load_NewerSchemaWithFutureKnownEnumValue_DoesNotBindOlderShape()
    {
        var store = CreateStore();
        Directory.CreateDirectory(_dir);
        string original =
            $$"""{"SchemaVersion":{{AppSettings.CurrentSchemaVersion + 1}},"ImageFormat":"Avif"}""";
        File.WriteAllText(store.FilePath, original);

        AppSettings loaded = store.Load();

        Assert.True(store.HasNewerSchema);
        Assert.Equal(AppSettings.CreateDefault().ImageFormat, loaded.ImageFormat);
        Assert.Equal(original, File.ReadAllText(store.FilePath));
        Assert.False(File.Exists(store.FilePath + ".corrupt.json"));
    }

    [Fact]
    public void Save_WritesCurrentSchemaVersion()
    {
        var store = CreateStore();
        var settings = new AppSettings
        {
            SchemaVersion = 0,
            SaveDirectory = @"C:\Captures",
        };

        store.Save(settings);

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        string json = File.ReadAllText(store.FilePath);
        Assert.Contains(
            $"\"SchemaVersion\": {AppSettings.CurrentSchemaVersion}",
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsManager_NewerSchema_RemainsReadOnlyAndDoesNotPublishChanges()
    {
        var store = CreateStore();
        Directory.CreateDirectory(_dir);
        string original =
            $$"""{"SchemaVersion":{{AppSettings.CurrentSchemaVersion + 1}},"FutureSetting":true}""";
        File.WriteAllText(store.FilePath, original);
        var manager = new SettingsManager(store);
        var attempted = manager.Current.Clone();
        attempted.SaveToFile = !manager.Current.SaveToFile;
        bool changed = false;
        manager.Changed += () => changed = true;

        manager.Update(attempted);
        manager.Current.LaunchAtStartup = !manager.Current.LaunchAtStartup;
        manager.SaveCurrent();

        Assert.True(manager.IsReadOnly);
        Assert.False(changed);
        Assert.NotEqual(attempted.SaveToFile, manager.Current.SaveToFile);
        Assert.Equal(original, File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void Load_LockedFile_ReturnsDefaultsWithoutCreatingEvidence()
    {
        var store = CreateStore();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.FilePath, """{"CopyToClipboard":false}""");

        using var locked = new FileStream(
            store.FilePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        AppSettings settings = store.Load();

        Assert.True(settings.CopyToClipboard);
        Assert.False(File.Exists(store.FilePath + ".corrupt.json"));
    }

    [Fact]
    public void Save_LockedExistingFile_PreservesPriorSettingsAndCleansTemporaryFile()
    {
        var store = CreateStore();
        var original = AppSettings.CreateDefault();
        original.SaveToFile = true;
        Assert.True(store.Save(original).Succeeded);

        var replacement = AppSettings.CreateDefault();
        replacement.SaveToFile = false;
        SettingsSaveResult result;
        using (var locked = new FileStream(
            store.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            result = store.Save(replacement);
            Assert.False(File.Exists(store.FilePath + ".tmp"));
        }

        Assert.Equal(SettingsSaveStatus.Failed, result.Status);
        Assert.False(result.Succeeded);
        Assert.True(store.Load().SaveToFile);
    }

    [Fact]
    public void Save_OversizedPayload_PreservesPriorSettingsAndCleansTemporaryFile()
    {
        var store = CreateStore();
        var original = AppSettings.CreateDefault();
        original.SaveToFile = false;
        store.Save(original);
        byte[] originalBytes = File.ReadAllBytes(store.FilePath);

        var oversized = AppSettings.CreateDefault();
        oversized.SaveToFile = true;
        oversized.FileNameTemplate = new string('x', SettingsStore.MaximumFileSizeBytes);
        store.Save(oversized);

        Assert.Equal(originalBytes, File.ReadAllBytes(store.FilePath));
        Assert.False(File.Exists(store.FilePath + ".tmp"));
        Assert.False(store.Load().SaveToFile);
    }

    [Fact]
    public void Load_HandEditedValues_NormalizesToConsumerContract()
    {
        var store = CreateStore();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.FilePath,
            """
            {
              "SaveDirectory": "  \\\\server\\share\\Captures  ",
              "FileNameTemplate": "CON",
              "ImageFormat": 99,
              "JpgQuality": 2147483647,
              "Hotkeys": {
                "AreaCapture": "not a gesture",
                "FullscreenCapture": "   ",
                "AllMonitorsCapture": " control + shift + d ",
                "RepeatLastArea": "Ctrl+Shift+R",
                "TextCapture": "Ctrl+Shift+O",
                "RecordScreen": "Ctrl+Shift+E",
                "AllInOne": "Ctrl+Shift+5"
              },
              "QuickAccessAutoHideSeconds": 29,
              "WindowBackground": -10,
              "WindowBackgroundColor": "transparent",
              "WindowGradientIndex": 2147483647,
              "LastArea": [2147483640, -200, 100, 80],
              "RecordingFps": 144,
              "WindowPadding": 1000000,
              "QuickAccessPosition": "Top",
              "QuickAccessScale": 1e300,
              "TimerSeconds": 60
            }
            """);

        AppSettings loaded = store.Load();
        AppSettings defaults = AppSettings.CreateDefault();

        Assert.Equal(@"\\server\share\Captures", loaded.SaveDirectory);
        Assert.Equal(defaults.FileNameTemplate, loaded.FileNameTemplate);
        Assert.Equal(defaults.ImageFormat, loaded.ImageFormat);
        Assert.Equal(100, loaded.JpgQuality);
        Assert.Equal("", loaded.Hotkeys.AreaCapture);
        Assert.Equal("", loaded.Hotkeys.FullscreenCapture);
        Assert.Equal("Ctrl+Shift+D", loaded.Hotkeys.AllMonitorsCapture);
        Assert.Equal(0, loaded.QuickAccessAutoHideSeconds);
        Assert.Equal(defaults.WindowBackground, loaded.WindowBackground);
        Assert.Equal(defaults.WindowBackgroundColor, loaded.WindowBackgroundColor);
        Assert.Equal(5, loaded.WindowGradientIndex);
        Assert.Null(loaded.LastArea);
        Assert.Equal(60, loaded.RecordingFps);
        Assert.Equal(200, loaded.WindowPadding);
        Assert.Equal("Left", loaded.QuickAccessPosition);
        Assert.Equal(1.4, loaded.QuickAccessScale);
        Assert.Equal(5, loaded.TimerSeconds);
    }

    [Fact]
    public void Load_NullNestedSettingsAndMalformedArea_FallBackSafely()
    {
        var store = CreateStore();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.FilePath,
            """
            {
              "Hotkeys": null,
              "LastArea": [1, 2, 0, 4],
              "FileNameTemplate": null,
              "QuickAccessPosition": null,
              "WindowBackgroundColor": null
            }
            """);

        AppSettings loaded = store.Load();
        AppSettings defaults = AppSettings.CreateDefault();

        Assert.Equal(defaults.Hotkeys.AllInOne, loaded.Hotkeys.AllInOne);
        Assert.Null(loaded.LastArea);
        Assert.Equal(defaults.FileNameTemplate, loaded.FileNameTemplate);
        Assert.Equal(defaults.QuickAccessPosition, loaded.QuickAccessPosition);
        Assert.Equal(defaults.WindowBackgroundColor, loaded.WindowBackgroundColor);
    }

    [Fact]
    public void Load_UnknownEnumName_RepairsOnlyThatValue()
    {
        var store = CreateStore();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.FilePath,
            """
            {
              "ImageFormat": "FutureFormat",
              "WindowBackground": "future-background",
              "Appearance": "future-appearance",
              "SaveToFile": false
            }
            """);

        AppSettings loaded = store.Load();

        Assert.Equal(ImageFormatKind.Png, loaded.ImageFormat);
        Assert.Equal(WindowBackgroundKind.Transparent, loaded.WindowBackground);
        Assert.Equal(AppearanceKind.Dark, loaded.Appearance);
        Assert.False(loaded.SaveToFile);
        Assert.False(File.Exists(store.FilePath + ".corrupt.json"));
    }

    [Fact]
    public void Appearance_RoundTripsAndMissingProfilesDefaultDark()
    {
        var store = CreateStore();
        AppSettings settings = AppSettings.CreateDefault();
        Assert.Equal(AppearanceKind.Dark, settings.Appearance);

        settings.Appearance = AppearanceKind.Light;
        Assert.True(store.Save(settings).Succeeded);

        Assert.Equal(AppearanceKind.Light, store.Load().Appearance);

        File.WriteAllText(store.FilePath, "{}");
        Assert.Equal(AppearanceKind.Dark, store.Load().Appearance);
        Assert.False(store.Load().ApplyWindowPresentationToAreaCaptures);
    }

    [Fact]
    public void Load_ReservedDeviceTemplateWithExtensions_FallsBack()
    {
        var store = CreateStore();
        var settings = AppSettings.CreateDefault();
        settings.FileNameTemplate = "LPT1.capture.png";
        store.Save(settings);

        Assert.Equal(AppSettings.CreateDefault().FileNameTemplate, store.Load().FileNameTemplate);
    }

    [Fact]
    public void SettingsManager_UpdateNormalizesBeforePublishingAndPersisting()
    {
        var store = CreateStore();
        var manager = new SettingsManager(store);
        var updated = AppSettings.CreateDefault();
        updated.JpgQuality = -500;
        updated.RecordingFps = 31;
        updated.TimerSeconds = -1;
        updated.QuickAccessScale = double.NaN;
        updated.Hotkeys.RecordScreen = "Ctrl+NoSuchKey";
        updated.LastArea = new[] { -1920, 100, 640, 480 };
        bool changed = false;
        manager.Changed += () => changed = true;

        SettingsSaveResult result = manager.Update(updated);

        Assert.True(result.Succeeded);
        Assert.True(changed);
        Assert.Equal(1, manager.Current.JpgQuality);
        Assert.Equal(30, manager.Current.RecordingFps);
        Assert.Equal(5, manager.Current.TimerSeconds);
        Assert.Equal(1.0, manager.Current.QuickAccessScale);
        Assert.Equal("", manager.Current.Hotkeys.RecordScreen);
        Assert.Equal(new[] { -1920, 100, 640, 480 }, manager.Current.LastArea);

        AppSettings reloaded = store.Load();
        Assert.Equal(1, reloaded.JpgQuality);
        Assert.Equal(new[] { -1920, 100, 640, 480 }, reloaded.LastArea);
    }

    [Fact]
    public void SettingsManager_UpdateFailure_DoesNotPublishCandidateOrRaiseChanged()
    {
        var store = CreateStore();
        var original = AppSettings.CreateDefault();
        original.LaunchAtStartup = true;
        Assert.True(store.Save(original).Succeeded);
        var manager = new SettingsManager(store);
        AppSettings priorCurrent = manager.Current;
        var attempted = manager.Current.Clone();
        attempted.LaunchAtStartup = false;
        bool changed = false;
        manager.Changed += () => changed = true;

        SettingsSaveResult result;
        using (new FileStream(
            store.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            result = manager.Update(attempted);
        }

        Assert.Equal(SettingsSaveStatus.Failed, result.Status);
        Assert.Same(priorCurrent, manager.Current);
        Assert.True(manager.Current.LaunchAtStartup);
        Assert.False(changed);
        Assert.True(new SettingsStore(store.FilePath).Load().LaunchAtStartup);
    }

    [Fact]
    public void SettingsManager_Update_CommitsBeforePublishingChanged()
    {
        var store = CreateStore();
        var manager = new SettingsManager(store);
        var updated = manager.Current.Clone();
        updated.LaunchAtStartup = false;
        bool observedPersistedValue = false;
        manager.Changed += () =>
        {
            observedPersistedValue =
                !new SettingsStore(store.FilePath).Load().LaunchAtStartup;
        };

        SettingsSaveResult result = manager.Update(updated);

        Assert.True(result.Succeeded);
        Assert.True(observedPersistedValue);
        Assert.False(manager.Current.LaunchAtStartup);
    }

    [Fact]
    public void Load_PreservesSupportedDirectoryFormsWithoutResolvingThem()
    {
        string[] paths =
        {
            @"C:\Captures",
            @"Z:\Offline\Captures",
            @"\\server\share\Captures",
            @"\\?\C:\VeryLong\Captures",
            @"\\?\UNC\server\share\Captures",
            @"relative\captures",
        };

        foreach (string path in paths)
        {
            var store = CreateStore();
            var settings = AppSettings.CreateDefault();
            settings.SaveDirectory = path;
            store.Save(settings);

            Assert.Equal(path, store.Load().SaveDirectory);
        }
    }

    [Fact]
    public void Load_InvalidWindowsPathSyntax_FallsBackWithoutTouchingNetwork()
    {
        var store = CreateStore();
        var settings = AppSettings.CreateDefault();
        settings.SaveDirectory = @"\\offline-server\share\bad*segment";
        store.Save(settings);

        Assert.Equal(AppSettings.CreateDefault().SaveDirectory, store.Load().SaveDirectory);
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var original = AppSettings.CreateDefault();
        original.LastArea = new[] { 10, 20, 30, 40 };
        var clone = original.Clone();
        clone.Hotkeys.AreaCapture = "F6";
        clone.LastArea![0] = 99;
        Assert.NotEqual("F6", original.Hotkeys.AreaCapture);
        Assert.Equal(10, original.LastArea[0]);
    }

    private static void WriteValidJsonAtExactSize(string path, int sizeInBytes)
    {
        const string prefix = "{\"SaveToFile\":false,\"Padding\":\"";
        const string suffix = "\"}";
        int paddingLength = sizeInBytes - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix);
        Assert.True(paddingLength >= 0);

        string json = prefix + new string('a', paddingLength) + suffix;
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Assert.Equal(sizeInBytes, new FileInfo(path).Length);
    }

    private sealed class LengthUnderreportingStream(long reportedLength, long readableLength) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => reportedLength;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytesRead = (int)Math.Min(count, readableLength - _position);
            if (bytesRead <= 0)
                return 0;

            Array.Clear(buffer, offset, bytesRead);
            _position += bytesRead;
            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
