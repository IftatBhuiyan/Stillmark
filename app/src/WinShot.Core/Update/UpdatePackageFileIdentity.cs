using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinShot.Core.Update;

/// <summary>
/// Stable Windows identity captured from the already opened package handle.
/// </summary>
public sealed class UpdatePackageFileIdentity
{
    internal UpdatePackageFileIdentity(
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

    public ulong VolumeSerialNumber { get; }

    public ulong FileIdLow { get; }

    public ulong FileIdHigh { get; }

    public long FileLength { get; }
}

internal sealed record OpenedUpdateFile(
    FileStream? Stream,
    UpdatePackageFileIdentity? Identity,
    bool IsUnsafeFilesystemEntry);

internal static class UpdatePackageFileHandle
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int FileIdInfoClass = 18;

    internal static OpenedUpdateFile OpenReadNoReparse(string path, int bufferSize)
    {
        SafeFileHandle handle = CreateFileW(
            path,
            GenericRead,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagSequentialScan | FileFlagOverlapped,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                "Unable to open the staged update payload.",
                new Win32Exception(error));
        }

        try
        {
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
            {
                throw new IOException(
                    "Unable to identify the staged update payload.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
                return new OpenedUpdateFile(null, null, IsUnsafeFilesystemEntry: true);

            string openedPath = ResolveOpenedPath(handle);
            string expectedPath = Path.GetFullPath(path);
            if (!openedPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                return new OpenedUpdateFile(null, null, IsUnsafeFilesystemEntry: true);

            if (!GetFileInformationByHandleEx(
                    handle,
                    FileIdInfoClass,
                    out FileIdInformation fileId,
                    (uint)Marshal.SizeOf<FileIdInformation>()))
            {
                throw new IOException(
                    "Unable to capture the staged update payload identity.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            if (!HasUsableFileId(
                    fileId.Identifier.Low,
                    fileId.Identifier.High))
            {
                throw new IOException(
                    "The staged update payload did not provide a usable file identity.");
            }

            ulong unsignedLength = ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;
            if (unsignedLength > long.MaxValue)
                throw new IOException("The staged update payload length is unsupported.");

            var identity = new UpdatePackageFileIdentity(
                fileId.VolumeSerialNumber,
                fileId.Identifier.Low,
                fileId.Identifier.High,
                (long)unsignedLength);
            var stream = new FileStream(
                handle,
                FileAccess.Read,
                bufferSize,
                isAsync: true);
            handle = null!;
            return new OpenedUpdateFile(
                stream,
                identity,
                IsUnsafeFilesystemEntry: false);
        }
        finally
        {
            handle?.Dispose();
        }
    }

    internal static bool IsSameFile(
        UpdatePackageFileIdentity expected,
        UpdatePackageFileIdentity actual) =>
        HasUsableFileId(expected.FileIdLow, expected.FileIdHigh)
        && HasUsableFileId(actual.FileIdLow, actual.FileIdHigh)
        && expected.VolumeSerialNumber == actual.VolumeSerialNumber
        && expected.FileIdLow == actual.FileIdLow
        && expected.FileIdHigh == actual.FileIdHigh
        && expected.FileLength == actual.FileLength;

    internal static bool HasUsableFileId(ulong fileIdLow, ulong fileIdHigh) =>
        fileIdLow != 0 || fileIdHigh != 0;

    internal static UpdatePackageFileIdentity CaptureIdentity(SafeFileHandle handle)
    {
        if (handle.IsInvalid || handle.IsClosed)
            throw new IOException("The staged update payload handle is unavailable.");

        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
        {
            throw new IOException(
                "Unable to identify the staged update payload.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
            throw new IOException("The staged update payload is a reparse point.");

        if (!GetFileInformationByHandleEx(
                handle,
                FileIdInfoClass,
                out FileIdInformation fileId,
                (uint)Marshal.SizeOf<FileIdInformation>()))
        {
            throw new IOException(
                "Unable to capture the staged update payload identity.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        if (!HasUsableFileId(fileId.Identifier.Low, fileId.Identifier.High))
        {
            throw new IOException(
                "The staged update payload did not provide a usable file identity.");
        }

        ulong unsignedLength = ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;
        if (unsignedLength > long.MaxValue)
            throw new IOException("The staged update payload length is unsupported.");

        return new UpdatePackageFileIdentity(
            fileId.VolumeSerialNumber,
            fileId.Identifier.Low,
            fileId.Identifier.High,
            (long)unsignedLength);
    }

    internal static string ResolveOpenedPath(SafeFileHandle handle)
    {
        if (handle.IsInvalid || handle.IsClosed)
            throw new IOException("The staged update payload handle is unavailable.");

        var buffer = new StringBuilder(32_768);
        uint length = GetFinalPathNameByHandleW(
            handle,
            buffer,
            (uint)buffer.Capacity,
            flags: 0);
        if (length == 0 || length >= buffer.Capacity)
        {
            throw new IOException(
                "Unable to resolve the staged update payload path.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        const string uncPrefix = @"\\?\UNC\";
        const string devicePrefix = @"\\?\";
        string resolved = buffer.ToString();
        if (resolved.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + resolved[uncPrefix.Length..];
        if (resolved.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase))
            return resolved[devicePrefix.Length..];
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
