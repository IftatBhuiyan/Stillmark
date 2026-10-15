namespace WinShot.App.Lifecycle;

/// <summary>
/// Publishes one monotonic ticket per interruption and one shared recovery ticket for every
/// subsequent usable-session event until another interruption occurs.
/// </summary>
internal sealed class SessionTransitionTicketSource
{
    private readonly object _gate = new();
    private long _latestTicket;
    private bool _recoveryTicketIssued;

    internal long LatestTicket => Volatile.Read(ref _latestTicket);

    internal long BeginInterruption()
    {
        lock (_gate)
        {
            _recoveryTicketIssued = false;
            return ++_latestTicket;
        }
    }

    internal long GetOrCreateRecoveryTicket()
    {
        lock (_gate)
        {
            if (!_recoveryTicketIssued)
            {
                _latestTicket++;
                _recoveryTicketIssued = true;
            }

            return _latestTicket;
        }
    }
}
