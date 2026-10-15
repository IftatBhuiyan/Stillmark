using System.Text.Json;
using WinShot.App;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class CrashRecoveryGuardTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "WinShotRecoveryTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Evaluate_DeniesFourthRecoveredStartWithinWindow()
    {
        string path = Path.Combine(_directory, "state.json");
        DateTimeOffset start = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

        Assert.True(CrashRecoveryGuard.Evaluate(true, start, path).Allowed);
        Assert.True(CrashRecoveryGuard.Evaluate(true, start.AddMinutes(1), path).Allowed);
        Assert.True(CrashRecoveryGuard.Evaluate(true, start.AddMinutes(2), path).Allowed);
        CrashRecoveryDecision fourth =
            CrashRecoveryGuard.Evaluate(true, start.AddMinutes(3), path);

        Assert.False(fourth.Allowed);
        Assert.Equal(4, fourth.AttemptCount);
        Assert.Equal(
            CrashRecoveryDenialReason.AttemptLimitReached,
            fourth.DenialReason);
    }

    [Fact]
    public void Evaluate_NewWindowAndNormalLaunchResetTheCounter()
    {
        string path = Path.Combine(_directory, "state.json");
        DateTimeOffset start = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 4; i++)
            CrashRecoveryGuard.Evaluate(true, start.AddMinutes(i), path);

        CrashRecoveryDecision later =
            CrashRecoveryGuard.Evaluate(true, start.AddMinutes(11), path);
        CrashRecoveryDecision normal =
            CrashRecoveryGuard.Evaluate(false, start.AddMinutes(12), path);
        CrashRecoveryDecision recoveredAfterNormal =
            CrashRecoveryGuard.Evaluate(true, start.AddMinutes(13), path);

        Assert.True(later.Allowed);
        Assert.Equal(1, later.AttemptCount);
        Assert.True(normal.Allowed);
        Assert.Equal(0, normal.AttemptCount);
        Assert.True(recoveredAfterNormal.Allowed);
        Assert.Equal(1, recoveredAfterNormal.AttemptCount);
        Assert.Equal(
            CrashRecoveryDenialReason.None,
            recoveredAfterNormal.DenialReason);
    }

    [Fact]
    public void Evaluate_CorruptStateFailsOpenAsFirstAttempt()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "state.json");
        File.WriteAllText(path, "{not-json");

        CrashRecoveryDecision decision =
            CrashRecoveryGuard.Evaluate(true, DateTimeOffset.UtcNow, path);

        Assert.True(decision.Allowed);
        Assert.Equal(1, decision.AttemptCount);
        Assert.Equal(CrashRecoveryDenialReason.None, decision.DenialReason);
    }

    [Fact]
    public void Evaluate_OversizedStateFailsOpenAndWritesBoundedReplacement()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "state.json");
        File.WriteAllBytes(path, new byte[CrashRecoveryGuard.MaximumStateBytes + 1]);

        CrashRecoveryDecision decision =
            CrashRecoveryGuard.Evaluate(true, DateTimeOffset.UtcNow, path);

        Assert.True(decision.Allowed);
        Assert.Equal(1, decision.AttemptCount);
        Assert.InRange(
            new FileInfo(path).Length,
            1,
            CrashRecoveryGuard.MaximumStateBytes);
        Assert.Equal(CrashRecoveryDenialReason.None, decision.DenialReason);
    }

    [Fact]
    public void Evaluate_NonPersistableRecoveredStateFailsClosedEveryTime()
    {
        Directory.CreateDirectory(_directory);
        string parentFile = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(parentFile, "synthetic fixture");
        string path = Path.Combine(parentFile, "state.json");
        DateTimeOffset start = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

        CrashRecoveryDecision first =
            CrashRecoveryGuard.Evaluate(true, start, path);
        CrashRecoveryDecision second =
            CrashRecoveryGuard.Evaluate(true, start.AddMinutes(1), path);

        Assert.False(first.Allowed);
        Assert.Equal(1, first.AttemptCount);
        Assert.Equal(
            CrashRecoveryDenialReason.StatePersistenceUnavailable,
            first.DenialReason);
        Assert.False(second.Allowed);
        Assert.Equal(1, second.AttemptCount);
        Assert.Equal(
            CrashRecoveryDenialReason.StatePersistenceUnavailable,
            second.DenialReason);
    }

    [Fact]
    public void Evaluate_MaximumPersistedAttemptRemainsSaturatedAndDenied()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "state.json");
        DateTimeOffset start = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                WindowStartedUtc = start,
                AttemptCount = int.MaxValue,
            }));

        CrashRecoveryDecision first =
            CrashRecoveryGuard.Evaluate(true, start.AddMinutes(1), path);
        CrashRecoveryDecision second =
            CrashRecoveryGuard.Evaluate(true, start.AddMinutes(2), path);

        Assert.False(first.Allowed);
        Assert.Equal(int.MaxValue, first.AttemptCount);
        Assert.Equal(
            CrashRecoveryDenialReason.AttemptLimitReached,
            first.DenialReason);
        Assert.False(second.Allowed);
        Assert.Equal(int.MaxValue, second.AttemptCount);
        Assert.Equal(
            CrashRecoveryDenialReason.AttemptLimitReached,
            second.DenialReason);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
