namespace WinShot.Core.Startup;

public enum ApplicationPathKind
{
    Development,
    Portable,
    InstalledPayload,
    InstalledStableLauncher,
}

/// <summary>
/// Installer-owned paths supplied by an adapter or recognized from the strict checked layout.
/// The policy does not select or initialize an installer technology.
/// </summary>
public sealed record InstalledApplicationLayout(
    string InstallRootPath,
    string StableLauncherPath);

public readonly record struct StartupLaunchTarget(
    ApplicationPathKind PathKind,
    string ExecutablePath)
{
    public bool IsInstallerOwned =>
        PathKind is ApplicationPathKind.InstalledPayload
            or ApplicationPathKind.InstalledStableLauncher;
}

/// <summary>
/// Pure path policy for keeping launch-at-sign-in pointed at an installer-owned stable launcher
/// rather than a versioned payload. No path is required to exist so installer planning and repair
/// can be tested without filesystem mutation.
/// </summary>
public static class InstalledLaunchPolicy
{
    private const string CurrentPayloadDirectoryName = "current";
    private const string VersionedPayloadDirectoryName = "versions";
    private const int MaximumVersionSegmentLength = 128;
    private static readonly string[] ReservedWindowsDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Discovers either the checked <c>versions\{Version}</c> layout or the reviewed
    /// <c>current</c>-payload installer candidate. The running executable and matching stable root
    /// launcher must both exist, and the installer root must be an immediate child of Local AppData.
    /// Other layouts remain portable until an installer adapter supplies them explicitly.
    /// </summary>
    public static InstalledApplicationLayout? DiscoverInstalledLayout(
        string processExecutablePath,
        string localApplicationDataPath) =>
        DiscoverInstalledLayout(processExecutablePath, localApplicationDataPath, File.Exists);

    internal static InstalledApplicationLayout? DiscoverInstalledLayout(
        string processExecutablePath,
        string localApplicationDataPath,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentException.ThrowIfNullOrWhiteSpace(processExecutablePath);
        if (HasUnsafeRawVersionSegment(processExecutablePath))
            return null;

        string processPath = NormalizeExecutablePath(
            processExecutablePath,
            nameof(processExecutablePath));
        string localRoot = NormalizeAbsolutePath(
            localApplicationDataPath,
            nameof(localApplicationDataPath));
        string executableName = Path.GetFileName(processPath);
        string processDirectory = Path.GetDirectoryName(processPath)
            ?? throw new ArgumentException(
                "The executable path must have a parent directory.",
                nameof(processExecutablePath));

        string installRoot;
        string payloadPath;
        if (Path.GetFileName(processDirectory).Equals(
                CurrentPayloadDirectoryName,
                StringComparison.OrdinalIgnoreCase))
        {
            installRoot = Path.GetDirectoryName(processDirectory)
                ?? throw new ArgumentException(
                    "The current payload directory must have an installer root.",
                    nameof(processExecutablePath));
            payloadPath = Path.Combine(
                installRoot,
                CurrentPayloadDirectoryName,
                executableName);
        }
        else
        {
            string? versionsDirectory = Path.GetDirectoryName(processDirectory);
            string versionSegment = Path.GetFileName(processDirectory);
            if (versionsDirectory is not null
                && Path.GetFileName(versionsDirectory).Equals(
                    VersionedPayloadDirectoryName,
                    StringComparison.OrdinalIgnoreCase)
                && IsSafeVersionSegment(versionSegment))
            {
                installRoot = Path.GetDirectoryName(versionsDirectory)
                    ?? throw new ArgumentException(
                        "The versions directory must have an installer root.",
                        nameof(processExecutablePath));
                payloadPath = Path.Combine(
                    installRoot,
                    VersionedPayloadDirectoryName,
                    versionSegment,
                    executableName);
            }
            else
            {
                installRoot = processDirectory;
                payloadPath = Path.Combine(
                    installRoot,
                    CurrentPayloadDirectoryName,
                    executableName);
            }
        }

        string? installParent = Path.GetDirectoryName(installRoot);
        if (!string.Equals(installParent, localRoot, StringComparison.OrdinalIgnoreCase))
            return null;

        string stableLauncher = Path.Combine(installRoot, executableName);
        bool processIsStableLauncher = string.Equals(
            processPath,
            stableLauncher,
            StringComparison.OrdinalIgnoreCase);
        bool processIsRecognizedPayload = string.Equals(
            processPath,
            payloadPath,
            StringComparison.OrdinalIgnoreCase);

        if ((!processIsStableLauncher && !processIsRecognizedPayload)
            || !fileExists(stableLauncher)
            || !fileExists(payloadPath))
        {
            return null;
        }

        return new InstalledApplicationLayout(installRoot, stableLauncher);
    }

    private static bool HasUnsafeRawVersionSegment(string processExecutablePath)
    {
        string? payloadDirectory = Path.GetDirectoryName(processExecutablePath.Trim());
        string? versionsDirectory = payloadDirectory is null
            ? null
            : Path.GetDirectoryName(payloadDirectory);
        return versionsDirectory is not null
            && Path.GetFileName(versionsDirectory).Equals(
                VersionedPayloadDirectoryName,
                StringComparison.OrdinalIgnoreCase)
            && !IsSafeVersionSegment(Path.GetFileName(payloadDirectory) ?? string.Empty);
    }

    private static bool IsSafeVersionSegment(string segment)
    {
        if (segment.Length is 0 or > MaximumVersionSegmentLength
            || !char.IsAsciiLetterOrDigit(segment[0])
            || !char.IsAsciiLetterOrDigit(segment[^1]))
        {
            return false;
        }

        if (!segment.All(static character =>
            char.IsAsciiLetterOrDigit(character)
                || character is '.' or '-' or '_' or '+'))
        {
            return false;
        }

        int extensionSeparator = segment.IndexOf('.');
        string deviceName = extensionSeparator < 0
            ? segment
            : segment[..extensionSeparator];
        return !ReservedWindowsDeviceNames.Contains(
            deviceName,
            StringComparer.OrdinalIgnoreCase);
    }

    public static string BuildLaunchCommand(
        string processExecutablePath,
        InstalledApplicationLayout? installedLayout = null)
    {
        StartupLaunchTarget target = Resolve(processExecutablePath, installedLayout);
        return StartupService.BuildLaunchCommand(target.ExecutablePath);
    }

    public static StartupLaunchTarget Resolve(
        string processExecutablePath,
        InstalledApplicationLayout? installedLayout = null)
    {
        string processPath = NormalizeExecutablePath(
            processExecutablePath,
            nameof(processExecutablePath));

        if (installedLayout is not null)
        {
            string installRoot = NormalizeAbsolutePath(
                installedLayout.InstallRootPath,
                nameof(installedLayout));
            string stableLauncher = NormalizeExecutablePath(
                installedLayout.StableLauncherPath,
                nameof(installedLayout));

            if (!IsPathWithin(stableLauncher, installRoot))
            {
                throw new ArgumentException(
                    "The stable launcher must be inside the installer-owned root.",
                    nameof(installedLayout));
            }

            if (string.Equals(processPath, stableLauncher, StringComparison.OrdinalIgnoreCase))
            {
                return new StartupLaunchTarget(
                    ApplicationPathKind.InstalledStableLauncher,
                    stableLauncher);
            }

            if (IsPathWithin(processPath, installRoot))
            {
                return new StartupLaunchTarget(
                    ApplicationPathKind.InstalledPayload,
                    stableLauncher);
            }
        }

        return new StartupLaunchTarget(
            LooksLikeDevelopmentOutput(processPath)
                ? ApplicationPathKind.Development
                : ApplicationPathKind.Portable,
            processPath);
    }

    private static bool LooksLikeDevelopmentOutput(string executablePath)
    {
        string? directory = Path.GetDirectoryName(executablePath);
        if (directory is null)
            return false;

        string[] segments = directory.Split(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        for (int index = 0; index < segments.Length - 1; index++)
        {
            if (!segments[index].Equals("bin", StringComparison.OrdinalIgnoreCase))
                continue;

            int configurationEnd = Math.Min(index + 2, segments.Length - 1);
            for (int configurationIndex = index + 1;
                configurationIndex <= configurationEnd;
                configurationIndex++)
            {
                if (segments[configurationIndex].Equals(
                        "Debug",
                        StringComparison.OrdinalIgnoreCase)
                    || segments[configurationIndex].Equals(
                        "Release",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsPathWithin(string candidatePath, string rootPath)
    {
        string relativePath = Path.GetRelativePath(rootPath, candidatePath);
        return !Path.IsPathRooted(relativePath)
            && !relativePath.Equals("..", StringComparison.Ordinal)
            && !relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal);
    }

    private static string NormalizeAbsolutePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);

        string trimmed = path.Trim();
        if (trimmed.Contains('"'))
            throw new ArgumentException("The path cannot contain a quotation mark.", parameterName);
        if (!Path.IsPathFullyQualified(trimmed))
            throw new ArgumentException("The path must be fully qualified.", parameterName);

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
    }

    private static string NormalizeExecutablePath(string path, string parameterName)
    {
        string normalizedPath = NormalizeAbsolutePath(path, parameterName);
        if (!Path.GetExtension(normalizedPath).Equals(
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The executable path must identify a Windows .exe file.",
                parameterName);
        }

        return normalizedPath;
    }
}
