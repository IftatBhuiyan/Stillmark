namespace WinShot.App.Lifecycle;

internal enum PackagedStartupTaskOperation
{
    Query,
    RequestEnable,
    Disable,
}

/// <summary>
/// Privacy-safe failure evidence. It deliberately excludes exception messages, package identity,
/// task identity, paths, and user data.
/// </summary>
internal readonly record struct PackagedStartupTaskFailure(
    PackagedStartupTaskOperation Operation,
    int HResult);

internal readonly record struct PackagedStartupChangeResult(
    PackagedStartupSettingsPresentation Presentation,
    bool PreferenceConfirmed);

internal interface IPackagedStartupSettingsController
{
    ValueTask<PackagedStartupSettingsPresentation> RefreshAsync(
        CancellationToken cancellationToken);

    ValueTask<PackagedStartupChangeResult> SetEnabledAsync(
        bool requestedEnabled,
        CancellationToken cancellationToken);
}

/// <summary>
/// Centralizes policy decisions around the platform backend. Windows' effective state remains
/// authoritative; a preference is confirmed only after the backend reports a matching state.
/// </summary>
internal sealed class PackagedStartupTaskController
    : IPackagedStartupSettingsController
{
    private readonly IPackagedStartupTaskBackend _backend;
    private readonly Action<PackagedStartupTaskFailure> _reportFailure;

    internal PackagedStartupTaskController(
        IPackagedStartupTaskBackend backend,
        Action<PackagedStartupTaskFailure> reportFailure)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _reportFailure = reportFailure
            ?? throw new ArgumentNullException(nameof(reportFailure));
    }

    public async ValueTask<PackagedStartupSettingsPresentation> RefreshAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            PackagedStartupRegistrationState state =
                await _backend.QueryAsync(cancellationToken);
            return PackagedStartupTaskPolicy.Present(state);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportFailure(
                new PackagedStartupTaskFailure(
                    PackagedStartupTaskOperation.Query,
                    ex.HResult));
            return PackagedStartupTaskPolicy.Present(
                PackagedStartupRegistrationState.Unavailable);
        }
    }

    public async ValueTask<PackagedStartupChangeResult> SetEnabledAsync(
        bool requestedEnabled,
        CancellationToken cancellationToken)
    {
        PackagedStartupRegistrationState state;
        try
        {
            state = await _backend.QueryAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportFailure(
                new PackagedStartupTaskFailure(
                    PackagedStartupTaskOperation.Query,
                    ex.HResult));
            return UnavailableChange();
        }

        PackagedStartupMutation mutation =
            PackagedStartupTaskPolicy.DecideMutation(requestedEnabled, state);
        try
        {
            state = mutation switch
            {
                PackagedStartupMutation.RequestEnable =>
                    await _backend.RequestEnableAsync(cancellationToken),
                PackagedStartupMutation.Disable =>
                    await _backend.DisableAsync(cancellationToken),
                _ => state,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportFailure(
                new PackagedStartupTaskFailure(
                    mutation == PackagedStartupMutation.Disable
                        ? PackagedStartupTaskOperation.Disable
                        : PackagedStartupTaskOperation.RequestEnable,
                    ex.HResult));
            return UnavailableChange();
        }

        return new PackagedStartupChangeResult(
            PackagedStartupTaskPolicy.Present(state),
            PackagedStartupTaskPolicy.ConfirmsPreference(
                requestedEnabled,
                state));
    }

    private static PackagedStartupChangeResult UnavailableChange() =>
        new(
            PackagedStartupTaskPolicy.Present(
                PackagedStartupRegistrationState.Unavailable),
            PreferenceConfirmed: false);

    private void ReportFailure(PackagedStartupTaskFailure failure)
    {
        try
        {
            _reportFailure(failure);
        }
        catch
        {
            // Diagnostic reporting must not change the authoritative Settings presentation.
        }
    }
}
