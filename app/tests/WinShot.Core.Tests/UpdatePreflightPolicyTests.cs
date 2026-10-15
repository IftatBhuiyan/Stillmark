using WinShot.Core.Update;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class UpdatePreflightPolicyTests
{
    [Theory]
    [InlineData(1, 0, 0, 1, 0, 1, 1, UpdatePreflightStatus.Ready)]
    [InlineData(1, 4, 9, 1, 4, 9, 1, UpdatePreflightStatus.NotNewer)]
    [InlineData(2, 0, 0, 1, 9, 9, 2, UpdatePreflightStatus.NotNewer)]
    [InlineData(1, 9, 9, 2, 0, 0, 1, UpdatePreflightStatus.CandidateMajorNotEntitled)]
    [InlineData(2, 0, 0, 2, 0, 1, 1, UpdatePreflightStatus.CurrentVersionNotEntitled)]
    public void VersionAndEntitlementPreflight_FailsClosed(
        int currentMajor,
        int currentMinor,
        int currentBuild,
        int candidateMajor,
        int candidateMinor,
        int candidateBuild,
        int entitledMajor,
        UpdatePreflightStatus expectedStatus)
    {
        UpdatePreflightDecision decision = UpdatePreflightPolicy.Evaluate(
            new UpdatePreflightInput(
                new Version(currentMajor, currentMinor, currentBuild),
                new Version(candidateMajor, candidateMinor, candidateBuild),
                entitledMajor));

        Assert.Equal(expectedStatus, decision.Status);
        Assert.Equal(expectedStatus == UpdatePreflightStatus.Ready, decision.CanStage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveEntitledMajor_IsRejected(int entitledMajor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UpdatePreflightPolicy.Evaluate(
                new UpdatePreflightInput(
                    new Version(1, 0, 0),
                    new Version(1, 0, 1),
                    entitledMajor)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void NonPositivePaidMajorVersion_IsRejected(int currentMajor, int candidateMajor)
    {
        Assert.Throws<ArgumentException>(
            () => UpdatePreflightPolicy.Evaluate(
                new UpdatePreflightInput(
                    new Version(currentMajor, 0, 0),
                    new Version(candidateMajor, 0, 1),
                    EntitledMajor: 1)));
    }
}
