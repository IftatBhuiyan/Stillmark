using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace WinShot.Licensing;

/// <summary>
/// A stable pseudonymous machine identifier. It is derived from the Windows MachineGuid and current
/// user name, then hashed so the raw source identifiers are not persisted by WinShot. A stable hash
/// can still be identifying when linked with other data, so it must not be logged or transmitted
/// without an explicit disclosed purpose. Provider activation uses a generic device label instead.
/// The fingerprint binds local storage to this Windows user; it is not hardware-locked DRM.
/// </summary>
public static class MachineFingerprint
{
    private static string? _cached;

    public static string Current => _cached ??= Compute();

    private static string Compute()
    {
        string machineGuid = ReadMachineGuid() ?? "no-machine-guid";
        string user = Environment.UserName;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"winshot|{machineGuid}|{user}"));
        // 128 bits is plenty to distinguish machines and keeps the value compact.
        return Convert.ToHexString(hash, 0, 16);
    }

    private static string? ReadMachineGuid()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography", writable: false);
            return key?.GetValue("MachineGuid") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
