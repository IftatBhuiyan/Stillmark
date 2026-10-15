using Microsoft.Win32;
using System.Runtime.InteropServices;
using WinShot.Core.Startup;

namespace WinShot.App;

internal static class ImageFileRegistration
{
    private const string ProgId = "Stillmark.Image";
    private const string Capabilities = @"Software\Stillmark\Capabilities";
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg"];

    internal static void RegisterInstalled()
    {
        try
        {
            var layout = InstalledLaunchPolicy.DiscoverInstalledLayout(
                Environment.ProcessPath!, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (layout is null) return;
            string command = $"\"{layout.StableLauncherPath}\" --open \"%1\"";
            Set(@"Software\Classes\" + ProgId, "", "Stillmark image");
            Set(@"Software\Classes\" + ProgId + @"\Application", "ApplicationName", "Stillmark");
            Set(@"Software\Classes\" + ProgId + @"\DefaultIcon", "", $"\"{layout.StableLauncherPath}\",0");
            Set(@"Software\Classes\" + ProgId + @"\shell\open\command", "", command);
            Set(Capabilities, "ApplicationName", "Stillmark");
            Set(Capabilities, "ApplicationDescription", "Open images in the Stillmark editor.");
            foreach (string extension in Extensions)
            {
                Set(@"Software\Classes\" + extension + @"\OpenWithProgids", ProgId, "");
                Set(Capabilities + @"\FileAssociations", extension, ProgId);
            }
            Set(@"Software\RegisteredApplications", "Stillmark", Capabilities);
            NotifyShell();
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    internal static void Unregister()
    {
        using var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true);
        if (registered?.GetValue("Stillmark") as string != Capabilities) return;
        registered.DeleteValue("Stillmark", false);
        foreach (string extension in Extensions)
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids", true);
            key?.DeleteValue(ProgId, false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false);
        Registry.CurrentUser.DeleteSubKeyTree(Capabilities, false);
        NotifyShell();
    }

    private static void Set(string path, string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path);
        key.SetValue(name, value);
    }

    private static void NotifyShell() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
