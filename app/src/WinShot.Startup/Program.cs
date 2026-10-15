using System;
using System.Collections.Generic;
using System.Threading;

namespace WinShot.Startup
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            string[] commandLine = Environment.GetCommandLineArgs();
            var arguments = new List<string>();
            for (int index = 1; index < commandLine.Length; index++)
                arguments.Add(commandLine[index]);

            PackagedStartupBootstrapResult result;
            try
            {
                result = PackagedStartupBootstrapRuntime.TryLaunch(
                    arguments,
                    CancellationToken.None,
                    new WindowsPackagedStartupBootstrapPlatform());
            }
            catch
            {
                result = new PackagedStartupBootstrapResult(
                    PackagedStartupBootstrapStatus.UnexpectedFailure,
                    0,
                    0);
            }

            return PackagedStartupBootstrapExitCode.FromStatus(result.Status);
        }
    }
}
