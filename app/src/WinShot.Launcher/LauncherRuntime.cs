using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

#pragma warning disable CS8600, CS8602, CS8625

namespace WinShot.Launcher
{
    internal interface ILauncherTrustVerifier
    {
        bool TryAcquireTrust(
            SafeFileHandle launcherHandle,
            string launcherPath,
            SafeFileHandle payloadHandle,
            string payloadPath,
            string versionDirectory,
            string activeVersion,
            LauncherConfiguration configuration,
            out IDisposable retainedTrust);
    }

    internal interface ILauncherProcessCreator
    {
        LauncherProcessCreationResult TryCreateProcess(LauncherProcessPlan plan);
    }

    internal sealed class LauncherProcessPlan
    {
        internal LauncherProcessPlan(
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

        internal bool InheritHandles
        {
            get { return false; }
        }
    }

    internal sealed class LauncherProcessCreationResult
    {
        internal LauncherProcessCreationResult(uint processId, int errorCode)
        {
            ProcessId = processId;
            ErrorCode = errorCode;
        }

        internal uint ProcessId { get; private set; }

        internal int ErrorCode { get; private set; }
    }

    internal sealed class LauncherFileIdentity
    {
        internal LauncherFileIdentity(
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

    internal sealed class OpenedLauncherFile : IDisposable
    {
        private FileStream _stream;

        internal OpenedLauncherFile(
            FileStream stream,
            LauncherFileIdentity identity,
            string resolvedPath)
        {
            _stream = stream;
            Identity = identity;
            ResolvedPath = resolvedPath;
        }

        internal SafeFileHandle Handle
        {
            get
            {
                if (_stream == null)
                    throw new ObjectDisposedException("OpenedLauncherFile");
                return _stream.SafeFileHandle;
            }
        }

        internal LauncherFileIdentity Identity { get; private set; }

        internal string ResolvedPath { get; private set; }

        internal FileStream Stream
        {
            get
            {
                if (_stream == null)
                    throw new ObjectDisposedException("OpenedLauncherFile");
                return _stream;
            }
        }

        public void Dispose()
        {
            FileStream stream = _stream;
            _stream = null;
            if (stream != null)
                stream.Dispose();
        }
    }

    internal static class StableLauncherRuntime
    {
        internal const int MaximumCommandLineCharacters = 32767;
        private const int MaximumActivationPointerBytes =
            LauncherConfiguration.MaximumVersionSegmentLength;

        internal static LauncherResult TryLaunch(
            string launcherPath,
            string localApplicationDataPath,
            IList<string> arguments,
            LauncherConfiguration configuration)
        {
            return TryLaunch(
                launcherPath,
                localApplicationDataPath,
                arguments,
                configuration,
                new WindowsLauncherTrustVerifier(),
                new WindowsLauncherProcessCreator());
        }

        internal static LauncherResult TryLaunch(
            string launcherPath,
            string localApplicationDataPath,
            IList<string> arguments,
            LauncherConfiguration configuration,
            ILauncherTrustVerifier trustVerifier,
            ILauncherProcessCreator processCreator)
        {
            LauncherMode mode;
            string childArguments;
            if (arguments == null
                || configuration == null
                || trustVerifier == null
                || processCreator == null
                || !LauncherArgumentPolicy.TryParse(
                    arguments,
                    out mode,
                    out childArguments))
            {
                return Failure(
                    LauncherStatus.InvalidArguments,
                    LauncherMode.Interactive);
            }

            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                return Failure(LauncherStatus.PlatformUnavailable, mode);

            string initialInstallRoot;
            string pointerPath;
            if (!TryResolveInitialPaths(
                    launcherPath,
                    localApplicationDataPath,
                    configuration,
                    out initialInstallRoot,
                    out pointerPath))
            {
                return Failure(LauncherStatus.InvalidInstallation, mode);
            }

            LauncherStatus unavailableStatus =
                LauncherStatus.InvalidInstallation;
            try
            {
                using (OpenedLauncherFile launcher =
                    LauncherFileHandle.OpenReadNoReparse(launcherPath))
                {
                    if (!String.Equals(
                            launcher.ResolvedPath,
                            launcherPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return Failure(LauncherStatus.UnsafeFileSystemEntry, mode);
                    }

                    unavailableStatus =
                        LauncherStatus.ActivationPointerUnavailable;
                    string activeVersion;
                    using (OpenedLauncherFile pointer =
                        LauncherFileHandle.OpenReadNoReparse(pointerPath))
                    {
                        if (!String.Equals(
                                pointer.ResolvedPath,
                                pointerPath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return Failure(
                                LauncherStatus.UnsafeFileSystemEntry,
                                mode);
                        }

                        if (!TryReadActivationPointer(
                                pointer.Stream,
                                out activeVersion))
                        {
                            return Failure(
                                LauncherStatus.ActivationPointerInvalid,
                                mode);
                        }
                        unavailableStatus = LauncherStatus.PayloadUnavailable;
                        LauncherPaths paths;
                        if (!LauncherPathPolicy.TryResolveInstallation(
                                launcherPath,
                                localApplicationDataPath,
                                activeVersion,
                                configuration,
                                out paths))
                        {
                            return Failure(LauncherStatus.InvalidInstallation, mode);
                        }

                        using (OpenedLauncherFile payload =
                            LauncherFileHandle.OpenReadNoReparse(paths.PayloadPath))
                        {
                        if (!String.Equals(
                                payload.ResolvedPath,
                                paths.PayloadPath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return Failure(
                                LauncherStatus.UnsafeFileSystemEntry,
                                mode);
                        }

                        IDisposable retainedTrust;
                        bool trustAcquired = trustVerifier.TryAcquireTrust(
                                launcher.Handle,
                                launcher.ResolvedPath,
                                payload.Handle,
                                payload.ResolvedPath,
                                paths.VersionDirectory,
                                activeVersion,
                                configuration,
                                out retainedTrust);
                        if (!trustAcquired || retainedTrust == null)
                        {
                            if (retainedTrust != null)
                                retainedTrust.Dispose();
                            return Failure(
                                LauncherStatus.PayloadTrustRejected,
                                mode);
                        }

                        using (retainedTrust)
                        using (OpenedLauncherFile reopened =
                            LauncherFileHandle.OpenReadNoReparse(paths.PayloadPath))
                        {
                            if (!String.Equals(
                                    reopened.ResolvedPath,
                                    paths.PayloadPath,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                return Failure(
                                    LauncherStatus.UnsafeFileSystemEntry,
                                    mode);
                            }

                            if (!LauncherFileHandle.IsSameFile(
                                    payload.Identity,
                                    reopened.Identity))
                            {
                                return Failure(
                                    LauncherStatus.PayloadIdentityChanged,
                                    mode);
                            }

                            LauncherProcessPlan plan;
                            if (!TryCreateLaunchPlan(
                                    paths.PayloadPath,
                                    childArguments,
                                    out plan))
                            {
                                return Failure(
                                    LauncherStatus.InvalidInstallation,
                                    mode);
                            }

                            LauncherProcessCreationResult created =
                                processCreator.TryCreateProcess(plan);
                            if (created == null || created.ProcessId == 0)
                            {
                                return new LauncherResult(
                                    LauncherStatus.ProcessCreationFailed,
                                    mode,
                                    created == null ? 0 : created.ErrorCode,
                                    0);
                            }

                            return new LauncherResult(
                                LauncherStatus.Launched,
                                mode,
                                0,
                                created.ProcessId);
                        }
                        }
                    }
                }
            }
            catch (FileNotFoundException)
            {
                return Failure(unavailableStatus, mode);
            }
            catch (DirectoryNotFoundException)
            {
                return Failure(unavailableStatus, mode);
            }
            catch (UnauthorizedAccessException)
            {
                return Failure(LauncherStatus.UnsafeFileSystemEntry, mode);
            }
            catch (IOException)
            {
                return Failure(unavailableStatus, mode);
            }
            catch (DllNotFoundException)
            {
                return Failure(LauncherStatus.PlatformUnavailable, mode);
            }
            catch (EntryPointNotFoundException)
            {
                return Failure(LauncherStatus.PlatformUnavailable, mode);
            }
            catch
            {
                return Failure(LauncherStatus.UnexpectedFailure, mode);
            }
        }

        internal static bool TryCreateLaunchPlan(
            string payloadPath,
            string childArguments,
            out LauncherProcessPlan plan)
        {
            plan = null;
            if (String.IsNullOrWhiteSpace(payloadPath)
                || !Path.IsPathRooted(payloadPath)
                || payloadPath.StartsWith(@"\\", StringComparison.Ordinal)
                || payloadPath.IndexOf('"') >= 0
                || !payloadPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || (childArguments.Length != 0
                    && !String.Equals(
                        childArguments,
                        "--tray",
                        StringComparison.Ordinal)
                    && !String.Equals(
                        childArguments,
                        "--tray --launcher-recovered",
                        StringComparison.Ordinal)))
            {
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(payloadPath);
            }
            catch (Exception exception)
            {
                if (exception is ArgumentException
                    || exception is IOException
                    || exception is NotSupportedException)
                {
                    return false;
                }

                throw;
            }

            if (!String.Equals(
                    fullPath,
                    payloadPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string workingDirectory = Path.GetDirectoryName(fullPath);
            if (String.IsNullOrWhiteSpace(workingDirectory))
                return false;

            string commandLine = "\"" + fullPath + "\"";
            if (childArguments.Length != 0)
                commandLine += " " + childArguments;
            if (commandLine.Length >= MaximumCommandLineCharacters)
                return false;

            plan = new LauncherProcessPlan(
                fullPath,
                commandLine,
                workingDirectory);
            return true;
        }

        internal static bool TryReadActivationPointer(
            Stream stream,
            out string activeVersion)
        {
            activeVersion = null;
            if (stream == null || !stream.CanRead || !stream.CanSeek)
                return false;
            if (stream.Length <= 0 || stream.Length > MaximumActivationPointerBytes)
                return false;

            stream.Position = 0;
            byte[] bytes = new byte[(int)stream.Length];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read <= 0)
                    return false;
                offset += read;
            }

            char[] characters = new char[bytes.Length];
            for (int index = 0; index < bytes.Length; index++)
            {
                byte value = bytes[index];
                if (value > 0x7F)
                    return false;
                characters[index] = (char)value;
            }

            string candidate = new string(characters);
            if (!LauncherPathPolicy.IsSafeVersionSegment(candidate))
                return false;

            activeVersion = candidate;
            return true;
        }

        private static bool TryResolveInitialPaths(
            string launcherPath,
            string localApplicationDataPath,
            LauncherConfiguration configuration,
            out string installRoot,
            out string pointerPath)
        {
            installRoot = null;
            pointerPath = null;
            if (String.IsNullOrWhiteSpace(launcherPath)
                || String.IsNullOrWhiteSpace(localApplicationDataPath)
                || launcherPath.StartsWith(@"\\", StringComparison.Ordinal)
                || launcherPath.IndexOf('"') >= 0
                || !Path.IsPathRooted(launcherPath)
                || !Path.IsPathRooted(localApplicationDataPath))
            {
                return false;
            }

            string localRoot;
            try
            {
                localRoot = LauncherPathPolicy.TrimEndingDirectorySeparators(
                    Path.GetFullPath(localApplicationDataPath));
                installRoot = Path.Combine(localRoot, configuration.PackageId);
                string expectedLauncher = Path.Combine(
                    installRoot,
                    configuration.ExecutableName);
                if (!String.Equals(
                        Path.GetFullPath(launcherPath),
                        expectedLauncher,
                        StringComparison.OrdinalIgnoreCase))
                {
                    installRoot = null;
                    return false;
                }

                pointerPath = Path.Combine(installRoot, "active-version");
                return true;
            }
            catch (Exception exception)
            {
                if (exception is ArgumentException
                    || exception is IOException
                    || exception is NotSupportedException)
                {
                    installRoot = null;
                    pointerPath = null;
                    return false;
                }

                throw;
            }
        }

        private static LauncherResult Failure(
            LauncherStatus status,
            LauncherMode mode)
        {
            return new LauncherResult(status, mode, 0, 0);
        }
    }

    internal static class LauncherFileHandle
    {
        private const uint GenericRead = 0x80000000;
        private const uint FileShareRead = 0x00000001;
        private const uint OpenExisting = 3;
        private const uint FileAttributeReparsePoint = 0x00000400;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagSequentialScan = 0x08000000;
        private const int FileIdInfoClass = 18;

        internal static OpenedLauncherFile OpenReadNoReparse(string path)
        {
            SafeFileHandle handle = CreateFileW(
                path,
                GenericRead,
                FileShareRead,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagSequentialScan,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error == 2)
                    throw new FileNotFoundException("The launcher file is unavailable.", path);
                if (error == 3)
                    throw new DirectoryNotFoundException(
                        "The launcher directory is unavailable.");
                throw new IOException(
                    "Unable to open a launcher file.",
                    new Win32Exception(error));
            }

            try
            {
                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(handle, out information))
                {
                    throw new IOException(
                        "Unable to identify a launcher file.",
                        new Win32Exception(Marshal.GetLastWin32Error()));
                }

                if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
                    throw new UnauthorizedAccessException(
                        "A launcher file is a reparse point.");

                FileIdInformation fileId;
                if (!GetFileInformationByHandleEx(
                        handle,
                        FileIdInfoClass,
                        out fileId,
                        (uint)Marshal.SizeOf(typeof(FileIdInformation))))
                {
                    throw new IOException(
                        "Unable to capture launcher file identity.",
                        new Win32Exception(Marshal.GetLastWin32Error()));
                }

                if (fileId.Identifier.Low == 0 && fileId.Identifier.High == 0)
                    throw new IOException("A launcher file has no usable identity.");

                ulong unsignedLength =
                    ((ulong)information.FileSizeHigh << 32)
                    | information.FileSizeLow;
                if (unsignedLength > Int64.MaxValue)
                    throw new IOException("A launcher file is too large.");

                string resolvedPath = ResolveOpenedPath(handle);
                var identity = new LauncherFileIdentity(
                    fileId.VolumeSerialNumber,
                    fileId.Identifier.Low,
                    fileId.Identifier.High,
                    (long)unsignedLength);
                var stream = new FileStream(handle, FileAccess.Read);
                handle = null;
                return new OpenedLauncherFile(stream, identity, resolvedPath);
            }
            finally
            {
                if (handle != null)
                    handle.Dispose();
            }
        }

        internal static bool IsSameFile(
            LauncherFileIdentity expected,
            LauncherFileIdentity actual)
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

        private static string ResolveOpenedPath(SafeFileHandle handle)
        {
            var buffer = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandleW(
                handle,
                buffer,
                (uint)buffer.Capacity,
                0);
            if (length == 0 || length >= buffer.Capacity)
            {
                throw new IOException(
                    "Unable to resolve a launcher file.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            string resolved = buffer.ToString();
            const string uncPrefix = @"\\?\UNC\";
            const string devicePrefix = @"\\?\";
            if (resolved.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
                return @"\\" + resolved.Substring(uncPrefix.Length);
            if (resolved.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase))
                return resolved.Substring(devicePrefix.Length);
            return resolved;
        }

        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateFileW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation fileInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            int fileInformationClass,
            out FileIdInformation fileInformation,
            uint bufferSize);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetFinalPathNameByHandleW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle file,
            StringBuilder filePath,
            uint filePathLength,
            uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            internal uint FileAttributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdInformation
        {
            internal ulong VolumeSerialNumber;
            internal FileId128 Identifier;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileId128
        {
            internal ulong Low;
            internal ulong High;
        }
    }

    internal sealed class WindowsLauncherProcessCreator : ILauncherProcessCreator
    {
        private const uint CreateDefaultErrorMode = 0x04000000;

        public LauncherProcessCreationResult TryCreateProcess(
            LauncherProcessPlan plan)
        {
            if (plan == null)
                throw new ArgumentNullException("plan");

            var commandLine = new StringBuilder(
                plan.CommandLine,
                plan.CommandLine.Length + 1);
            var startup = new StartupInfo();
            startup.Size = (uint)Marshal.SizeOf(typeof(StartupInfo));

            ProcessInformation processInformation;
            bool created = CreateProcessW(
                plan.ApplicationName,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CreateDefaultErrorMode,
                IntPtr.Zero,
                plan.WorkingDirectory,
                ref startup,
                out processInformation);
            if (!created)
            {
                int error = Marshal.GetLastWin32Error();
                CloseUnexpectedHandles(processInformation);
                return new LauncherProcessCreationResult(0, error);
            }

            using (var processHandle = new SafeKernelHandle(
                processInformation.ProcessHandle))
            using (var threadHandle = new SafeKernelHandle(
                processInformation.ThreadHandle))
            {
                return new LauncherProcessCreationResult(
                    processInformation.ProcessId,
                    0);
            }
        }

        private static void CloseUnexpectedHandles(ProcessInformation information)
        {
            if (information.ThreadHandle != IntPtr.Zero)
            {
                using (var thread = new SafeKernelHandle(information.ThreadHandle))
                {
                }
            }

            if (information.ProcessHandle != IntPtr.Zero)
            {
                using (var process = new SafeKernelHandle(information.ProcessHandle))
                {
                }
            }
        }

        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateProcessW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            internal uint Size;
            internal string Reserved;
            internal string Desktop;
            internal string Title;
            internal uint X;
            internal uint Y;
            internal uint XSize;
            internal uint YSize;
            internal uint XCountChars;
            internal uint YCountChars;
            internal uint FillAttribute;
            internal uint Flags;
            internal ushort ShowWindow;
            internal ushort Reserved2Size;
            internal IntPtr Reserved2;
            internal IntPtr StandardInput;
            internal IntPtr StandardOutput;
            internal IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            internal IntPtr ProcessHandle;
            internal IntPtr ThreadHandle;
            internal uint ProcessId;
            internal uint ThreadId;
        }

        private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            internal SafeKernelHandle(IntPtr handle)
                : base(true)
            {
                SetHandle(handle);
            }

            protected override bool ReleaseHandle()
            {
                return CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
