using System.Drawing;
using System.Windows;

namespace WinShot.App;

/// <summary>Loads the embedded identity asset for notification-area use.</summary>
/// <remarks>The executable and notification area intentionally share one embedded asset.</remarks>
public static class IconFactory
{
    public static Icon CreateAppIcon()
    {
        var resource = Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/winshot.ico"));
        if (resource is null)
            throw new InvalidOperationException("The embedded Stillmark icon could not be loaded.");

        using (resource.Stream)
        using (var icon = new Icon(resource.Stream, 32, 32))
            return (Icon)icon.Clone();
    }
}
