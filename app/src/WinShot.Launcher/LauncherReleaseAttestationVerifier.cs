using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

#pragma warning disable CS8600, CS8602, CS8625

namespace WinShot.Launcher
{
    internal sealed class LauncherReleaseTrustLease : IDisposable
    {
        private List<OpenedLauncherFile> _files;

        internal LauncherReleaseTrustLease(List<OpenedLauncherFile> files)
        {
            _files = files;
        }

        public void Dispose()
        {
            List<OpenedLauncherFile> files = _files;
            _files = null;
            if (files == null)
                return;

            for (int index = files.Count - 1; index >= 0; index--)
                files[index].Dispose();
        }
    }

    internal sealed class LauncherReleaseFileEvidence
    {
        internal LauncherReleaseFileEvidence(
            string path,
            long length,
            string sha256)
        {
            Path = path;
            Length = length;
            Sha256 = sha256;
        }

        internal string Path { get; private set; }

        internal long Length { get; private set; }

        internal string Sha256 { get; private set; }
    }

    internal sealed class LauncherReleaseAttestation
    {
        internal LauncherReleaseAttestation(
            string releaseManifestSha256,
            List<LauncherReleaseFileEvidence> files)
        {
            ReleaseManifestSha256 = releaseManifestSha256;
            Files = files;
        }

        internal string ReleaseManifestSha256 { get; private set; }

        internal List<LauncherReleaseFileEvidence> Files { get; private set; }
    }

    internal static class LauncherReleaseAttestationVerifier
    {
        internal const string AttestationFileName =
            "activation-release-attestation.v1";
        internal const string SignatureFileName =
            "activation-release-attestation.sig";
        private const string ReleaseManifestFileName = "release-manifest.json";
        private const string Magic =
            "WINSHOT-ACTIVATION-RELEASE-ATTESTATION-V1";
        private const int MaximumAttestationBytes = 4194304;
        private const int MaximumSignatureBytes = 1024;
        private const int MaximumFileCount = 4094;
        private const int MaximumDirectoryCount = 4094;
        private const int MaximumTraversalDepth = 32;
        private const int MaximumObservedEntries = 8192;
        private const int MaximumInventoryPathLength = 1024;
        private const long MaximumObservedPayloadBytes = 2147483648;
        private static readonly string[] ReservedWindowsDeviceNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5",
            "LPT6", "LPT7", "LPT8", "LPT9",
        };

        internal static bool TryAcquire(
            string versionDirectory,
            string activeVersion,
            LauncherConfiguration configuration,
            out IDisposable retainedTrust)
        {
            retainedTrust = null;
            if (configuration == null
                || !configuration.HasConfiguredReleaseTrust
                || !LauncherPathPolicy.IsSafeVersionSegment(activeVersion)
                || String.IsNullOrWhiteSpace(versionDirectory)
                || !Path.IsPathRooted(versionDirectory))
            {
                return false;
            }

            var retainedFiles = new List<OpenedLauncherFile>();
            try
            {
                string root = LauncherPathPolicy.TrimEndingDirectorySeparators(
                    Path.GetFullPath(versionDirectory));
                string attestationPath = Path.Combine(
                    root,
                    AttestationFileName);
                string signaturePath = Path.Combine(
                    root,
                    SignatureFileName);
                OpenedLauncherFile attestationFile =
                    OpenExact(attestationPath);
                retainedFiles.Add(attestationFile);
                OpenedLauncherFile signatureFile =
                    OpenExact(signaturePath);
                retainedFiles.Add(signatureFile);

                byte[] attestationBytes = ReadBounded(
                    attestationFile.Stream,
                    MaximumAttestationBytes);
                byte[] signatureBytes = ReadBounded(
                    signatureFile.Stream,
                    MaximumSignatureBytes);
                if (!VerifySignature(
                        attestationBytes,
                        signatureBytes,
                        configuration))
                {
                    return false;
                }

                LauncherReleaseAttestation attestation;
                if (!TryParse(
                        attestationBytes,
                        activeVersion,
                        configuration,
                        out attestation))
                {
                    return false;
                }

                List<string> observedFiles = EnumerateBoundedFiles(root);
                var observedByPath = new Dictionary<string, string>(
                    StringComparer.Ordinal);
                var observedWindowsPaths = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (string observedFile in observedFiles)
                {
                    string fullPath = Path.GetFullPath(observedFile);
                    if (!IsStrictlyWithin(fullPath, root))
                        return false;
                    string relativePath = fullPath
                        .Substring(root.Length + 1)
                        .Replace('\\', '/');
                    if (String.Equals(
                            relativePath,
                            AttestationFileName,
                            StringComparison.Ordinal)
                        || String.Equals(
                            relativePath,
                            SignatureFileName,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!observedByPath.ContainsKey(relativePath))
                        observedByPath.Add(relativePath, fullPath);
                    else
                        return false;
                    if (!observedWindowsPaths.Add(relativePath))
                        return false;
                }

                if (observedByPath.Count != attestation.Files.Count)
                    return false;

                for (int index = 0; index < attestation.Files.Count; index++)
                {
                    LauncherReleaseFileEvidence expected =
                        attestation.Files[index];
                    string fullPath;
                    if (!observedByPath.TryGetValue(expected.Path, out fullPath))
                        return false;

                    OpenedLauncherFile opened = OpenExact(fullPath);
                    retainedFiles.Add(opened);
                    if (opened.Identity.FileLength != expected.Length
                        || !String.Equals(
                            ComputeSha256(opened.Stream),
                            expected.Sha256,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                retainedTrust = new LauncherReleaseTrustLease(retainedFiles);
                retainedFiles = null;
                return true;
            }
            catch (Exception exception)
            {
                if (!(exception is ArgumentException)
                    && !(exception is IOException)
                    && !(exception is UnauthorizedAccessException)
                    && !(exception is NotSupportedException)
                    && !(exception is CryptographicException)
                    && !(exception is OverflowException)
                    && !(exception is System.Security.SecurityException))
                {
                    throw;
                }

                return false;
            }
            finally
            {
                if (retainedFiles != null)
                {
                    for (int index = retainedFiles.Count - 1;
                        index >= 0;
                        index--)
                    {
                        retainedFiles[index].Dispose();
                    }
                }
            }
        }

        internal static bool TryParse(
            byte[] attestationBytes,
            string activeVersion,
            LauncherConfiguration configuration,
            out LauncherReleaseAttestation attestation)
        {
            attestation = null;
            if (attestationBytes == null
                || attestationBytes.Length == 0
                || attestationBytes.Length > MaximumAttestationBytes
                || attestationBytes[attestationBytes.Length - 1] != 0x0A
                || (attestationBytes.Length >= 3
                    && attestationBytes[0] == 0xEF
                    && attestationBytes[1] == 0xBB
                    && attestationBytes[2] == 0xBF))
            {
                return false;
            }

            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(
                    attestationBytes);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
            if (text.IndexOf('\r') >= 0
                || text.IndexOf('\0') >= 0
                || text.Length == 0
                || text[text.Length - 1] != '\n')
            {
                return false;
            }

            string[] lines = text.Substring(0, text.Length - 1).Split('\n');
            if (lines.Length < 9
                || !String.Equals(lines[0], Magic, StringComparison.Ordinal))
            {
                return false;
            }

            string[] headerNames =
            {
                "package-id",
                "version",
                "channel",
                "runtime-identifier",
                "paid-major",
                "release-manifest-sha256",
                "file-count",
            };
            string[] expectedValues =
            {
                configuration.PackageId,
                activeVersion,
                configuration.ExpectedReleaseChannel,
                configuration.ExpectedRuntimeIdentifier,
                configuration.ExpectedPaidMajor.ToString(
                    CultureInfo.InvariantCulture),
                null,
                null,
            };
            string manifestSha256 = null;
            int fileCount = 0;
            for (int index = 0; index < headerNames.Length; index++)
            {
                string[] parts = lines[index + 1].Split('\t');
                if (parts.Length != 2
                    || !String.Equals(
                        parts[0],
                        headerNames[index],
                        StringComparison.Ordinal))
                {
                    return false;
                }

                if (expectedValues[index] != null
                    && !String.Equals(
                        parts[1],
                        expectedValues[index],
                        StringComparison.Ordinal))
                {
                    return false;
                }
                if (index == 5)
                {
                    if (!LauncherConfiguration.IsUppercaseSha256(parts[1]))
                        return false;
                    manifestSha256 = parts[1];
                }
                else if (index == 6
                    && (!IsCanonicalPositiveInt(parts[1], out fileCount)
                        || fileCount > MaximumFileCount))
                {
                    return false;
                }
            }

            if (fileCount <= 0 || lines.Length != 8 + fileCount)
                return false;

            var files = new List<LauncherReleaseFileEvidence>(fileCount);
            var windowsPaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            string previousPath = null;
            string manifestInventorySha256 = null;
            for (int index = 0; index < fileCount; index++)
            {
                string[] parts = lines[index + 8].Split('\t');
                long length;
                if (parts.Length != 4
                    || !String.Equals(
                        parts[0],
                        "file",
                        StringComparison.Ordinal)
                    || !LauncherConfiguration.IsUppercaseSha256(parts[1])
                    || !IsCanonicalNonNegativeLong(parts[2], out length)
                    || !IsSafeInventoryPath(parts[3])
                    || (previousPath != null
                        && StringComparer.Ordinal.Compare(
                            previousPath,
                            parts[3]) >= 0)
                    || !windowsPaths.Add(parts[3]))
                {
                    return false;
                }

                previousPath = parts[3];
                if (String.Equals(
                        parts[3],
                        ReleaseManifestFileName,
                        StringComparison.Ordinal))
                {
                    manifestInventorySha256 = parts[1];
                }
                files.Add(new LauncherReleaseFileEvidence(
                    parts[3],
                    length,
                    parts[1]));
            }

            if (manifestSha256 == null
                || !String.Equals(
                    manifestSha256,
                    manifestInventorySha256,
                    StringComparison.Ordinal))
            {
                return false;
            }

            attestation = new LauncherReleaseAttestation(
                manifestSha256,
                files);
            return true;
        }

        private static OpenedLauncherFile OpenExact(string path)
        {
            OpenedLauncherFile opened =
                LauncherFileHandle.OpenReadNoReparse(path);
            if (!String.Equals(
                    opened.ResolvedPath,
                    path,
                    StringComparison.OrdinalIgnoreCase))
            {
                opened.Dispose();
                throw new UnauthorizedAccessException(
                    "A release file resolved outside its exact path.");
            }

            return opened;
        }

        private static bool VerifySignature(
            byte[] attestationBytes,
            byte[] signatureBytes,
            LauncherConfiguration configuration)
        {
            byte[] publicKeyDer;
            try
            {
                publicKeyDer = Convert.FromBase64String(
                    configuration.ReleaseAttestationPublicKeyDerBase64);
            }
            catch (FormatException)
            {
                return false;
            }
            if (!String.Equals(
                    ComputeSha256(publicKeyDer),
                    configuration.ReleaseAttestationPublicKeySha256,
                    StringComparison.Ordinal))
            {
                return false;
            }

            RSAParameters parameters;
            if (!StrictRsaSubjectPublicKeyInfo.TryRead(
                    publicKeyDer,
                    out parameters))
            {
                return false;
            }

            using (var rsa = new RSACng())
            {
                rsa.ImportParameters(parameters);
                if (rsa.KeySize < 3072
                    || rsa.KeySize > 8192
                    || signatureBytes.Length != rsa.KeySize / 8)
                {
                    return false;
                }

                return rsa.VerifyData(
                    attestationBytes,
                    signatureBytes,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss);
            }
        }

        private static byte[] ReadBounded(Stream stream, int maximumBytes)
        {
            if (stream == null
                || !stream.CanRead
                || !stream.CanSeek
                || stream.Length <= 0
                || stream.Length > maximumBytes)
            {
                throw new IOException("A release trust file has invalid length.");
            }

            stream.Position = 0;
            byte[] bytes = new byte[(int)stream.Length];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read <= 0)
                    throw new EndOfStreamException();
                offset += read;
            }

            return bytes;
        }

        private static string ComputeSha256(Stream stream)
        {
            stream.Position = 0;
            using (SHA256 algorithm = SHA256.Create())
                return ToUpperHex(algorithm.ComputeHash(stream));
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create())
                return ToUpperHex(algorithm.ComputeHash(bytes));
        }

        private static string ToUpperHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
                builder.Append(value.ToString("X2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        private static bool IsCanonicalPositiveInt(
            string value,
            out int parsed)
        {
            parsed = 0;
            return !String.IsNullOrEmpty(value)
                && value[0] >= '1'
                && value[0] <= '9'
                && IsAsciiDigits(value)
                && Int32.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parsed)
                && parsed > 0;
        }

        private static bool IsCanonicalNonNegativeLong(
            string value,
            out long parsed)
        {
            parsed = 0;
            return !String.IsNullOrEmpty(value)
                && IsAsciiDigits(value)
                && (value.Length == 1 || value[0] != '0')
                && Int64.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parsed)
                && parsed >= 0;
        }

        private static bool IsAsciiDigits(string value)
        {
            foreach (char character in value)
            {
                if (character < '0' || character > '9')
                    return false;
            }

            return true;
        }

        internal static bool IsSafeInventoryPath(string value)
        {
            if (String.IsNullOrEmpty(value)
                || value.Length > MaximumInventoryPathLength
                || value[0] == '/'
                || value.IndexOf('\\') >= 0
                || value.IndexOf(':') >= 0
                || String.Equals(
                    value,
                    AttestationFileName,
                    StringComparison.OrdinalIgnoreCase)
                || String.Equals(
                    value,
                    SignatureFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string[] segments = value.Split('/');
            foreach (string segment in segments)
            {
                if (String.IsNullOrEmpty(segment)
                    || segment.Length > 255
                    || String.Equals(segment, ".", StringComparison.Ordinal)
                    || String.Equals(segment, "..", StringComparison.Ordinal))
                {
                    return false;
                }

                char last = segment[segment.Length - 1];
                if (last == '.' || last == ' ')
                    return false;
                int extensionSeparator = segment.IndexOf('.');
                string deviceName = extensionSeparator < 0
                    ? segment
                    : segment.Substring(0, extensionSeparator);
                foreach (string reserved in ReservedWindowsDeviceNames)
                {
                    if (String.Equals(
                            deviceName,
                            reserved,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }
            foreach (char character in value)
            {
                if (character < 0x20
                    || character == '"'
                    || character == '<'
                    || character == '>'
                    || character == '|'
                    || character == '?'
                    || character == '*')
                {
                    return false;
                }
            }

            return true;
        }

        internal static List<string> EnumerateBoundedFiles(string root)
        {
            var files = new List<string>();
            var pending = new Stack<PendingDirectory>();
            pending.Push(new PendingDirectory(root, 0));
            int directoryCount = 0;
            int observedEntryCount = 0;
            long observedBytes = 0;

            while (pending.Count != 0)
            {
                PendingDirectory current = pending.Pop();
                if (current.Depth > MaximumTraversalDepth)
                    throw new IOException("The release inventory is too deep.");
                FileAttributes directoryAttributes =
                    File.GetAttributes(current.Path);
                if ((directoryAttributes & FileAttributes.Directory) == 0
                    || (directoryAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UnauthorizedAccessException(
                        "A release inventory directory is unsafe.");
                }

                foreach (string entry in Directory.EnumerateFileSystemEntries(
                    current.Path,
                    "*",
                    SearchOption.TopDirectoryOnly))
                {
                    observedEntryCount++;
                    if (observedEntryCount > MaximumObservedEntries)
                    {
                        throw new IOException(
                            "The release inventory has too many entries.");
                    }

                    if (entry.Length > 32767)
                        throw new IOException("A release path is too long.");
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new UnauthorizedAccessException(
                            "A release inventory entry is a reparse point.");
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directoryCount++;
                        if (directoryCount > MaximumDirectoryCount)
                        {
                            throw new IOException(
                                "The release inventory has too many directories.");
                        }
                        pending.Push(new PendingDirectory(
                            entry,
                            current.Depth + 1));
                        continue;
                    }

                    if (files.Count >= MaximumFileCount + 2)
                    {
                        throw new IOException(
                            "The release inventory has too many files.");
                    }
                    long length = new FileInfo(entry).Length;
                    observedBytes = checked(observedBytes + length);
                    if (observedBytes > MaximumObservedPayloadBytes)
                    {
                        throw new IOException(
                            "The release inventory exceeds its byte budget.");
                    }
                    files.Add(entry);
                }
            }

            return files;
        }

        private static bool IsStrictlyWithin(string candidate, string root)
        {
            string prefix = root + Path.DirectorySeparatorChar;
            return candidate.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase);
        }

        private sealed class PendingDirectory
        {
            internal PendingDirectory(string path, int depth)
            {
                Path = path;
                Depth = depth;
            }

            internal string Path { get; private set; }

            internal int Depth { get; private set; }
        }
    }

    internal static class StrictRsaSubjectPublicKeyInfo
    {
        private static readonly byte[] RsaEncryptionOid =
        {
            0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01,
        };

        internal static bool TryRead(
            byte[] encoded,
            out RSAParameters parameters)
        {
            parameters = new RSAParameters();
            if (encoded == null || encoded.Length == 0 || encoded.Length > 8192)
                return false;

            try
            {
                var outer = new StrictDerReader(encoded);
                StrictDerReader subjectPublicKeyInfo = outer.ReadSequence();
                if (!outer.IsComplete)
                    return false;

                StrictDerReader algorithm = subjectPublicKeyInfo.ReadSequence();
                byte[] oid = algorithm.ReadValue(0x06);
                byte[] nullValue = algorithm.ReadValue(0x05);
                if (!algorithm.IsComplete
                    || nullValue.Length != 0
                    || !BytesEqual(oid, RsaEncryptionOid))
                {
                    return false;
                }

                byte[] bitString = subjectPublicKeyInfo.ReadValue(0x03);
                if (!subjectPublicKeyInfo.IsComplete
                    || bitString.Length < 2
                    || bitString[0] != 0)
                {
                    return false;
                }

                byte[] rsaBytes = new byte[bitString.Length - 1];
                Buffer.BlockCopy(
                    bitString,
                    1,
                    rsaBytes,
                    0,
                    rsaBytes.Length);
                var rsaOuter = new StrictDerReader(rsaBytes);
                StrictDerReader rsa = rsaOuter.ReadSequence();
                if (!rsaOuter.IsComplete)
                    return false;
                byte[] modulus = rsa.ReadPositiveInteger();
                byte[] exponent = rsa.ReadPositiveInteger();
                if (!rsa.IsComplete
                    || modulus.Length < 384
                    || modulus.Length > 1024
                    || (modulus[0] & 0x80) == 0
                    || exponent.Length == 0
                    || exponent.Length > 8
                    || (exponent.Length == 1 && exponent[0] < 3)
                    || (exponent[exponent.Length - 1] & 1) == 0)
                {
                    return false;
                }

                parameters = new RSAParameters
                {
                    Modulus = modulus,
                    Exponent = exponent,
                };
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
                return false;
            int difference = 0;
            for (int index = 0; index < left.Length; index++)
                difference |= left[index] ^ right[index];
            return difference == 0;
        }
    }

    internal sealed class StrictDerReader
    {
        private readonly byte[] _bytes;
        private readonly int _end;
        private int _offset;

        internal StrictDerReader(byte[] bytes)
            : this(bytes, 0, bytes.Length)
        {
        }

        private StrictDerReader(byte[] bytes, int offset, int length)
        {
            _bytes = bytes;
            _offset = offset;
            _end = checked(offset + length);
        }

        internal bool IsComplete
        {
            get { return _offset == _end; }
        }

        internal StrictDerReader ReadSequence()
        {
            int length = ReadHeader(0x30);
            var reader = new StrictDerReader(_bytes, _offset, length);
            _offset += length;
            return reader;
        }

        internal byte[] ReadValue(byte expectedTag)
        {
            int length = ReadHeader(expectedTag);
            var value = new byte[length];
            Buffer.BlockCopy(_bytes, _offset, value, 0, length);
            _offset += length;
            return value;
        }

        internal byte[] ReadPositiveInteger()
        {
            byte[] value = ReadValue(0x02);
            if (value.Length == 0)
                throw new CryptographicException("A DER integer is empty.");
            int offset = 0;
            if (value[0] == 0)
            {
                if (value.Length == 1 || (value[1] & 0x80) == 0)
                {
                    throw new CryptographicException(
                        "A DER integer has noncanonical padding.");
                }
                offset = 1;
            }
            else if ((value[0] & 0x80) != 0)
            {
                throw new CryptographicException(
                    "A DER integer is negative.");
            }

            byte[] positive = new byte[value.Length - offset];
            Buffer.BlockCopy(
                value,
                offset,
                positive,
                0,
                positive.Length);
            return positive;
        }

        private int ReadHeader(byte expectedTag)
        {
            if (_offset >= _end || _bytes[_offset++] != expectedTag)
                throw new CryptographicException("A DER tag is invalid.");
            if (_offset >= _end)
                throw new CryptographicException("A DER length is missing.");

            int first = _bytes[_offset++];
            int length;
            if ((first & 0x80) == 0)
            {
                length = first;
            }
            else
            {
                int count = first & 0x7F;
                if (count == 0
                    || count > 4
                    || count > _end - _offset
                    || _bytes[_offset] == 0)
                {
                    throw new CryptographicException(
                        "A DER length is invalid.");
                }

                length = 0;
                for (int index = 0; index < count; index++)
                    length = checked((length << 8) | _bytes[_offset++]);
                if (length < 128)
                {
                    throw new CryptographicException(
                        "A DER length is noncanonical.");
                }
            }

            if (length < 0 || length > _end - _offset)
                throw new CryptographicException("A DER value is truncated.");
            return length;
        }
    }
}
