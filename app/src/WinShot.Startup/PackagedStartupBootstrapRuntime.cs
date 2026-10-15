using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

#pragma warning disable CS8600, CS8603, CS8604

namespace WinShot.Startup
{
    internal static class PackagedStartupBootstrapRuntime
    {
        internal const string BootstrapExecutableName = "WinShot.Startup.exe";
        internal const string MainExecutableName = "WinShot.exe";
        internal const string BackgroundArgument = "--tray";

        internal static PackagedStartupBootstrapResult TryLaunch(
            IList<string> arguments,
            CancellationToken cancellationToken,
            IPackagedStartupBootstrapPlatform platform)
        {
            if (arguments == null)
                throw new ArgumentNullException("arguments");
            if (platform == null)
                throw new ArgumentNullException("platform");

            if (arguments.Count != 0)
                return Failure(PackagedStartupBootstrapStatus.InvalidArguments);
            if (cancellationToken.IsCancellationRequested)
                return Failure(PackagedStartupBootstrapStatus.Canceled);

            try
            {
                if (platform.DetectDeployment() != PackagedDeploymentKind.Packaged)
                    return Failure(PackagedStartupBootstrapStatus.NotPackaged);

                string modulePath = NormalizeAbsoluteLocalPath(
                    platform.GetCurrentModulePath());
                string packageRoot = NormalizeAbsoluteLocalPath(
                    platform.GetCurrentPackageInstallPath());
                if (modulePath == null
                    || packageRoot == null
                    || !platform.IsFixedLocalPath(packageRoot)
                    || !String.Equals(
                        Path.GetFileName(modulePath),
                        BootstrapExecutableName,
                        StringComparison.Ordinal)
                    || !String.Equals(
                        Path.GetDirectoryName(modulePath),
                        packageRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Failure(
                        PackagedStartupBootstrapStatus.InvalidBootstrapLocation);
                }

                string mainPath = NormalizeAbsoluteLocalPath(
                    Path.Combine(packageRoot, MainExecutableName));
                if (mainPath == null
                    || !String.Equals(
                        Path.GetDirectoryName(mainPath),
                        packageRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Failure(
                        PackagedStartupBootstrapStatus.InvalidBootstrapLocation);
                }

                if (cancellationToken.IsCancellationRequested)
                    return Failure(PackagedStartupBootstrapStatus.Canceled);

                using (OpenedPackagedStartupEntry openedRoot =
                    platform.OpenDirectoryNoReparse(packageRoot))
                using (OpenedPackagedStartupEntry openedBootstrap =
                    platform.OpenFileNoReparse(modulePath))
                using (OpenedPackagedStartupEntry openedMain =
                    platform.OpenFileNoReparse(mainPath))
                {
                    if (!IsSafeDirectory(openedRoot, packageRoot)
                        || !IsSafeDirectChild(
                            openedBootstrap,
                            modulePath,
                            openedRoot.ResolvedPath)
                        || !IsSafeDirectChild(
                            openedMain,
                            mainPath,
                            openedRoot.ResolvedPath)
                        || IsSameFile(
                            openedBootstrap.Identity,
                            openedMain.Identity))
                    {
                        return Failure(
                            PackagedStartupBootstrapStatus.UnsafeFileSystemEntry);
                    }

                    if (cancellationToken.IsCancellationRequested)
                        return Failure(PackagedStartupBootstrapStatus.Canceled);

                    using (OpenedPackagedStartupEntry reopenedMain =
                        platform.OpenFileNoReparse(mainPath))
                    {
                        if (!IsSafeDirectChild(
                                reopenedMain,
                                mainPath,
                                openedRoot.ResolvedPath)
                            || !IsSameFile(
                                openedMain.Identity,
                                reopenedMain.Identity))
                        {
                            return Failure(
                                PackagedStartupBootstrapStatus
                                    .TargetIdentityChanged);
                        }

                        if (cancellationToken.IsCancellationRequested)
                            return Failure(
                                PackagedStartupBootstrapStatus.Canceled);

                        var plan = new PackagedStartupProcessPlan(
                            reopenedMain.ResolvedPath,
                            Quote(reopenedMain.ResolvedPath)
                                + " "
                                + BackgroundArgument,
                            openedRoot.ResolvedPath);
                        PackagedStartupProcessCreationResult creation =
                            platform.TryCreateProcess(plan);
                        if (creation == null || !creation.Created)
                        {
                            return new PackagedStartupBootstrapResult(
                                PackagedStartupBootstrapStatus
                                    .ProcessCreationFailed,
                                0,
                                creation == null ? 0 : creation.NativeError);
                        }

                        return new PackagedStartupBootstrapResult(
                            PackagedStartupBootstrapStatus.Launched,
                            creation.ProcessId,
                            0);
                    }
                }
            }
            catch (PackagedStartupPlatformException exception)
            {
                return new PackagedStartupBootstrapResult(
                    exception.Status,
                    0,
                    exception.NativeError);
            }
            catch (OperationCanceledException)
            {
                return Failure(PackagedStartupBootstrapStatus.Canceled);
            }
            catch (Exception exception)
            {
                if (exception is ArgumentException
                    || exception is IOException
                    || exception is NotSupportedException
                    || exception is UnauthorizedAccessException)
                {
                    return Failure(
                        PackagedStartupBootstrapStatus.UnsafeFileSystemEntry);
                }

                return Failure(PackagedStartupBootstrapStatus.UnexpectedFailure);
            }
        }

        internal static bool IsSameFile(
            PackagedStartupFileIdentity expected,
            PackagedStartupFileIdentity actual)
        {
            return expected != null
                && actual != null
                && (expected.FileIdLow != 0 || expected.FileIdHigh != 0)
                && (actual.FileIdLow != 0 || actual.FileIdHigh != 0)
                && expected.VolumeSerialNumber == actual.VolumeSerialNumber
                && expected.FileIdLow == actual.FileIdLow
                && expected.FileIdHigh == actual.FileIdHigh
                && expected.FileLength == actual.FileLength;
        }

        private static bool IsSafeDirectory(
            OpenedPackagedStartupEntry entry,
            string expectedPath)
        {
            return entry != null
                && entry.IsDirectory
                && !entry.IsReparsePoint
                && PathsEqual(entry.ResolvedPath, expectedPath);
        }

        private static bool IsSafeDirectChild(
            OpenedPackagedStartupEntry entry,
            string expectedPath,
            string expectedParent)
        {
            return entry != null
                && !entry.IsDirectory
                && !entry.IsReparsePoint
                && PathsEqual(entry.ResolvedPath, expectedPath)
                && PathsEqual(
                    Path.GetDirectoryName(entry.ResolvedPath),
                    expectedParent);
        }

        private static bool PathsEqual(string first, string second)
        {
            string normalizedFirst = NormalizeAbsoluteLocalPath(first);
            string normalizedSecond = NormalizeAbsoluteLocalPath(second);
            return normalizedFirst != null
                && normalizedSecond != null
                && String.Equals(
                    normalizedFirst,
                    normalizedSecond,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeAbsoluteLocalPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)
                || path.IndexOf('"') >= 0
                || path.StartsWith(@"\\", StringComparison.Ordinal)
                || path.StartsWith(@"\\?\", StringComparison.Ordinal)
                || path.StartsWith(@"\\.\", StringComparison.Ordinal)
                || !Path.IsPathRooted(path))
            {
                return null;
            }

            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath);
            if (String.IsNullOrEmpty(root)
                || !String.Equals(
                    root,
                    Path.GetPathRoot(path),
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (fullPath.Length > root.Length)
                fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar);
            return fullPath;
        }

        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        private static PackagedStartupBootstrapResult Failure(
            PackagedStartupBootstrapStatus status)
        {
            return new PackagedStartupBootstrapResult(status, 0, 0);
        }
    }
}

#pragma warning restore CS8600, CS8603, CS8604
