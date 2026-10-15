using System.Text.Json;
using WinShot.App.Lifecycle;
using WinShot.Core.Settings;
using WinShot.Core.Startup;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class ApplicationStartupSettingsControllerTests
    : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"WinShot.ApplicationStartupSettings.{Guid.NewGuid():N}");

    [Fact]
    public async Task UnpackagedSet_PersistsBeforeRegistrySynchronization()
    {
        string settingsPath = Path.Combine(_root, "ordering", "settings.json");
        var manager = new SettingsManager(new SettingsStore(settingsPath));
        bool? observedEnabled = null;
        bool? observedConfigured = null;
        var controller = Create(
            ApplicationDeploymentKind.Unpackaged,
            manager,
            synchronizeUnpackaged: enabled =>
            {
                AppSettings disk = new SettingsStore(settingsPath).Load();
                observedEnabled = disk.LaunchAtStartup;
                observedConfigured = disk.HasConfiguredStartup;
                Assert.Equal(enabled, disk.LaunchAtStartup);
                return Synchronized();
            });

        ApplicationStartupSettingsResult result =
            await controller.SetEnabledAsync(
                requestedEnabled: false,
                CancellationToken.None);

        Assert.False(observedEnabled);
        Assert.True(observedConfigured);
        Assert.Equal(SettingsSaveStatus.Saved, result.SettingsSaveStatus);
        Assert.Equal(
            StartupSynchronizationStatus.AlreadySynchronized,
            result.SynchronizationStatus);
        Assert.False(result.Presentation.IsChecked);
    }

    [Fact]
    public async Task UnpackagedSaveFailure_PreservesPriorPresentationAndNeverSynchronizes()
    {
        string blocker = Path.Combine(_root, "save-blocker");
        Directory.CreateDirectory(_root);
        File.WriteAllText(blocker, "not a directory");
        var manager = new SettingsManager(
            new SettingsStore(Path.Combine(blocker, "settings.json")));
        int synchronizationCalls = 0;
        var controller = Create(
            ApplicationDeploymentKind.Unpackaged,
            manager,
            synchronizeUnpackaged: _ =>
            {
                synchronizationCalls++;
                return Synchronized();
            });
        bool prior = manager.Current.LaunchAtStartup;

        ApplicationStartupSettingsResult result =
            await controller.SetEnabledAsync(
                requestedEnabled: !prior,
                CancellationToken.None);

        Assert.Equal(SettingsSaveStatus.Failed, result.SettingsSaveStatus);
        Assert.Equal(prior, result.Presentation.IsChecked);
        Assert.Equal(prior, manager.Current.LaunchAtStartup);
        Assert.Null(result.SynchronizationResult);
        Assert.Equal(0, synchronizationCalls);
    }

    [Fact]
    public async Task PackagedConfirmedChange_PersistsOnlyAfterWindowsConfirmation()
    {
        string settingsPath = Path.Combine(_root, "packaged", "settings.json");
        var manager = new SettingsManager(new SettingsStore(settingsPath));
        var packaged = new FakePackagedController(
            new PackagedStartupChangeResult(
                PackagedStartupTaskPolicy.Present(
                    PackagedStartupRegistrationState.Disabled),
                PreferenceConfirmed: true));
        int synchronizationCalls = 0;
        var controller = Create(
            ApplicationDeploymentKind.Packaged,
            manager,
            packaged,
            _ =>
            {
                synchronizationCalls++;
                return Synchronized();
            });

        ApplicationStartupSettingsResult result =
            await controller.SetEnabledAsync(
                requestedEnabled: false,
                CancellationToken.None);

        AppSettings disk = new SettingsStore(settingsPath).Load();
        Assert.False(disk.LaunchAtStartup);
        Assert.True(disk.HasConfiguredStartup);
        Assert.Equal(SettingsSaveStatus.Saved, result.SettingsSaveStatus);
        Assert.Equal(1, packaged.SetCalls);
        Assert.Equal(0, synchronizationCalls);
    }

    [Fact]
    public async Task PackagedUnconfirmedChange_DoesNotPersistOrInvokeUnpackagedSync()
    {
        string settingsPath = Path.Combine(
            _root,
            "packaged-unconfirmed",
            "settings.json");
        var manager = new SettingsManager(new SettingsStore(settingsPath));
        bool prior = manager.Current.LaunchAtStartup;
        var packaged = new FakePackagedController(
            new PackagedStartupChangeResult(
                PackagedStartupTaskPolicy.Present(
                    PackagedStartupRegistrationState.DisabledByUser),
                PreferenceConfirmed: false));
        int synchronizationCalls = 0;
        var controller = Create(
            ApplicationDeploymentKind.Packaged,
            manager,
            packaged,
            _ =>
            {
                synchronizationCalls++;
                return Synchronized();
            });

        ApplicationStartupSettingsResult result =
            await controller.SetEnabledAsync(
                requestedEnabled: !prior,
                CancellationToken.None);

        Assert.Equal(prior, manager.Current.LaunchAtStartup);
        Assert.False(File.Exists(settingsPath));
        Assert.Null(result.SettingsSaveStatus);
        Assert.Null(result.SynchronizationResult);
        Assert.Equal(1, packaged.SetCalls);
        Assert.Equal(0, synchronizationCalls);
    }

    [Theory]
    [InlineData((int)ApplicationDeploymentKind.Packaged)]
    [InlineData((int)ApplicationDeploymentKind.Unknown)]
    [InlineData(int.MaxValue)]
    public async Task NonUnpackagedRoute_NeverInvokesRegistrySynchronization(
        int deploymentKindValue)
    {
        var manager = CreateManager($"route-{deploymentKindValue}");
        int synchronizationCalls = 0;
        var packaged = new FakePackagedController(
            new PackagedStartupChangeResult(
                PackagedStartupTaskPolicy.Present(
                    PackagedStartupRegistrationState.Enabled),
                PreferenceConfirmed: true));
        var controller = Create(
            (ApplicationDeploymentKind)deploymentKindValue,
            manager,
            packaged,
            _ =>
            {
                synchronizationCalls++;
                return Synchronized();
            });

        await controller.SetEnabledAsync(
            requestedEnabled: true,
            CancellationToken.None);
        controller.SynchronizeUnpackagedAtApplicationStart();

        Assert.Equal(0, synchronizationCalls);
        if ((ApplicationDeploymentKind)deploymentKindValue
            == ApplicationDeploymentKind.Packaged)
        {
            Assert.Equal(1, packaged.SetCalls);
        }
        else
        {
            Assert.Equal(0, packaged.SetCalls);
        }
    }

    [Fact]
    public async Task UnknownRoute_IsUnavailableAndNeverPersists()
    {
        string settingsPath = Path.Combine(_root, "unknown", "settings.json");
        var manager = new SettingsManager(new SettingsStore(settingsPath));
        var packaged = new FakePackagedController(
            default);
        var controller = Create(
            ApplicationDeploymentKind.Unknown,
            manager,
            packaged,
            _ => throw new InvalidOperationException());

        ApplicationStartupSettingsResult result =
            await controller.SetEnabledAsync(
                requestedEnabled: false,
                CancellationToken.None);

        Assert.Null(result.Presentation.IsChecked);
        Assert.False(result.Presentation.CanToggle);
        Assert.Null(result.SettingsSaveStatus);
        Assert.False(File.Exists(settingsPath));
        Assert.Equal(0, packaged.SetCalls);
    }

    [Fact]
    public async Task ReadOnlySettings_QueryStateButNeverMutateEitherAuthority()
    {
        string settingsPath = Path.Combine(_root, "readonly", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(
            settingsPath,
            JsonSerializer.Serialize(
                new
                {
                    SchemaVersion = AppSettings.CurrentSchemaVersion + 1,
                    LaunchAtStartup = false,
                }));
        var manager = new SettingsManager(new SettingsStore(settingsPath));
        var packaged = new FakePackagedController(
            new PackagedStartupChangeResult(
                PackagedStartupTaskPolicy.Present(
                    PackagedStartupRegistrationState.Enabled),
                PreferenceConfirmed: true))
        {
            RefreshPresentation = PackagedStartupTaskPolicy.Present(
                PackagedStartupRegistrationState.Enabled),
        };
        int synchronizationCalls = 0;
        var controller = Create(
            ApplicationDeploymentKind.Packaged,
            manager,
            packaged,
            _ =>
            {
                synchronizationCalls++;
                return Synchronized();
            });

        ApplicationStartupSettingsResult result =
            await controller.SetEnabledAsync(
                requestedEnabled: false,
                CancellationToken.None);

        Assert.True(manager.IsReadOnly);
        Assert.Equal(SettingsSaveStatus.ReadOnly, result.SettingsSaveStatus);
        Assert.True(result.Presentation.IsChecked);
        Assert.False(result.Presentation.CanToggle);
        Assert.Equal(1, packaged.RefreshCalls);
        Assert.Equal(0, packaged.SetCalls);
        Assert.Equal(0, synchronizationCalls);
    }

    [Fact]
    public async Task ReadOnlyUnpackagedSettings_NeverPersistOrSynchronize()
    {
        string settingsPath = Path.Combine(
            _root,
            "readonly-unpackaged",
            "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(
            settingsPath,
            JsonSerializer.Serialize(
                new
                {
                    SchemaVersion = AppSettings.CurrentSchemaVersion + 1,
                    LaunchAtStartup = false,
                }));
        var manager = new SettingsManager(new SettingsStore(settingsPath));
        int synchronizationCalls = 0;
        var controller = Create(
            ApplicationDeploymentKind.Unpackaged,
            manager,
            synchronizeUnpackaged: _ =>
            {
                synchronizationCalls++;
                return Synchronized();
            });

        ApplicationStartupSettingsResult setResult =
            await controller.SetEnabledAsync(
                requestedEnabled: false,
                CancellationToken.None);
        ApplicationStartupSettingsResult startResult =
            controller.SynchronizeUnpackagedAtApplicationStart();

        Assert.True(manager.IsReadOnly);
        Assert.Equal(
            SettingsSaveStatus.ReadOnly,
            setResult.SettingsSaveStatus);
        Assert.Equal(
            SettingsSaveStatus.ReadOnly,
            startResult.SettingsSaveStatus);
        Assert.Null(setResult.Presentation.IsChecked);
        Assert.False(setResult.Presentation.CanToggle);
        Assert.Null(setResult.SynchronizationResult);
        Assert.Null(startResult.SynchronizationResult);
        Assert.Equal(0, synchronizationCalls);
    }

    [Fact]
    public void UnpackagedApplicationStart_MigratesDurablyBeforeSynchronizing()
    {
        string settingsPath = Path.Combine(
            _root,
            "application-start",
            "settings.json");
        var manager = new SettingsManager(new SettingsStore(settingsPath));
        bool observedConfigured = false;
        var controller = Create(
            ApplicationDeploymentKind.Unpackaged,
            manager,
            synchronizeUnpackaged: _ =>
            {
                observedConfigured =
                    new SettingsStore(settingsPath).Load().HasConfiguredStartup;
                return Synchronized();
            });

        ApplicationStartupSettingsResult result =
            controller.SynchronizeUnpackagedAtApplicationStart();

        Assert.True(observedConfigured);
        Assert.True(manager.Current.HasConfiguredStartup);
        Assert.Equal(SettingsSaveStatus.Saved, result.SettingsSaveStatus);
        Assert.NotNull(result.SynchronizationResult);
    }

    [Fact]
    public void UnpackagedApplicationStart_SaveFailureNeverSynchronizes()
    {
        string blocker = Path.Combine(_root, "start-save-blocker");
        Directory.CreateDirectory(_root);
        File.WriteAllText(blocker, "not a directory");
        var manager = new SettingsManager(
            new SettingsStore(Path.Combine(blocker, "settings.json")));
        int synchronizationCalls = 0;
        var controller = Create(
            ApplicationDeploymentKind.Unpackaged,
            manager,
            synchronizeUnpackaged: _ =>
            {
                synchronizationCalls++;
                return Synchronized();
            });

        ApplicationStartupSettingsResult result =
            controller.SynchronizeUnpackagedAtApplicationStart();

        Assert.Equal(SettingsSaveStatus.Failed, result.SettingsSaveStatus);
        Assert.Null(result.SynchronizationResult);
        Assert.Equal(0, synchronizationCalls);
    }

    [Fact]
    public async Task RefreshUsesOnlyTheSelectedAuthority()
    {
        var unpackagedManager = CreateManager("refresh-unpackaged");
        var packagedManager = CreateManager("refresh-packaged");
        var packaged = new FakePackagedController(default)
        {
            RefreshPresentation = PackagedStartupTaskPolicy.Present(
                PackagedStartupRegistrationState.DisabledByUser),
        };
        int synchronizationCalls = 0;
        var unpackagedController = Create(
            ApplicationDeploymentKind.Unpackaged,
            unpackagedManager,
            packaged,
            _ =>
            {
                synchronizationCalls++;
                return Synchronized();
            });
        var packagedController = Create(
            ApplicationDeploymentKind.Packaged,
            packagedManager,
            packaged,
            _ =>
            {
                synchronizationCalls++;
                return Synchronized();
            });

        ApplicationStartupSettingsResult unpackaged =
            await unpackagedController.RefreshAsync(CancellationToken.None);
        ApplicationStartupSettingsResult packagedResult =
            await packagedController.RefreshAsync(CancellationToken.None);

        Assert.Equal(
            unpackagedManager.Current.LaunchAtStartup,
            unpackaged.Presentation.IsChecked);
        Assert.Equal(
            PackagedStartupAttention.EnableInWindowsSettings,
            packagedResult.Presentation.Attention);
        Assert.Equal(1, packaged.RefreshCalls);
        Assert.Equal(0, synchronizationCalls);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private SettingsManager CreateManager(string name) =>
        new(new SettingsStore(Path.Combine(_root, name, "settings.json")));

    private static ApplicationStartupSettingsController Create(
        ApplicationDeploymentKind deploymentKind,
        SettingsManager manager,
        IPackagedStartupSettingsController? packaged = null,
        Func<bool, StartupSynchronizationResult>? synchronizeUnpackaged = null) =>
        new(
            deploymentKind,
            manager,
            packaged,
            synchronizeUnpackaged);

    private static StartupSynchronizationResult Synchronized() =>
        new(
            StartupSynchronizationStatus.AlreadySynchronized,
            ApplicationPathKind.InstalledStableLauncher);

    private sealed class FakePackagedController
        : IPackagedStartupSettingsController
    {
        private readonly PackagedStartupChangeResult _changeResult;

        internal FakePackagedController(
            PackagedStartupChangeResult changeResult)
        {
            _changeResult = changeResult;
            RefreshPresentation = changeResult.Presentation;
        }

        internal PackagedStartupSettingsPresentation RefreshPresentation
        {
            get;
            init;
        }

        internal int RefreshCalls { get; private set; }

        internal int SetCalls { get; private set; }

        public ValueTask<PackagedStartupSettingsPresentation> RefreshAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCalls++;
            return ValueTask.FromResult(RefreshPresentation);
        }

        public ValueTask<PackagedStartupChangeResult> SetEnabledAsync(
            bool requestedEnabled,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetCalls++;
            return ValueTask.FromResult(_changeResult);
        }
    }
}
