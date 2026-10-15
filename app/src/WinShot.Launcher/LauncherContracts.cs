using System;
using System.Collections.Generic;
using System.IO;

#pragma warning disable CS8600, CS8602, CS8625

namespace WinShot.Launcher
{
    internal enum LauncherMode
    {
        Interactive,
        Tray,
        Recovered,
    }

    internal enum LauncherStatus
    {
        Launched,
        InvalidArguments,
        InvalidInstallation,
        ActivationPointerUnavailable,
        ActivationPointerInvalid,
        PayloadUnavailable,
        UnsafeFileSystemEntry,
        PayloadTrustRejected,
        PayloadIdentityChanged,
        ProcessCreationFailed,
        PlatformUnavailable,
        UnexpectedFailure,
    }

    internal sealed class LauncherResult
    {
        internal LauncherResult(
            LauncherStatus status,
            LauncherMode mode,
            int nativeErrorCode,
            uint processId)
        {
            Status = status;
            Mode = mode;
            NativeErrorCode = nativeErrorCode;
            ProcessId = processId;
        }

        internal LauncherStatus Status { get; private set; }

        internal LauncherMode Mode { get; private set; }

        internal int NativeErrorCode { get; private set; }

        internal uint ProcessId { get; private set; }

        internal bool Launched
        {
            get { return Status == LauncherStatus.Launched && ProcessId != 0; }
        }
    }

    internal sealed class LauncherConfiguration
    {
        internal const int MaximumVersionSegmentLength = 128;

        internal LauncherConfiguration(
            string packageId,
            string executableName,
            string productName,
            string companyName,
            string expectedPublisherSubject,
            string expectedArtifactSigningIdentityEku,
            string expectedReleaseChannel,
            string expectedRuntimeIdentifier,
            int expectedPaidMajor,
            string releaseAttestationPublicKeyDerBase64,
            string releaseAttestationPublicKeySha256)
        {
            if (!LauncherPathPolicy.IsSafePackageId(packageId))
                throw new ArgumentException("The package ID is invalid.", "packageId");
            if (!LauncherPathPolicy.IsSafeExecutableName(executableName))
                throw new ArgumentException("The executable name is invalid.", "executableName");
            if (String.IsNullOrWhiteSpace(productName) || productName.Length > 128)
                throw new ArgumentException("The product name is invalid.", "productName");
            if (String.IsNullOrWhiteSpace(companyName) || companyName.Length > 256)
                throw new ArgumentException("The company name is invalid.", "companyName");
            if (!IsSafePublisherSubject(expectedPublisherSubject))
            {
                throw new ArgumentException(
                    "The expected publisher subject is invalid.",
                    "expectedPublisherSubject");
            }
            if (!IsSafeArtifactSigningIdentityEku(expectedArtifactSigningIdentityEku))
            {
                throw new ArgumentException(
                    "The expected Artifact Signing identity EKU is invalid.",
                    "expectedArtifactSigningIdentityEku");
            }
            if (!IsSafeReleaseIdentity(expectedReleaseChannel, 64))
            {
                throw new ArgumentException(
                    "The expected release channel is invalid.",
                    "expectedReleaseChannel");
            }
            if (!IsSafeReleaseIdentity(expectedRuntimeIdentifier, 64))
            {
                throw new ArgumentException(
                    "The expected runtime identifier is invalid.",
                    "expectedRuntimeIdentifier");
            }
            if (expectedPaidMajor <= 0 || expectedPaidMajor > 9999)
                throw new ArgumentOutOfRangeException("expectedPaidMajor");
            if (!IsSafeReleaseKeyConfiguration(
                    releaseAttestationPublicKeyDerBase64,
                    releaseAttestationPublicKeySha256))
            {
                throw new ArgumentException(
                    "The release-attestation public key is invalid.",
                    "releaseAttestationPublicKeyDerBase64");
            }

            PackageId = packageId;
            ExecutableName = executableName;
            ProductName = productName;
            CompanyName = companyName;
            ExpectedPublisherSubject = expectedPublisherSubject;
            ExpectedArtifactSigningIdentityEku =
                expectedArtifactSigningIdentityEku;
            ExpectedReleaseChannel = expectedReleaseChannel;
            ExpectedRuntimeIdentifier = expectedRuntimeIdentifier;
            ExpectedPaidMajor = expectedPaidMajor;
            ReleaseAttestationPublicKeyDerBase64 =
                releaseAttestationPublicKeyDerBase64;
            ReleaseAttestationPublicKeySha256 =
                releaseAttestationPublicKeySha256;
        }

        internal string PackageId { get; private set; }

        internal string ExecutableName { get; private set; }

        internal string ProductName { get; private set; }

        internal string CompanyName { get; private set; }

        internal string ExpectedPublisherSubject { get; private set; }

        internal string ExpectedArtifactSigningIdentityEku { get; private set; }

        internal string ExpectedReleaseChannel { get; private set; }

        internal string ExpectedRuntimeIdentifier { get; private set; }

        internal int ExpectedPaidMajor { get; private set; }

        internal string ReleaseAttestationPublicKeyDerBase64 { get; private set; }

        internal string ReleaseAttestationPublicKeySha256 { get; private set; }

        internal bool HasConfiguredTrustIdentity
        {
            get
            {
                return !String.Equals(
                        ExpectedPublisherSubject,
                        "UNSET",
                        StringComparison.Ordinal)
                    && !String.Equals(
                        ExpectedArtifactSigningIdentityEku,
                        "UNSET",
                        StringComparison.Ordinal);
            }
        }

        internal bool HasConfiguredReleaseTrust
        {
            get
            {
                return !String.Equals(
                        ExpectedReleaseChannel,
                        "UNSET",
                        StringComparison.Ordinal)
                    && !String.Equals(
                        ReleaseAttestationPublicKeyDerBase64,
                        "UNSET",
                        StringComparison.Ordinal)
                    && !String.Equals(
                        ReleaseAttestationPublicKeySha256,
                        "UNSET",
                        StringComparison.Ordinal);
            }
        }

        internal bool HasConfiguredProductionTrust
        {
            get
            {
                return HasConfiguredTrustIdentity
                    && HasConfiguredReleaseTrust;
            }
        }

        private static bool IsSafePublisherSubject(string value)
        {
            if (String.Equals(value, "UNSET", StringComparison.Ordinal))
                return true;
            if (String.IsNullOrWhiteSpace(value) || value.Length > 512)
                return false;

            foreach (char character in value)
            {
                if (Char.IsControl(character))
                    return false;
            }

            return true;
        }

        private static bool IsSafeArtifactSigningIdentityEku(string value)
        {
            const string prefix = "1.3.6.1.4.1.311.97.";
            if (String.Equals(value, "UNSET", StringComparison.Ordinal))
                return true;
            if (String.IsNullOrEmpty(value)
                || value.Length > 128
                || !value.StartsWith(prefix, StringComparison.Ordinal)
                || String.Equals(
                    value,
                    "1.3.6.1.4.1.311.97.1.0",
                    StringComparison.Ordinal))
            {
                return false;
            }

            bool previousWasDot = true;
            foreach (char character in value)
            {
                if (character == '.')
                {
                    if (previousWasDot)
                        return false;
                    previousWasDot = true;
                }
                else if (character >= '0' && character <= '9')
                {
                    previousWasDot = false;
                }
                else
                {
                    return false;
                }
            }

            return !previousWasDot;
        }

        private static bool IsSafeReleaseIdentity(string value, int maximumLength)
        {
            if (String.Equals(value, "UNSET", StringComparison.Ordinal))
                return true;
            if (String.IsNullOrEmpty(value)
                || value.Length > maximumLength
                || value[0] == '.'
                || value[value.Length - 1] == '.')
            {
                return false;
            }

            foreach (char character in value)
            {
                if ((character < 'A' || character > 'Z')
                    && (character < 'a' || character > 'z')
                    && (character < '0' || character > '9')
                    && character != '.'
                    && character != '-'
                    && character != '_')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsSafeReleaseKeyConfiguration(
            string publicKeyDerBase64,
            string publicKeySha256)
        {
            bool keyUnset = String.Equals(
                publicKeyDerBase64,
                "UNSET",
                StringComparison.Ordinal);
            bool hashUnset = String.Equals(
                publicKeySha256,
                "UNSET",
                StringComparison.Ordinal);
            if (keyUnset || hashUnset)
                return keyUnset && hashUnset;
            if (String.IsNullOrEmpty(publicKeyDerBase64)
                || publicKeyDerBase64.Length > 16384
                || !IsUppercaseSha256(publicKeySha256))
            {
                return false;
            }

            try
            {
                byte[] key = Convert.FromBase64String(publicKeyDerBase64);
                return key.Length >= 384 && key.Length <= 8192;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        internal static bool IsUppercaseSha256(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length != 64)
                return false;
            foreach (char character in value)
            {
                if ((character < '0' || character > '9')
                    && (character < 'A' || character > 'F'))
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal sealed class LauncherPaths
    {
        internal LauncherPaths(
            string installRoot,
            string launcherPath,
            string activationPointerPath,
            string versionDirectory,
            string payloadPath)
        {
            InstallRoot = installRoot;
            LauncherPath = launcherPath;
            ActivationPointerPath = activationPointerPath;
            VersionDirectory = versionDirectory;
            PayloadPath = payloadPath;
        }

        internal string InstallRoot { get; private set; }

        internal string LauncherPath { get; private set; }

        internal string ActivationPointerPath { get; private set; }

        internal string VersionDirectory { get; private set; }

        internal string PayloadPath { get; private set; }
    }

    internal static class LauncherArgumentPolicy
    {
        internal static bool TryParse(
            IList<string> arguments,
            out LauncherMode mode,
            out string childArguments)
        {
            if (arguments == null)
                throw new ArgumentNullException("arguments");

            mode = LauncherMode.Interactive;
            childArguments = String.Empty;
            if (arguments.Count == 0)
                return true;

            if (arguments.Count == 1
                && String.Equals(
                    arguments[0],
                    "--tray",
                    StringComparison.OrdinalIgnoreCase))
            {
                mode = LauncherMode.Tray;
                childArguments = "--tray";
                return true;
            }

            if (arguments.Count == 2
                && String.Equals(arguments[0], "--tray", StringComparison.Ordinal)
                && String.Equals(arguments[1], "--recovered", StringComparison.Ordinal))
            {
                mode = LauncherMode.Recovered;
                childArguments = "--tray --launcher-recovered";
                return true;
            }

            return false;
        }

        internal static bool RequestsQuietFailure(IList<string> arguments)
        {
            if (arguments == null)
                return false;

            foreach (string argument in arguments)
            {
                if (String.Equals(
                        argument,
                        "--tray",
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }

    internal static class LauncherPathPolicy
    {
        private static readonly string[] ReservedWindowsDeviceNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5",
            "LPT6", "LPT7", "LPT8", "LPT9",
        };

        internal static bool TryResolveInstallation(
            string launcherPath,
            string localApplicationDataPath,
            string activeVersion,
            LauncherConfiguration configuration,
            out LauncherPaths paths)
        {
            paths = null;
            if (configuration == null
                || !IsCanonicalAbsoluteLocalPath(launcherPath)
                || !IsCanonicalAbsoluteLocalPath(localApplicationDataPath)
                || !IsSafeVersionSegment(activeVersion))
            {
                return false;
            }

            string localRoot = TrimEndingDirectorySeparators(
                Path.GetFullPath(localApplicationDataPath));
            string expectedInstallRoot = Path.Combine(
                localRoot,
                configuration.PackageId);
            string expectedLauncherPath = Path.Combine(
                expectedInstallRoot,
                configuration.ExecutableName);
            if (!String.Equals(
                    Path.GetFullPath(launcherPath),
                    expectedLauncherPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string versionDirectory = Path.Combine(
                expectedInstallRoot,
                "versions",
                activeVersion);
            string payloadPath = Path.Combine(
                versionDirectory,
                configuration.ExecutableName);
            if (!IsStrictlyWithin(payloadPath, expectedInstallRoot))
                return false;

            paths = new LauncherPaths(
                expectedInstallRoot,
                expectedLauncherPath,
                Path.Combine(expectedInstallRoot, "active-version"),
                versionDirectory,
                payloadPath);
            return true;
        }

        internal static bool IsSafeVersionSegment(string value)
        {
            if (String.IsNullOrEmpty(value)
                || value.Length > LauncherConfiguration.MaximumVersionSegmentLength
                || !IsAsciiLetterOrDigit(value[0])
                || !IsAsciiLetterOrDigit(value[value.Length - 1]))
            {
                return false;
            }

            foreach (char character in value)
            {
                if (!IsAsciiLetterOrDigit(character)
                    && character != '.'
                    && character != '-'
                    && character != '_'
                    && character != '+')
                {
                    return false;
                }
            }

            int separator = value.IndexOf('.');
            string deviceName = separator < 0 ? value : value.Substring(0, separator);
            foreach (string reserved in ReservedWindowsDeviceNames)
            {
                if (String.Equals(deviceName, reserved, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        internal static bool IsSafePackageId(string value)
        {
            if (String.IsNullOrEmpty(value)
                || value.Length > 128
                || value[0] == '.'
                || value[value.Length - 1] == '.')
            {
                return false;
            }

            foreach (char character in value)
            {
                if (!IsAsciiLetterOrDigit(character)
                    && character != '.'
                    && character != '-'
                    && character != '_')
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool IsSafeExecutableName(string value)
        {
            return !String.IsNullOrEmpty(value)
                && value.Length <= 128
                && String.Equals(value, Path.GetFileName(value), StringComparison.Ordinal)
                && value.IndexOf('"') < 0
                && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCanonicalAbsoluteLocalPath(string value)
        {
            if (String.IsNullOrWhiteSpace(value)
                || value.IndexOf('"') >= 0
                || value.StartsWith(@"\\", StringComparison.Ordinal)
                || !Path.IsPathRooted(value))
            {
                return false;
            }

            try
            {
                return String.Equals(
                    Path.GetFullPath(value),
                    TrimEndingDirectorySeparators(value),
                    StringComparison.OrdinalIgnoreCase)
                    || String.Equals(
                        Path.GetFullPath(value),
                        value,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception)
            {
                return exception is ArgumentException
                    || exception is IOException
                    || exception is NotSupportedException
                    ? false
                    : ThrowUnexpected<bool>(exception);
            }
        }

        private static bool IsStrictlyWithin(string candidate, string root)
        {
            string normalizedRoot = TrimEndingDirectorySeparators(Path.GetFullPath(root));
            string prefix = normalizedRoot + Path.DirectorySeparatorChar;
            return Path.GetFullPath(candidate).StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAsciiLetterOrDigit(char character)
        {
            return (character >= 'A' && character <= 'Z')
                || (character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9');
        }

        internal static string TrimEndingDirectorySeparators(string value)
        {
            string root = Path.GetPathRoot(value);
            int minimumLength = String.IsNullOrEmpty(root) ? 0 : root.Length;
            int length = value.Length;
            while (length > minimumLength
                && (value[length - 1] == Path.DirectorySeparatorChar
                    || value[length - 1] == Path.AltDirectorySeparatorChar))
            {
                length--;
            }

            return length == value.Length ? value : value.Substring(0, length);
        }

        private static T ThrowUnexpected<T>(Exception exception)
        {
            throw exception;
        }
    }
}
