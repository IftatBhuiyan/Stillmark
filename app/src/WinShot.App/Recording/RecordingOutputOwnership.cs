using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace WinShot.App.Recording;

/// <summary>
/// Gives a native media writer an unoccupied path inside an atomically-owned directory, then
/// publishes the completed file to a human-readable destination without overwriting an existing
/// capture. Cleanup is limited to the exact staging file and directory created by this instance.
/// </summary>
internal sealed class RecordingOutputOwnership
{
    internal const int DefaultMaxAttempts = 1_000;
    internal const int MaximumAttempts = 10_000;

    private const int ErrorAlreadyExists = 183;
    internal const string WorkspacePrefix = ".winshot-recording-";

    private readonly string _destinationDirectory;
    private readonly string _baseName;
    private readonly string _extension;
    private readonly int _maxPublishAttempts;
    private bool _published;

    private RecordingOutputOwnership(
        string destinationDirectory,
        string workspaceDirectory,
        string baseName,
        string extension,
        int maxPublishAttempts)
    {
        _destinationDirectory = destinationDirectory;
        WorkspaceDirectory = workspaceDirectory;
        _baseName = baseName;
        _extension = extension;
        _maxPublishAttempts = maxPublishAttempts;
        NativePath = Path.Combine(workspaceDirectory, baseName + extension);
    }

    internal string NativePath { get; }

    internal string WorkspaceDirectory { get; }

    internal bool IsPublished => _published;

    internal static RecordingOutputOwnership Create(
        string destinationDirectory,
        string baseName,
        string extension,
        int maxAttempts = DefaultMaxAttempts)
    {
        return Create(
            destinationDirectory,
            baseName,
            extension,
            maxAttempts,
            _ => WorkspacePrefix + Guid.NewGuid().ToString("N"));
    }

    internal static RecordingOutputOwnership Create(
        string destinationDirectory,
        string baseName,
        string extension,
        int maxAttempts,
        Func<int, string> workspaceNameFactory)
    {
        ArgumentNullException.ThrowIfNull(workspaceNameFactory);
        string fullDirectory = ValidateInputs(
            destinationDirectory,
            baseName,
            extension,
            maxAttempts);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            string workspaceName = workspaceNameFactory(attempt);
            ValidateWorkspaceName(workspaceName);
            string workspaceDirectory = Path.Combine(fullDirectory, workspaceName);
            if (CreateDirectory(workspaceDirectory, IntPtr.Zero))
            {
                return new RecordingOutputOwnership(
                    fullDirectory,
                    workspaceDirectory,
                    baseName,
                    extension,
                    maxAttempts);
            }

            int error = Marshal.GetLastWin32Error();
            if (error == ErrorAlreadyExists)
                continue;

            throw new Win32Exception(
                error,
                "Unable to create an owned recording workspace.");
        }

        throw new IOException(
            $"Unable to create an owned recording workspace after {maxAttempts} attempts.");
    }

    /// <summary>
    /// Atomically moves the native output to the first available final name. If all names collide,
    /// the native file remains untouched in its owned workspace for recovery.
    /// </summary>
    internal string Publish()
    {
        if (_published)
            throw new InvalidOperationException("The recording output has already been published.");
        if (!File.Exists(NativePath))
            throw new FileNotFoundException("The native recording output was not found.", NativePath);

        for (int attempt = 1; attempt <= _maxPublishAttempts; attempt++)
        {
            string suffix = attempt == 1 ? "" : $" {attempt}";
            string destination = Path.Combine(
                _destinationDirectory,
                _baseName + suffix + _extension);
            try
            {
                File.Move(NativePath, destination);
            }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination))
            {
                continue;
            }

            _published = true;
            TryDeleteEmptyWorkspace();
            return destination;
        }

        throw new IOException(
            $"Unable to publish the recording after {_maxPublishAttempts} name collisions.");
    }

    internal bool OwnsNativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return string.Equals(
            Path.GetFullPath(path),
            Path.GetFullPath(NativePath),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Deletes only this instance's unpublished native output and empty workspace.</summary>
    internal void DeleteOwnedOutput()
    {
        if (_published)
            return;

        if (File.Exists(NativePath))
            File.Delete(NativePath);
        DeleteEmptyWorkspace();
    }

    /// <summary>
    /// Removes the owned workspace only when it is empty. A native output is deliberately retained
    /// when publication fails so the sole recoverable recording is never destroyed.
    /// </summary>
    internal void LeaveRecoverableOutput()
    {
        if (!File.Exists(NativePath))
            TryDeleteEmptyWorkspace();
    }

    internal static string CopyAndPublish(
        string sourcePath,
        string destinationDirectory,
        string baseName,
        string extension,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string sourceFullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourceFullPath))
            throw new FileNotFoundException("The recording source was not found.", sourceFullPath);

        var output = Create(
            destinationDirectory,
            baseName,
            extension,
            maxAttempts);
        try
        {
            long sourceLength;
            using (var source = new FileStream(
                       sourceFullPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       bufferSize: 64 * 1024,
                       FileOptions.SequentialScan))
            {
                sourceLength = source.Length;
                using var destination = new FileStream(
                    output.NativePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    FileOptions.SequentialScan);
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    destination.Write(buffer, 0, read);
                }
                cancellationToken.ThrowIfCancellationRequested();
                destination.Flush(flushToDisk: true);
            }

            if (new FileInfo(output.NativePath).Length != sourceLength)
                throw new IOException("The preserved recording copy was incomplete.");

            return output.Publish();
        }
        catch
        {
            try
            {
                output.DeleteOwnedOutput();
            }
            catch
            {
                // Preserve the original exception; cleanup remains limited to the exact owned path.
            }
            throw;
        }
    }

    private static string ValidateInputs(
        string destinationDirectory,
        string baseName,
        string extension,
        int maxAttempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        if (maxAttempts < 1 || maxAttempts > MaximumAttempts)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (!string.Equals(baseName, baseName.Trim(), StringComparison.Ordinal) ||
            baseName.EndsWith('.') ||
            baseName is "." or ".." ||
            baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            IsReservedDeviceName(baseName))
            throw new ArgumentException("The recording base name is not valid.", nameof(baseName));
        if (!extension.StartsWith('.') ||
            extension.Length < 2 ||
            extension.EndsWith('.') ||
            !string.Equals(extension, extension.Trim(), StringComparison.Ordinal) ||
            extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("The recording extension is not valid.", nameof(extension));

        string maximumSuffix = maxAttempts == 1 ? "" : $" {maxAttempts}";
        if (baseName.Length + maximumSuffix.Length + extension.Length > 255)
            throw new PathTooLongException("The recording file name is too long.");

        string fullDirectory = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(fullDirectory))
            throw new DirectoryNotFoundException("The recording destination does not exist.");
        return fullDirectory;
    }

    private static void ValidateWorkspaceName(string workspaceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceName);
        if (!string.Equals(workspaceName, workspaceName.Trim(), StringComparison.Ordinal) ||
            workspaceName is "." or ".." ||
            workspaceName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("The recording workspace name is not valid.");
    }

    private static bool IsReservedDeviceName(string baseName)
    {
        string stem = baseName.Split('.')[0].TrimEnd(' ', '.');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;

        return stem.Length == 4 &&
               stem[3] is >= '1' and <= '9' &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private void DeleteEmptyWorkspace()
    {
        if (Directory.Exists(WorkspaceDirectory))
            Directory.Delete(WorkspaceDirectory, recursive: false);
    }

    private void TryDeleteEmptyWorkspace()
    {
        try
        {
            DeleteEmptyWorkspace();
        }
        catch (IOException)
        {
            // Unexpected content is never deleted recursively. The published media remains valid.
        }
        catch (UnauthorizedAccessException)
        {
            // Publication already succeeded; a cleanup failure must not relabel it as data loss.
        }
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateDirectoryW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(
        string lpPathName,
        IntPtr lpSecurityAttributes);
}
