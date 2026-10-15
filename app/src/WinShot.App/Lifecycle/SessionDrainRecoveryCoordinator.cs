namespace WinShot.App.Lifecycle;

internal enum SessionDrainRecoveryOutcome
{
    Recovered,
    Exhausted,
    Superseded,
    Canceled,
}

/// <summary>
/// Serializes session-drain recovery without depending on a dispatcher or timer. A newer
/// transition ticket supersedes the prior owner, but waits for that owner to finish so retry
/// drains never overlap. Recovery remains fail-closed until the latest admitted ticket observes a
/// successful current drain or retry. RequestRecovery must be called from the synchronization
/// context that owns the retry delegate; asynchronous delay and drain continuations deliberately
/// preserve that context.
/// </summary>
internal sealed class SessionDrainRecoveryCoordinator
{
    private readonly object _gate = new();
    private readonly Func<Task<bool>?> _snapshotCurrentDrain;
    private readonly Func<CancellationToken, Task<bool>> _retryDrain;
    private readonly Func<long, bool> _canContinue;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan[] _retryDelays;

    private long _latestTicket;
    private Owner? _latestOwner;

    internal SessionDrainRecoveryCoordinator(
        Func<Task<bool>?> snapshotCurrentDrain,
        Func<CancellationToken, Task<bool>> retryDrain,
        Func<long, bool> canContinue,
        IReadOnlyList<TimeSpan> retryDelays,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _snapshotCurrentDrain = snapshotCurrentDrain
            ?? throw new ArgumentNullException(nameof(snapshotCurrentDrain));
        _retryDrain = retryDrain
            ?? throw new ArgumentNullException(nameof(retryDrain));
        _canContinue = canContinue
            ?? throw new ArgumentNullException(nameof(canContinue));
        ArgumentNullException.ThrowIfNull(retryDelays);
        _delay = delay
            ?? throw new ArgumentNullException(nameof(delay));

        _retryDelays = retryDelays.ToArray();
        if (_retryDelays.Any(static retryDelay => retryDelay < TimeSpan.Zero))
        {
            throw new ArgumentOutOfRangeException(
                nameof(retryDelays),
                "Recovery retry delays cannot be negative.");
        }
    }

    internal Task<SessionDrainRecoveryOutcome> RequestRecovery(long transitionTicket)
    {
        if (transitionTicket <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transitionTicket),
                "A recovery transition ticket must be positive.");
        }

        Owner? predecessor;
        Owner owner;
        lock (_gate)
        {
            if (transitionTicket < _latestTicket)
            {
                return Task.FromResult(
                    SessionDrainRecoveryOutcome.Superseded);
            }

            if (transitionTicket == _latestTicket)
            {
                return _latestOwner?.Completion.Task
                    ?? Task.FromResult(
                        SessionDrainRecoveryOutcome.Superseded);
            }

            predecessor = _latestOwner;
            predecessor?.RequestStop(OwnerStopReason.Superseded);

            owner = new Owner(transitionTicket);
            _latestTicket = transitionTicket;
            _latestOwner = owner;
        }

        _ = RunOwnerAsync(owner, predecessor?.Completion.Task);
        return owner.Completion.Task;
    }

    internal void CancelPending()
    {
        Owner? owner;
        lock (_gate)
            owner = _latestOwner;

        owner?.RequestStop(OwnerStopReason.Canceled);
    }

    private async Task RunOwnerAsync(
        Owner owner,
        Task<SessionDrainRecoveryOutcome>? predecessor)
    {
        SessionDrainRecoveryOutcome outcome;
        try
        {
            if (predecessor is not null)
                await predecessor;

            outcome = await ExecuteRecoveryAsync(owner);
        }
        catch
        {
            // The coordinator is a lifecycle safety boundary. Unexpected injected infrastructure
            // failures must not escape as an unobserved task or enable capture.
            outcome = ResolveStoppedOutcome(owner)
                ?? SessionDrainRecoveryOutcome.Exhausted;
        }

        owner.Complete(outcome);
    }

    private async Task<SessionDrainRecoveryOutcome> ExecuteRecoveryAsync(
        Owner owner)
    {
        SessionDrainRecoveryOutcome? stopped = ResolveStoppedOutcome(owner);
        if (stopped is not null)
            return stopped.Value;

        if (await ObserveCurrentDrainAsync(owner))
        {
            return ResolveStoppedOutcome(owner)
                ?? SessionDrainRecoveryOutcome.Recovered;
        }

        foreach (TimeSpan retryDelay in _retryDelays)
        {
            stopped = ResolveStoppedOutcome(owner);
            if (stopped is not null)
                return stopped.Value;

            try
            {
                Task delayTask = _delay(
                    retryDelay,
                    owner.Cancellation.Token)
                    ?? throw new InvalidOperationException(
                        "The recovery delay returned no task.");
                await delayTask;
            }
            catch (OperationCanceledException)
                when (owner.Cancellation.IsCancellationRequested)
            {
                return ResolveStoppedOutcome(owner)
                    ?? SessionDrainRecoveryOutcome.Canceled;
            }
            catch
            {
                return ResolveStoppedOutcome(owner)
                    ?? SessionDrainRecoveryOutcome.Exhausted;
            }

            stopped = ResolveStoppedOutcome(owner);
            if (stopped is not null)
                return stopped.Value;

            if (await TryRetryDrainAsync(owner))
            {
                return ResolveStoppedOutcome(owner)
                    ?? SessionDrainRecoveryOutcome.Recovered;
            }
        }

        return ResolveStoppedOutcome(owner)
            ?? SessionDrainRecoveryOutcome.Exhausted;
    }

    private async Task<bool> ObserveCurrentDrainAsync(Owner owner)
    {
        Task<bool>? currentDrain;
        try
        {
            currentDrain = _snapshotCurrentDrain();
        }
        catch
        {
            return false;
        }

        if (currentDrain is null)
            return true;

        return await ObserveDrainResultAsync(
            currentDrain,
            owner.Cancellation.Token);
    }

    private async Task<bool> TryRetryDrainAsync(Owner owner)
    {
        Task<bool>? retry;
        try
        {
            retry = _retryDrain(owner.Cancellation.Token);
        }
        catch
        {
            return false;
        }

        if (retry is null)
            return false;

        return await ObserveDrainResultAsync(
            retry,
            owner.Cancellation.Token);
    }

    private static async Task<bool> ObserveDrainResultAsync(
        Task<bool> drain,
        CancellationToken ownerCancellation)
    {
        try
        {
            return await drain;
        }
        catch (OperationCanceledException)
            when (!ownerCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private SessionDrainRecoveryOutcome? ResolveStoppedOutcome(
        Owner owner)
    {
        OwnerStopReason stopReason = owner.StopReason;
        if (stopReason == OwnerStopReason.Canceled)
            return SessionDrainRecoveryOutcome.Canceled;
        if (stopReason == OwnerStopReason.Superseded)
            return SessionDrainRecoveryOutcome.Superseded;

        lock (_gate)
        {
            if (!ReferenceEquals(owner, _latestOwner)
                || owner.Ticket != _latestTicket)
            {
                return SessionDrainRecoveryOutcome.Superseded;
            }
        }

        try
        {
            return _canContinue(owner.Ticket)
                ? null
                : SessionDrainRecoveryOutcome.Superseded;
        }
        catch
        {
            return SessionDrainRecoveryOutcome.Superseded;
        }
    }

    private enum OwnerStopReason
    {
        None,
        Superseded,
        Canceled,
    }

    private sealed class Owner
    {
        private readonly object _lifetimeGate = new();
        private int _stopReason;
        private bool _finished;

        internal Owner(long ticket)
        {
            Ticket = ticket;
            Completion = new TaskCompletionSource<SessionDrainRecoveryOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Cancellation = new CancellationTokenSource();
        }

        internal long Ticket { get; }

        internal TaskCompletionSource<SessionDrainRecoveryOutcome> Completion { get; }

        internal CancellationTokenSource Cancellation { get; }

        internal OwnerStopReason StopReason =>
            (OwnerStopReason)Volatile.Read(ref _stopReason);

        internal void RequestStop(OwnerStopReason reason)
        {
            if (reason == OwnerStopReason.None)
                throw new ArgumentOutOfRangeException(nameof(reason));

            lock (_lifetimeGate)
            {
                if (_finished
                    || Interlocked.CompareExchange(
                        ref _stopReason,
                        (int)reason,
                        (int)OwnerStopReason.None)
                    != (int)OwnerStopReason.None)
                {
                    return;
                }

                Cancellation.Cancel();
            }
        }

        internal void Complete(SessionDrainRecoveryOutcome outcome)
        {
            lock (_lifetimeGate)
            {
                if (_finished)
                    return;

                _finished = true;
                Cancellation.Dispose();
            }

            Completion.TrySetResult(outcome);
        }
    }
}
