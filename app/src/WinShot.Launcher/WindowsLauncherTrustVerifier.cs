using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

#pragma warning disable CS8600, CS8602, CS8625

namespace WinShot.Launcher
{
    /// <summary>
    /// Performs an offline Windows trust check against the two retained file handles, then requires
    /// the root launcher and activated payload to carry the exact approved Azure Artifact Signing
    /// public publisher identity and configured product metadata. An UNSET authoring identity
    /// always fails closed. Release validation remains responsible for proving the exact RFC 3161
    /// timestamp before the production identity constants are generated.
    /// </summary>
    internal sealed class WindowsLauncherTrustVerifier : ILauncherTrustVerifier
    {
        private const string CodeSigningEkuOid = "1.3.6.1.5.5.7.3.3";
        private const string PublicTrustMarkerEkuOid = "1.3.6.1.4.1.311.97.1.0";
        private const string ArtifactSigningIdentityEkuPrefix = "1.3.6.1.4.1.311.97.";

        public bool TryAcquireTrust(
            SafeFileHandle launcherHandle,
            string launcherPath,
            SafeFileHandle payloadHandle,
            string payloadPath,
            string versionDirectory,
            string activeVersion,
            LauncherConfiguration configuration,
            out IDisposable retainedTrust)
        {
            retainedTrust = null;
            if (launcherHandle == null
                || payloadHandle == null
                || launcherHandle.IsInvalid
                || launcherHandle.IsClosed
                || payloadHandle.IsInvalid
                || payloadHandle.IsClosed
                || configuration == null
                || !configuration.HasConfiguredProductionTrust)
            {
                return false;
            }

            if (!VerifyWindowsTrust(launcherHandle, launcherPath)
                || !VerifyWindowsTrust(payloadHandle, payloadPath))
            {
                return false;
            }

            LauncherSignerEvidence launcherEvidence;
            LauncherSignerEvidence payloadEvidence;
            if (!TryReadSignerEvidence(launcherPath, out launcherEvidence)
                || !TryReadSignerEvidence(payloadPath, out payloadEvidence))
            {
                return false;
            }

            if (!IsExpectedSignerPair(
                    launcherEvidence,
                    payloadEvidence,
                    configuration))
            {
                return false;
            }

            if (!HasExpectedProductMetadata(
                    launcherPath,
                    configuration)
                || !HasExpectedProductMetadata(
                    payloadPath,
                    configuration))
            {
                return false;
            }

            return LauncherReleaseAttestationVerifier.TryAcquire(
                versionDirectory,
                activeVersion,
                configuration,
                out retainedTrust);
        }

        internal static bool IsExpectedSignerPair(
            LauncherSignerEvidence launcherEvidence,
            LauncherSignerEvidence payloadEvidence,
            LauncherConfiguration configuration)
        {
            return launcherEvidence != null
                && payloadEvidence != null
                && configuration != null
                && configuration.HasConfiguredTrustIdentity
                && String.Equals(
                    launcherEvidence.PublisherSubject,
                    configuration.ExpectedPublisherSubject,
                    StringComparison.Ordinal)
                && String.Equals(
                    payloadEvidence.PublisherSubject,
                    configuration.ExpectedPublisherSubject,
                    StringComparison.Ordinal)
                && String.Equals(
                    launcherEvidence.ArtifactSigningIdentityEku,
                    configuration.ExpectedArtifactSigningIdentityEku,
                    StringComparison.Ordinal)
                && String.Equals(
                    payloadEvidence.ArtifactSigningIdentityEku,
                    configuration.ExpectedArtifactSigningIdentityEku,
                    StringComparison.Ordinal);
        }

        private static bool VerifyWindowsTrust(
            SafeFileHandle fileHandle,
            string filePath)
        {
            IntPtr pathPointer = IntPtr.Zero;
            IntPtr fileInfoPointer = IntPtr.Zero;
            var trustData = new WinTrustData();
            trustData.StructureSize = (uint)Marshal.SizeOf(typeof(WinTrustData));
            trustData.UiChoice = 2;
            trustData.RevocationChecks = 0;
            trustData.UnionChoice = 1;
            trustData.StateAction = 1;
            trustData.ProviderFlags =
                0x1000
                | 0x2000;
            trustData.UiContext = 1;

            try
            {
                pathPointer = Marshal.StringToCoTaskMemUni(filePath);
                var fileInfo = new WinTrustFileInfo();
                fileInfo.StructureSize =
                    (uint)Marshal.SizeOf(typeof(WinTrustFileInfo));
                fileInfo.FilePath = pathPointer;
                fileInfo.FileHandle = fileHandle.DangerousGetHandle();
                fileInfoPointer =
                    Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
                trustData.FileInformation = fileInfoPointer;

                Guid action = new Guid(
                    "00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
                int result = WinVerifyTrust(
                    new IntPtr(-1),
                    ref action,
                    ref trustData);
                return result == 0;
            }
            catch (Exception exception)
            {
                if (exception is DllNotFoundException
                    || exception is EntryPointNotFoundException
                    || exception is BadImageFormatException
                    || exception is ExternalException
                    || exception is ArgumentException)
                {
                    return false;
                }

                throw;
            }
            finally
            {
                if (trustData.StateAction == 1)
                {
                    trustData.StateAction = 2;
                    Guid action = new Guid(
                        "00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
                    WinVerifyTrust(
                        new IntPtr(-1),
                        ref action,
                        ref trustData);
                }

                if (fileInfoPointer != IntPtr.Zero)
                    Marshal.FreeHGlobal(fileInfoPointer);
                if (pathPointer != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(pathPointer);
            }
        }

        private static bool TryReadSignerEvidence(
            string filePath,
            out LauncherSignerEvidence evidence)
        {
            evidence = null;
            try
            {
#pragma warning disable SYSLIB0057 // Launcher targets inbox .NET Framework, which has no X509CertificateLoader.
                using (X509Certificate source =
                    X509Certificate.CreateFromSignedFile(filePath))
#pragma warning restore SYSLIB0057
                using (var certificate = new X509Certificate2(source))
                {
                    bool hasCodeSigning = false;
                    bool hasPublicTrustMarker = false;
                    string identityEku = null;
                    int identityCount = 0;
                    int enhancedKeyUsageExtensionCount = 0;
                    var seenUsages = new HashSet<string>(
                        StringComparer.Ordinal);
                    bool duplicateUsage = false;

                    foreach (X509Extension extension in certificate.Extensions)
                    {
                        if (!String.Equals(
                                extension.Oid.Value,
                                "2.5.29.37",
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        enhancedKeyUsageExtensionCount++;
                        var enhancedKeyUsage =
                            new X509EnhancedKeyUsageExtension(
                                extension,
                                extension.Critical);
                        foreach (System.Security.Cryptography.Oid usage
                            in enhancedKeyUsage.EnhancedKeyUsages)
                        {
                            string value = usage.Value;
                            if (!String.IsNullOrEmpty(value)
                                && !seenUsages.Add(value))
                            {
                                duplicateUsage = true;
                            }
                            if (String.Equals(
                                    value,
                                    CodeSigningEkuOid,
                                    StringComparison.Ordinal))
                            {
                                hasCodeSigning = true;
                            }

                            if (String.Equals(
                                    value,
                                    PublicTrustMarkerEkuOid,
                                    StringComparison.Ordinal))
                            {
                                hasPublicTrustMarker = true;
                            }

                            if (value != null
                                && value.StartsWith(
                                    ArtifactSigningIdentityEkuPrefix,
                                    StringComparison.Ordinal)
                                && !String.Equals(
                                    value,
                                    PublicTrustMarkerEkuOid,
                                    StringComparison.Ordinal))
                            {
                                identityCount++;
                                identityEku = value;
                            }
                        }
                    }

                    if (!hasCodeSigning
                        || !hasPublicTrustMarker
                        || enhancedKeyUsageExtensionCount != 1
                        || duplicateUsage
                        || identityCount != 1
                        || String.IsNullOrEmpty(identityEku))
                    {
                        return false;
                    }

                    evidence = new LauncherSignerEvidence(
                        certificate.Subject,
                        identityEku);
                    return !String.IsNullOrWhiteSpace(
                        evidence.PublisherSubject);
                }
            }
            catch (Exception exception)
            {
                return exception is System.Security.Cryptography.CryptographicException
                    || exception is ArgumentException
                    || exception is InvalidOperationException
                    ? false
                    : ThrowUnexpected<bool>(exception);
            }
        }

        private static bool HasExpectedProductMetadata(
            string filePath,
            LauncherConfiguration configuration)
        {
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(filePath);
            return String.Equals(
                    version.ProductName,
                    configuration.ProductName,
                    StringComparison.Ordinal)
                && String.Equals(
                    version.CompanyName,
                    configuration.CompanyName,
                    StringComparison.Ordinal);
        }

        private static T ThrowUnexpected<T>(Exception exception)
        {
            throw exception;
        }

        [DllImport(
            "wintrust.dll",
            EntryPoint = "WinVerifyTrust",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern int WinVerifyTrust(
            IntPtr windowHandle,
            ref Guid actionId,
            ref WinTrustData trustData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            internal uint StructureSize;
            internal IntPtr FilePath;
            internal IntPtr FileHandle;
            internal IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            internal uint StructureSize;
            internal IntPtr PolicyCallbackData;
            internal IntPtr SipClientData;
            internal uint UiChoice;
            internal uint RevocationChecks;
            internal uint UnionChoice;
            internal IntPtr FileInformation;
            internal uint StateAction;
            internal IntPtr StateData;
            internal string UrlReference;
            internal uint ProviderFlags;
            internal uint UiContext;
            internal IntPtr SignatureSettings;
        }

    }

    internal sealed class LauncherSignerEvidence
    {
        internal LauncherSignerEvidence(
            string publisherSubject,
            string artifactSigningIdentityEku)
        {
            PublisherSubject = publisherSubject;
            ArtifactSigningIdentityEku =
                artifactSigningIdentityEku;
        }

        internal string PublisherSubject { get; private set; }

        internal string ArtifactSigningIdentityEku { get; private set; }
    }
}
