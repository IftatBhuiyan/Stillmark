using WinShot.App.Lifecycle;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class SessionTransitionTicketSourceTests
{
    [Fact]
    public void RepeatedRecoveryEventsShareOneTicket()
    {
        var source = new SessionTransitionTicketSource();

        long first = source.GetOrCreateRecoveryTicket();

        Assert.Equal(1, first);
        Assert.Equal(first, source.GetOrCreateRecoveryTicket());
        Assert.Equal(first, source.GetOrCreateRecoveryTicket());
        Assert.Equal(first, source.LatestTicket);
    }

    [Fact]
    public void NewInterruptionInvalidatesPriorRecoveryAndStartsOneNewRecoveryEpoch()
    {
        var source = new SessionTransitionTicketSource();
        long priorRecovery = source.GetOrCreateRecoveryTicket();

        long interruption = source.BeginInterruption();
        long nextRecovery = source.GetOrCreateRecoveryTicket();

        Assert.Equal(priorRecovery + 1, interruption);
        Assert.Equal(interruption + 1, nextRecovery);
        Assert.Equal(nextRecovery, source.GetOrCreateRecoveryTicket());
        Assert.Equal(nextRecovery, source.LatestTicket);
    }

    [Fact]
    public async Task ConcurrentRecoveryEventsCollapseToOneMonotonicTicket()
    {
        var source = new SessionTransitionTicketSource();
        using var start = new ManualResetEventSlim();

        Task<long>[] requests = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return source.GetOrCreateRecoveryTicket();
            }))
            .ToArray();

        start.Set();
        long[] tickets = await Task.WhenAll(requests);

        Assert.All(tickets, ticket => Assert.Equal(1, ticket));
        Assert.Equal(1, source.LatestTicket);
    }

    [Fact]
    public void AlternatingTransitionsRemainStrictlyMonotonic()
    {
        var source = new SessionTransitionTicketSource();

        long recoveryOne = source.GetOrCreateRecoveryTicket();
        long interruptionOne = source.BeginInterruption();
        long interruptionTwo = source.BeginInterruption();
        long recoveryTwo = source.GetOrCreateRecoveryTicket();

        Assert.Equal([1L, 2L, 3L, 4L],
            [recoveryOne, interruptionOne, interruptionTwo, recoveryTwo]);
    }
}
