using System.Collections.Generic;
using System.Threading;
using WinShot.Startup;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class PackagedStartupBootstrapTests
{
    private const string PackageRoot =
        @"C:\Program Files\WindowsApps\PROVISIONAL.WinShot_1.0.0.0_x64";
    private const string BootstrapPath =
        PackageRoot + @"\WinShot.Startup.exe";
    private const string MainPath = PackageRoot + @"\WinShot.exe";

    [Fact]
    public void ZeroArgumentsLaunchesOnlyExactSiblingBackgroundPlan()
    {
        var platform = new FakePlatform();

        PackagedStartupBootstrapResult result =
            PackagedStartupBootstrapRuntime.TryLaunch(
                [],
                CancellationToken.None,
                platform);

        Assert.Equal(PackagedStartupBootstrapStatus.Launched, result.Status);
        Assert.Equal(42U, result.ProcessId);
        Assert.NotNull(platform.ProcessPlan);
        Assert.Equal(MainPath, platform.ProcessPlan.ApplicationName);
        Assert.Equal("\"" + MainPath + "\" --tray", platform.ProcessPlan.CommandLine);
        Assert.Equal(PackageRoot, platform.ProcessPlan.WorkingDirectory);
        Assert.True(platform.AllHandlesOpenDuringCreate);
        Assert.All(platform.Owners, owner => Assert.True(owner.Disposed));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("--tray")]
    [InlineData("--TRAY")]
    [InlineData("--recover")]
    [InlineData("\"C:\\Other.exe\"")]
    public void AnyArgumentFailsBeforePlatformAccess(string argument)
    {
        var platform = new FakePlatform();

        PackagedStartupBootstrapResult result =
            PackagedStartupBootstrapRuntime.TryLaunch(
                [argument],
                CancellationToken.None,
                platform);

        Assert.Equal(
            PackagedStartupBootstrapStatus.InvalidArguments,
            result.Status);
        Assert.Equal(0, platform.AccessCount);
        Assert.Null(platform.ProcessPlan);
    }

    [Theory]
    [InlineData((int)PackagedDeploymentKind.Unknown)]
    [InlineData((int)PackagedDeploymentKind.Unpackaged)]
    public void NonPackagedOrAmbiguousDeploymentFailsClosed(
        int deploymentValue)
    {
        var platform = new FakePlatform
        {
            Deployment = (PackagedDeploymentKind)deploymentValue,
        };

        PackagedStartupBootstrapResult result =
            PackagedStartupBootstrapRuntime.TryLaunch(
                [],
                CancellationToken.None,
                platform);

        Assert.Equal(PackagedStartupBootstrapStatus.NotPackaged, result.Status);
        Assert.Null(platform.ProcessPlan);
    }

    [Theory]
    [InlineData(@"C:\Temp\WinShot.Startup.exe")]
    [InlineData(
        @"C:\Program Files\WindowsApps\PROVISIONAL.WinShot_1.0.0.0_x64\bin\WinShot.Startup.exe")]
    [InlineData(
        @"C:\Program Files\WindowsApps\PROVISIONAL.WinShot_1.0.0.0_x64\Renamed.exe")]
    [InlineData(
        @"\\server\share\PROVISIONAL.WinShot\WinShot.Startup.exe")]
    public void BootstrapMustBeExactDirectPackageRootChild(string modulePath)
    {
        var platform = new FakePlatform { ModulePath = modulePath };

        PackagedStartupBootstrapResult result =
            PackagedStartupBootstrapRuntime.TryLaunch(
                [],
                CancellationToken.None,
                platform);

        Assert.Equal(
            PackagedStartupBootstrapStatus.InvalidBootstrapLocation,
            result.Status);
        Assert.Null(platform.ProcessPlan);
    }

    [Fact]
    public void PackageRootMustBeFixedLocalStorage()
    {
        var platform = new FakePlatform { FixedLocalPath = false };

        PackagedStartupBootstrapResult result =
            PackagedStartupBootstrapRuntime.TryLaunch(
                [],
                CancellationToken.None,
                platform);

        Assert.Equal(
            PackagedStartupBootstrapStatus.InvalidBootstrapLocation,
            result.Status);
        Assert.Null(platform.ProcessPlan);
    }

    [Theory]
    [InlineData(EntryKind.Root)]
    [InlineData(EntryKind.Bootstrap)]
    [InlineData(EntryKind.Main)]
    public void ReparseEntryFailsBeforeProcessCreation(EntryKind entry)
    {
        var platform = new FakePlatform { ReparseEntry = entry };

        PackagedStartupBootstrapResult result =
            PackagedStartupBootstrapRuntime.TryLaunch(
                [],
                CancellationToken.None,
                platform);

        Assert.Equal(
            PackagedStartupBootstrapStatus.UnsafeFileSystemEntry,
            result.Status);
        Assert.Null(platform.ProcessPlan);
        Assert.All(platform.Owners, owner => Assert.True(owner.Disposed));
    }

    [Fact]
    public void BootstrapAndMainMustHaveDistinctFileIdentity()
    {
        var platform = new FakePlatform { MainFileId = 10 };

        PackagedStartupBootstrapResult result =
            PackagedStartupBootstrapRuntime.TryLaunch(
                [],
                CancellationToken.None,
                platform);

        Assert.Equal(
            PackagedStartupBootstrapStatus.UnsafeFileSystemEntry,
            result.Status);
        Assert.Null(platform.ProcessPlan);
    }

    [Fact]
    public void MainReplacementBeforeCreationFailsClosed()
    {
        var platform = new FakePlatform { ReopenedMainFileId = 99 };

        PackagedStartupBootstrapResult result =
            PackagedStartupBootstrapRuntime.TryLaunch(
                [],
                CancellationToken.None,
                platform);

        Assert.Equal(
            PackagedStartupBootstrapStatus.TargetIdentityChanged,
            result.Status);
        Assert.Null(platform.ProcessPlan);
        Assert.All(platform.Owners, owner => Assert.True(owner.Disposed));
    }

    [Fact]
    public void ResolvedPathMismatchFailsClosed()
    {
        var platform = new FakePlatform
        {
            ResolvedMainPath = PackageRoot + @"\alternate\WinShot.exe",
        };

        PackagedStartupBootstrapResult result =
            PackagedStartupBootstrapRuntime.TryLaunch(
                [],
                CancellationToken.None,
                platform);

        Assert.Equal(
            PackagedStartupBootstrapStatus.UnsafeFileSystemEntry,
            result.Status);
        Assert.Null(platform.ProcessPlan);
    }

    [Fact]
    public void PreCanceledLaunchTouchesNoPlatformBoundary()
    {
        var platform = new FakePlatform();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        PackagedStartupBootstrapResult result =
            PackagedStartupBootstrapRuntime.TryLaunch(
                [],
                cancellation.Token,
                platform);

        Assert.Equal(PackagedStartupBootstrapStatus.Canceled, result.Status);
        Assert.Equal(0, platform.AccessCount);
    }

    [Fact]
    public void ProcessCreationFailureIsBoundedAndClosesEntries()
    {
        var platform = new FakePlatform
        {
            CreationResult =
                new PackagedStartupProcessCreationResult(0, nativeError: 5),
        };

        PackagedStartupBootstrapResult result =
            PackagedStartupBootstrapRuntime.TryLaunch(
                [],
                CancellationToken.None,
                platform);

        Assert.Equal(
            PackagedStartupBootstrapStatus.ProcessCreationFailed,
            result.Status);
        Assert.Equal(5, result.NativeError);
        Assert.Equal(0U, result.ProcessId);
        Assert.All(platform.Owners, owner => Assert.True(owner.Disposed));
    }

    [Theory]
    [InlineData((int)PackagedStartupBootstrapStatus.Launched, 0)]
    [InlineData((int)PackagedStartupBootstrapStatus.InvalidArguments, 1)]
    [InlineData((int)PackagedStartupBootstrapStatus.NotPackaged, 2)]
    [InlineData((int)PackagedStartupBootstrapStatus.UnexpectedFailure, 10)]
    public void ExitCodesAreStable(
        int statusValue,
        int expected)
    {
        var status = (PackagedStartupBootstrapStatus)statusValue;
        Assert.Equal(expected, PackagedStartupBootstrapExitCode.FromStatus(status));
    }

    public enum EntryKind
    {
        None,
        Root,
        Bootstrap,
        Main,
    }

    private sealed class FakePlatform : IPackagedStartupBootstrapPlatform
    {
        private int mainOpenCount;

        internal PackagedDeploymentKind Deployment { get; set; } =
            PackagedDeploymentKind.Packaged;

        internal string ModulePath { get; set; } = BootstrapPath;

        internal string PackageInstallPath { get; set; } = PackageRoot;

        internal bool FixedLocalPath { get; set; } = true;

        internal EntryKind ReparseEntry { get; set; }

        internal ulong MainFileId { get; set; } = 20;

        internal ulong ReopenedMainFileId { get; set; } = 20;

        internal string ResolvedMainPath { get; set; } = MainPath;

        internal int AccessCount { get; private set; }

        internal List<FakeOwner> Owners { get; } = [];

        internal PackagedStartupProcessPlan? ProcessPlan { get; private set; }

        internal bool AllHandlesOpenDuringCreate { get; private set; }

        internal PackagedStartupProcessCreationResult CreationResult { get; set; } =
            new(42, 0);

        public PackagedDeploymentKind DetectDeployment()
        {
            AccessCount++;
            return Deployment;
        }

        public string GetCurrentModulePath()
        {
            AccessCount++;
            return ModulePath;
        }

        public string GetCurrentPackageInstallPath()
        {
            AccessCount++;
            return PackageInstallPath;
        }

        public bool IsFixedLocalPath(string path)
        {
            AccessCount++;
            return FixedLocalPath;
        }

        public OpenedPackagedStartupEntry OpenDirectoryNoReparse(string path)
        {
            AccessCount++;
            return Entry(
                PackageRoot,
                fileId: 1,
                isDirectory: true,
                ReparseEntry == EntryKind.Root);
        }

        public OpenedPackagedStartupEntry OpenFileNoReparse(string path)
        {
            AccessCount++;
            if (path.EndsWith(
                    PackagedStartupBootstrapRuntime.BootstrapExecutableName,
                    StringComparison.Ordinal))
            {
                return Entry(
                    BootstrapPath,
                    fileId: 10,
                    isDirectory: false,
                    ReparseEntry == EntryKind.Bootstrap);
            }

            mainOpenCount++;
            return Entry(
                ResolvedMainPath,
                mainOpenCount == 1 ? MainFileId : ReopenedMainFileId,
                isDirectory: false,
                ReparseEntry == EntryKind.Main);
        }

        public PackagedStartupProcessCreationResult TryCreateProcess(
            PackagedStartupProcessPlan plan)
        {
            AccessCount++;
            ProcessPlan = plan;
            AllHandlesOpenDuringCreate = Owners.All(owner => !owner.Disposed);
            return CreationResult;
        }

        private OpenedPackagedStartupEntry Entry(
            string path,
            ulong fileId,
            bool isDirectory,
            bool isReparse)
        {
            var owner = new FakeOwner();
            Owners.Add(owner);
            return new OpenedPackagedStartupEntry(
                path,
                new PackagedStartupFileIdentity(
                    volumeSerialNumber: 1,
                    fileId,
                    fileIdHigh: 0,
                    fileLength: isDirectory ? 0 : 4096),
                isDirectory,
                isReparse,
                owner);
        }
    }

    private sealed class FakeOwner : IDisposable
    {
        internal bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
