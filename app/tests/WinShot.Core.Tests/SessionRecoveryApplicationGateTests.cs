using WinShot.App.Lifecycle;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class SessionRecoveryApplicationGateTests
{
    [Fact]
    public void RepeatedRecoveredWaitersRestoreOnlyOnce()
    {
        var gate = new SessionRecoveryApplicationGate();

        Assert.Equal(
            SessionRecoveryApplicationAction.RestoreCaptureAndHotkeys,
            gate.Evaluate(
                SessionDrainRecoveryOutcome.Recovered,
                recoveryTicket: 4,
                latestTransitionTicket: 4,
                stableDrain: true,
                canContinue: true));
        Assert.Equal(
            SessionRecoveryApplicationAction.None,
            gate.Evaluate(
                SessionDrainRecoveryOutcome.Recovered,
                recoveryTicket: 4,
                latestTransitionTicket: 4,
                stableDrain: true,
                canContinue: true));
    }

    [Fact]
    public void FailedFinalStableDrainDoesNotConsumeLaterSuccessfulRestore()
    {
        var gate = new SessionRecoveryApplicationGate();

        Assert.Equal(
            SessionRecoveryApplicationAction.None,
            gate.Evaluate(
                SessionDrainRecoveryOutcome.Recovered,
                recoveryTicket: 7,
                latestTransitionTicket: 7,
                stableDrain: false,
                canContinue: true));
        Assert.Equal(
            SessionRecoveryApplicationAction.RestoreCaptureAndHotkeys,
            gate.Evaluate(
                SessionDrainRecoveryOutcome.Recovered,
                recoveryTicket: 7,
                latestTransitionTicket: 7,
                stableDrain: true,
                canContinue: true));
    }

    [Fact]
    public void NonterminalOutcomesNeverRestoreOrNotify()
    {
        var gate = new SessionRecoveryApplicationGate();

        Assert.Equal(
            SessionRecoveryApplicationAction.None,
            gate.Evaluate(
                SessionDrainRecoveryOutcome.Superseded,
                recoveryTicket: 2,
                latestTransitionTicket: 2,
                stableDrain: true,
                canContinue: true));
        Assert.Equal(
            SessionRecoveryApplicationAction.None,
            gate.Evaluate(
                SessionDrainRecoveryOutcome.Canceled,
                recoveryTicket: 2,
                latestTransitionTicket: 2,
                stableDrain: true,
                canContinue: true));
    }

    [Fact]
    public void StaleOrTerminalResultNeverRestoresOrNotifies()
    {
        var gate = new SessionRecoveryApplicationGate();

        Assert.Equal(
            SessionRecoveryApplicationAction.None,
            gate.Evaluate(
                SessionDrainRecoveryOutcome.Recovered,
                recoveryTicket: 2,
                latestTransitionTicket: 3,
                stableDrain: true,
                canContinue: true));
        Assert.Equal(
            SessionRecoveryApplicationAction.None,
            gate.Evaluate(
                SessionDrainRecoveryOutcome.Exhausted,
                recoveryTicket: 3,
                latestTransitionTicket: 3,
                stableDrain: false,
                canContinue: false));
    }

    [Fact]
    public void ExhaustionNotifiesOncePerUnavailableEpoch()
    {
        var gate = new SessionRecoveryApplicationGate();

        Assert.Equal(
            SessionRecoveryApplicationAction.NotifyExhaustion,
            gate.Evaluate(
                SessionDrainRecoveryOutcome.Exhausted,
                recoveryTicket: 5,
                latestTransitionTicket: 5,
                stableDrain: false,
                canContinue: true));
        Assert.Equal(
            SessionRecoveryApplicationAction.None,
            gate.Evaluate(
                SessionDrainRecoveryOutcome.Exhausted,
                recoveryTicket: 5,
                latestTransitionTicket: 5,
                stableDrain: false,
                canContinue: true));
        Assert.Equal(
            SessionRecoveryApplicationAction.NotifyExhaustion,
            gate.Evaluate(
                SessionDrainRecoveryOutcome.Exhausted,
                recoveryTicket: 8,
                latestTransitionTicket: 8,
                stableDrain: false,
                canContinue: true));
    }

    [Fact]
    public void InvalidTicketIsRejected()
    {
        var gate = new SessionRecoveryApplicationGate();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            gate.Evaluate(
                SessionDrainRecoveryOutcome.Recovered,
                recoveryTicket: 0,
                latestTransitionTicket: 0,
                stableDrain: true,
                canContinue: true));
    }
}
