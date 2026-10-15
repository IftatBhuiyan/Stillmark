using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace WinShot.Licensing;

/// <summary>A computer binding shared across Windows users. Only the product-specific hash
/// goes to the account API. Missing Windows identity fails closed instead of identifying every
/// such computer as the same device.</summary>
public static class LicenseDeviceIdentity
{
    private static readonly Lazy<string?> Identity = new(Read);
    public static string? Current => Identity.Value;

    private static string? Read()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
            if (key?.GetValue("MachineGuid") is not string value || !Guid.TryParse(value, out var guid))
                return null;
            return Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes("relic-screenshot-device-v1|" + guid.ToString("D")))).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
