using System;
using System.Windows;
using Velopack;

namespace WinShot.App;

/// <summary>
/// The real process entrypoint. Velopack must see its install/update hook arguments before WPF
/// creates the application object; normal update checking and applying remain application-owned.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnAfterInstallFastCallback(_ => ImageFileRegistration.RegisterInstalled())
            .OnAfterUpdateFastCallback(_ => ImageFileRegistration.RegisterInstalled())
            .OnBeforeUninstallFastCallback(_ => VelopackStartupCleanup.RemoveOwnedRegistrations())
            .Run();

        var application = new App();
        application.InitializeComponent();
        application.Run();
    }
}
