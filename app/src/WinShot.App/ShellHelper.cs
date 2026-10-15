using System.Diagnostics;
using System.IO;

namespace WinShot.App;

/// <summary>
/// Launches Explorer by its full, non-relative path. Calling <c>Process.Start("explorer.exe", …)</c>
/// by bare name goes through the CreateProcess search order (app dir + CWD before System32), so a
/// planted explorer.exe in the per-user install directory could run as the user. Resolving the real
/// path closes that hijack vector for the "reveal in folder" / "open folder" actions.
/// </summary>
public static class ShellHelper
{
    private static string ExplorerPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>Opens the containing folder in Explorer with the file selected.</summary>
    public static bool RevealInFolder(string path)
    {
        try
        {
            Process.Start(ExplorerPath, $"/select,\"{path}\"");
            return true;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return false;
        }
    }

    /// <summary>Opens a folder in Explorer.</summary>
    public static void OpenFolder(string directory)
    {
        try
        {
            Process.Start(ExplorerPath, $"\"{directory}\"");
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }
}
