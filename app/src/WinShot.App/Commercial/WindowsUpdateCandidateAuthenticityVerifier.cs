using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinShot.Core.Update;

internal sealed record WindowsAuthenticodeExpectations(
    string PublisherSubject,
    string ArtifactSigningIdentityEkuOid);

internal sealed record WindowsAuthenticodeEvidence(
    string PublisherSubject,
    string LeafCertificateThumbprint,
    IReadOnlySet<string> EnhancedKeyUsageOids,
    bool HasSingleEnhancedKeyUsageExtension,
    bool HasDuplicateEnhancedKeyUsage,
    bool HasRfc3161Timestamp,
    bool HasTrustedTimestampSigner);

/// <summary>
/// Verifies Authenticode trust against the exact already-open package handle, then pins the
/// approved Azure Artifact Signing subscriber identity and requires a cryptographically bound
/// RFC 3161 timestamp signer. The display path supplied to WinVerifyTrust is resolved from that
/// handle and is never reopened as authority.
/// </summary>
internal sealed class WindowsUpdateCandidateAuthenticityVerifier :
    IUpdateCandidateAuthenticityVerifier
{
    private readonly WindowsAuthenticodeExpectations _expectations;

    internal WindowsUpdateCandidateAuthenticityVerifier(
        WindowsAuthenticodeExpectations expectations)
    {
        ArgumentNullException.ThrowIfNull(expectations);
        if (string.IsNullOrWhiteSpace(expectations.PublisherSubject)
            || expectations.PublisherSubject.Length > 512)
        {
            throw new ArgumentException(
                "An exact publisher subject is required.",
                nameof(expectations));
        }

        if (!IsArtifactSigningIdentityEku(expectations.ArtifactSigningIdentityEkuOid))
        {
            throw new ArgumentException(
                "An exact Azure Artifact Signing subscriber identity EKU is required.",
                nameof(expectations));
        }

        _expectations = expectations;
    }

    public ValueTask<UpdateCandidateAuthenticityStatus> VerifyAsync(
        SafeFileHandle packageHandle,
        string packageDisplayName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(packageHandle);
        if (packageHandle.IsClosed
            || packageHandle.IsInvalid
            || string.IsNullOrWhiteSpace(packageDisplayName)
            || packageDisplayName.Length > 260
            || !string.Equals(
                packageDisplayName,
                Path.GetFileName(packageDisplayName),
                StringComparison.Ordinal))
        {
            return ValueTask.FromResult(
                UpdateCandidateAuthenticityStatus.VerificationUnavailable);
        }

        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(
                UpdateCandidateAuthenticityStatus.VerificationUnavailable);
        }

        bool handleReferenceAdded = false;
        try
        {
            packageHandle.DangerousAddRef(ref handleReferenceAdded);
            Task<UpdateCandidateAuthenticityStatus> verification = Task.Run(
                () =>
                {
                    try
                    {
                        return VerifyCore(packageHandle, packageDisplayName);
                    }
                    finally
                    {
                        packageHandle.DangerousRelease();
                    }
                },
                CancellationToken.None);
            handleReferenceAdded = false;
            return new ValueTask<UpdateCandidateAuthenticityStatus>(
                verification.WaitAsync(cancellationToken));
        }
        catch
        {
            if (handleReferenceAdded)
                packageHandle.DangerousRelease();
            throw;
        }
    }

    internal UpdateCandidateAuthenticityStatus Evaluate(
        WindowsAuthenticodeEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!string.Equals(
                evidence.PublisherSubject,
                _expectations.PublisherSubject,
                StringComparison.Ordinal)
            || !evidence.HasSingleEnhancedKeyUsageExtension
            || evidence.HasDuplicateEnhancedKeyUsage
            || !evidence.EnhancedKeyUsageOids.Contains(
                _expectations.ArtifactSigningIdentityEkuOid)
            || !evidence.EnhancedKeyUsageOids.Contains(
                WindowsAuthenticodeNative.CodeSigningEkuOid)
            || !evidence.EnhancedKeyUsageOids.Contains(
                WindowsAuthenticodeNative.PublicTrustMarkerEkuOid)
            || evidence.EnhancedKeyUsageOids.Count(
                static oid => oid.StartsWith(
                        WindowsAuthenticodeNative.ArtifactSigningIdentityEkuPrefix,
                        StringComparison.Ordinal)
                    && !string.Equals(
                        oid,
                        WindowsAuthenticodeNative.PublicTrustMarkerEkuOid,
                        StringComparison.Ordinal)) != 1)
        {
            return UpdateCandidateAuthenticityStatus.PublisherMismatch;
        }

        return evidence.HasRfc3161Timestamp && evidence.HasTrustedTimestampSigner
            ? UpdateCandidateAuthenticityStatus.Trusted
            : UpdateCandidateAuthenticityStatus.TimestampInvalid;
    }

    private UpdateCandidateAuthenticityStatus VerifyCore(
        SafeFileHandle packageHandle,
        string packageDisplayName)
    {
        string openedPath;
        try
        {
            openedPath = GetOpenedPath(packageHandle);
        }
        catch (IOException)
        {
            return UpdateCandidateAuthenticityStatus.VerificationUnavailable;
        }

        if (!string.Equals(
                Path.GetFileName(openedPath),
                packageDisplayName,
                StringComparison.Ordinal))
        {
            return UpdateCandidateAuthenticityStatus.VerificationUnavailable;
        }

        WindowsAuthenticodeNativeResult nativeResult;
        try
        {
            nativeResult = WindowsAuthenticodeNative.Verify(
                packageHandle,
                openedPath);
        }
        catch (Exception ex) when (ex is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or ExternalException
            or CryptographicException
            or ArgumentException
            or OverflowException)
        {
            return UpdateCandidateAuthenticityStatus.VerificationUnavailable;
        }
        if (nativeResult.TrustResult != 0)
            return UpdateCandidateAuthenticityStatus.Untrusted;
        if (nativeResult.PolicyRejected)
            return UpdateCandidateAuthenticityStatus.Untrusted;
        if (nativeResult.Evidence is null)
            return UpdateCandidateAuthenticityStatus.VerificationUnavailable;
        return Evaluate(nativeResult.Evidence);
    }

    private static bool IsArtifactSigningIdentityEku(string? value)
    {
        if (value is null
            || value.Length > 256
            || !value.StartsWith(
                WindowsAuthenticodeNative.ArtifactSigningIdentityEkuPrefix,
                StringComparison.Ordinal)
            || value.StartsWith(
                WindowsAuthenticodeNative.PrivateTrustCodeSigningEkuPrefix,
                StringComparison.Ordinal)
            || value.StartsWith(
                WindowsAuthenticodeNative.PrivateTrustCiPolicyEkuPrefix,
                StringComparison.Ordinal)
            || string.Equals(
                value,
                WindowsAuthenticodeNative.PublicTrustMarkerEkuOid,
                StringComparison.Ordinal))
        {
            return false;
        }

        string[] components = value.Split('.');
        if (components.Length <= 8)
            return false;
        foreach (string component in components)
        {
            if (component.Length == 0
                || (component.Length > 1 && component[0] == '0')
                || !uint.TryParse(
                    component,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out _))
            {
                return false;
            }
        }

        return true;
    }

    private static string GetOpenedPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32_768);
        uint length = GetFinalPathNameByHandleW(
            handle,
            buffer,
            (uint)buffer.Capacity,
            flags: 0);
        if (length == 0 || length >= buffer.Capacity)
            throw new IOException("Unable to resolve the update package handle.");

        const string uncPrefix = @"\\?\UNC\";
        const string devicePrefix = @"\\?\";
        string resolved = buffer.ToString();
        if (resolved.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + resolved[uncPrefix.Length..];
        if (resolved.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase))
            return resolved[devicePrefix.Length..];
        return resolved;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);
}

internal sealed record WindowsAuthenticodeNativeResult(
    int TrustResult,
    WindowsAuthenticodeEvidence? Evidence,
    bool PolicyRejected = false);

internal static class WindowsAuthenticodeNative
{
    internal const string CodeSigningEkuOid = "1.3.6.1.5.5.7.3.3";
    internal const string PublicTrustMarkerEkuOid = "1.3.6.1.4.1.311.97.1.0";
    internal const string ArtifactSigningIdentityEkuPrefix =
        "1.3.6.1.4.1.311.97.";
    internal const string PrivateTrustCodeSigningEkuPrefix =
        "1.3.6.1.4.1.311.97.1.3.1.";
    internal const string PrivateTrustCiPolicyEkuPrefix =
        "1.3.6.1.4.1.311.97.1.4.1.";

    private const uint WinTrustUiNone = 2;
    private const uint WinTrustRevokeWholeChain = 1;
    private const uint WinTrustChoiceFile = 1;
    private const uint WinTrustStateActionVerify = 1;
    private const uint WinTrustStateActionClose = 2;
    private const uint WinTrustRevocationCheckChainExcludeRoot = 0x80;
    private const uint WinTrustDisableMd2Md4 = 0x2000;
    private const uint WinTrustUiContextInstall = 1;
    private const uint WinTrustGetSecondarySignatureCount = 0x2;
    private const uint TimestampSignerType = 0x10;
    private const int MaximumCertificateBytes = 1024 * 1024;
    private const int MaximumTimestampTokenBytes = 1024 * 1024;
    private const int MaximumEncryptedHashBytes = 1024;
    private const uint MaximumCertificateChainCount = 16;
    private const uint MaximumUnauthenticatedAttributes = 64;
    private const string Rfc3161CounterSignatureOid = "1.3.6.1.4.1.311.3.3.1";
    private const string LegacyCounterSignatureOid = "1.2.840.113549.1.9.6";
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";

    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    internal static WindowsAuthenticodeNativeResult Verify(
        SafeFileHandle packageHandle,
        string openedPath)
    {
        IntPtr path = IntPtr.Zero;
        IntPtr fileInfoPointer = IntPtr.Zero;
        IntPtr signatureSettingsPointer = IntPtr.Zero;
        IntPtr winTrustModule = IntPtr.Zero;
        var trustData = new WinTrustData
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustData>(),
            UiChoice = WinTrustUiNone,
            RevocationChecks = WinTrustRevokeWholeChain,
            UnionChoice = WinTrustChoiceFile,
            StateAction = WinTrustStateActionVerify,
            ProviderFlags =
                WinTrustRevocationCheckChainExcludeRoot | WinTrustDisableMd2Md4,
            UiContext = WinTrustUiContextInstall,
        };

        try
        {
            path = Marshal.StringToCoTaskMemUni(openedPath);
            var fileInfo = new WinTrustFileInfo
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = path,
                FileHandle = packageHandle.DangerousGetHandle(),
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            trustData.FileInformation = fileInfoPointer;
            var signatureSettings = new WinTrustSignatureSettings
            {
                StructureSize =
                    (uint)Marshal.SizeOf<WinTrustSignatureSettings>(),
                Flags = WinTrustGetSecondarySignatureCount,
            };
            signatureSettingsPointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<WinTrustSignatureSettings>());
            Marshal.StructureToPtr(
                signatureSettings,
                signatureSettingsPointer,
                fDeleteOld: false);
            trustData.SignatureSettings = signatureSettingsPointer;

            Guid action = GenericVerifyV2;
            int trustResult = WinVerifyTrust(
                new IntPtr(-1),
                ref action,
                ref trustData);
            if (trustResult != 0)
                return new WindowsAuthenticodeNativeResult(trustResult, Evidence: null);
            signatureSettings = Marshal.PtrToStructure<WinTrustSignatureSettings>(
                signatureSettingsPointer);
            if (signatureSettings.SecondarySignatureCount != 0
                || signatureSettings.VerifiedSignatureIndex != 0)
            {
                return new WindowsAuthenticodeNativeResult(
                    trustResult,
                    Evidence: null,
                    PolicyRejected: true);
            }

            if (!NativeLibrary.TryLoad(
                    "wintrust.dll",
                    typeof(WindowsAuthenticodeNative).Assembly,
                    DllImportSearchPath.System32,
                    out winTrustModule)
                || !TryLoadHelpers(
                    winTrustModule,
                    out WinTrustHelpers? helpers)
                || helpers is null)
            {
                return new WindowsAuthenticodeNativeResult(
                    trustResult,
                    Evidence: null);
            }

            WindowsAuthenticodeEvidence? evidence = TryExtractEvidence(
                trustData.StateData,
                helpers.ProviderDataFromState,
                helpers.GetSigner,
                helpers.GetCertificate);
            return new WindowsAuthenticodeNativeResult(trustResult, evidence);
        }
        finally
        {
            if (trustData.StateAction == WinTrustStateActionVerify)
            {
                trustData.StateAction = WinTrustStateActionClose;
                Guid action = GenericVerifyV2;
                _ = WinVerifyTrust(
                    new IntPtr(-1),
                    ref action,
                    ref trustData);
            }

            if (winTrustModule != IntPtr.Zero)
                NativeLibrary.Free(winTrustModule);
            if (signatureSettingsPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(signatureSettingsPointer);
            if (fileInfoPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(fileInfoPointer);
            if (path != IntPtr.Zero)
                Marshal.FreeCoTaskMem(path);
        }
    }

    private static WindowsAuthenticodeEvidence? TryExtractEvidence(
        IntPtr stateData,
        ProviderDataFromStateData providerDataFromState,
        GetProviderSignerFromChain getSigner,
        GetProviderCertificateFromChain getCertificate)
    {
        if (stateData == IntPtr.Zero)
            return null;
        IntPtr providerData = providerDataFromState(stateData);
        if (providerData == IntPtr.Zero)
            return null;

        IntPtr primarySignerPointer = getSigner(
            providerData,
            signerIndex: 0,
            counterSigner: false,
            counterSignerIndex: 0);
        if (primarySignerPointer == IntPtr.Zero)
            return null;
        CryptProviderSigner primarySigner =
            Marshal.PtrToStructure<CryptProviderSigner>(primarySignerPointer);
        if (primarySigner.StructureSize < Marshal.SizeOf<CryptProviderSigner>()
            || primarySigner.Error != 0
            || primarySigner.CertificateChainCount == 0
            || primarySigner.SignerInformation == IntPtr.Zero)
        {
            return null;
        }

        IntPtr primaryCertificatePointer = getCertificate(
            primarySignerPointer,
            certificateIndex: 0);
        X509Certificate2? primaryCertificate =
            CopyCertificate(primaryCertificatePointer);
        if (primaryCertificate is null)
            return null;

        using (primaryCertificate)
        {
            EnhancedKeyUsageEvidence keyUsage =
                ReadEnhancedKeyUsageEvidence(primaryCertificate);
            bool hasRfc3161Timestamp = TryReadRfc3161Timestamp(
                primarySigner.SignerInformation,
                out byte[]? timestampToken,
                out byte[]? encryptedHash);
            bool hasTrustedTimestampSigner = hasRfc3161Timestamp
                && timestampToken is not null
                && encryptedHash is not null
                && HasTrustedTimestampSigner(
                    providerData,
                    primarySigner,
                    timestampToken,
                    encryptedHash,
                    getSigner,
                    getCertificate);
            return new WindowsAuthenticodeEvidence(
                primaryCertificate.Subject,
                primaryCertificate.GetCertHashString(
                    HashAlgorithmName.SHA1),
                keyUsage.Oids,
                keyUsage.HasSingleExtension,
                keyUsage.HasDuplicateUsage,
                hasRfc3161Timestamp,
                hasTrustedTimestampSigner);
        }
    }

    private static EnhancedKeyUsageEvidence ReadEnhancedKeyUsageEvidence(
        X509Certificate2 certificate)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        int extensionCount = 0;
        bool hasDuplicate = false;
        foreach (X509Extension extension in certificate.Extensions)
        {
            if (extension is not X509EnhancedKeyUsageExtension enhancedKeyUsage)
                continue;
            extensionCount++;
            foreach (Oid usage in enhancedKeyUsage.EnhancedKeyUsages)
            {
                if (string.IsNullOrWhiteSpace(usage.Value))
                    continue;
                if (!result.Add(usage.Value))
                    hasDuplicate = true;
            }
        }

        return new EnhancedKeyUsageEvidence(
            result,
            HasSingleExtension: extensionCount == 1,
            HasDuplicateUsage: hasDuplicate);
    }

    private static bool HasTrustedTimestampSigner(
        IntPtr providerData,
        CryptProviderSigner primarySigner,
        byte[] timestampToken,
        byte[] encryptedHash,
        GetProviderSignerFromChain getSigner,
        GetProviderCertificateFromChain getCertificate)
    {
        if (primarySigner.CounterSignerCount != 1)
            return false;
        IntPtr timestampSignerPointer = getSigner(
            providerData,
            signerIndex: 0,
            counterSigner: true,
            counterSignerIndex: 0);
        if (timestampSignerPointer == IntPtr.Zero)
            return false;

        CryptProviderSigner timestampSigner =
            Marshal.PtrToStructure<CryptProviderSigner>(timestampSignerPointer);
        if (timestampSigner.StructureSize < Marshal.SizeOf<CryptProviderSigner>()
            || timestampSigner.Error != 0
            || (timestampSigner.SignerType & TimestampSignerType) == 0
            || timestampSigner.CertificateChainCount == 0
            || timestampSigner.CertificateChainCount > MaximumCertificateChainCount
            || (timestampSigner.VerifyAsOf.dwLowDateTime == 0
                && timestampSigner.VerifyAsOf.dwHighDateTime == 0))
        {
            return false;
        }

        var candidates = new X509Certificate2Collection();
        try
        {
            for (uint index = 0;
                 index < timestampSigner.CertificateChainCount;
                 index++)
            {
                X509Certificate2? certificate = CopyCertificate(
                    getCertificate(timestampSignerPointer, index));
                if (certificate is null)
                    return false;
                candidates.Add(certificate);
            }

            X509Certificate2? tokenSigner = null;
            if (!Rfc3161TimestampToken.TryDecode(
                    timestampToken,
                    out Rfc3161TimestampToken? token,
                    out int bytesConsumed)
                || token is null
                || bytesConsumed != timestampToken.Length
                || !string.Equals(
                    token.TokenInfo.HashAlgorithmId.Value,
                    Sha256Oid,
                    StringComparison.Ordinal)
                || !token.VerifySignatureForData(
                    encryptedHash,
                    out tokenSigner,
                    candidates)
                || tokenSigner is null)
            {
                tokenSigner?.Dispose();
                return false;
            }

            using (tokenSigner)
            {
                return CryptographicOperations.FixedTimeEquals(
                    tokenSigner.RawData,
                    candidates[0].RawData);
            }
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            foreach (X509Certificate2 candidate in candidates)
                candidate.Dispose();
        }
    }

    private static bool TryReadRfc3161Timestamp(
        IntPtr signerInformationPointer,
        out byte[]? timestampToken,
        out byte[]? encryptedHash)
    {
        timestampToken = null;
        encryptedHash = null;
        CMsgSignerInfo signerInformation =
            Marshal.PtrToStructure<CMsgSignerInfo>(signerInformationPointer);
        encryptedHash = CopyBlob(
            signerInformation.EncryptedHash,
            MaximumEncryptedHashBytes);
        if (encryptedHash is null)
            return false;

        CryptAttributes attributes = signerInformation.UnauthenticatedAttributes;
        if (attributes.Count == 0
            || attributes.Count > MaximumUnauthenticatedAttributes
            || attributes.Items == IntPtr.Zero)
        {
            return false;
        }

        int attributeSize = Marshal.SizeOf<CryptAttribute>();
        int rfc3161AttributeCount = 0;
        for (uint index = 0; index < attributes.Count; index++)
        {
            IntPtr item = IntPtr.Add(
                attributes.Items,
                checked((int)index * attributeSize));
            CryptAttribute attribute =
                Marshal.PtrToStructure<CryptAttribute>(item);
            if (!TryReadBoundedAscii(
                    attribute.ObjectIdentifier,
                    128,
                    out string? oid))
            {
                return false;
            }
            if (string.Equals(oid, LegacyCounterSignatureOid, StringComparison.Ordinal))
                return false;
            if (!string.Equals(oid, Rfc3161CounterSignatureOid, StringComparison.Ordinal))
                continue;

            rfc3161AttributeCount++;
            if (rfc3161AttributeCount != 1
                || attribute.ValueCount != 1
                || attribute.Values == IntPtr.Zero)
            {
                return false;
            }

            CryptDataBlob encodedToken =
                Marshal.PtrToStructure<CryptDataBlob>(attribute.Values);
            timestampToken = CopyBlob(
                encodedToken,
                MaximumTimestampTokenBytes);
            if (timestampToken is null)
                return false;
        }

        return rfc3161AttributeCount == 1 && timestampToken is not null;
    }

    private static byte[]? CopyBlob(CryptDataBlob blob, int maximumBytes)
    {
        if (blob.Data == IntPtr.Zero
            || blob.Size == 0
            || blob.Size > maximumBytes)
        {
            return null;
        }

        var bytes = new byte[blob.Size];
        Marshal.Copy(blob.Data, bytes, startIndex: 0, bytes.Length);
        return bytes;
    }

    private static X509Certificate2? CopyCertificate(
        IntPtr providerCertificatePointer)
    {
        if (providerCertificatePointer == IntPtr.Zero)
            return null;
        CryptProviderCertificatePrefix providerCertificate =
            Marshal.PtrToStructure<CryptProviderCertificatePrefix>(
                providerCertificatePointer);
        if (providerCertificate.StructureSize
                < Marshal.SizeOf<CryptProviderCertificatePrefix>()
            || providerCertificate.CertificateContext == IntPtr.Zero)
        {
            return null;
        }

        CertificateContext certificateContext =
            Marshal.PtrToStructure<CertificateContext>(
                providerCertificate.CertificateContext);
        if (certificateContext.EncodedCertificate == IntPtr.Zero
            || certificateContext.EncodedCertificateBytes == 0
            || certificateContext.EncodedCertificateBytes > MaximumCertificateBytes)
        {
            return null;
        }

        var encoded = new byte[certificateContext.EncodedCertificateBytes];
        Marshal.Copy(
            certificateContext.EncodedCertificate,
            encoded,
            startIndex: 0,
            encoded.Length);
        try
        {
            return X509CertificateLoader.LoadCertificate(encoded);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static bool TryReadBoundedAscii(
        IntPtr pointer,
        int maximumBytes,
        out string? value)
    {
        value = null;
        if (pointer == IntPtr.Zero || maximumBytes <= 0)
            return false;
        var bytes = new byte[maximumBytes];
        int length = 0;
        for (; length < bytes.Length; length++)
        {
            byte current = Marshal.ReadByte(pointer, length);
            if (current == 0)
            {
                value = Encoding.ASCII.GetString(bytes, 0, length);
                return true;
            }
            if (current > 0x7F)
                return false;
            bytes[length] = current;
        }

        return false;
    }

    private static bool TryLoadHelpers(
        IntPtr module,
        out WinTrustHelpers? helpers)
    {
        helpers = null;
        if (!NativeLibrary.TryGetExport(
                module,
                "WTHelperProvDataFromStateData",
                out IntPtr providerDataAddress)
            || !NativeLibrary.TryGetExport(
                module,
                "WTHelperGetProvSignerFromChain",
                out IntPtr signerAddress)
            || !NativeLibrary.TryGetExport(
                module,
                "WTHelperGetProvCertFromChain",
                out IntPtr certificateAddress))
        {
            return false;
        }

        helpers = new WinTrustHelpers(
            Marshal.GetDelegateForFunctionPointer<ProviderDataFromStateData>(
                providerDataAddress),
            Marshal.GetDelegateForFunctionPointer<GetProviderSignerFromChain>(
                signerAddress),
            Marshal.GetDelegateForFunctionPointer<GetProviderCertificateFromChain>(
                certificateAddress));
        return true;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr ProviderDataFromStateData(IntPtr stateData);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr GetProviderSignerFromChain(
        IntPtr providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr GetProviderCertificateFromChain(
        IntPtr providerSigner,
        uint certificateIndex);

    private sealed record WinTrustHelpers(
        ProviderDataFromStateData ProviderDataFromState,
        GetProviderSignerFromChain GetSigner,
        GetProviderCertificateFromChain GetCertificate);

    private sealed record EnhancedKeyUsageEvidence(
        IReadOnlySet<string> Oids,
        bool HasSingleExtension,
        bool HasDuplicateUsage);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        internal uint StructureSize;
        internal IntPtr FilePath;
        internal IntPtr FileHandle;
        internal IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
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
        internal IntPtr UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
        internal IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustSignatureSettings
    {
        internal uint StructureSize;
        internal uint Index;
        internal uint Flags;
        internal uint SecondarySignatureCount;
        internal uint VerifiedSignatureIndex;
        internal IntPtr CryptographicPolicy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderSigner
    {
        internal uint StructureSize;
        internal System.Runtime.InteropServices.ComTypes.FILETIME VerifyAsOf;
        internal uint CertificateChainCount;
        internal IntPtr CertificateChain;
        internal uint SignerType;
        internal IntPtr SignerInformation;
        internal uint Error;
        internal uint CounterSignerCount;
        internal IntPtr CounterSigners;
        internal IntPtr ChainContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertificatePrefix
    {
        internal uint StructureSize;
        internal IntPtr CertificateContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext
    {
        internal uint EncodingType;
        internal IntPtr EncodedCertificate;
        internal uint EncodedCertificateBytes;
        internal IntPtr CertificateInformation;
        internal IntPtr CertificateStore;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptDataBlob
    {
        internal uint Size;
        internal IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAlgorithmIdentifier
    {
        internal IntPtr ObjectIdentifier;
        internal CryptDataBlob Parameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAttributes
    {
        internal uint Count;
        internal IntPtr Items;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAttribute
    {
        internal IntPtr ObjectIdentifier;
        internal uint ValueCount;
        internal IntPtr Values;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMsgSignerInfo
    {
        internal uint Version;
        internal CryptDataBlob Issuer;
        internal CryptDataBlob SerialNumber;
        internal CryptAlgorithmIdentifier HashAlgorithm;
        internal CryptAlgorithmIdentifier HashEncryptionAlgorithm;
        internal CryptDataBlob EncryptedHash;
        internal CryptAttributes AuthenticatedAttributes;
        internal CryptAttributes UnauthenticatedAttributes;
    }
}
