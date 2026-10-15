using WinShot.App.Lifecycle;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class PackagedStartupTaskControllerTests
{
    [Fact]
    public async Task EnableFromDisabled_RequestsOnceAndConfirmsReturnedEnabledState()
    {
        var backend = new FakeBackend(
            PackagedStartupRegistrationState.Disabled)
        {
            EnableResult = PackagedStartupRegistrationState.Enabled,
        };
        var failures = new List<PackagedStartupTaskFailure>();
        var controller = new PackagedStartupTaskController(
            backend,
            failures.Add);

        PackagedStartupChangeResult result =
            await controller.SetEnabledAsync(
                requestedEnabled: true,
                CancellationToken.None);

        Assert.True(result.PreferenceConfirmed);
        Assert.True(result.Presentation.IsChecked);
        Assert.Equal(1, backend.QueryCalls);
        Assert.Equal(1, backend.EnableCalls);
        Assert.Equal(0, backend.DisableCalls);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task DisableFromEnabled_DisablesOnceAndConfirmsReturnedDisabledState()
    {
        var backend = new FakeBackend(
            PackagedStartupRegistrationState.Enabled)
        {
            DisableResult = PackagedStartupRegistrationState.Disabled,
        };
        var controller = new PackagedStartupTaskController(
            backend,
            static _ => { });

        PackagedStartupChangeResult result =
            await controller.SetEnabledAsync(
                requestedEnabled: false,
                CancellationToken.None);

        Assert.True(result.PreferenceConfirmed);
        Assert.False(result.Presentation.IsChecked);
        Assert.Equal(1, backend.QueryCalls);
        Assert.Equal(0, backend.EnableCalls);
        Assert.Equal(1, backend.DisableCalls);
    }

    [Theory]
    [InlineData(true, (int)PackagedStartupRegistrationState.Enabled)]
    [InlineData(false, (int)PackagedStartupRegistrationState.Disabled)]
    [InlineData(false, (int)PackagedStartupRegistrationState.DisabledByUser)]
    [InlineData(false, (int)PackagedStartupRegistrationState.DisabledByPolicy)]
    [InlineData(true, (int)PackagedStartupRegistrationState.EnabledByPolicy)]
    public async Task MatchingOrAuthoritativeState_DoesNotMutate(
        bool requestedEnabled,
        int stateValue)
    {
        var backend = new FakeBackend(
            (PackagedStartupRegistrationState)stateValue);
        var controller = new PackagedStartupTaskController(
            backend,
            static _ => { });

        PackagedStartupChangeResult result =
            await controller.SetEnabledAsync(
                requestedEnabled,
                CancellationToken.None);

        Assert.True(result.PreferenceConfirmed);
        Assert.Equal(0, backend.EnableCalls);
        Assert.Equal(0, backend.DisableCalls);
    }

    [Theory]
    [InlineData((int)PackagedStartupRegistrationState.DisabledByUser)]
    [InlineData((int)PackagedStartupRegistrationState.DisabledByPolicy)]
    [InlineData((int)PackagedStartupRegistrationState.Unavailable)]
    public async Task EnableCannotOverrideUserPolicyOrUnavailableState(
        int stateValue)
    {
        var backend = new FakeBackend(
            (PackagedStartupRegistrationState)stateValue);
        var controller = new PackagedStartupTaskController(
            backend,
            static _ => { });

        PackagedStartupChangeResult result =
            await controller.SetEnabledAsync(
                requestedEnabled: true,
                CancellationToken.None);

        Assert.False(result.PreferenceConfirmed);
        Assert.Equal(0, backend.EnableCalls);
        Assert.Equal(0, backend.DisableCalls);
    }

    [Fact]
    public async Task EnableReturningDisabledByUser_IsNotConfirmedAndShowsWindowsAttention()
    {
        var backend = new FakeBackend(
            PackagedStartupRegistrationState.Disabled)
        {
            EnableResult =
                PackagedStartupRegistrationState.DisabledByUser,
        };
        var controller = new PackagedStartupTaskController(
            backend,
            static _ => { });

        PackagedStartupChangeResult result =
            await controller.SetEnabledAsync(
                requestedEnabled: true,
                CancellationToken.None);

        Assert.False(result.PreferenceConfirmed);
        Assert.False(result.Presentation.IsChecked);
        Assert.False(result.Presentation.CanToggle);
        Assert.Equal(
            PackagedStartupAttention.EnableInWindowsSettings,
            result.Presentation.Attention);
    }

    [Fact]
    public async Task QueryFailure_IsPrivacySafeUnavailableAndDoesNotConfirmPreference()
    {
        var failure = new InvalidOperationException(
            "sensitive package and task identity");
        var backend = new FakeBackend(
            PackagedStartupRegistrationState.Disabled)
        {
            QueryFailure = failure,
        };
        var failures = new List<PackagedStartupTaskFailure>();
        var controller = new PackagedStartupTaskController(
            backend,
            failures.Add);

        PackagedStartupChangeResult result =
            await controller.SetEnabledAsync(
                requestedEnabled: true,
                CancellationToken.None);

        Assert.False(result.PreferenceConfirmed);
        Assert.Null(result.Presentation.IsChecked);
        PackagedStartupTaskFailure reported = Assert.Single(failures);
        Assert.Equal(PackagedStartupTaskOperation.Query, reported.Operation);
        Assert.Equal(failure.HResult, reported.HResult);
    }

    [Theory]
    [InlineData(true, (int)PackagedStartupTaskOperation.RequestEnable)]
    [InlineData(false, (int)PackagedStartupTaskOperation.Disable)]
    public async Task MutationFailure_IsUnavailableAndReportsOnlyBoundedEvidence(
        bool requestedEnabled,
        int expectedOperationValue)
    {
        var failure = new InvalidOperationException(
            "sensitive package and task identity");
        var backend = new FakeBackend(
            requestedEnabled
                ? PackagedStartupRegistrationState.Disabled
                : PackagedStartupRegistrationState.Enabled)
        {
            EnableFailure = failure,
            DisableFailure = failure,
        };
        var failures = new List<PackagedStartupTaskFailure>();
        var controller = new PackagedStartupTaskController(
            backend,
            failures.Add);

        PackagedStartupChangeResult result =
            await controller.SetEnabledAsync(
                requestedEnabled,
                CancellationToken.None);

        Assert.False(result.PreferenceConfirmed);
        Assert.Null(result.Presentation.IsChecked);
        PackagedStartupTaskFailure reported = Assert.Single(failures);
        Assert.Equal(
            (PackagedStartupTaskOperation)expectedOperationValue,
            reported.Operation);
        Assert.Equal(failure.HResult, reported.HResult);
    }

    [Fact]
    public async Task DiagnosticSinkFailure_DoesNotEscapeOrChangeUnavailableResult()
    {
        var backend = new FakeBackend(
            PackagedStartupRegistrationState.Disabled)
        {
            QueryFailure = new InvalidOperationException(),
        };
        var controller = new PackagedStartupTaskController(
            backend,
            static _ => throw new InvalidOperationException());

        PackagedStartupSettingsPresentation result =
            await controller.RefreshAsync(CancellationToken.None);

        Assert.Null(result.IsChecked);
        Assert.False(result.CanToggle);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesWithoutFailureReportOrMutation()
    {
        var backend = new FakeBackend(
            PackagedStartupRegistrationState.Disabled);
        var failures = new List<PackagedStartupTaskFailure>();
        var controller = new PackagedStartupTaskController(
            backend,
            failures.Add);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await controller.SetEnabledAsync(
                requestedEnabled: true,
                cancellation.Token));

        Assert.Equal(0, backend.EnableCalls);
        Assert.Equal(0, backend.DisableCalls);
        Assert.Empty(failures);
    }

    private sealed class FakeBackend
        : IPackagedStartupTaskBackend
    {
        private readonly PackagedStartupRegistrationState _queryResult;

        internal FakeBackend(PackagedStartupRegistrationState queryResult)
        {
            _queryResult = queryResult;
            EnableResult = queryResult;
            DisableResult = queryResult;
        }

        internal PackagedStartupRegistrationState EnableResult { get; init; }

        internal PackagedStartupRegistrationState DisableResult { get; init; }

        internal Exception? QueryFailure { get; init; }

        internal Exception? EnableFailure { get; init; }

        internal Exception? DisableFailure { get; init; }

        internal int QueryCalls { get; private set; }

        internal int EnableCalls { get; private set; }

        internal int DisableCalls { get; private set; }

        public ValueTask<PackagedStartupRegistrationState> QueryAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueryCalls++;
            return QueryFailure is null
                ? ValueTask.FromResult(_queryResult)
                : ValueTask.FromException<PackagedStartupRegistrationState>(
                    QueryFailure);
        }

        public ValueTask<PackagedStartupRegistrationState> RequestEnableAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnableCalls++;
            return EnableFailure is null
                ? ValueTask.FromResult(EnableResult)
                : ValueTask.FromException<PackagedStartupRegistrationState>(
                    EnableFailure);
        }

        public ValueTask<PackagedStartupRegistrationState> DisableAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DisableCalls++;
            return DisableFailure is null
                ? ValueTask.FromResult(DisableResult)
                : ValueTask.FromException<PackagedStartupRegistrationState>(
                    DisableFailure);
        }
    }
}
