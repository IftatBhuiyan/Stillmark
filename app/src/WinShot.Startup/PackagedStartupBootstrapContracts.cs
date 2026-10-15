using System;
using System.Collections.Generic;
using System.Threading;

namespace WinShot.Startup
{
    internal enum PackagedDeploymentKind
    {
        Unknown,
        Unpackaged,
        Packaged
    }

    internal enum PackagedStartupBootstrapStatus
    {
        Launched = 0,
        InvalidArguments = 1,
        NotPackaged = 2,
        InvalidBootstrapLocation = 3,
        TargetUnavailable = 4,
        UnsafeFileSystemEntry = 5,
        TargetIdentityChanged = 6,
        Canceled = 7,
        ProcessCreationFailed = 8,
        PlatformUnavailable = 9,
        UnexpectedFailure = 10
    }

    internal sealed class PackagedStartupBootstrapResult
    {
        internal PackagedStartupBootstrapResult(
            PackagedStartupBootstrapStatus status,
            uint processId,
            int nativeError)
        {
            Status = status;
            ProcessId = processId;
            NativeError = nativeError;
        }

        internal PackagedStartupBootstrapStatus Status { get; private set; }

        internal uint ProcessId { get; private set; }

        internal int NativeError { get; private set; }

        internal bool Launched
        {
            get { return Status == PackagedStartupBootstrapStatus.Launched; }
        }
    }

    internal sealed class PackagedStartupProcessPlan
    {
        internal PackagedStartupProcessPlan(
            string applicationName,
            string commandLine,
            string workingDirectory)
        {
            ApplicationName = applicationName;
            CommandLine = commandLine;
            WorkingDirectory = workingDirectory;
        }

        internal string ApplicationName { get; private set; }

        internal string CommandLine { get; private set; }

        internal string WorkingDirectory { get; private set; }
    }

    internal sealed class PackagedStartupProcessCreationResult
    {
        internal PackagedStartupProcessCreationResult(
            uint processId,
            int nativeError)
        {
            ProcessId = processId;
            NativeError = nativeError;
        }

        internal uint ProcessId { get; private set; }

        internal int NativeError { get; private set; }

        internal bool Created
        {
            get { return ProcessId != 0 && NativeError == 0; }
        }
    }

    internal sealed class PackagedStartupFileIdentity
    {
        internal PackagedStartupFileIdentity(
            ulong volumeSerialNumber,
            ulong fileIdLow,
            ulong fileIdHigh,
            long fileLength)
        {
            VolumeSerialNumber = volumeSerialNumber;
            FileIdLow = fileIdLow;
            FileIdHigh = fileIdHigh;
            FileLength = fileLength;
        }

        internal ulong VolumeSerialNumber { get; private set; }

        internal ulong FileIdLow { get; private set; }

        internal ulong FileIdHigh { get; private set; }

        internal long FileLength { get; private set; }
    }

    internal sealed class OpenedPackagedStartupEntry : IDisposable
    {
        private readonly IDisposable owner;

        internal OpenedPackagedStartupEntry(
            string resolvedPath,
            PackagedStartupFileIdentity identity,
            bool isDirectory,
            bool isReparsePoint,
            IDisposable owner)
        {
            if (String.IsNullOrWhiteSpace(resolvedPath))
                throw new ArgumentException("A resolved path is required.", "resolvedPath");
            if (identity == null)
                throw new ArgumentNullException("identity");
            if (owner == null)
                throw new ArgumentNullException("owner");

            ResolvedPath = resolvedPath;
            Identity = identity;
            IsDirectory = isDirectory;
            IsReparsePoint = isReparsePoint;
            this.owner = owner;
        }

        internal string ResolvedPath { get; private set; }

        internal PackagedStartupFileIdentity Identity { get; private set; }

        internal bool IsDirectory { get; private set; }

        internal bool IsReparsePoint { get; private set; }

        public void Dispose()
        {
            owner.Dispose();
        }
    }

    internal sealed class PackagedStartupPlatformException : Exception
    {
        internal PackagedStartupPlatformException(
            PackagedStartupBootstrapStatus status,
            int nativeError)
            : base("The packaged startup platform boundary failed.")
        {
            Status = status;
            NativeError = nativeError;
        }

        internal PackagedStartupBootstrapStatus Status { get; private set; }

        internal int NativeError { get; private set; }
    }

    internal interface IPackagedStartupBootstrapPlatform
    {
        PackagedDeploymentKind DetectDeployment();

        string GetCurrentModulePath();

        string GetCurrentPackageInstallPath();

        bool IsFixedLocalPath(string path);

        OpenedPackagedStartupEntry OpenDirectoryNoReparse(string path);

        OpenedPackagedStartupEntry OpenFileNoReparse(string path);

        PackagedStartupProcessCreationResult TryCreateProcess(
            PackagedStartupProcessPlan plan);
    }

    internal static class PackagedStartupBootstrapExitCode
    {
        internal static int FromStatus(PackagedStartupBootstrapStatus status)
        {
            return (int)status;
        }
    }
}
