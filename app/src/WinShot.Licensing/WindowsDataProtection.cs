using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace WinShot.Licensing;

/// <summary>
/// Narrow dependency-free wrapper over Windows DPAPI. Protection is scoped to the current Windows
/// user by the operating system; UI is forbidden so background startup can never display a system
/// credential prompt.
/// </summary>
internal static class WindowsDataProtection
{
    private const uint CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    public static byte[] Protect(byte[] plaintext, byte[] entropy) =>
        Transform(plaintext, entropy, protect: true);

    public static byte[] Unprotect(byte[] protectedBytes, byte[] entropy) =>
        Transform(protectedBytes, entropy, protect: false);

    private static byte[] Transform(byte[] input, byte[] entropy, bool protect)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(entropy);
        if (input.Length == 0)
            throw new ArgumentException("DPAPI input cannot be empty.", nameof(input));

        DataBlob inputBlob = Allocate(input);
        DataBlob entropyBlob = Allocate(entropy);
        DataBlob outputBlob = default;
        try
        {
            bool succeeded = protect
                ? CryptProtectData(
                    ref inputBlob,
                    null,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob);

            if (!succeeded)
            {
                int error = Marshal.GetLastWin32Error();
                throw new CryptographicException(
                    protect ? "Windows could not protect the license record."
                        : "Windows could not unprotect the license record.",
                    new Win32Exception(error));
            }

            if (outputBlob.Data == IntPtr.Zero || outputBlob.Size <= 0)
                throw new CryptographicException("Windows returned an empty DPAPI record.");

            var output = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            FreeAllocated(inputBlob);
            FreeAllocated(entropyBlob);
            if (outputBlob.Data != IntPtr.Zero)
                _ = LocalFree(outputBlob.Data);
        }
    }

    private static DataBlob Allocate(byte[] bytes)
    {
        if (bytes.Length == 0)
            return default;

        IntPtr data = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, data, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = data };
    }

    private static void FreeAllocated(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
            Marshal.FreeHGlobal(blob.Data);
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
