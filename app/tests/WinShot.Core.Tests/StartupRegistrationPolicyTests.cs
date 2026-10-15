using WinShot.Core.Startup;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class StartupRegistrationPolicyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UnconfiguredPreference_NeverMutatesRegistration(
        bool registrationExists,
        bool registrationMatches)
    {
        StartupRegistrationAction action = StartupRegistrationPolicy.Decide(
            StartupPreference.Unconfigured,
            registrationExists,
            registrationMatches);

        Assert.Equal(StartupRegistrationAction.None, action);
    }

    [Theory]
    [InlineData(false, false, StartupRegistrationAction.None)]
    [InlineData(true, false, StartupRegistrationAction.None)]
    [InlineData(true, true, StartupRegistrationAction.Remove)]
    public void DisabledPreference_RemovesOnlyMatchingOwnedRegistration(
        bool registrationExists,
        bool registrationMatches,
        StartupRegistrationAction expected)
    {
        StartupRegistrationAction action = StartupRegistrationPolicy.Decide(
            StartupPreference.Disabled,
            registrationExists,
            registrationMatches);

        Assert.Equal(expected, action);
    }

    [Fact]
    public void DisabledPreference_PreservesMismatchedSameNameRegistration()
    {
        StartupRegistrationAction action = StartupRegistrationPolicy.Decide(
            StartupPreference.Disabled,
            registrationExists: true,
            registrationMatches: false);

        Assert.Equal(StartupRegistrationAction.None, action);
    }

    [Theory]
    [InlineData(false, false, StartupRegistrationAction.WriteExpected)]
    [InlineData(true, false, StartupRegistrationAction.WriteExpected)]
    [InlineData(true, true, StartupRegistrationAction.None)]
    public void EnabledPreference_RepairsOnlyMissingOrMismatchedRegistration(
        bool registrationExists,
        bool registrationMatches,
        StartupRegistrationAction expected)
    {
        StartupRegistrationAction action = StartupRegistrationPolicy.Decide(
            StartupPreference.Enabled,
            registrationExists,
            registrationMatches);

        Assert.Equal(expected, action);
    }

    [Fact]
    public void UnknownPreference_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StartupRegistrationPolicy.Decide(
                (StartupPreference)99,
                registrationExists: false,
                registrationMatches: false));
    }

    [Fact]
    public void MissingRegistrationCannotBeReportedAsMatching()
    {
        Assert.Throws<ArgumentException>(
            () => StartupRegistrationPolicy.Decide(
                StartupPreference.Enabled,
                registrationExists: false,
                registrationMatches: true));
    }
}
