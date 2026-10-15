using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using WinShot.Core.Update;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class UpdateProcessLauncherTests : IDisposable
{
    private readonly string _temporaryDirectory;

    public UpdateProcessLauncherTests()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"winshot-update-launch-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public void VerifiedLease_CreatesExplicitNoShellPlanAndClosesNativeHandles()
    {
        using UpdateExecutionLease lease = CreateLease();
        var native = new RecordingUpdateProcessNative(processId: 731);

        UpdateProcessLaunchResult result = UpdateProcessLauncher.TryLaunch(
            lease,
            native);

        Assert.True(result.Launched);
        Assert.Equal(UpdateProcessLaunchStatus.Launched, result.Status);
        Assert.Equal((uint)731, result.ProcessId);
        Assert.Equal(0, result.NativeErrorCode);
        Assert.True(native.WasCalled);
        UpdateProcessLaunchPlan plan = Assert.IsType<UpdateProcessLaunchPlan>(
            native.Plan);
        string expectedPath = Path.Combine(
            _temporaryDirectory,
            "WinShot-update.exe");
        Assert.Equal(expectedPath, plan.ApplicationName);
        Assert.Equal(
            $"\"{expectedPath}\"",
            plan.CommandLine);
        Assert.Equal(_temporaryDirectory, plan.WorkingDirectory);
        Assert.False(plan.InheritHandles);
        Assert.True(native.ProcessHandle!.IsClosed);
        Assert.True(native.ThreadHandle!.IsClosed);
        Assert.Throws<ObjectDisposedException>(() => lease.PackageHandle);
    }

    [Fact]
    public void NativeCreationFailure_FailsClosedAndConsumesLease()
    {
        using UpdateExecutionLease lease = CreateLease();
        var native = new RecordingUpdateProcessNative(errorCode: 5);

        UpdateProcessLaunchResult result = UpdateProcessLauncher.TryLaunch(
            lease,
            native);

        Assert.Equal(
            UpdateProcessLaunchStatus.ProcessCreationFailed,
            result.Status);
        Assert.False(result.Launched);
        Assert.Null(result.ProcessId);
        Assert.Equal(5, result.NativeErrorCode);
        Assert.True(native.WasCalled);
        Assert.Throws<ObjectDisposedException>(() => lease.PackageHandle);
    }

    [Fact]
    public void ConsumedLease_CannotReachASecondNativeCreation()
    {
        using UpdateExecutionLease lease = CreateLease();
        var firstNative = new RecordingUpdateProcessNative(processId: 731);
        var secondNative = new RecordingUpdateProcessNative(processId: 732);

        UpdateProcessLaunchResult first = UpdateProcessLauncher.TryLaunch(
            lease,
            firstNative);
        UpdateProcessLaunchResult second = UpdateProcessLauncher.TryLaunch(
            lease,
            secondNative);

        Assert.True(first.Launched);
        Assert.Equal(UpdateProcessLaunchStatus.LeaseUnavailable, second.Status);
        Assert.False(secondNative.WasCalled);
    }

    [Fact]
    public void ExpiredExecutionLease_CannotReachNativeCreation()
    {
        long expiredTimestamp = Stopwatch.GetTimestamp()
            - (Stopwatch.Frequency * 2);
        using UpdateExecutionLease lease = CreateLease(
            verifiedTimestamp: expiredTimestamp,
            capabilityLifetime: TimeSpan.FromSeconds(1));
        var native = new RecordingUpdateProcessNative(processId: 731);

        UpdateProcessLaunchResult result = UpdateProcessLauncher.TryLaunch(
            lease,
            native);

        Assert.Equal(UpdateProcessLaunchStatus.LeaseUnavailable, result.Status);
        Assert.False(result.Launched);
        Assert.False(native.WasCalled);
        Assert.Throws<ObjectDisposedException>(() => lease.PackageHandle);
    }

    [Fact]
    public void WindowsNativeLauncher_CreatesProcessWhileLeasesAreRetained()
    {
        Assert.True(OperatingSystem.IsWindows());
        string sourcePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "where.exe");
        Assert.True(File.Exists(sourcePath));
        using UpdateExecutionLease lease = CreateLease(sourcePath);

        UpdateProcessLaunchResult result = UpdateProcessLauncher.TryLaunch(lease);

        Assert.True(
            result.Launched,
            $"CreateProcessW failed with native error {result.NativeErrorCode}.");
        Assert.NotEqual((uint)0, result.ProcessId);
        try
        {
            using Process child = Process.GetProcessById(
                checked((int)result.ProcessId!.Value));
            Assert.True(child.WaitForExit(milliseconds: 5_000));
        }
        catch (ArgumentException)
        {
            // The no-argument helper can exit before its PID is reopened.
        }
    }

    [Fact]
    public void LeaseAndReopenedFileRemainLockedThroughNativeCreation()
    {
        using UpdateExecutionLease lease = CreateLease();
        string packagePath = Path.Combine(
            _temporaryDirectory,
            "WinShot-update.exe");
        bool mutationWasBlocked = false;
        var native = new RecordingUpdateProcessNative(
            processId: 731,
            onCreate: () =>
            {
                lease.Dispose();
                try
                {
                    File.AppendAllText(packagePath, "tampered");
                }
                catch (IOException)
                {
                    mutationWasBlocked = true;
                }
            });

        UpdateProcessLaunchResult result = UpdateProcessLauncher.TryLaunch(
            lease,
            native);

        Assert.True(result.Launched);
        Assert.True(mutationWasBlocked);
    }

    [Fact]
    public void StoredIdentityMismatch_DoesNotReachNativeCreation()
    {
        using UpdateExecutionLease lease = CreateLease(useMismatchedIdentity: true);
        var native = new RecordingUpdateProcessNative(processId: 731);

        UpdateProcessLaunchResult result = UpdateProcessLauncher.TryLaunch(
            lease,
            native);

        Assert.Equal(
            UpdateProcessLaunchStatus.PackageIdentityMismatch,
            result.Status);
        Assert.False(native.WasCalled);
        Assert.Throws<ObjectDisposedException>(() => lease.PackageHandle);
    }

    [Fact]
    public void DeclaredPathMismatch_DoesNotReachNativeCreation()
    {
        string otherPath = Path.Combine(_temporaryDirectory, "other.exe");
        File.WriteAllText(otherPath, "not the leased file");
        using UpdateExecutionLease lease = CreateLease(
            declaredPathOverride: otherPath);
        var native = new RecordingUpdateProcessNative(processId: 731);

        UpdateProcessLaunchResult result = UpdateProcessLauncher.TryLaunch(
            lease,
            native);

        Assert.Equal(
            UpdateProcessLaunchStatus.PackagePathMismatch,
            result.Status);
        Assert.False(native.WasCalled);
        Assert.Throws<ObjectDisposedException>(() => lease.PackageHandle);
    }

    [Theory]
    [InlineData("relative.exe")]
    [InlineData(@"\\server\share\update.exe")]
    [InlineData(@"C:\staging\update.dll")]
    [InlineData("C:\\staging\\bad\"name.exe")]
    public void UnsafeLaunchPlanInput_IsRejected(string applicationPath)
    {
        bool created = UpdateProcessLauncher.TryCreateLaunchPlan(
            applicationPath,
            out UpdateProcessLaunchPlan? plan);

        Assert.False(created);
        Assert.Null(plan);
    }

    private UpdateExecutionLease CreateLease(
        string? sourcePackagePath = null,
        bool useMismatchedIdentity = false,
        string? declaredPathOverride = null,
        long? verifiedTimestamp = null,
        TimeSpan? capabilityLifetime = null)
    {
        string packagePath = Path.Combine(
            _temporaryDirectory,
            "WinShot-update.exe");
        if (sourcePackagePath is null)
            File.WriteAllText(packagePath, "synthetic updater");
        else
            File.Copy(sourcePackagePath, packagePath);
        OpenedUpdateFile opened = UpdatePackageFileHandle.OpenReadNoReparse(
            packagePath,
            bufferSize: 4096);
        Assert.False(opened.IsUnsafeFilesystemEntry);
        FileStream packageLease = Assert.IsType<FileStream>(opened.Stream);
        UpdatePackageFileIdentity identity =
            Assert.IsType<UpdatePackageFileIdentity>(opened.Identity);
        if (useMismatchedIdentity)
        {
            identity = new UpdatePackageFileIdentity(
                identity.VolumeSerialNumber,
                identity.FileIdLow ^ 1,
                identity.FileIdHigh,
                identity.FileLength);
        }

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
        return new UpdateExecutionLease(
            declaredPathOverride ?? packagePath,
            new string('D', 64),
            identity,
            evidence,
            packageLease,
            manifestLease,
            verifiedTimestamp,
            capabilityLifetime);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
            Directory.Delete(_temporaryDirectory, recursive: true);
    }

    private sealed class RecordingUpdateProcessNative : IUpdateProcessNative
    {
        private readonly uint? _processId;
        private readonly int _errorCode;
        private readonly Action? _onCreate;

        internal RecordingUpdateProcessNative(
            uint? processId = null,
            int errorCode = 0,
            Action? onCreate = null)
        {
            _processId = processId;
            _errorCode = errorCode;
            _onCreate = onCreate;
        }

        internal bool WasCalled { get; private set; }

        internal UpdateProcessLaunchPlan? Plan { get; private set; }

        internal TrackingSafeHandle? ProcessHandle { get; private set; }

        internal TrackingSafeHandle? ThreadHandle { get; private set; }

        public UpdateNativeProcessCreationResult TryCreateProcess(
            UpdateProcessLaunchPlan plan)
        {
            WasCalled = true;
            Plan = plan;
            _onCreate?.Invoke();
            if (!_processId.HasValue)
            {
                return new UpdateNativeProcessCreationResult(
                    Process: null,
                    ErrorCode: _errorCode);
            }

            ProcessHandle = new TrackingSafeHandle();
            ThreadHandle = new TrackingSafeHandle();
            return new UpdateNativeProcessCreationResult(
                new UpdateCreatedProcess(
                    ProcessHandle,
                    ThreadHandle,
                    _processId.Value),
                ErrorCode: 0);
        }
    }

    private sealed class TrackingSafeHandle
        : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal TrackingSafeHandle()
            : base(ownsHandle: true)
        {
            SetHandle(new IntPtr(1));
        }

        protected override bool ReleaseHandle()
        {
            handle = IntPtr.Zero;
            return true;
        }
    }
}
