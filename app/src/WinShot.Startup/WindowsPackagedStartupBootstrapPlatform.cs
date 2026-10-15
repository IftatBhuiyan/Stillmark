using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinShot.Startup
{
    internal sealed class WindowsPackagedStartupBootstrapPlatform
        : IPackagedStartupBootstrapPlatform
    {
        private const int ErrorSuccess = 0;
        private const int ErrorInsufficientBuffer = 122;
        private const int AppModelErrorNoPackage = 15700;
        private const uint MaximumPackageFullNameCharacters = 128;
        private const uint MaximumPathCharacters = 32768;
        private const uint GenericRead = 0x80000000;
        private const uint FileShareRead = 0x00000001;
        private const uint OpenExisting = 3;
        private const uint FileAttributeDirectory = 0x00000010;
        private const uint FileAttributeReparsePoint = 0x00000400;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagSequentialScan = 0x08000000;
        private const uint CreateDefaultErrorMode = 0x04000000;
        private const uint DriveFixed = 3;
        private const int FileIdInfoClass = 18;

        public PackagedDeploymentKind DetectDeployment()
        {
            uint requiredCharacters = 0;
            int result = GetCurrentPackageFullName(
                ref requiredCharacters,
                null);
            if (result == AppModelErrorNoPackage && requiredCharacters == 0)
                return PackagedDeploymentKind.Unpackaged;
            if (result == ErrorInsufficientBuffer
                && requiredCharacters >= 2
                && requiredCharacters <= MaximumPackageFullNameCharacters)
            {
                return PackagedDeploymentKind.Packaged;
            }

            return PackagedDeploymentKind.Unknown;
        }

        public string GetCurrentModulePath()
        {
            var buffer = new StringBuilder((int)MaximumPathCharacters);
            uint length = GetModuleFileNameW(
                IntPtr.Zero,
                buffer,
                buffer.Capacity);
            if (length == 0 || length >= buffer.Capacity)
            {
                throw PlatformFailure(
                    PackagedStartupBootstrapStatus.PlatformUnavailable);
            }

            return buffer.ToString();
        }

        public string GetCurrentPackageInstallPath()
        {
            uint requiredCharacters = 0;
            int result = GetCurrentPackagePath2(
                PackagePathTypeInstall,
                ref requiredCharacters,
                null);
            if (result != ErrorInsufficientBuffer
                || requiredCharacters < 4
                || requiredCharacters > MaximumPathCharacters)
            {
                throw new PackagedStartupPlatformException(
                    PackagedStartupBootstrapStatus.InvalidBootstrapLocation,
                    result);
            }

            var buffer = new StringBuilder((int)requiredCharacters);
            uint actualCharacters = requiredCharacters;
            result = GetCurrentPackagePath2(
                PackagePathTypeInstall,
                ref actualCharacters,
                buffer);
            if (result != ErrorSuccess
                || actualCharacters < 4
                || actualCharacters > requiredCharacters)
            {
                throw new PackagedStartupPlatformException(
                    PackagedStartupBootstrapStatus.InvalidBootstrapLocation,
                    result);
            }

            return buffer.ToString();
        }

        public bool IsFixedLocalPath(string path)
        {
            string root;
            try
            {
                root = Path.GetPathRoot(path);
            }
            catch
            {
                return false;
            }

            return !String.IsNullOrEmpty(root)
                && GetDriveTypeW(root) == DriveFixed;
        }

        public OpenedPackagedStartupEntry OpenDirectoryNoReparse(string path)
        {
            return OpenEntry(
                path,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                true);
        }

        public OpenedPackagedStartupEntry OpenFileNoReparse(string path)
        {
            return OpenEntry(
                path,
                FileFlagOpenReparsePoint | FileFlagSequentialScan,
                false);
        }

        public PackagedStartupProcessCreationResult TryCreateProcess(
            PackagedStartupProcessPlan plan)
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
                return new PackagedStartupProcessCreationResult(0, error);
            }

            using (var processHandle =
                new SafeKernelHandle(processInformation.ProcessHandle))
            using (var threadHandle =
                new SafeKernelHandle(processInformation.ThreadHandle))
            {
                return new PackagedStartupProcessCreationResult(
                    processInformation.ProcessId,
                    0);
            }
        }

        private static OpenedPackagedStartupEntry OpenEntry(
            string path,
            uint flags,
            bool expectDirectory)
        {
            SafeFileHandle handle = CreateFileW(
                path,
                GenericRead,
                FileShareRead,
                IntPtr.Zero,
                OpenExisting,
                flags,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new PackagedStartupPlatformException(
                    expectDirectory
                        ? PackagedStartupBootstrapStatus
                            .InvalidBootstrapLocation
                        : PackagedStartupBootstrapStatus.TargetUnavailable,
                    error);
            }

            try
            {
                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(handle, out information))
                    throw PlatformFailure(
                        PackagedStartupBootstrapStatus
                            .UnsafeFileSystemEntry);

                bool isDirectory =
                    (information.FileAttributes & FileAttributeDirectory) != 0;
                bool isReparsePoint =
                    (information.FileAttributes
                        & FileAttributeReparsePoint) != 0;
                if (isDirectory != expectDirectory || isReparsePoint)
                {
                    throw new PackagedStartupPlatformException(
                        PackagedStartupBootstrapStatus.UnsafeFileSystemEntry,
                        0);
                }

                FileIdInformation fileId;
                if (!GetFileInformationByHandleEx(
                        handle,
                        FileIdInfoClass,
                        out fileId,
                        (uint)Marshal.SizeOf(typeof(FileIdInformation))))
                {
                    throw PlatformFailure(
                        PackagedStartupBootstrapStatus
                            .UnsafeFileSystemEntry);
                }

                if (fileId.Identifier.Low == 0
                    && fileId.Identifier.High == 0)
                {
                    throw new PackagedStartupPlatformException(
                        PackagedStartupBootstrapStatus.UnsafeFileSystemEntry,
                        0);
                }

                ulong unsignedLength =
                    ((ulong)information.FileSizeHigh << 32)
                    | information.FileSizeLow;
                if (unsignedLength > Int64.MaxValue)
                {
                    throw new PackagedStartupPlatformException(
                        PackagedStartupBootstrapStatus.UnsafeFileSystemEntry,
                        0);
                }

                string resolvedPath = ResolveOpenedPath(handle);
                var identity = new PackagedStartupFileIdentity(
                    fileId.VolumeSerialNumber,
                    fileId.Identifier.Low,
                    fileId.Identifier.High,
                    (long)unsignedLength);
                SafeFileHandle retainedHandle = handle;
                handle = null;
                return new OpenedPackagedStartupEntry(
                    resolvedPath,
                    identity,
                    isDirectory,
                    isReparsePoint,
                    retainedHandle);
            }
            finally
            {
                if (handle != null)
                    handle.Dispose();
            }
        }

        private static string ResolveOpenedPath(SafeFileHandle handle)
        {
            var buffer = new StringBuilder((int)MaximumPathCharacters);
            uint length = GetFinalPathNameByHandleW(
                handle,
                buffer,
                (uint)buffer.Capacity,
                0);
            if (length == 0 || length >= buffer.Capacity)
            {
                throw PlatformFailure(
                    PackagedStartupBootstrapStatus.UnsafeFileSystemEntry);
            }

            string resolved = buffer.ToString();
            const string uncPrefix = @"\\?\UNC\";
            const string devicePrefix = @"\\?\";
            if (resolved.StartsWith(
                    uncPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + resolved.Substring(uncPrefix.Length);
            }
            if (resolved.StartsWith(
                    devicePrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return resolved.Substring(devicePrefix.Length);
            }
            return resolved;
        }

        private static PackagedStartupPlatformException PlatformFailure(
            PackagedStartupBootstrapStatus status)
        {
            return new PackagedStartupPlatformException(
                status,
                Marshal.GetLastWin32Error());
        }

        private static void CloseUnexpectedHandles(
            ProcessInformation information)
        {
            if (information.ThreadHandle != IntPtr.Zero)
            {
                using (var thread =
                    new SafeKernelHandle(information.ThreadHandle))
                {
                }
            }

            if (information.ProcessHandle != IntPtr.Zero)
            {
                using (var process =
                    new SafeKernelHandle(information.ProcessHandle))
                {
                }
            }
        }

        private const int PackagePathTypeInstall = 0;

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetCurrentPackageFullName",
            CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFullName(
            ref uint packageFullNameLength,
            StringBuilder packageFullName);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetCurrentPackagePath2",
            CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackagePath2(
            int packagePathType,
            ref uint pathLength,
            StringBuilder path);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetModuleFileNameW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern uint GetModuleFileNameW(
            IntPtr module,
            StringBuilder fileName,
            int size);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetDriveTypeW",
            CharSet = CharSet.Unicode)]
        private static extern uint GetDriveTypeW(string rootPathName);

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

        private sealed class SafeKernelHandle : SafeHandle
        {
            internal SafeKernelHandle(IntPtr handle)
                : base(IntPtr.Zero, true)
            {
                SetHandle(handle);
            }

            public override bool IsInvalid
            {
                get
                {
                    return handle == IntPtr.Zero
                        || handle == new IntPtr(-1);
                }
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
