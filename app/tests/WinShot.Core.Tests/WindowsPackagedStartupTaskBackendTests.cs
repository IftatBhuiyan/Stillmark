using Windows.ApplicationModel;
using WinShot.App.Lifecycle;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class WindowsPackagedStartupTaskBackendTests
{
    [Theory]
    [InlineData((int)StartupTaskState.Disabled, (int)PackagedStartupRegistrationState.Disabled)]
    [InlineData(
        (int)StartupTaskState.DisabledByUser,
        (int)PackagedStartupRegistrationState.DisabledByUser)]
    [InlineData((int)StartupTaskState.Enabled, (int)PackagedStartupRegistrationState.Enabled)]
    [InlineData(
        (int)StartupTaskState.DisabledByPolicy,
        (int)PackagedStartupRegistrationState.DisabledByPolicy)]
    [InlineData(
        (int)StartupTaskState.EnabledByPolicy,
        (int)PackagedStartupRegistrationState.EnabledByPolicy)]
    [InlineData(int.MinValue, (int)PackagedStartupRegistrationState.Unavailable)]
    [InlineData(int.MaxValue, (int)PackagedStartupRegistrationState.Unavailable)]
    public void StateMappingIsExactAndInvalidValuesFailClosed(
        int stateValue,
        int expectedValue)
    {
        Assert.Equal(
            (PackagedStartupRegistrationState)expectedValue,
            WindowsPackagedStartupTaskBackend.MapState(
                (StartupTaskState)stateValue));
    }

    [Fact]
    public async Task Query_RequiresExactlyOneTask()
    {
        var none = new WindowsPackagedStartupTaskBackend(
            new FakeCatalog([]));
        var multiple = new WindowsPackagedStartupTaskBackend(
            new FakeCatalog(
            [
                new FakeTask(StartupTaskState.Enabled),
                new FakeTask(StartupTaskState.Disabled),
            ]));

        Assert.Equal(
            PackagedStartupRegistrationState.Unavailable,
            await none.QueryAsync(CancellationToken.None));
        Assert.Equal(
            PackagedStartupRegistrationState.Unavailable,
            await multiple.QueryAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RequestEnable_OnlyMutatesDisabledTask()
    {
        var disabled = new FakeTask(
            StartupTaskState.Disabled,
            enableResult: StartupTaskState.Enabled);
        var backend = new WindowsPackagedStartupTaskBackend(
            new FakeCatalog([disabled]));

        PackagedStartupRegistrationState result =
            await backend.RequestEnableAsync(CancellationToken.None);

        Assert.Equal(PackagedStartupRegistrationState.Enabled, result);
        Assert.Equal(1, disabled.EnableCalls);
        Assert.Equal(0, disabled.DisableCalls);
    }

    [Theory]
    [InlineData((int)StartupTaskState.DisabledByUser)]
    [InlineData((int)StartupTaskState.DisabledByPolicy)]
    [InlineData((int)StartupTaskState.EnabledByPolicy)]
    [InlineData((int)StartupTaskState.Enabled)]
    public async Task RequestEnable_DoesNotOverrideAuthoritativeOrEnabledState(
        int stateValue)
    {
        var task = new FakeTask((StartupTaskState)stateValue);
        var backend = new WindowsPackagedStartupTaskBackend(
            new FakeCatalog([task]));

        await backend.RequestEnableAsync(CancellationToken.None);

        Assert.Equal(0, task.EnableCalls);
        Assert.Equal(0, task.DisableCalls);
    }

    [Fact]
    public async Task Disable_OnlyMutatesEnabledTask()
    {
        var enabled = new FakeTask(StartupTaskState.Enabled);
        var backend = new WindowsPackagedStartupTaskBackend(
            new FakeCatalog([enabled]));

        PackagedStartupRegistrationState result =
            await backend.DisableAsync(CancellationToken.None);

        Assert.Equal(PackagedStartupRegistrationState.Disabled, result);
        Assert.Equal(0, enabled.EnableCalls);
        Assert.Equal(1, enabled.DisableCalls);
    }

    [Theory]
    [InlineData((int)StartupTaskState.Disabled)]
    [InlineData((int)StartupTaskState.DisabledByUser)]
    [InlineData((int)StartupTaskState.DisabledByPolicy)]
    [InlineData((int)StartupTaskState.EnabledByPolicy)]
    public async Task Disable_DoesNotOverrideAuthoritativeOrDisabledState(
        int stateValue)
    {
        var task = new FakeTask((StartupTaskState)stateValue);
        var backend = new WindowsPackagedStartupTaskBackend(
            new FakeCatalog([task]));

        await backend.DisableAsync(CancellationToken.None);

        Assert.Equal(0, task.EnableCalls);
        Assert.Equal(0, task.DisableCalls);
    }

    [Fact]
    public async Task PreCancelledOperation_DoesNotEnumerateOrMutate()
    {
        var catalog = new FakeCatalog(
            [new FakeTask(StartupTaskState.Disabled)]);
        var backend = new WindowsPackagedStartupTaskBackend(catalog);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await backend.QueryAsync(cancellation.Token));

        Assert.Equal(0, catalog.Calls);
    }

    private sealed class FakeCatalog
        : IWindowsPackagedStartupTaskCatalog
    {
        private readonly IReadOnlyList<IWindowsPackagedStartupTask> _tasks;

        internal FakeCatalog(
            IReadOnlyList<IWindowsPackagedStartupTask> tasks)
        {
            _tasks = tasks;
        }

        internal int Calls { get; private set; }

        public ValueTask<IReadOnlyList<IWindowsPackagedStartupTask>>
            GetForCurrentPackageAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(_tasks);
        }
    }

    private sealed class FakeTask
        : IWindowsPackagedStartupTask
    {
        private readonly StartupTaskState _enableResult;

        internal FakeTask(
            StartupTaskState state,
            StartupTaskState enableResult = StartupTaskState.Enabled)
        {
            State = state;
            _enableResult = enableResult;
        }

        public StartupTaskState State { get; private set; }

        internal int EnableCalls { get; private set; }

        internal int DisableCalls { get; private set; }

        public ValueTask<StartupTaskState> RequestEnableAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnableCalls++;
            State = _enableResult;
            return ValueTask.FromResult(_enableResult);
        }

        public void Disable()
        {
            DisableCalls++;
            State = StartupTaskState.Disabled;
        }
    }
}
