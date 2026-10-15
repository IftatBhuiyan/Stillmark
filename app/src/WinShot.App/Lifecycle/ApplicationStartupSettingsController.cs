using WinShot.Core.Settings;
using WinShot.Core.Startup;

namespace WinShot.App.Lifecycle;

internal readonly record struct ApplicationStartupSettingsResult(
    PackagedStartupSettingsPresentation Presentation,
    SettingsSaveStatus? SettingsSaveStatus,
    StartupSynchronizationResult? SynchronizationResult)
{
    internal StartupSynchronizationStatus? SynchronizationStatus =>
        SynchronizationResult?.Status;
}

internal interface IApplicationStartupSettingsController
{
    ValueTask<ApplicationStartupSettingsResult> RefreshAsync(
        CancellationToken cancellationToken);

    ValueTask<ApplicationStartupSettingsResult> SetEnabledAsync(
        bool requestedEnabled,
        CancellationToken cancellationToken);

    ApplicationStartupSettingsResult SynchronizeUnpackagedAtApplicationStart();
}

/// <summary>
/// Owns the mutually exclusive packaged and unpackaged startup-registration routes. Durable
/// preference writes precede unpackaged registry reconciliation, while packaged preference writes
/// follow Windows' confirmed effective state.
/// </summary>
internal sealed class ApplicationStartupSettingsController
    : IApplicationStartupSettingsController
{
    private readonly StartupRegistrationRoute _route;
    private readonly SettingsManager _settings;
    private readonly IPackagedStartupSettingsController? _packaged;
    private readonly Func<bool, StartupSynchronizationResult>?
        _synchronizeUnpackaged;

    internal ApplicationStartupSettingsController(
        ApplicationDeploymentKind deploymentKind,
        SettingsManager settings,
        IPackagedStartupSettingsController? packaged,
        Func<bool, StartupSynchronizationResult>? synchronizeUnpackaged)
    {
        _route = PackagedStartupRegistrationRoutePolicy.Evaluate(
            deploymentKind);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _packaged = packaged;
        _synchronizeUnpackaged = synchronizeUnpackaged;

        if (_route == StartupRegistrationRoute.UnpackagedRunValue
            && _synchronizeUnpackaged is null)
        {
            throw new ArgumentNullException(
                nameof(synchronizeUnpackaged),
                "Unpackaged deployment requires an explicit synchronization boundary.");
        }
    }

    public async ValueTask<ApplicationStartupSettingsResult> RefreshAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SettingsSaveStatus? settingsStatus = _settings.IsReadOnly
            ? SettingsSaveStatus.ReadOnly
            : null;

        if (_route == StartupRegistrationRoute.UnpackagedRunValue)
        {
            return new ApplicationStartupSettingsResult(
                _settings.IsReadOnly
                    ? UnavailablePresentation()
                    : PersistedPresentation(canToggle: true),
                settingsStatus,
                SynchronizationResult: null);
        }

        if (_route != StartupRegistrationRoute.PackagedStartupTask
            || _packaged is null)
        {
            return new ApplicationStartupSettingsResult(
                UnavailablePresentation(),
                settingsStatus,
                SynchronizationResult: null);
        }

        PackagedStartupSettingsPresentation presentation =
            await _packaged.RefreshAsync(cancellationToken);
        if (_settings.IsReadOnly)
            presentation = presentation with { CanToggle = false };

        return new ApplicationStartupSettingsResult(
            presentation,
            settingsStatus,
            SynchronizationResult: null);
    }

    public async ValueTask<ApplicationStartupSettingsResult> SetEnabledAsync(
        bool requestedEnabled,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_settings.IsReadOnly)
            return await RefreshAsync(cancellationToken);

        if (_route == StartupRegistrationRoute.UnpackagedRunValue)
            return SetUnpackagedEnabled(requestedEnabled);

        if (_route != StartupRegistrationRoute.PackagedStartupTask
            || _packaged is null)
        {
            return new ApplicationStartupSettingsResult(
                UnavailablePresentation(),
                SettingsSaveStatus: null,
                SynchronizationResult: null);
        }

        PackagedStartupChangeResult change =
            await _packaged.SetEnabledAsync(
                requestedEnabled,
                cancellationToken);
        if (!change.PreferenceConfirmed)
        {
            return new ApplicationStartupSettingsResult(
                change.Presentation,
                SettingsSaveStatus: null,
                SynchronizationResult: null);
        }

        AppSettings updated = _settings.Current.Clone();
        updated.LaunchAtStartup = requestedEnabled;
        updated.HasConfiguredStartup = true;
        SettingsSaveResult save = _settings.Update(updated);
        return new ApplicationStartupSettingsResult(
            change.Presentation,
            save.Status,
            SynchronizationResult: null);
    }

    public ApplicationStartupSettingsResult
        SynchronizeUnpackagedAtApplicationStart()
    {
        if (_settings.IsReadOnly)
        {
            return new ApplicationStartupSettingsResult(
                UnavailablePresentation(),
                SettingsSaveStatus.ReadOnly,
                SynchronizationResult: null);
        }

        if (_route != StartupRegistrationRoute.UnpackagedRunValue)
        {
            return new ApplicationStartupSettingsResult(
                UnavailablePresentation(),
                SettingsSaveStatus: null,
                SynchronizationResult: null);
        }

        SettingsSaveStatus? settingsStatus = null;
        if (!_settings.Current.HasConfiguredStartup)
        {
            AppSettings migrated = _settings.Current.Clone();
            migrated.HasConfiguredStartup = true;
            SettingsSaveResult save = _settings.Update(migrated);
            settingsStatus = save.Status;
            if (!save.Succeeded)
            {
                return new ApplicationStartupSettingsResult(
                    PersistedPresentation(canToggle: true),
                    settingsStatus,
                    SynchronizationResult: null);
            }
        }

        StartupSynchronizationResult synchronization =
            _synchronizeUnpackaged!(_settings.Current.LaunchAtStartup);
        return new ApplicationStartupSettingsResult(
            PersistedPresentation(canToggle: true),
            settingsStatus,
            synchronization);
    }

    private ApplicationStartupSettingsResult SetUnpackagedEnabled(
        bool requestedEnabled)
    {
        PackagedStartupSettingsPresentation priorPresentation =
            PersistedPresentation(canToggle: true);
        AppSettings updated = _settings.Current.Clone();
        updated.LaunchAtStartup = requestedEnabled;
        updated.HasConfiguredStartup = true;

        SettingsSaveResult save = _settings.Update(updated);
        if (!save.Succeeded)
        {
            return new ApplicationStartupSettingsResult(
                priorPresentation,
                save.Status,
                SynchronizationResult: null);
        }

        StartupSynchronizationResult synchronization =
            _synchronizeUnpackaged!(requestedEnabled);
        return new ApplicationStartupSettingsResult(
            PersistedPresentation(canToggle: true),
            save.Status,
            synchronization);
    }

    private PackagedStartupSettingsPresentation PersistedPresentation(
        bool canToggle) =>
        new(
            IsChecked: _settings.Current.LaunchAtStartup,
            CanToggle: canToggle,
            canToggle
                ? PackagedStartupAttention.None
                : PackagedStartupAttention.StatusUnavailable);

    private static PackagedStartupSettingsPresentation
        UnavailablePresentation() =>
        PackagedStartupTaskPolicy.Present(
            PackagedStartupRegistrationState.Unavailable);
}
