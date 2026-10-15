using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WinShot.Launcher
{
    internal static class Program
    {
        private const string SupportTitle = "WinShot needs repair";
        private const string SupportMessage =
            "WinShot's installation is incomplete or could not be verified. "
            + "Run the original WinShot installer again to repair it, then retry.\r\n\r\n"
            + "Support code: {0}";

        [STAThread]
        private static int Main()
        {
            string[] commandLine = Environment.GetCommandLineArgs();
            var arguments = new List<string>();
            for (int index = 1; index < commandLine.Length; index++)
                arguments.Add(commandLine[index]);

            LauncherResult result;
            try
            {
                string launcherPath = GetCurrentModulePath();
                string localApplicationData = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                var configuration = new LauncherConfiguration(
                    LauncherIdentity.PackageId,
                    LauncherIdentity.ExecutableName,
                    LauncherIdentity.ProductName,
                    LauncherIdentity.CompanyName,
                    LauncherIdentity.ExpectedPublisherSubject,
                    LauncherIdentity.ExpectedArtifactSigningIdentityEku,
                    LauncherIdentity.ExpectedReleaseChannel,
                    LauncherIdentity.ExpectedRuntimeIdentifier,
                    LauncherIdentity.ExpectedPaidMajor,
                    LauncherIdentity.ReleaseAttestationPublicKeyDerBase64,
                    LauncherIdentity.ReleaseAttestationPublicKeySha256);
                result = StableLauncherRuntime.TryLaunch(
                    launcherPath,
                    localApplicationData,
                    arguments,
                    configuration);
            }
            catch
            {
                result = new LauncherResult(
                    LauncherStatus.UnexpectedFailure,
                    LauncherMode.Interactive,
                    0,
                    0);
            }

            if (result.Launched)
                return 0;

            string supportCode = LauncherFailureReporter.GetSupportCode(
                result.Status);
            LauncherFailureReporter.TryRecord(result, supportCode);
            if (!LauncherArgumentPolicy.RequestsQuietFailure(arguments))
            {
                MessageBoxW(
                    IntPtr.Zero,
                    String.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        SupportMessage,
                        supportCode),
                    SupportTitle,
                    0x00000000 | 0x00000010 | 0x00010000);
            }

            return LauncherFailureReporter.GetExitCode(result.Status);
        }

        private static string GetCurrentModulePath()
        {
            var buffer = new StringBuilder(32768);
            uint length = GetModuleFileNameW(
                IntPtr.Zero,
                buffer,
                buffer.Capacity);
            if (length == 0 || length >= buffer.Capacity)
                throw new IOException("The launcher path is unavailable.");
            return buffer.ToString();
        }

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetModuleFileNameW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern uint GetModuleFileNameW(
            IntPtr module,
            StringBuilder fileName,
            int size);

        [DllImport(
            "user32.dll",
            EntryPoint = "MessageBoxW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern int MessageBoxW(
            IntPtr owner,
            string text,
            string caption,
            uint type);
    }

    internal static class LauncherFailureReporter
    {
        private const long MaximumLogBytes = 65536;

        internal static string GetSupportCode(LauncherStatus status)
        {
            switch (status)
            {
                case LauncherStatus.InvalidArguments:
                    return "WS-L001";
                case LauncherStatus.InvalidInstallation:
                    return "WS-L002";
                case LauncherStatus.ActivationPointerUnavailable:
                    return "WS-L003";
                case LauncherStatus.ActivationPointerInvalid:
                    return "WS-L004";
                case LauncherStatus.PayloadUnavailable:
                    return "WS-L005";
                case LauncherStatus.UnsafeFileSystemEntry:
                    return "WS-L006";
                case LauncherStatus.PayloadTrustRejected:
                    return "WS-L007";
                case LauncherStatus.PayloadIdentityChanged:
                    return "WS-L008";
                case LauncherStatus.ProcessCreationFailed:
                    return "WS-L009";
                case LauncherStatus.PlatformUnavailable:
                    return "WS-L010";
                default:
                    return "WS-L011";
            }
        }

        internal static int GetExitCode(LauncherStatus status)
        {
            return 10 + (int)status;
        }

        internal static void TryRecord(
            LauncherResult result,
            string supportCode)
        {
            try
            {
                string localRoot = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                string directory = Path.Combine(localRoot, "WinShot");
                Directory.CreateDirectory(directory);
                string logPath = Path.Combine(
                    directory,
                    "launcher-error.log");
                string line = String.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:O}|{1}|{2}|native={3}\r\n",
                    DateTimeOffset.UtcNow,
                    supportCode,
                    result.Status,
                    result.NativeErrorCode);

                var existing = new FileInfo(logPath);
                if (existing.Exists
                    && existing.Length + Encoding.UTF8.GetByteCount(line)
                        > MaximumLogBytes)
                {
                    File.WriteAllText(logPath, line, new UTF8Encoding(false));
                }
                else
                {
                    File.AppendAllText(logPath, line, new UTF8Encoding(false));
                }
            }
            catch
            {
                // A privacy-safe diagnostic failure must not create a launcher crash loop or
                // change the user's launch-at-sign-in preference.
            }
        }
    }
}
