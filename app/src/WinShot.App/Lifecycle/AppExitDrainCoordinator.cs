namespace WinShot.App.Lifecycle;

/// <summary>
/// Shares one in-flight active-work drain across suspend, Quit, and session-ending callers.
/// A later caller may start a new drain only after the prior drain has completed.
/// </summary>
internal sealed class AppExitDrainCoordinator
{
    private readonly object _gate = new();
    private Task<bool>? _activeDrain;
    private Task? _activeDrainObservation;
    private Task _observationTail = Task.CompletedTask;

    internal Task<bool> GetOrStart(Func<Task<bool>> drainFactory) =>
        GetOrStart(drainFactory, out _);

    internal Task<bool> GetOrStart(
        Func<Task<bool>> drainFactory,
        out bool startedNew)
    {
        ArgumentNullException.ThrowIfNull(drainFactory);

        TaskCompletionSource<bool> owner;
        lock (_gate)
        {
            if (_activeDrain is { IsCompleted: false } active)
            {
                startedNew = false;
                return active;
            }

            owner = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _activeDrain = owner.Task;
            _activeDrainObservation = null;
            startedNew = true;
        }

        Task<bool> drain;
        try
        {
            drain = drainFactory()
                ?? throw new InvalidOperationException("The active-work drain returned no task.");
        }
        catch (Exception ex)
        {
            owner.TrySetException(ex);
            return owner.Task;
        }

        _ = CompleteAsync(drain, owner);
        return owner.Task;
    }

    internal Task GetOrStartObservation(
        Task<bool> drain,
        Func<Task<bool>, Task> observationFactory)
    {
        ArgumentNullException.ThrowIfNull(drain);
        ArgumentNullException.ThrowIfNull(observationFactory);

        TaskCompletionSource owner;
        Task observationTail;
        lock (_gate)
        {
            if (!ReferenceEquals(_activeDrain, drain))
            {
                throw new InvalidOperationException(
                    "Only the active drain can own an observation.");
            }

            if (_activeDrainObservation is not null)
                return _observationTail;

            owner = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _activeDrainObservation = owner.Task;
            Task priorTail = _observationTail.IsCompletedSuccessfully
                ? Task.CompletedTask
                : _observationTail;
            _observationTail = priorTail.IsCompletedSuccessfully
                ? owner.Task
                : Task.WhenAll(priorTail, owner.Task);
            observationTail = _observationTail;
        }

        Task observation;
        try
        {
            observation = observationFactory(drain)
                ?? throw new InvalidOperationException(
                    "The active-work observation returned no task.");
        }
        catch (Exception ex)
        {
            owner.TrySetException(ex);
            return observationTail;
        }

        _ = CompleteObservationAsync(observation, owner);
        return observationTail;
    }

    private static async Task CompleteObservationAsync(
        Task observation,
        TaskCompletionSource owner)
    {
        try
        {
            await observation.ConfigureAwait(false);
            owner.TrySetResult();
        }
        catch (OperationCanceledException ex)
        {
            owner.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            owner.TrySetException(ex);
        }
    }

    private static async Task CompleteAsync(
        Task<bool> drain,
        TaskCompletionSource<bool> owner)
    {
        try
        {
            owner.TrySetResult(await drain.ConfigureAwait(false));
        }
        catch (OperationCanceledException ex)
        {
            owner.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            owner.TrySetException(ex);
        }
    }
}
