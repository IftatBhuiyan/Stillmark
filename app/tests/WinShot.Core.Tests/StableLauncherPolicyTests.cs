using Microsoft.Win32.SafeHandles;
using WinShot.Launcher;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class StableLauncherPolicyTests : IDisposable
{
    private readonly string _localApplicationData;
    private readonly LauncherConfiguration _configuration = new(
        "PROVISIONAL.WinShot",
        "WinShot.exe",
        "WinShot",
        "Iftat Bhuiyan",
        "UNSET",
        "UNSET",
        "UNSET",
        "win-x64",
        1,
        "UNSET",
        "UNSET");

    public StableLauncherPolicyTests()
    {
        _localApplicationData = Path.Combine(
            Path.GetTempPath(),
            $"winshot-launcher-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_localApplicationData);
    }

    [Fact]
    public void Arguments_AllowOnlyPublicInteractiveTrayAndRecoveryShapes()
    {
        AssertArguments([], LauncherMode.Interactive, "");
        AssertArguments(["--tray"], LauncherMode.Tray, "--tray");
        AssertArguments(["--TRAY"], LauncherMode.Tray, "--tray");
        AssertArguments(
            ["--tray", "--recovered"],
            LauncherMode.Recovered,
            "--tray --launcher-recovered");

        AssertRejected(["--recovered"]);
        AssertRejected(["--tray", "--tray"]);
        AssertRejected(["--tray", "--update"]);
        AssertRejected([""]);
        AssertRejected(["C:\\payload.exe"]);
    }

    [Fact]
    public void QuietFailureIntent_SuppressesAnyInvalidBackgroundTrayIntent()
    {
        Assert.True(LauncherArgumentPolicy.RequestsQuietFailure(["--tray", "--invalid"]));
        Assert.True(LauncherArgumentPolicy.RequestsQuietFailure(["--invalid", "--tray"]));
        Assert.True(LauncherArgumentPolicy.RequestsQuietFailure(["--TRAY"]));
        Assert.False(LauncherArgumentPolicy.RequestsQuietFailure([]));
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0.0-beta.1")]
    [InlineData("2026_07_28+stable")]
    [InlineData("A")]
    public void ActivationPointer_AcceptsOnlyCanonicalAsciiVersionSegment(string version)
    {
        using var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(version));

        bool accepted = StableLauncherRuntime.TryReadActivationPointer(
            stream,
            out string? actual);

        Assert.True(accepted);
        Assert.Equal(version, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 1.0.0")]
    [InlineData("1.0.0 ")]
    [InlineData("1.0.0\n")]
    [InlineData("1/0")]
    [InlineData("..")]
    [InlineData(".1")]
    [InlineData("1.")]
    [InlineData("CON")]
    [InlineData("LPT1.release")]
    [InlineData("é")]
    public void ActivationPointer_RejectsAmbiguousOrUnsafeContent(string version)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(version));

        bool accepted = StableLauncherRuntime.TryReadActivationPointer(
            stream,
            out string? actual);

        Assert.False(accepted);
        Assert.Null(actual);
    }

    [Fact]
    public void ActivationPointer_RejectsBomAndOversizedContent()
    {
        byte[] bomValue =
        [
            0xEF, 0xBB, 0xBF,
            (byte)'1', (byte)'.', (byte)'0',
        ];
        using var bom = new MemoryStream(bomValue);
        using var oversized = new MemoryStream(
            Enumerable.Repeat((byte)'A', 129).ToArray());

        Assert.False(
            StableLauncherRuntime.TryReadActivationPointer(bom, out _));
        Assert.False(
            StableLauncherRuntime.TryReadActivationPointer(oversized, out _));
    }

    [Fact]
    public void PathPolicy_ResolvesOnlyExactImmediateLocalAppDataPackageLayout()
    {
        string launcher = LauncherPath();

        bool resolved = LauncherPathPolicy.TryResolveInstallation(
            launcher,
            _localApplicationData,
            "1.0.0",
            _configuration,
            out LauncherPaths? paths);

        Assert.True(resolved);
        Assert.NotNull(paths);
        Assert.Equal(
            Path.Combine(
                _localApplicationData,
                "PROVISIONAL.WinShot"),
            paths.InstallRoot);
        Assert.Equal(
            Path.Combine(
                _localApplicationData,
                "PROVISIONAL.WinShot",
                "active-version"),
            paths.ActivationPointerPath);
        Assert.Equal(
            Path.Combine(
                _localApplicationData,
                "PROVISIONAL.WinShot",
                "versions",
                "1.0.0",
                "WinShot.exe"),
            paths.PayloadPath);
    }

    [Theory]
    [InlineData(@"C:\portable\WinShot.exe")]
    [InlineData(@"\\server\share\PROVISIONAL.WinShot\WinShot.exe")]
    [InlineData(@"C:\Users\person\AppData\Local\PROVISIONAL.WinShot\other.exe")]
    public void PathPolicy_RejectsPortableUncAndWrongExecutable(string launcher)
    {
        Assert.False(
            LauncherPathPolicy.TryResolveInstallation(
                launcher,
                _localApplicationData,
                "1.0.0",
                _configuration,
                out _));
    }

    [Fact]
    public void LaunchPlan_UsesExplicitApplicationNameFixedArgumentsAndPayloadWorkingDirectory()
    {
        string path = Path.Combine(
            _localApplicationData,
            "PROVISIONAL.WinShot",
            "versions",
            "1.0.0",
            "WinShot.exe");

        bool created = StableLauncherRuntime.TryCreateLaunchPlan(
            path,
            "--tray --launcher-recovered",
            out LauncherProcessPlan? plan);

        Assert.True(created);
        Assert.NotNull(plan);
        Assert.Equal(path, plan.ApplicationName);
        Assert.Equal($"\"{path}\" --tray --launcher-recovered", plan.CommandLine);
        Assert.Equal(Path.GetDirectoryName(path), plan.WorkingDirectory);
        Assert.False(plan.InheritHandles);
    }

    [Theory]
    [InlineData("relative.exe", "")]
    [InlineData(@"\\server\share\WinShot.exe", "")]
    [InlineData(@"C:\root\WinShot.dll", "")]
    [InlineData("C:\\root\\bad\"name.exe", "")]
    [InlineData(@"C:\root\WinShot.exe", "--update")]
    [InlineData(@"C:\root\WinShot.exe", "--tray --recovered")]
    [InlineData(@"C:\root\WinShot.exe", "--tray --recovered --extra")]
    public void LaunchPlan_RejectsPathAndArgumentInjection(
        string path,
        string arguments)
    {
        Assert.False(
            StableLauncherRuntime.TryCreateLaunchPlan(
                path,
                arguments,
                out _));
    }

    [Fact]
    public void Runtime_ValidLayoutCreatesOneNoShellProcessPlan()
    {
        Layout layout = CreateLayout("1.0.0");
        var trust = new RecordingTrustVerifier(true);
        var process = new RecordingProcessCreator(processId: 914);

        LauncherResult result = StableLauncherRuntime.TryLaunch(
            layout.LauncherPath,
            _localApplicationData,
            ["--tray"],
            _configuration,
            trust,
            process);

        Assert.True(result.Launched);
        Assert.Equal(LauncherStatus.Launched, result.Status);
        Assert.Equal((uint)914, result.ProcessId);
        Assert.True(trust.WasCalled);
        Assert.NotNull(process.Plan);
        Assert.Equal(layout.PayloadPath, process.Plan.ApplicationName);
        Assert.Equal($"\"{layout.PayloadPath}\" --tray", process.Plan.CommandLine);
        Assert.False(process.Plan.InheritHandles);
    }

    [Fact]
    public void SignerPair_RequiresExactConfiguredPublisherAndIdentityEku()
    {
        const string publisher = "CN=WinShot Test Publisher, O=WinShot Test";
        const string identityEku = "1.3.6.1.4.1.311.97.1.9999";
        var configured = new LauncherConfiguration(
            "PROVISIONAL.WinShot",
            "WinShot.exe",
            "WinShot",
            "Iftat Bhuiyan",
            publisher,
            identityEku,
            "stable",
            "win-x64",
            1,
            "UNSET",
            "UNSET");
        var exact = new LauncherSignerEvidence(publisher, identityEku);

        Assert.True(
            WindowsLauncherTrustVerifier.IsExpectedSignerPair(
                exact,
                exact,
                configured));
        Assert.False(
            WindowsLauncherTrustVerifier.IsExpectedSignerPair(
                exact,
                new LauncherSignerEvidence("CN=Other Publisher", identityEku),
                configured));
        Assert.False(
            WindowsLauncherTrustVerifier.IsExpectedSignerPair(
                exact,
                new LauncherSignerEvidence(
                    publisher,
                    "1.3.6.1.4.1.311.97.1.10000"),
                configured));
        Assert.False(
            WindowsLauncherTrustVerifier.IsExpectedSignerPair(
                exact,
                exact,
                _configuration));
    }

    [Theory]
    [InlineData("", "1.3.6.1.4.1.311.97.1.9999")]
    [InlineData("CN=Publisher", "1.3.6.1.4.1.311.97.1.0")]
    [InlineData("CN=Publisher", "1.3.6.1.4.1.311.97.bad")]
    public void Configuration_RejectsMalformedTrustPins(
        string publisher,
        string identityEku)
    {
        Assert.Throws<ArgumentException>(
            () => new LauncherConfiguration(
                "PROVISIONAL.WinShot",
                "WinShot.exe",
                "WinShot",
                "Iftat Bhuiyan",
                publisher,
                identityEku,
                "UNSET",
                "win-x64",
                1,
                "UNSET",
                "UNSET"));
    }

    [Fact]
    public void Runtime_TrustRefusalNeverCreatesProcess()
    {
        Layout layout = CreateLayout("1.0.0");
        var trust = new RecordingTrustVerifier(false);
        var process = new RecordingProcessCreator(processId: 914);

        LauncherResult result = StableLauncherRuntime.TryLaunch(
            layout.LauncherPath,
            _localApplicationData,
            [],
            _configuration,
            trust,
            process);

        Assert.Equal(LauncherStatus.PayloadTrustRejected, result.Status);
        Assert.True(trust.WasCalled);
        Assert.Null(process.Plan);
    }

    [Fact]
    public void Runtime_DisposesUnexpectedLeaseReturnedWithTrustRefusal()
    {
        Layout layout = CreateLayout("1.0.0");
        var trust = new RefusingTrustVerifierWithLease();
        var process = new RecordingProcessCreator(processId: 914);

        LauncherResult result = StableLauncherRuntime.TryLaunch(
            layout.LauncherPath,
            _localApplicationData,
            [],
            _configuration,
            trust,
            process);

        Assert.Equal(LauncherStatus.PayloadTrustRejected, result.Status);
        Assert.True(trust.LeaseDisposed);
        Assert.Null(process.Plan);
    }

    [Fact]
    public void Runtime_ProcessFailureReturnsBoundedNativeError()
    {
        Layout layout = CreateLayout("1.0.0");
        var process = new RecordingProcessCreator(errorCode: 5);

        LauncherResult result = StableLauncherRuntime.TryLaunch(
            layout.LauncherPath,
            _localApplicationData,
            [],
            _configuration,
            new RecordingTrustVerifier(true),
            process);

        Assert.Equal(LauncherStatus.ProcessCreationFailed, result.Status);
        Assert.Equal(5, result.NativeErrorCode);
        Assert.False(result.Launched);
    }

    [Fact]
    public void Runtime_MalformedPointerFailsWithoutScanningVersionDirectories()
    {
        Layout layout = CreateLayout("1.0.0");
        File.WriteAllText(layout.PointerPath, "../1.0.0");
        Directory.CreateDirectory(
            Path.Combine(
                _localApplicationData,
                "PROVISIONAL.WinShot",
                "versions",
                "9.9.9"));

        var process = new RecordingProcessCreator(processId: 914);
        LauncherResult result = StableLauncherRuntime.TryLaunch(
            layout.LauncherPath,
            _localApplicationData,
            [],
            _configuration,
            new RecordingTrustVerifier(true),
            process);

        Assert.Equal(LauncherStatus.ActivationPointerInvalid, result.Status);
        Assert.Null(process.Plan);
    }

    [Fact]
    public void Runtime_MissingPointerAndPayloadHaveDistinctFailClosedStatuses()
    {
        Layout layout = CreateLayout("1.0.0");
        File.Delete(layout.PointerPath);
        LauncherResult missingPointer = StableLauncherRuntime.TryLaunch(
            layout.LauncherPath,
            _localApplicationData,
            [],
            _configuration,
            new RecordingTrustVerifier(true),
            new RecordingProcessCreator(processId: 914));

        File.WriteAllBytes(
            layout.PointerPath,
            System.Text.Encoding.ASCII.GetBytes("1.0.0"));
        File.Delete(layout.PayloadPath);
        LauncherResult missingPayload = StableLauncherRuntime.TryLaunch(
            layout.LauncherPath,
            _localApplicationData,
            [],
            _configuration,
            new RecordingTrustVerifier(true),
            new RecordingProcessCreator(processId: 914));

        Assert.Equal(
            LauncherStatus.ActivationPointerUnavailable,
            missingPointer.Status);
        Assert.Equal(
            LauncherStatus.PayloadUnavailable,
            missingPayload.Status);
    }

    [Fact]
    public void Runtime_HoldsPayloadAgainstWriteAndDeleteThroughProcessCreation()
    {
        Layout layout = CreateLayout("1.0.0");
        bool mutationBlocked = false;
        var process = new RecordingProcessCreator(
            processId: 914,
            onCreate: () =>
            {
                try
                {
                    File.AppendAllText(layout.PayloadPath, "tampered");
                }
                catch (IOException)
                {
                    mutationBlocked = true;
                }
            });

        LauncherResult result = StableLauncherRuntime.TryLaunch(
            layout.LauncherPath,
            _localApplicationData,
            [],
            _configuration,
            new RecordingTrustVerifier(true),
            process);

        Assert.True(result.Launched);
        Assert.True(mutationBlocked);
    }

    [Fact]
    public void Runtime_HoldsActivationPointerAgainstSwitchThroughProcessCreation()
    {
        Layout layout = CreateLayout("1.0.0");
        string replacement = Path.Combine(
            Path.GetDirectoryName(layout.PointerPath)!,
            "active-version.next");
        File.WriteAllBytes(
            replacement,
            System.Text.Encoding.ASCII.GetBytes("2.0.0"));
        bool switchBlocked = false;
        var process = new RecordingProcessCreator(
            processId: 914,
            onCreate: () =>
            {
                try
                {
                    File.Replace(replacement, layout.PointerPath, null);
                }
                catch (IOException)
                {
                    switchBlocked = true;
                }
            });

        LauncherResult result = StableLauncherRuntime.TryLaunch(
            layout.LauncherPath,
            _localApplicationData,
            [],
            _configuration,
            new RecordingTrustVerifier(true),
            process);

        Assert.True(result.Launched);
        Assert.True(switchBlocked);
        Assert.Equal("1.0.0", File.ReadAllText(layout.PointerPath));
    }

    private Layout CreateLayout(string version)
    {
        string installRoot = Path.Combine(
            _localApplicationData,
            "PROVISIONAL.WinShot");
        string versionDirectory = Path.Combine(
            installRoot,
            "versions",
            version);
        Directory.CreateDirectory(versionDirectory);
        string launcherPath = Path.Combine(installRoot, "WinShot.exe");
        string payloadPath = Path.Combine(versionDirectory, "WinShot.exe");
        string pointerPath = Path.Combine(installRoot, "active-version");
        File.WriteAllText(launcherPath, "synthetic stable launcher");
        File.WriteAllText(payloadPath, "synthetic versioned payload");
        File.WriteAllBytes(
            pointerPath,
            System.Text.Encoding.ASCII.GetBytes(version));
        return new Layout(launcherPath, pointerPath, payloadPath);
    }

    private string LauncherPath() =>
        Path.Combine(
            _localApplicationData,
            "PROVISIONAL.WinShot",
            "WinShot.exe");

    private static void AssertArguments(
        string[] arguments,
        LauncherMode expectedMode,
        string expectedChildArguments)
    {
        Assert.True(
            LauncherArgumentPolicy.TryParse(
                arguments,
                out LauncherMode mode,
                out string childArguments));
        Assert.Equal(expectedMode, mode);
        Assert.Equal(expectedChildArguments, childArguments);
    }

    private static void AssertRejected(string[] arguments)
    {
        Assert.False(
            LauncherArgumentPolicy.TryParse(
                arguments,
                out _,
                out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_localApplicationData))
            Directory.Delete(_localApplicationData, recursive: true);
    }

    private sealed record Layout(
        string LauncherPath,
        string PointerPath,
        string PayloadPath);

    private sealed class RecordingTrustVerifier : ILauncherTrustVerifier
    {
        private readonly bool _trusted;

        internal RecordingTrustVerifier(bool trusted)
        {
            _trusted = trusted;
        }

        internal bool WasCalled { get; private set; }

        public bool TryAcquireTrust(
            SafeFileHandle launcherHandle,
            string launcherPath,
            SafeFileHandle payloadHandle,
            string payloadPath,
            string versionDirectory,
            string activeVersion,
            LauncherConfiguration configuration,
            out IDisposable retainedTrust)
        {
            WasCalled = true;
            Assert.False(launcherHandle.IsClosed);
            Assert.False(payloadHandle.IsClosed);
            retainedTrust = _trusted ? new MemoryStream() : null!;
            return _trusted;
        }
    }

    private sealed class RecordingProcessCreator : ILauncherProcessCreator
    {
        private readonly uint _processId;
        private readonly int _errorCode;
        private readonly Action? _onCreate;

        internal RecordingProcessCreator(
            uint processId = 0,
            int errorCode = 0,
            Action? onCreate = null)
        {
            _processId = processId;
            _errorCode = errorCode;
            _onCreate = onCreate;
        }

        internal LauncherProcessPlan? Plan { get; private set; }

        public LauncherProcessCreationResult TryCreateProcess(
            LauncherProcessPlan plan)
        {
            Plan = plan;
            _onCreate?.Invoke();
            return new LauncherProcessCreationResult(
                _processId,
                _errorCode);
        }
    }

    private sealed class RefusingTrustVerifierWithLease
        : ILauncherTrustVerifier
    {
        internal bool LeaseDisposed { get; private set; }

        public bool TryAcquireTrust(
            SafeFileHandle launcherHandle,
            string launcherPath,
            SafeFileHandle payloadHandle,
            string payloadPath,
            string versionDirectory,
            string activeVersion,
            LauncherConfiguration configuration,
            out IDisposable retainedTrust)
        {
            retainedTrust = new CallbackDisposable(
                () => LeaseDisposed = true);
            return false;
        }
    }

    private sealed class CallbackDisposable : IDisposable
    {
        private Action? _dispose;

        internal CallbackDisposable(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            Action? dispose = Interlocked.Exchange(ref _dispose, null);
            dispose?.Invoke();
        }
    }
}
