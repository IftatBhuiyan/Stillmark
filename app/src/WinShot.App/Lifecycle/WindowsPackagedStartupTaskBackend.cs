using Windows.ApplicationModel;

namespace WinShot.App.Lifecycle;

internal interface IWindowsPackagedStartupTask
{
    StartupTaskState State { get; }

    ValueTask<StartupTaskState> RequestEnableAsync(
        CancellationToken cancellationToken);

    void Disable();
}

internal interface IWindowsPackagedStartupTaskCatalog
{
    ValueTask<IReadOnlyList<IWindowsPackagedStartupTask>> GetForCurrentPackageAsync(
        CancellationToken cancellationToken);
}

internal sealed class WindowsPackagedStartupTaskCatalog
    : IWindowsPackagedStartupTaskCatalog
{
    public async ValueTask<IReadOnlyList<IWindowsPackagedStartupTask>>
        GetForCurrentPackageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<StartupTask> tasks =
            await StartupTask.GetForCurrentPackageAsync();
        cancellationToken.ThrowIfCancellationRequested();

        var results = new IWindowsPackagedStartupTask[tasks.Count];
        for (int index = 0; index < tasks.Count; index++)
            results[index] = new WindowsPackagedStartupTask(tasks[index]);
        return results;
    }
}

internal sealed class WindowsPackagedStartupTask
    : IWindowsPackagedStartupTask
{
    private readonly StartupTask _task;

    internal WindowsPackagedStartupTask(StartupTask task)
    {
        _task = task ?? throw new ArgumentNullException(nameof(task));
    }

    public StartupTaskState State => _task.State;

    public async ValueTask<StartupTaskState> RequestEnableAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartupTaskState result = await _task.RequestEnableAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public void Disable() => _task.Disable();
}

/// <summary>
/// Uses the current package's only declared startup task without obtaining or retaining package or
/// task identity. Zero or multiple declarations are ambiguous and remain unavailable.
/// </summary>
internal sealed class WindowsPackagedStartupTaskBackend
    : IPackagedStartupTaskBackend
{
    private readonly IWindowsPackagedStartupTaskCatalog _catalog;

    internal WindowsPackagedStartupTaskBackend()
        : this(new WindowsPackagedStartupTaskCatalog())
    {
    }

    internal WindowsPackagedStartupTaskBackend(
        IWindowsPackagedStartupTaskCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public async ValueTask<PackagedStartupRegistrationState> QueryAsync(
        CancellationToken cancellationToken)
    {
        IWindowsPackagedStartupTask? task =
            await GetSingleTaskAsync(cancellationToken);
        return task is null
            ? PackagedStartupRegistrationState.Unavailable
            : MapState(task.State);
    }

    public async ValueTask<PackagedStartupRegistrationState> RequestEnableAsync(
        CancellationToken cancellationToken)
    {
        IWindowsPackagedStartupTask? task =
            await GetSingleTaskAsync(cancellationToken);
        if (task is null)
            return PackagedStartupRegistrationState.Unavailable;

        PackagedStartupRegistrationState current = MapState(task.State);
        if (current != PackagedStartupRegistrationState.Disabled)
            return current;

        StartupTaskState result =
            await task.RequestEnableAsync(cancellationToken);
        return MapState(result);
    }

    public async ValueTask<PackagedStartupRegistrationState> DisableAsync(
        CancellationToken cancellationToken)
    {
        IWindowsPackagedStartupTask? task =
            await GetSingleTaskAsync(cancellationToken);
        if (task is null)
            return PackagedStartupRegistrationState.Unavailable;

        PackagedStartupRegistrationState current = MapState(task.State);
        if (current != PackagedStartupRegistrationState.Enabled)
            return current;

        cancellationToken.ThrowIfCancellationRequested();
        task.Disable();
        cancellationToken.ThrowIfCancellationRequested();
        return MapState(task.State);
    }

    internal static PackagedStartupRegistrationState MapState(
        StartupTaskState state) =>
        state switch
        {
            StartupTaskState.Disabled =>
                PackagedStartupRegistrationState.Disabled,
            StartupTaskState.DisabledByUser =>
                PackagedStartupRegistrationState.DisabledByUser,
            StartupTaskState.Enabled =>
                PackagedStartupRegistrationState.Enabled,
            StartupTaskState.DisabledByPolicy =>
                PackagedStartupRegistrationState.DisabledByPolicy,
            StartupTaskState.EnabledByPolicy =>
                PackagedStartupRegistrationState.EnabledByPolicy,
            _ => PackagedStartupRegistrationState.Unavailable,
        };

    private async ValueTask<IWindowsPackagedStartupTask?> GetSingleTaskAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IWindowsPackagedStartupTask> tasks =
            await _catalog.GetForCurrentPackageAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return tasks.Count == 1
            ? tasks[0]
            : null;
    }
}
