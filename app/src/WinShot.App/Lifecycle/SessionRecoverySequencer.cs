namespace WinShot.App.Lifecycle;

/// <summary>
/// Prevents session recovery from overtaking an active-work interruption. If another interruption
/// replaces the observed task while it is draining, recovery waits for the replacement too.
/// </summary>
internal static class SessionRecoverySequencer
{
    internal static async Task<bool> WaitForStableDrainAsync(
        Func<Task<bool>?> snapshotLatestDrain)
    {
        ArgumentNullException.ThrowIfNull(snapshotLatestDrain);

        while (true)
        {
            Task<bool>? observed = snapshotLatestDrain();
            if (observed is not null &&
                !await observed.ConfigureAwait(false))
            {
                return false;
            }

            if (ReferenceEquals(observed, snapshotLatestDrain()))
                return true;
        }
    }

    internal static bool CanEnableRecovery(
        long recoveryTicket,
        long latestTransitionTicket,
        Task<bool>? latestDrain)
    {
        return recoveryTicket == latestTransitionTicket &&
            (latestDrain is null ||
                (latestDrain.IsCompletedSuccessfully && latestDrain.Result));
    }
}
