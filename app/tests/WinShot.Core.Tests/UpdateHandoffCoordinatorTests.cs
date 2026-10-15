using System.Diagnostics;
using WinShot.Core.Update;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class UpdateHandoffCoordinatorTests : IDisposable
{
    private readonly string _temporaryDirectory;

    public UpdateHandoffCoordinatorTests()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"winshot-handoff-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public async Task AcceptedCommit_RequestsShutdownAndRetainsQuiescenceUntilExit()
    {
        var trace = new List<string>();
        var ledger = new UpdateActivityLedger();
        var restart = new RecordingRestartController(trace, registered: true);
        var session = new RecordingChildSession(trace);
        var launcher = new RecordingLauncher(trace, session);
        var shutdown = new RecordingShutdownRequester(trace);
        await using var coordinator = CreateCoordinator(
            ledger,
            restart,
            launcher,
            shutdown,
            trace);
        using VerifiedUpdateCandidate candidate = CreateCandidate();

        UpdateHandoffResult result =
            await coordinator.TryHandoffAsync(candidate);

        Assert.Equal(UpdateHandoffStatus.ShutdownRequested, result.Status);
        Assert.True(result.ShutdownWasRequested);
        Assert.True(result.SafetyHoldRetained);
        Assert.False(result.RecoveryRestored);
        Assert.True(coordinator.IsSafetyHoldRetained);
        Assert.False(
            ledger.TryBeginActivity(
                UpdateActivityKind.Capture,
                out UpdateActivityLease? blocked));
        Assert.Null(blocked);
        Assert.Equal(
            [
                "eligibility.acquire",
                "restart.unregister",
                "launcher.launch",
                "child.ready",
                "child.commit",
                "shutdown.request",
            ],
            trace);

        await coordinator.DisposeAsync();
        Assert.True(
            ledger.TryBeginActivity(
                UpdateActivityKind.Capture,
                out UpdateActivityLease? admitted));
        admitted.Dispose();
    }

    [Fact]
    public async Task ActiveWork_RefusesBeforeEligibilityAndLeavesCandidateUnconsumed()
    {
        var trace = new List<string>();
        var ledger = new UpdateActivityLedger();
        using UpdateActivityLease activity =
            ledger.BeginActivity(UpdateActivityKind.Recording);
        var restart = new RecordingRestartController(trace, registered: true);
        var launcher = new RecordingLauncher(
            trace,
            new RecordingChildSession(trace));
        await using var coordinator = CreateCoordinator(
            ledger,
            restart,
            launcher,
            new RecordingShutdownRequester(trace),
            trace);
        using VerifiedUpdateCandidate candidate = CreateCandidate();

        UpdateHandoffResult result =
            await coordinator.TryHandoffAsync(candidate);

        Assert.Equal(UpdateHandoffStatus.ActivityInProgress, result.Status);
        Assert.True(result.CandidateConsumed);
        Assert.Empty(trace);
        Assert.True(restart.IsRegistered);
        Assert.False(coordinator.IsSafetyHoldRetained);
    }

    [Theory]
    [InlineData("other-package", "stable", "win-x64")]
    [InlineData("winshot", "beta", "win-x64")]
    [InlineData("winshot", "stable", "win-arm64")]
    public async Task IdentityDrift_RefusesBeforeRecoveryOrLaunch(
        string packageId,
        string channel,
        string runtimeIdentifier)
    {
        var trace = new List<string>();
        var ledger = new UpdateActivityLedger();
        var restart = new RecordingRestartController(trace, registered: true);
        var launcher = new RecordingLauncher(
            trace,
            new RecordingChildSession(trace));
        var eligibility = new RecordingEligibilityGate(
            trace,
            new UpdateHandoffEligibility(
                new Version(1, 0, 0),
                EntitledMajor: 1,
                packageId,
                channel,
                runtimeIdentifier));
        await using var coordinator = new UpdateHandoffCoordinator(
            ledger,
            eligibility,
            restart,
            launcher,
            new RecordingShutdownRequester(trace),
            TimeSpan.FromSeconds(1));
        using VerifiedUpdateCandidate candidate = CreateCandidate();

        UpdateHandoffResult result =
            await coordinator.TryHandoffAsync(candidate);

        Assert.Equal(UpdateHandoffStatus.EligibilityChanged, result.Status);
        Assert.Equal(["eligibility.acquire", "eligibility.dispose"], trace);
        Assert.True(restart.IsRegistered);
        Assert.True(
            ledger.TryBeginActivity(
                UpdateActivityKind.Capture,
                out UpdateActivityLease? admitted));
        admitted.Dispose();
    }

    [Fact]
    public async Task UnregisterFailure_NeverLaunchesAndReleasesQuiescence()
    {
        var trace = new List<string>();
        var ledger = new UpdateActivityLedger();
        var restart = new RecordingRestartController(
            trace,
            registered: true,
            unregisterSucceeds: false);
        var launcher = new RecordingLauncher(
            trace,
            new RecordingChildSession(trace));
        await using var coordinator = CreateCoordinator(
            ledger,
            restart,
            launcher,
            new RecordingShutdownRequester(trace),
            trace);
        using VerifiedUpdateCandidate candidate = CreateCandidate();

        UpdateHandoffResult result =
            await coordinator.TryHandoffAsync(candidate);

        Assert.Equal(
            UpdateHandoffStatus.ApplicationRestartUnregisterFailed,
            result.Status);
        Assert.Equal(
            [
                "eligibility.acquire",
                "restart.unregister",
                "restart.register",
                "eligibility.dispose",
            ],
            trace);
        Assert.True(restart.IsRegistered);
        Assert.True(
            ledger.TryBeginActivity(
                UpdateActivityKind.Recording,
                out UpdateActivityLease? admitted));
        admitted.Dispose();
    }

    [Fact]
    public async Task LaunchFailure_RestoresRecoveryBeforeReopeningAdmission()
    {
        var trace = new List<string>();
        var ledger = new UpdateActivityLedger();
        var restart = new RecordingRestartController(trace, registered: true);
        var launcher = new RecordingLauncher(trace, session: null);
        await using var coordinator = CreateCoordinator(
            ledger,
            restart,
            launcher,
            new RecordingShutdownRequester(trace),
            trace);
        using VerifiedUpdateCandidate candidate = CreateCandidate();

        UpdateHandoffResult result =
            await coordinator.TryHandoffAsync(candidate);

        Assert.Equal(UpdateHandoffStatus.LaunchFailed, result.Status);
        Assert.True(result.RecoveryRestored);
        Assert.Equal(
            [
                "eligibility.acquire",
                "restart.unregister",
                "launcher.launch",
                "restart.register",
                "eligibility.dispose",
            ],
            trace);
        Assert.True(
            ledger.TryBeginActivity(
                UpdateActivityKind.Transcode,
                out UpdateActivityLease? admitted));
        admitted.Dispose();
    }

    [Fact]
    public async Task RecoveryRestoreFailure_IsReportedWithoutForcingShutdown()
    {
        var trace = new List<string>();
        var ledger = new UpdateActivityLedger();
        var restart = new RecordingRestartController(
            trace,
            registered: true,
            registerSucceeds: false);
        var shutdown = new RecordingShutdownRequester(trace);
        await using var coordinator = CreateCoordinator(
            ledger,
            restart,
            new RecordingLauncher(trace, session: null),
            shutdown,
            trace);
        using VerifiedUpdateCandidate candidate = CreateCandidate();

        UpdateHandoffResult result =
            await coordinator.TryHandoffAsync(candidate);

        Assert.Equal(UpdateHandoffStatus.LaunchFailed, result.Status);
        Assert.False(result.RecoveryRestored);
        Assert.False(result.SafetyHoldRetained);
        Assert.Equal(0, shutdown.CallCount);
        Assert.True(
            ledger.TryBeginActivity(
                UpdateActivityKind.Capture,
                out UpdateActivityLease? admitted));
        admitted.Dispose();
    }

    [Fact]
    public async Task LauncherException_IsIndeterminateAndRetainsSafetyHold()
    {
        var trace = new List<string>();
        var ledger = new UpdateActivityLedger();
        var restart = new RecordingRestartController(trace, registered: true);
        await using var coordinator = new UpdateHandoffCoordinator(
            ledger,
            new RecordingEligibilityGate(
                trace,
                new UpdateHandoffEligibility(
                    new Version(1, 0, 0),
                    EntitledMajor: 1,
                    PackageId: "winshot",
                    Channel: "stable",
                    RuntimeIdentifier: "win-x64")),
            restart,
            new ThrowingLauncher(trace),
            new RecordingShutdownRequester(trace),
            TimeSpan.FromSeconds(1));
        using VerifiedUpdateCandidate candidate = CreateCandidate();

        UpdateHandoffResult result =
            await coordinator.TryHandoffAsync(candidate);

        Assert.Equal(UpdateHandoffStatus.LaunchIndeterminate, result.Status);
        Assert.True(result.SafetyHoldRetained);
        Assert.False(result.RecoveryRestored);
        Assert.False(restart.IsRegistered);
        Assert.DoesNotContain("restart.register", trace);
        Assert.False(
            ledger.TryBeginActivity(
                UpdateActivityKind.Capture,
                out UpdateActivityLease? blocked));
        Assert.Null(blocked);
    }

    [Fact]
    public async Task PreCancelledRequest_DoesNotTouchEligibilityOrRecovery()
    {
        var trace = new List<string>();
        var ledger = new UpdateActivityLedger();
        var restart = new RecordingRestartController(trace, registered: true);
        await using var coordinator = CreateCoordinator(
            ledger,
            restart,
            new RecordingLauncher(
                trace,
                new RecordingChildSession(trace)),
            new RecordingShutdownRequester(trace),
            trace);
        using VerifiedUpdateCandidate candidate = CreateCandidate();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        UpdateHandoffResult result = await coordinator.TryHandoffAsync(
            candidate,
            cancellation.Token);

        Assert.Equal(UpdateHandoffStatus.Cancelled, result.Status);
        Assert.Empty(trace);
        Assert.True(restart.IsRegistered);
        Assert.False(coordinator.IsSafetyHoldRetained);
    }

    [Theory]
    [InlineData(UpdateChildReadyStatus.Rejected)]
    [InlineData(UpdateChildReadyStatus.ChildExited)]
    [InlineData(UpdateChildReadyStatus.InvalidAcknowledgement)]
    public async Task ReadyRefusal_AbortsChildThenRestoresRecovery(
        UpdateChildReadyStatus readyStatus)
    {
        var trace = new List<string>();
        var ledger = new UpdateActivityLedger();
        var restart = new RecordingRestartController(trace, registered: true);
        var session = new RecordingChildSession(trace, readyStatus);
        await using var coordinator = CreateCoordinator(
            ledger,
            restart,
            new RecordingLauncher(trace, session),
            new RecordingShutdownRequester(trace),
            trace);
        using VerifiedUpdateCandidate candidate = CreateCandidate();

        UpdateHandoffResult result =
            await coordinator.TryHandoffAsync(candidate);

        Assert.Equal(UpdateHandoffStatus.ReadyRejected, result.Status);
        Assert.True(result.RecoveryRestored);
        Assert.Equal(
            [
                "eligibility.acquire",
                "restart.unregister",
                "launcher.launch",
                "child.ready",
                "child.abort",
                "restart.register",
                "child.dispose",
                "eligibility.dispose",
            ],
            trace);
        Assert.False(coordinator.IsSafetyHoldRetained);
    }

    [Fact]
    public async Task ReadyTimeout_AbortsAndKeepsResidentUsable()
    {
        var trace = new List<string>();
        var ledger = new UpdateActivityLedger();
        var restart = new RecordingRestartController(trace, registered: true);
        var session = new RecordingChildSession(
            trace,
            readyTask: new TaskCompletionSource<UpdateChildReadyStatus>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task);
        await using var coordinator = CreateCoordinator(
            ledger,
            restart,
            new RecordingLauncher(trace, session),
            new RecordingShutdownRequester(trace),
            trace,
            timeout: TimeSpan.FromMilliseconds(25));
        using VerifiedUpdateCandidate candidate = CreateCandidate();

        UpdateHandoffResult result =
            await coordinator.TryHandoffAsync(candidate);

        Assert.Equal(UpdateHandoffStatus.ReadyTimedOut, result.Status);
        Assert.True(result.RecoveryRestored);
        Assert.Contains("child.abort", trace);
        Assert.True(
            ledger.TryBeginActivity(
                UpdateActivityKind.Capture,
                out UpdateActivityLease? admitted));
        admitted.Dispose();
    }

    [Fact]
    public async Task UnconfirmedAbort_RetainsSafetyHoldAndDoesNotRestoreRecovery()
    {
        var trace = new List<string>();
        var ledger = new UpdateActivityLedger();
        var restart = new RecordingRestartController(trace, registered: true);
        var session = new RecordingChildSession(
            trace,
            readyStatus: UpdateChildReadyStatus.Rejected,
            abortStatus: UpdateChildAbortStatus.Unconfirmed);
        await using var coordinator = CreateCoordinator(
            ledger,
            restart,
            new RecordingLauncher(trace, session),
            new RecordingShutdownRequester(trace),
            trace);
        using VerifiedUpdateCandidate candidate = CreateCandidate();

        UpdateHandoffResult result =
            await coordinator.TryHandoffAsync(candidate);

        Assert.Equal(UpdateHandoffStatus.AbortUnconfirmed, result.Status);
        Assert.True(result.SafetyHoldRetained);
        Assert.False(result.RecoveryRestored);
        Assert.False(restart.IsRegistered);
        Assert.DoesNotContain("restart.register", trace);
        Assert.False(
            ledger.TryBeginActivity(
                UpdateActivityKind.Capture,
                out UpdateActivityLease? blocked));
        Assert.Null(blocked);
    }

    [Fact]
    public async Task CommitRefusal_AbortsAndNeverRequestsShutdown()
    {
        var trace = new List<string>();
        var ledger = new UpdateActivityLedger();
        var restart = new RecordingRestartController(trace, registered: true);
        var session = new RecordingChildSession(
            trace,
            commitStatus: UpdateChildCommitStatus.Rejected);
        var shutdown = new RecordingShutdownRequester(trace);
        await using var coordinator = CreateCoordinator(
            ledger,
            restart,
            new RecordingLauncher(trace, session),
            shutdown,
            trace);
        using VerifiedUpdateCandidate candidate = CreateCandidate();

        UpdateHandoffResult result =
            await coordinator.TryHandoffAsync(candidate);

        Assert.Equal(UpdateHandoffStatus.CommitRejected, result.Status);
        Assert.True(result.RecoveryRestored);
        Assert.Equal(0, shutdown.CallCount);
        Assert.Contains("child.abort", trace);
    }

    [Fact]
    public async Task CommitTimeout_IsIndeterminateAndRetainsSafetyHold()
    {
        var trace = new List<string>();
        var ledger = new UpdateActivityLedger();
        var restart = new RecordingRestartController(trace, registered: true);
        var session = new RecordingChildSession(
            trace,
            commitTask: new TaskCompletionSource<UpdateChildCommitStatus>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var shutdown = new RecordingShutdownRequester(trace);
        await using var coordinator = CreateCoordinator(
            ledger,
            restart,
            new RecordingLauncher(trace, session),
            shutdown,
            trace,
            timeout: TimeSpan.FromMilliseconds(25));
        using VerifiedUpdateCandidate candidate = CreateCandidate();

        UpdateHandoffResult result =
            await coordinator.TryHandoffAsync(candidate);

        Assert.Equal(UpdateHandoffStatus.CommitIndeterminate, result.Status);
        Assert.True(result.SafetyHoldRetained);
        Assert.False(result.RecoveryRestored);
        Assert.Equal(0, shutdown.CallCount);
        Assert.DoesNotContain("child.abort", trace);
        Assert.DoesNotContain("restart.register", trace);
    }

    [Fact]
    public async Task DuplicateRequest_LosesWithoutConsumingItsCandidate()
    {
        var trace = new List<string>();
        var ledger = new UpdateActivityLedger();
        var restart = new RecordingRestartController(trace, registered: true);
        var readySource =
            new TaskCompletionSource<UpdateChildReadyStatus>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new RecordingChildSession(
            trace,
            readyTask: readySource.Task);
        await using var coordinator = CreateCoordinator(
            ledger,
            restart,
            new RecordingLauncher(trace, session),
            new RecordingShutdownRequester(trace),
            trace,
            timeout: TimeSpan.FromSeconds(2));
        using VerifiedUpdateCandidate firstCandidate = CreateCandidate();
        using VerifiedUpdateCandidate losingCandidate = CreateCandidate("loser.exe");

        Task<UpdateHandoffResult> first =
            coordinator.TryHandoffAsync(firstCandidate).AsTask();
        await WaitForTraceAsync(trace, "child.ready");

        UpdateHandoffResult duplicate =
            await coordinator.TryHandoffAsync(losingCandidate);
        Assert.Equal(UpdateHandoffStatus.AlreadyInProgress, duplicate.Status);
        Assert.False(duplicate.CandidateConsumed);

        readySource.SetResult(UpdateChildReadyStatus.Rejected);
        UpdateHandoffResult completed = await first;
        Assert.Equal(UpdateHandoffStatus.ReadyRejected, completed.Status);
    }

    [Fact]
    public async Task EligibilityTimeout_ReleasesQuiescenceWithoutChangingRecovery()
    {
        var trace = new List<string>();
        var ledger = new UpdateActivityLedger();
        var restart = new RecordingRestartController(trace, registered: true);
        await using var coordinator = new UpdateHandoffCoordinator(
            ledger,
            new HangingEligibilityGate(trace),
            restart,
            new RecordingLauncher(
                trace,
                new RecordingChildSession(trace)),
            new RecordingShutdownRequester(trace),
            TimeSpan.FromMilliseconds(25));
        using VerifiedUpdateCandidate candidate = CreateCandidate();

        UpdateHandoffResult result =
            await coordinator.TryHandoffAsync(candidate);

        Assert.Equal(UpdateHandoffStatus.EligibilityUnavailable, result.Status);
        Assert.Equal(["eligibility.acquire"], trace);
        Assert.True(restart.IsRegistered);
        Assert.True(
            ledger.TryBeginActivity(
                UpdateActivityKind.Capture,
                out UpdateActivityLease? admitted));
        admitted.Dispose();
    }

    [Fact]
    public async Task LaunchTimeout_IsIndeterminateAndRetainsSafetyHold()
    {
        var trace = new List<string>();
        var ledger = new UpdateActivityLedger();
        var restart = new RecordingRestartController(trace, registered: true);
        await using var coordinator = new UpdateHandoffCoordinator(
            ledger,
            new RecordingEligibilityGate(
                trace,
                new UpdateHandoffEligibility(
                    new Version(1, 0, 0),
                    EntitledMajor: 1,
                    PackageId: "winshot",
                    Channel: "stable",
                    RuntimeIdentifier: "win-x64")),
            restart,
            new HangingLauncher(trace),
            new RecordingShutdownRequester(trace),
            TimeSpan.FromMilliseconds(25));
        using VerifiedUpdateCandidate candidate = CreateCandidate();

        UpdateHandoffResult result =
            await coordinator.TryHandoffAsync(candidate);

        Assert.Equal(UpdateHandoffStatus.LaunchIndeterminate, result.Status);
        Assert.True(result.SafetyHoldRetained);
        Assert.False(restart.IsRegistered);
        Assert.DoesNotContain("restart.register", trace);
        Assert.False(
            ledger.TryBeginActivity(
                UpdateActivityKind.Capture,
                out UpdateActivityLease? blocked));
        Assert.Null(blocked);
    }

    [Fact]
    public async Task DisposeDuringShutdownRequest_IsRejectedUntilMethodReturns()
    {
        var trace = new List<string>();
        var ledger = new UpdateActivityLedger();
        var restart = new RecordingRestartController(trace, registered: true);
        var shutdown = new BlockingShutdownRequester(trace);
        var coordinator = new UpdateHandoffCoordinator(
            ledger,
            new RecordingEligibilityGate(
                trace,
                new UpdateHandoffEligibility(
                    new Version(1, 0, 0),
                    EntitledMajor: 1,
                    PackageId: "winshot",
                    Channel: "stable",
                    RuntimeIdentifier: "win-x64")),
            restart,
            new RecordingLauncher(
                trace,
                new RecordingChildSession(trace)),
            shutdown,
            TimeSpan.FromSeconds(1));
        using VerifiedUpdateCandidate candidate = CreateCandidate();

        Task<UpdateHandoffResult> handoff = Task.Run(
            async () => await coordinator.TryHandoffAsync(candidate));
        Assert.True(shutdown.Entered.Wait(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.DisposeAsync().AsTask());

        shutdown.Release.Set();
        UpdateHandoffResult result = await handoff;
        Assert.Equal(UpdateHandoffStatus.ShutdownRequested, result.Status);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task MalformedLaunchResult_RetainsReturnedExactChildSession()
    {
        var trace = new List<string>();
        var ledger = new UpdateActivityLedger();
        var restart = new RecordingRestartController(trace, registered: true);
        var session = new RecordingChildSession(trace);
        var coordinator = new UpdateHandoffCoordinator(
            ledger,
            new RecordingEligibilityGate(
                trace,
                new UpdateHandoffEligibility(
                    new Version(1, 0, 0),
                    EntitledMajor: 1,
                    PackageId: "winshot",
                    Channel: "stable",
                    RuntimeIdentifier: "win-x64")),
            restart,
            new MalformedLauncher(trace, session),
            new RecordingShutdownRequester(trace),
            TimeSpan.FromSeconds(1));
        using VerifiedUpdateCandidate candidate = CreateCandidate();

        UpdateHandoffResult result =
            await coordinator.TryHandoffAsync(candidate);

        Assert.Equal(UpdateHandoffStatus.LaunchIndeterminate, result.Status);
        Assert.True(result.SafetyHoldRetained);
        Assert.Equal(0, session.DisposeCount);

        await coordinator.DisposeAsync();
        Assert.Equal(1, session.DisposeCount);
    }

    [Theory]
    [InlineData(-1, UpdateHandoffStatus.ShutdownRequested)]
    [InlineData(0, UpdateHandoffStatus.CandidateReleaseNotEntitled)]
    [InlineData(1, UpdateHandoffStatus.CandidateReleaseNotEntitled)]
    [InlineData(null, UpdateHandoffStatus.ReleaseDateMissing)]
    public async Task DatedEntitlement_IsRecheckedBeforeLaunchingUpdater(
        int? secondsFromCutoff, UpdateHandoffStatus expected)
    {
        var cutoff = new DateTimeOffset(2027, 9, 11, 12, 0, 0, TimeSpan.Zero);
        var trace = new List<string>();
        var restart = new RecordingRestartController(trace, registered: true);
        await using var coordinator = new UpdateHandoffCoordinator(
            new UpdateActivityLedger(),
            new RecordingEligibilityGate(trace, new UpdateHandoffEligibility(
                new Version(1, 0, 0), 1, "winshot", "stable", "win-x64",
                UpdatesUntilUtc: cutoff, CurrentReleasedAtUtc: cutoff.AddMonths(-6))),
            restart,
            new RecordingLauncher(trace, new RecordingChildSession(trace)),
            new RecordingShutdownRequester(trace),
            TimeSpan.FromSeconds(1));
        using var candidate = CreateCandidate(
            releasedAtUtc: secondsFromCutoff is { } seconds ? cutoff.AddSeconds(seconds) : null,
            version: new Version(2, 0, 0));

        var result = await coordinator.TryHandoffAsync(candidate);

        Assert.Equal(expected, result.Status);
        if (expected != UpdateHandoffStatus.ShutdownRequested)
        {
            Assert.DoesNotContain("child.launch", trace);
            Assert.True(restart.IsRegistered);
        }
    }

    private UpdateHandoffCoordinator CreateCoordinator(
        UpdateActivityLedger ledger,
        RecordingRestartController restart,
        RecordingLauncher launcher,
        RecordingShutdownRequester shutdown,
        List<string> trace,
        TimeSpan? timeout = null)
    {
        var eligibility = new RecordingEligibilityGate(
            trace,
            new UpdateHandoffEligibility(
                new Version(1, 0, 0),
                EntitledMajor: 1,
                PackageId: "winshot",
                Channel: "stable",
                RuntimeIdentifier: "win-x64"));
        return new UpdateHandoffCoordinator(
            ledger,
            eligibility,
            restart,
            launcher,
            shutdown,
            timeout ?? TimeSpan.FromSeconds(1));
    }

    private VerifiedUpdateCandidate CreateCandidate(
        string packageFileName = "WinShot-update.exe",
        DateTimeOffset? releasedAtUtc = null,
        Version? version = null)
    {
        string packagePath = Path.Combine(_temporaryDirectory, packageFileName);
        File.WriteAllText(packagePath, $"synthetic updater {Guid.NewGuid():N}");
        OpenedUpdateFile opened = UpdatePackageFileHandle.OpenReadNoReparse(
            packagePath,
            bufferSize: 4096);
        FileStream packageLease = Assert.IsType<FileStream>(opened.Stream);
        UpdatePackageFileIdentity identity =
            Assert.IsType<UpdatePackageFileIdentity>(opened.Identity);

        string manifestPath = Path.Combine(
            _temporaryDirectory,
            $"release-manifest-{Guid.NewGuid():N}.json");
        File.WriteAllText(manifestPath, "{}");
        var manifestLease = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var evidence = new ReleaseManifestEvidence(
            schemaVersion: 1,
            declaredFileCount: 1,
            declaredPayloadBytes: identity.FileLength,
            buildInputsSha256: new string('A', 64),
            packagingToolsSha256: new string('B', 64),
            commercialConfigurationSha256: new string('C', 64));
        return new VerifiedUpdateCandidate(
            version: version ?? new Version(1, 1, 0),
            packageId: "winshot",
            channel: "stable",
            runtimeIdentifier: "win-x64",
            packagePath,
            packageSha256: new string('D', 64),
            identity,
            releaseManifestSha256: new string('E', 64),
            evidence,
            verifiedAtUtc: DateTimeOffset.UtcNow,
            expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            verifiedTimestamp: Stopwatch.GetTimestamp(),
            capabilityLifetime: TimeSpan.FromMinutes(10),
            packageLease,
            manifestLease,
            releasedAtUtc: releasedAtUtc);
    }

    private static async Task WaitForTraceAsync(
        List<string> trace,
        string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!trace.Contains(expected))
            await Task.Delay(5, timeout.Token);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
            Directory.Delete(_temporaryDirectory, recursive: true);
    }

    private sealed class RecordingEligibilityGate
        : IUpdateHandoffEligibilityGate
    {
        private readonly List<string> _trace;
        private readonly UpdateHandoffEligibility _eligibility;

        internal RecordingEligibilityGate(
            List<string> trace,
            UpdateHandoffEligibility eligibility)
        {
            _trace = trace;
            _eligibility = eligibility;
        }

        public ValueTask<IUpdateHandoffEligibilityLease?> TryAcquireAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _trace.Add("eligibility.acquire");
            return ValueTask.FromResult<IUpdateHandoffEligibilityLease?>(
                new RecordingEligibilityLease(_trace, _eligibility));
        }
    }

    private sealed class HangingEligibilityGate
        : IUpdateHandoffEligibilityGate
    {
        private readonly List<string> _trace;
        private readonly TaskCompletionSource<IUpdateHandoffEligibilityLease?>
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal HangingEligibilityGate(List<string> trace)
        {
            _trace = trace;
        }

        public ValueTask<IUpdateHandoffEligibilityLease?> TryAcquireAsync(
            CancellationToken cancellationToken)
        {
            _trace.Add("eligibility.acquire");
            return new ValueTask<IUpdateHandoffEligibilityLease?>(
                _completion.Task);
        }
    }

    private sealed class RecordingEligibilityLease
        : IUpdateHandoffEligibilityLease
    {
        private readonly List<string> _trace;
        private int _disposed;

        internal RecordingEligibilityLease(
            List<string> trace,
            UpdateHandoffEligibility eligibility)
        {
            _trace = trace;
            Eligibility = eligibility;
        }

        public UpdateHandoffEligibility Eligibility { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _trace.Add("eligibility.dispose");
        }
    }

    private sealed class RecordingRestartController
        : IApplicationRestartController
    {
        private readonly List<string> _trace;
        private readonly bool _unregisterSucceeds;

        internal RecordingRestartController(
            List<string> trace,
            bool registered,
            bool unregisterSucceeds = true,
            bool registerSucceeds = true)
        {
            _trace = trace;
            IsRegistered = registered;
            _unregisterSucceeds = unregisterSucceeds;
            _registerSucceeds = registerSucceeds;
        }

        private readonly bool _registerSucceeds;

        public bool IsRegistered { get; private set; }

        public bool TryUnregister()
        {
            _trace.Add("restart.unregister");
            if (!_unregisterSucceeds)
                return false;
            IsRegistered = false;
            return true;
        }

        public bool TryRegister()
        {
            _trace.Add("restart.register");
            if (!_registerSucceeds)
                return false;
            IsRegistered = true;
            return true;
        }
    }

    private sealed class RecordingLauncher : IUpdateChildHandoffLauncher
    {
        private readonly List<string> _trace;
        private readonly IUpdateChildHandoffSession? _session;

        internal RecordingLauncher(
            List<string> trace,
            IUpdateChildHandoffSession? session)
        {
            _trace = trace;
            _session = session;
        }

        public ValueTask<UpdateChildLaunchResult> LaunchAsync(
            UpdateExecutionLease executionLease,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _trace.Add("launcher.launch");
            executionLease.Dispose();
            return ValueTask.FromResult(
                _session is null
                    ? new UpdateChildLaunchResult(
                        UpdateChildLaunchStatus.NotStarted)
                    : new UpdateChildLaunchResult(
                        UpdateChildLaunchStatus.Started,
                        _session));
        }
    }

    private sealed class ThrowingLauncher : IUpdateChildHandoffLauncher
    {
        private readonly List<string> _trace;

        internal ThrowingLauncher(List<string> trace)
        {
            _trace = trace;
        }

        public ValueTask<UpdateChildLaunchResult> LaunchAsync(
            UpdateExecutionLease executionLease,
            CancellationToken cancellationToken)
        {
            _trace.Add("launcher.launch");
            executionLease.Dispose();
            throw new InvalidOperationException("Synthetic launcher ambiguity.");
        }
    }

    private sealed class HangingLauncher : IUpdateChildHandoffLauncher
    {
        private readonly List<string> _trace;
        private readonly TaskCompletionSource<UpdateChildLaunchResult>
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal HangingLauncher(List<string> trace)
        {
            _trace = trace;
        }

        public ValueTask<UpdateChildLaunchResult> LaunchAsync(
            UpdateExecutionLease executionLease,
            CancellationToken cancellationToken)
        {
            _trace.Add("launcher.launch");
            return new ValueTask<UpdateChildLaunchResult>(_completion.Task);
        }
    }

    private sealed class MalformedLauncher : IUpdateChildHandoffLauncher
    {
        private readonly List<string> _trace;
        private readonly IUpdateChildHandoffSession _session;

        internal MalformedLauncher(
            List<string> trace,
            IUpdateChildHandoffSession session)
        {
            _trace = trace;
            _session = session;
        }

        public ValueTask<UpdateChildLaunchResult> LaunchAsync(
            UpdateExecutionLease executionLease,
            CancellationToken cancellationToken)
        {
            _trace.Add("launcher.launch");
            executionLease.Dispose();
            return ValueTask.FromResult(
                new UpdateChildLaunchResult(
                    (UpdateChildLaunchStatus)int.MaxValue,
                    _session));
        }
    }

    private sealed class RecordingChildSession : IUpdateChildHandoffSession
    {
        private readonly List<string> _trace;
        private readonly UpdateChildReadyStatus _readyStatus;
        private readonly UpdateChildCommitStatus _commitStatus;
        private readonly UpdateChildAbortStatus _abortStatus;
        private readonly Task<UpdateChildReadyStatus>? _readyTask;
        private readonly Task<UpdateChildCommitStatus>? _commitTask;
        private int _disposed;

        internal int DisposeCount => Volatile.Read(ref _disposed);

        internal RecordingChildSession(
            List<string> trace,
            UpdateChildReadyStatus readyStatus = UpdateChildReadyStatus.Ready,
            UpdateChildCommitStatus commitStatus =
                UpdateChildCommitStatus.Accepted,
            UpdateChildAbortStatus abortStatus =
                UpdateChildAbortStatus.ConfirmedInert,
            Task<UpdateChildReadyStatus>? readyTask = null,
            Task<UpdateChildCommitStatus>? commitTask = null)
        {
            _trace = trace;
            _readyStatus = readyStatus;
            _commitStatus = commitStatus;
            _abortStatus = abortStatus;
            _readyTask = readyTask;
            _commitTask = commitTask;
        }

        public ValueTask<UpdateChildReadyStatus> WaitForReadyAsync(
            CancellationToken cancellationToken)
        {
            _trace.Add("child.ready");
            return _readyTask is null
                ? ValueTask.FromResult(_readyStatus)
                : new ValueTask<UpdateChildReadyStatus>(
                    _readyTask.WaitAsync(cancellationToken));
        }

        public ValueTask<UpdateChildCommitStatus> CommitAsync(
            CancellationToken cancellationToken)
        {
            _trace.Add("child.commit");
            return _commitTask is null
                ? ValueTask.FromResult(_commitStatus)
                : new ValueTask<UpdateChildCommitStatus>(
                    _commitTask.WaitAsync(cancellationToken));
        }

        public ValueTask<UpdateChildAbortStatus> AbortAsync(
            CancellationToken cancellationToken)
        {
            _trace.Add("child.abort");
            return ValueTask.FromResult(_abortStatus);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _trace.Add("child.dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingShutdownRequester : IUpdateShutdownRequester
    {
        private readonly List<string> _trace;

        internal RecordingShutdownRequester(List<string> trace)
        {
            _trace = trace;
        }

        internal int CallCount { get; private set; }

        public bool TryRequestUpdateShutdown()
        {
            CallCount++;
            _trace.Add("shutdown.request");
            return true;
        }
    }

    private sealed class BlockingShutdownRequester : IUpdateShutdownRequester
    {
        private readonly List<string> _trace;

        internal BlockingShutdownRequester(List<string> trace)
        {
            _trace = trace;
        }

        internal ManualResetEventSlim Entered { get; } = new(false);

        internal ManualResetEventSlim Release { get; } = new(false);

        public bool TryRequestUpdateShutdown()
        {
            _trace.Add("shutdown.request");
            Entered.Set();
            Release.Wait();
            return true;
        }
    }
}
