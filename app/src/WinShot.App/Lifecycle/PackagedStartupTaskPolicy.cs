namespace WinShot.App.Lifecycle;

internal enum PackagedStartupRegistrationState
{
    Unavailable,
    Disabled,
    DisabledByUser,
    Enabled,
    DisabledByPolicy,
    EnabledByPolicy,
}

internal enum PackagedStartupAttention
{
    None,
    EnableInWindowsSettings,
    ManagedByAdministrator,
    StatusUnavailable,
}

internal enum PackagedStartupMutation
{
    None,
    RequestEnable,
    Disable,
}

internal readonly record struct PackagedStartupSettingsPresentation(
    bool? IsChecked,
    bool CanToggle,
    PackagedStartupAttention Attention);

internal interface IPackagedStartupTaskBackend
{
    ValueTask<PackagedStartupRegistrationState> QueryAsync(
        CancellationToken cancellationToken);

    ValueTask<PackagedStartupRegistrationState> RequestEnableAsync(
        CancellationToken cancellationToken);

    ValueTask<PackagedStartupRegistrationState> DisableAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// Keeps the Settings checkbox bound to Windows' effective StartupTask state rather than a stale
/// persisted wish. DisabledByUser and policy states are authoritative and never trigger a
/// programmatic override.
/// </summary>
internal static class PackagedStartupTaskPolicy
{
    internal static PackagedStartupSettingsPresentation Present(
        PackagedStartupRegistrationState state) =>
        state switch
        {
            PackagedStartupRegistrationState.Enabled =>
                new PackagedStartupSettingsPresentation(
                    IsChecked: true,
                    CanToggle: true,
                    PackagedStartupAttention.None),
            PackagedStartupRegistrationState.Disabled =>
                new PackagedStartupSettingsPresentation(
                    IsChecked: false,
                    CanToggle: true,
                    PackagedStartupAttention.None),
            PackagedStartupRegistrationState.DisabledByUser =>
                new PackagedStartupSettingsPresentation(
                    IsChecked: false,
                    CanToggle: false,
                    PackagedStartupAttention.EnableInWindowsSettings),
            PackagedStartupRegistrationState.DisabledByPolicy =>
                new PackagedStartupSettingsPresentation(
                    IsChecked: false,
                    CanToggle: false,
                    PackagedStartupAttention.ManagedByAdministrator),
            PackagedStartupRegistrationState.EnabledByPolicy =>
                new PackagedStartupSettingsPresentation(
                    IsChecked: true,
                    CanToggle: false,
                    PackagedStartupAttention.ManagedByAdministrator),
            _ => new PackagedStartupSettingsPresentation(
                IsChecked: null,
                CanToggle: false,
                PackagedStartupAttention.StatusUnavailable),
        };

    internal static PackagedStartupMutation DecideMutation(
        bool requestedEnabled,
        PackagedStartupRegistrationState state)
    {
        if (requestedEnabled
            && state == PackagedStartupRegistrationState.Disabled)
        {
            return PackagedStartupMutation.RequestEnable;
        }

        if (!requestedEnabled
            && state == PackagedStartupRegistrationState.Enabled)
        {
            return PackagedStartupMutation.Disable;
        }

        return PackagedStartupMutation.None;
    }

    internal static bool ConfirmsPreference(
        bool requestedEnabled,
        PackagedStartupRegistrationState effectiveState) =>
        requestedEnabled
            ? effectiveState is PackagedStartupRegistrationState.Enabled
                or PackagedStartupRegistrationState.EnabledByPolicy
            : effectiveState is PackagedStartupRegistrationState.Disabled
                or PackagedStartupRegistrationState.DisabledByUser
                or PackagedStartupRegistrationState.DisabledByPolicy;
}
