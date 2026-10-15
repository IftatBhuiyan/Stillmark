using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinShot.Core.Update;

public enum UpdateProcessLaunchStatus
{
    Launched,
    InvalidInput,
    PlatformUnavailable,
    LeaseUnavailable,
    PackagePathMismatch,
    PackageIdentityMismatch,
    UnsafeFilesystemEntry,
    ProcessCreationFailed,
}

public sealed record UpdateProcessLaunchResult(
    UpdateProcessLaunchStatus Status,
    uint? ProcessId = null,
    int NativeErrorCode = 0)
{
    public bool Launched =>
        Status == UpdateProcessLaunchStatus.Launched && ProcessId.HasValue;
}

/// <summary>
/// Consumes an update execution lease and performs a lease-protected pathname launch without
/// shell interpretation. Windows process creation remains path-based: the retained authoritative
/// handle and a just-reopened matching handle bound the path race, but CreateProcessW does not
/// create the process from either handle. A successful result means only that CreateProcessW
/// created the process; it does not prove that the child initialized or accepted an update.
/// </summary>
public static class UpdateProcessLauncher
{
    internal const int MaximumCommandLineCharacters = 32_767;

    public static UpdateProcessLaunchResult TryLaunch(
        UpdateExecutionLease? executionLease) =>
        TryLaunch(
            executionLease,
            WindowsUpdateProcessNative.Instance);

    internal static UpdateProcessLaunchResult TryLaunch(
        UpdateExecutionLease? executionLease,
        IUpdateProcessNative native)
    {
        if (executionLease is null || native is null)
        {
            return new UpdateProcessLaunchResult(
                UpdateProcessLaunchStatus.InvalidInput);
        }

        SafeFileHandle? authoritativeHandle = null;
        bool launchHandleAcquired = false;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return new UpdateProcessLaunchResult(
                    UpdateProcessLaunchStatus.PlatformUnavailable);
            }

            if (!executionLease.TryAcquireLaunchHandle(
                    out authoritativeHandle)
                || authoritativeHandle is null)
            {
                return new UpdateProcessLaunchResult(
                    UpdateProcessLaunchStatus.LeaseUnavailable);
            }
            launchHandleAcquired = true;

            UpdatePackageFileIdentity authoritativeIdentity =
                UpdatePackageFileHandle.CaptureIdentity(authoritativeHandle);
            if (!UpdatePackageFileHandle.IsSameFile(
                    executionLease.PackageFileIdentity,
                    authoritativeIdentity))
            {
                return new UpdateProcessLaunchResult(
                    UpdateProcessLaunchStatus.PackageIdentityMismatch);
            }

            string authoritativePath =
                UpdatePackageFileHandle.ResolveOpenedPath(authoritativeHandle);
            string declaredPath = Path.GetFullPath(executionLease.PackagePath);
            if (!authoritativePath.Equals(
                    declaredPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateProcessLaunchResult(
                    UpdateProcessLaunchStatus.PackagePathMismatch);
            }

            if (!TryCreateLaunchPlan(
                    authoritativePath,
                    out UpdateProcessLaunchPlan? plan)
                || plan is null)
            {
                return new UpdateProcessLaunchResult(
                    UpdateProcessLaunchStatus.InvalidInput);
            }

            OpenedUpdateFile reopened = UpdatePackageFileHandle.OpenReadNoReparse(
                authoritativePath,
                bufferSize: 4096);
            using FileStream? reopenedLease = reopened.Stream;
            if (reopened.IsUnsafeFilesystemEntry)
            {
                return new UpdateProcessLaunchResult(
                    UpdateProcessLaunchStatus.UnsafeFilesystemEntry);
            }

            if (reopenedLease is null
                || reopened.Identity is null
                || !UpdatePackageFileHandle.IsSameFile(
                    authoritativeIdentity,
                    reopened.Identity))
            {
                return new UpdateProcessLaunchResult(
                    UpdateProcessLaunchStatus.PackageIdentityMismatch);
            }

            UpdatePackageFileIdentity finalIdentity =
                UpdatePackageFileHandle.CaptureIdentity(authoritativeHandle);
            string finalPath =
                UpdatePackageFileHandle.ResolveOpenedPath(authoritativeHandle);
            if (!UpdatePackageFileHandle.IsSameFile(
                    authoritativeIdentity,
                    finalIdentity))
            {
                return new UpdateProcessLaunchResult(
                    UpdateProcessLaunchStatus.PackageIdentityMismatch);
            }

            if (!finalPath.Equals(
                    authoritativePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateProcessLaunchResult(
                    UpdateProcessLaunchStatus.PackagePathMismatch);
            }

            UpdateNativeProcessCreationResult created =
                native.TryCreateProcess(plan);
            using UpdateCreatedProcess? process = created.Process;
            if (process is null)
            {
                return new UpdateProcessLaunchResult(
                    UpdateProcessLaunchStatus.ProcessCreationFailed,
                    NativeErrorCode: created.ErrorCode);
            }

            return new UpdateProcessLaunchResult(
                UpdateProcessLaunchStatus.Launched,
                process.ProcessId);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return new UpdateProcessLaunchResult(
                UpdateProcessLaunchStatus.LeaseUnavailable);
        }
        catch (Exception ex) when (ex is DllNotFoundException
            or EntryPointNotFoundException
            or PlatformNotSupportedException)
        {
            return new UpdateProcessLaunchResult(
                UpdateProcessLaunchStatus.PlatformUnavailable);
        }
        finally
        {
            if (launchHandleAcquired)
            {
                authoritativeHandle!.DangerousRelease();
                executionLease.Dispose();
            }
        }
    }

    internal static bool TryCreateLaunchPlan(
        string? applicationPath,
        out UpdateProcessLaunchPlan? plan)
    {
        plan = null;
        if (string.IsNullOrWhiteSpace(applicationPath)
            || !Path.IsPathFullyQualified(applicationPath)
            || applicationPath.StartsWith(@"\\", StringComparison.Ordinal)
            || applicationPath.Contains('"')
            || !Path.GetExtension(applicationPath).Equals(
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fullPath;
        string? workingDirectory;
        try
        {
            fullPath = Path.GetFullPath(applicationPath);
            workingDirectory = Path.GetDirectoryName(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or NotSupportedException)
        {
            return false;
        }

        if (!fullPath.Equals(applicationPath, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(workingDirectory))
        {
            return false;
        }

        string commandLine = $"\"{fullPath}\"";
        if (commandLine.Length >= MaximumCommandLineCharacters)
            return false;

        plan = new UpdateProcessLaunchPlan(
            fullPath,
            commandLine,
            workingDirectory,
            InheritHandles: false);
        return true;
    }
}

internal sealed record UpdateProcessLaunchPlan(
    string ApplicationName,
    string CommandLine,
    string WorkingDirectory,
    bool InheritHandles);

internal interface IUpdateProcessNative
{
    UpdateNativeProcessCreationResult TryCreateProcess(
        UpdateProcessLaunchPlan plan);
}

internal readonly record struct UpdateNativeProcessCreationResult(
    UpdateCreatedProcess? Process,
    int ErrorCode);

internal sealed class UpdateCreatedProcess : IDisposable
{
    private SafeHandle? _processHandle;
    private SafeHandle? _threadHandle;

    internal UpdateCreatedProcess(
        SafeHandle processHandle,
        SafeHandle threadHandle,
        uint processId)
    {
        _processHandle = processHandle;
        _threadHandle = threadHandle;
        ProcessId = processId;
    }

    internal uint ProcessId { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _threadHandle, null)?.Dispose();
        Interlocked.Exchange(ref _processHandle, null)?.Dispose();
    }
}

internal sealed class WindowsUpdateProcessNative : IUpdateProcessNative
{
    private const uint CreateDefaultErrorMode = 0x04000000;

    internal static WindowsUpdateProcessNative Instance { get; } = new();

    private WindowsUpdateProcessNative()
    {
    }

    public UpdateNativeProcessCreationResult TryCreateProcess(
        UpdateProcessLaunchPlan plan)
    {
        var commandLine = new StringBuilder(
            plan.CommandLine,
            plan.CommandLine.Length + 1);
        var startup = new StartupInfo
        {
            Size = (uint)Marshal.SizeOf<StartupInfo>(),
        };

        bool created = CreateProcessW(
            plan.ApplicationName,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            plan.InheritHandles,
            CreateDefaultErrorMode,
            IntPtr.Zero,
            plan.WorkingDirectory,
            ref startup,
            out ProcessInformation processInformation);
        if (!created)
        {
            int error = Marshal.GetLastWin32Error();
            CloseUnexpectedHandles(processInformation);
            return new UpdateNativeProcessCreationResult(
                Process: null,
                ErrorCode: error);
        }

        var processHandle = new SafeKernelHandle(
            processInformation.ProcessHandle,
            ownsHandle: true);
        var threadHandle = new SafeKernelHandle(
            processInformation.ThreadHandle,
            ownsHandle: true);
        return new UpdateNativeProcessCreationResult(
            new UpdateCreatedProcess(
                processHandle,
                threadHandle,
                processInformation.ProcessId),
            ErrorCode: 0);
    }

    private static void CloseUnexpectedHandles(ProcessInformation information)
    {
        if (information.ThreadHandle != IntPtr.Zero)
        {
            using var thread = new SafeKernelHandle(
                information.ThreadHandle,
                ownsHandle: true);
        }

        if (information.ProcessHandle != IntPtr.Zero)
        {
            using var process = new SafeKernelHandle(
                information.ProcessHandle,
                ownsHandle: true);
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
        internal string? Reserved;
        internal string? Desktop;
        internal string? Title;
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
        internal SafeKernelHandle(IntPtr handle, bool ownsHandle)
            : base(ownsHandle)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
