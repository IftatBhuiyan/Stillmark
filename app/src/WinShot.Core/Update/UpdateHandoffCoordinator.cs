namespace WinShot.Core.Update;

public enum UpdateHandoffStatus
{
    ShutdownRequested,
    InvalidInput,
    AlreadyInProgress,
    ActivityInProgress,
    EligibilityUnavailable,
    EligibilityChanged,
    NotNewer,
    CurrentVersionNotEntitled,
    CandidateMajorNotEntitled,
    CandidateUnavailable,
    ApplicationRestartUnregisterFailed,
    LaunchFailed,
    LaunchIndeterminate,
    ReadyRejected,
    ReadyTimedOut,
    CommitRejected,
    CommitIndeterminate,
    Cancelled,
    DependencyFailure,
    AbortUnconfirmed,
    ShutdownRequestFailed,
    ReleaseDateMissing,
    CurrentReleaseNotEntitled,
    CandidateReleaseNotEntitled,
}

public sealed record UpdateHandoffResult(
    UpdateHandoffStatus Status,
    bool CandidateConsumed,
    bool RecoveryRestored,
    bool SafetyHoldRetained)
{
    public bool ShutdownWasRequested =>
        Status == UpdateHandoffStatus.ShutdownRequested;
}

public sealed record UpdateHandoffEligibility(
    Version CurrentVersion,
    int EntitledMajor,
    string PackageId,
    string Channel,
    string RuntimeIdentifier,
    DateTimeOffset? UpdatesUntilUtc = null,
    DateTimeOffset? CurrentReleasedAtUtc = null);

/// <summary>
/// Holds one atomic view of the installed identity, selected channel, and current entitlement.
/// Implementations must prevent those values changing until this lease is disposed.
/// </summary>
public interface IUpdateHandoffEligibilityLease : IDisposable
{
    UpdateHandoffEligibility Eligibility { get; }
}

public interface IUpdateHandoffEligibilityGate
{
    ValueTask<IUpdateHandoffEligibilityLease?> TryAcquireAsync(
        CancellationToken cancellationToken);
}

public interface IApplicationRestartController
{
    bool IsRegistered { get; }

    bool TryUnregister();

    bool TryRegister();
}

public enum UpdateChildLaunchStatus
{
    Started,
    NotStarted,
}

public sealed record UpdateChildLaunchResult(
    UpdateChildLaunchStatus Status,
    IUpdateChildHandoffSession? Session = null)
{
    public bool Started =>
        Status == UpdateChildLaunchStatus.Started && Session is not null;
}

public enum UpdateChildReadyStatus
{
    Ready,
    Rejected,
    ChildExited,
    InvalidAcknowledgement,
}

public enum UpdateChildCommitStatus
{
    Accepted,
    Rejected,
    ChildExited,
    InvalidAcknowledgement,
}

public enum UpdateChildAbortStatus
{
    ConfirmedInert,
    Unconfirmed,
}

/// <summary>
/// Technology-specific, exact-child session. A positive Ready response means that the child is
/// prepared but cannot mutate the installation until CommitAsync is called. Accepted commit means
/// the child owns recovery and will wait for the parent process to exit before applying.
/// </summary>
public interface IUpdateChildHandoffSession : IAsyncDisposable
{
    ValueTask<UpdateChildReadyStatus> WaitForReadyAsync(
        CancellationToken cancellationToken);

    ValueTask<UpdateChildCommitStatus> CommitAsync(
        CancellationToken cancellationToken);

    ValueTask<UpdateChildAbortStatus> AbortAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// Adapter for the selected installer technology. It must bind its private rendezvous to the
/// exact execution lease and must never throw after process creation without returning a session
/// capable of proving the child inert.
/// </summary>
public interface IUpdateChildHandoffLauncher
{
    ValueTask<UpdateChildLaunchResult> LaunchAsync(
        UpdateExecutionLease executionLease,
        CancellationToken cancellationToken);
}

public interface IUpdateShutdownRequester
{
    bool TryRequestUpdateShutdown();
}

/// <summary>
/// Coordinates the resident side of a two-phase update handoff. This class deliberately does not
/// choose an update feed, transport, installer, or rollback mechanism.
/// </summary>
public sealed class UpdateHandoffCoordinator : IAsyncDisposable
{
    private readonly IUpdateActivityCoordinator _activity;
    private readonly IUpdateHandoffEligibilityGate _eligibilityGate;
    private readonly IApplicationRestartController _applicationRestart;
    private readonly IUpdateChildHandoffLauncher _launcher;
    private readonly IUpdateShutdownRequester _shutdown;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _operationTimeout;
    private readonly object _safetyGate = new();
    private SafetyHold? _safetyHold;
    private int _requestOwned;
    private int _methodExecuting;
    private int _disposed;

    public UpdateHandoffCoordinator(
        IUpdateActivityCoordinator activity,
        IUpdateHandoffEligibilityGate eligibilityGate,
        IApplicationRestartController applicationRestart,
        IUpdateChildHandoffLauncher launcher,
        IUpdateShutdownRequester shutdown,
        TimeSpan operationTimeout,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(eligibilityGate);
        ArgumentNullException.ThrowIfNull(applicationRestart);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(shutdown);
        if (operationTimeout <= TimeSpan.Zero
            || operationTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        _activity = activity;
        _eligibilityGate = eligibilityGate;
        _applicationRestart = applicationRestart;
        _launcher = launcher;
        _shutdown = shutdown;
        _operationTimeout = operationTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsSafetyHoldRetained
    {
        get
        {
            lock (_safetyGate)
                return _safetyHold is not null;
        }
    }

    /// <summary>
    /// Takes ownership of <paramref name="candidate"/> only after winning the single-flight gate.
    /// An AlreadyInProgress result leaves the losing candidate owned by its caller.
    /// </summary>
    public async ValueTask<UpdateHandoffResult> TryHandoffAsync(
        VerifiedUpdateCandidate? candidate,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (candidate is null)
        {
            return new UpdateHandoffResult(
                UpdateHandoffStatus.InvalidInput,
                CandidateConsumed: false,
                RecoveryRestored: false,
                SafetyHoldRetained: IsSafetyHoldRetained);
        }

        if (Interlocked.CompareExchange(ref _requestOwned, 1, 0) != 0)
        {
            return new UpdateHandoffResult(
                UpdateHandoffStatus.AlreadyInProgress,
                CandidateConsumed: false,
                RecoveryRestored: false,
                SafetyHoldRetained: IsSafetyHoldRetained);
        }
        if (Volatile.Read(ref _disposed) != 0)
        {
            Volatile.Write(ref _requestOwned, 0);
            throw new ObjectDisposedException(nameof(UpdateHandoffCoordinator));
        }
        Volatile.Write(ref _methodExecuting, 1);

        UpdateQuiescenceLease? quiescence = null;
        IUpdateHandoffEligibilityLease? eligibilityLease = null;
        UpdateHandoffAuthorization? authorization = null;
        UpdateExecutionLease? executionLease = null;
        IUpdateChildHandoffSession? childSession = null;
        bool applicationRestartUnregistered = false;
        bool commitStarted = false;
        bool releaseRequestGate = true;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_activity.TryBeginQuiescence(out quiescence))
            {
                return SafeResult(UpdateHandoffStatus.ActivityInProgress);
            }

            try
            {
                eligibilityLease = await AwaitBoundedAsync(
                    token => _eligibilityGate.TryAcquireAsync(token),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return SafeResult(UpdateHandoffStatus.EligibilityUnavailable);
            }
            if (eligibilityLease is null)
                return SafeResult(UpdateHandoffStatus.EligibilityUnavailable);

            UpdateHandoffStatus? eligibilityFailure = ValidateEligibility(
                candidate,
                eligibilityLease.Eligibility);
            if (eligibilityFailure.HasValue)
                return SafeResult(eligibilityFailure.Value);

            if (!candidate.TryCreateHandoffAuthorization(out authorization)
                || authorization is null
                || !authorization.TryAcquireExecutionLease(out executionLease)
                || executionLease is null)
            {
                return SafeResult(UpdateHandoffStatus.CandidateUnavailable);
            }

            bool restartWasRegistered = _applicationRestart.IsRegistered;
            if (restartWasRegistered)
            {
                applicationRestartUnregistered = true;
                if (!_applicationRestart.TryUnregister())
                {
                    return SafeResult(
                        UpdateHandoffStatus.ApplicationRestartUnregisterFailed);
                }

                if (_applicationRestart.IsRegistered)
                {
                    return SafeResult(
                        UpdateHandoffStatus.ApplicationRestartUnregisterFailed);
                }
            }

            UpdateChildLaunchResult launchResult;
            try
            {
                launchResult = await AwaitBoundedAsync(
                    token => _launcher.LaunchAsync(executionLease, token),
                    cancellationToken).ConfigureAwait(false);
                executionLease = null;
            }
            catch
            {
                RetainSafetyHold(
                    quiescence,
                    eligibilityLease,
                    childSession: null);
                quiescence = null;
                eligibilityLease = null;
                releaseRequestGate = false;
                return UnsafeResult(UpdateHandoffStatus.LaunchIndeterminate);
            }

            childSession = launchResult?.Session;
            if (launchResult is null
                || !Enum.IsDefined(launchResult.Status)
                || (launchResult.Status == UpdateChildLaunchStatus.Started
                    && launchResult.Session is null))
            {
                RetainCurrentSafetyHold();
                return UnsafeResult(UpdateHandoffStatus.LaunchIndeterminate);
            }

            if (launchResult.Status == UpdateChildLaunchStatus.NotStarted
                && launchResult.Session is not null)
            {
                childSession = launchResult.Session;
                return await AbortOrHoldAsync(
                    UpdateHandoffStatus.LaunchFailed).ConfigureAwait(false);
            }

            if (!launchResult.Started)
                return SafeResult(UpdateHandoffStatus.LaunchFailed);

            UpdateChildReadyStatus readyStatus;
            try
            {
                readyStatus = await AwaitBoundedAsync(
                    token => childSession!.WaitForReadyAsync(token),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return await AbortOrHoldAsync(
                    UpdateHandoffStatus.ReadyTimedOut).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return await AbortOrHoldAsync(
                    UpdateHandoffStatus.Cancelled).ConfigureAwait(false);
            }
            catch
            {
                return await AbortOrHoldAsync(
                    UpdateHandoffStatus.DependencyFailure).ConfigureAwait(false);
            }

            if (readyStatus != UpdateChildReadyStatus.Ready)
            {
                return await AbortOrHoldAsync(
                    UpdateHandoffStatus.ReadyRejected).ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return await AbortOrHoldAsync(
                    UpdateHandoffStatus.Cancelled).ConfigureAwait(false);
            }

            commitStarted = true;
            UpdateChildCommitStatus commitStatus;
            try
            {
                commitStatus = await AwaitBoundedAsync(
                    token => childSession!.CommitAsync(token),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                RetainCurrentSafetyHold();
                return UnsafeResult(UpdateHandoffStatus.CommitIndeterminate);
            }

            if (commitStatus != UpdateChildCommitStatus.Accepted)
            {
                commitStarted = false;
                return await AbortOrHoldAsync(
                    UpdateHandoffStatus.CommitRejected).ConfigureAwait(false);
            }

            RetainCurrentSafetyHold();
            bool shutdownRequested;
            try
            {
                shutdownRequested = _shutdown.TryRequestUpdateShutdown();
            }
            catch
            {
                shutdownRequested = false;
            }
            if (!shutdownRequested)
                return UnsafeResult(UpdateHandoffStatus.ShutdownRequestFailed);

            return UnsafeResult(UpdateHandoffStatus.ShutdownRequested);
        }
        catch (OperationCanceledException)
        {
            if (childSession is not null && !commitStarted)
            {
                return await AbortOrHoldAsync(
                    UpdateHandoffStatus.Cancelled).ConfigureAwait(false);
            }

            return SafeResult(UpdateHandoffStatus.Cancelled);
        }
        catch
        {
            if (childSession is not null && !commitStarted)
            {
                return await AbortOrHoldAsync(
                    UpdateHandoffStatus.DependencyFailure).ConfigureAwait(false);
            }

            if (commitStarted)
            {
                RetainCurrentSafetyHold();
                return UnsafeResult(UpdateHandoffStatus.CommitIndeterminate);
            }

            return SafeResult(UpdateHandoffStatus.DependencyFailure);
        }
        finally
        {
            try
            {
                executionLease?.Dispose();
                authorization?.Dispose();
                candidate.Dispose();
                if (releaseRequestGate)
                {
                    if (applicationRestartUnregistered)
                    {
                        try
                        {
                            _ = _applicationRestart.TryRegister();
                        }
                        catch
                        {
                        }
                    }
                    if (childSession is not null)
                    {
                        try
                        {
                            await childSession.DisposeAsync().ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }
                    try
                    {
                        eligibilityLease?.Dispose();
                    }
                    catch
                    {
                    }
                    try
                    {
                        quiescence?.Dispose();
                    }
                    catch
                    {
                    }
                    Volatile.Write(ref _requestOwned, 0);
                }
            }
            finally
            {
                Volatile.Write(ref _methodExecuting, 0);
            }
        }

        UpdateHandoffResult SafeResult(UpdateHandoffStatus status) =>
            new(
                status,
                CandidateConsumed: true,
                RecoveryRestored: RestoreApplicationRestart(),
                SafetyHoldRetained: false);

        UpdateHandoffResult UnsafeResult(UpdateHandoffStatus status) =>
            new(
                status,
                CandidateConsumed: true,
                RecoveryRestored: false,
                SafetyHoldRetained: true);

        async ValueTask<UpdateHandoffResult> AbortOrHoldAsync(
            UpdateHandoffStatus safeFailureStatus)
        {
            UpdateChildAbortStatus abortStatus;
            try
            {
                abortStatus = await AwaitBoundedAsync(
                    token => childSession!.AbortAsync(token),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                abortStatus = UpdateChildAbortStatus.Unconfirmed;
            }

            if (abortStatus == UpdateChildAbortStatus.ConfirmedInert)
                return SafeResult(safeFailureStatus);

            RetainCurrentSafetyHold();
            return UnsafeResult(UpdateHandoffStatus.AbortUnconfirmed);
        }

        void RetainCurrentSafetyHold()
        {
            RetainSafetyHold(quiescence!, eligibilityLease!, childSession);
            quiescence = null;
            eligibilityLease = null;
            childSession = null;
            applicationRestartUnregistered = false;
            releaseRequestGate = false;
        }

        bool RestoreApplicationRestart()
        {
            if (!applicationRestartUnregistered)
                return true;

            bool restored;
            try
            {
                restored = _applicationRestart.TryRegister()
                    && _applicationRestart.IsRegistered;
            }
            catch
            {
                restored = false;
            }

            applicationRestartUnregistered = false;
            return restored;
        }
    }

    public async ValueTask DisposeAsync()
    {
        SafetyHold? hold;
        lock (_safetyGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            if (Volatile.Read(ref _methodExecuting) != 0
                || (Volatile.Read(ref _requestOwned) != 0
                    && _safetyHold is null))
            {
                throw new InvalidOperationException(
                    "The update coordinator cannot be disposed during an active handoff.");
            }

            Volatile.Write(ref _disposed, 1);
            hold = _safetyHold;
            _safetyHold = null;
        }

        if (hold is not null)
            await hold.DisposeAsync().ConfigureAwait(false);
    }

    private UpdateHandoffStatus? ValidateEligibility(
        VerifiedUpdateCandidate candidate,
        UpdateHandoffEligibility eligibility)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        ArgumentNullException.ThrowIfNull(eligibility.CurrentVersion);
        if (!string.Equals(
                candidate.PackageId,
                eligibility.PackageId,
                StringComparison.Ordinal)
            || !string.Equals(
                candidate.Channel,
                eligibility.Channel,
                StringComparison.Ordinal)
            || !string.Equals(
                candidate.RuntimeIdentifier,
                eligibility.RuntimeIdentifier,
                StringComparison.Ordinal))
        {
            return UpdateHandoffStatus.EligibilityChanged;
        }

        UpdatePreflightDecision preflight = UpdatePreflightPolicy.Evaluate(
            new UpdatePreflightInput(
                eligibility.CurrentVersion,
                candidate.Version,
                eligibility.EntitledMajor,
                eligibility.UpdatesUntilUtc,
                eligibility.CurrentReleasedAtUtc,
                candidate.ReleasedAtUtc));
        return preflight.Status switch
        {
            UpdatePreflightStatus.Ready => null,
            UpdatePreflightStatus.NotNewer => UpdateHandoffStatus.NotNewer,
            UpdatePreflightStatus.CurrentVersionNotEntitled =>
                UpdateHandoffStatus.CurrentVersionNotEntitled,
            UpdatePreflightStatus.CandidateMajorNotEntitled =>
                UpdateHandoffStatus.CandidateMajorNotEntitled,
            UpdatePreflightStatus.ReleaseDateMissing => UpdateHandoffStatus.ReleaseDateMissing,
            UpdatePreflightStatus.CurrentReleaseNotEntitled => UpdateHandoffStatus.CurrentReleaseNotEntitled,
            UpdatePreflightStatus.CandidateReleaseNotEntitled => UpdateHandoffStatus.CandidateReleaseNotEntitled,
            _ => throw new InvalidOperationException(
                "Unknown update preflight status."),
        };
    }

    private async ValueTask<T> AwaitBoundedAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using ITimer timer = _timeProvider.CreateTimer(
            _ =>
            {
                try
                {
                    timeoutCancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            },
            state: null,
            dueTime: _operationTimeout,
            period: Timeout.InfiniteTimeSpan);
        try
        {
            return await operation(timeoutCancellation.Token)
                .AsTask()
                .WaitAsync(
                    _operationTimeout,
                    _timeProvider,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The update handoff operation timed out.");
        }
        finally
        {
            try
            {
                timeoutCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void RetainSafetyHold(
        UpdateQuiescenceLease quiescence,
        IUpdateHandoffEligibilityLease eligibility,
        IUpdateChildHandoffSession? childSession)
    {
        lock (_safetyGate)
        {
            if (_safetyHold is not null)
                throw new InvalidOperationException("An update safety hold is already retained.");
            _safetyHold = new SafetyHold(
                quiescence,
                eligibility,
                childSession);
        }
    }

    private sealed class SafetyHold : IAsyncDisposable
    {
        private UpdateQuiescenceLease? _quiescence;
        private IUpdateHandoffEligibilityLease? _eligibility;
        private IUpdateChildHandoffSession? _childSession;

        internal SafetyHold(
            UpdateQuiescenceLease quiescence,
            IUpdateHandoffEligibilityLease eligibility,
            IUpdateChildHandoffSession? childSession)
        {
            _quiescence = quiescence;
            _eligibility = eligibility;
            _childSession = childSession;
        }

        public async ValueTask DisposeAsync()
        {
            IUpdateChildHandoffSession? child =
                Interlocked.Exchange(ref _childSession, null);
            if (child is not null)
            {
                try
                {
                    await child.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }
            try
            {
                Interlocked.Exchange(ref _eligibility, null)?.Dispose();
            }
            catch
            {
            }
            try
            {
                Interlocked.Exchange(ref _quiescence, null)?.Dispose();
            }
            catch
            {
            }
        }
    }
}
