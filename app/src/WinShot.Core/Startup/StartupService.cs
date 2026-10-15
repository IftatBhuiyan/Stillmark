using Microsoft.Win32;

namespace WinShot.Core.Startup;

public enum StartupSynchronizationStatus
{
    AlreadySynchronized,
    RegistrationWritten,
    RegistrationRemoved,
    RegistrationPartiallySynchronized,
    NoRegistration,
    ConflictingRegistrationPreserved,
    UnmanagedTarget,
    CommandTooLong,
    RegistryAccessFailed,
}

public readonly record struct StartupSynchronizationResult(
    StartupSynchronizationStatus Status,
    ApplicationPathKind TargetKind,
    Exception? Failure = null)
{
    public bool RegistryChanged =>
        Status is StartupSynchronizationStatus.RegistrationWritten
            or StartupSynchronizationStatus.RegistrationRemoved
            or StartupSynchronizationStatus.RegistrationPartiallySynchronized;

    public bool RequiresUserAttention =>
        Status is StartupSynchronizationStatus.ConflictingRegistrationPreserved
            or StartupSynchronizationStatus.UnmanagedTarget
            or StartupSynchronizationStatus.CommandTooLong
            or StartupSynchronizationStatus.RegistrationPartiallySynchronized
            or StartupSynchronizationStatus.RegistryAccessFailed;
}

internal interface IStartupRegistrationStore
{
    object? ReadValue();

    void WriteValue(string launchCommand);

    void DeleteValue();
}

internal interface INamedStartupRegistrationStore
{
    object? ReadValue(string valueName);

    void WriteValue(string valueName, string launchCommand);

    void DeleteValue(string valueName);
}

/// <summary>Registers the app in HKCU\...\Run so it launches with Windows.</summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinShot";
    private const int MaximumLaunchCommandLength = 260;

    public static string BuildLaunchCommand(string executablePath)
    {
        string launchCommand = FormatLaunchCommand(executablePath);
        if (launchCommand.Length > MaximumLaunchCommandLength)
        {
            throw new ArgumentException(
                $"The Windows startup command cannot exceed {MaximumLaunchCommandLength} characters.",
                nameof(executablePath));
        }

        return launchCommand;
    }

    public static bool IsExpectedLaunchCommand(string? launchCommand, string executablePath)
    {
        string normalizedPath = NormalizeExecutablePath(executablePath);

        if (!TryParseLaunchCommand(
                launchCommand,
                StringComparison.Ordinal,
                out string? registeredPath))
            return false;

        return string.Equals(
                registeredPath,
                normalizedPath,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                launchCommand,
                FormatLaunchCommand(registeredPath!),
                StringComparison.Ordinal);
    }

    /// <summary>
    /// Reconciles the registry value with the persisted user preference. Enabled registration is
    /// admitted only for an installer-owned stable target. A disabled preference removes only an
    /// command owned by this executable (including noncanonical argument casing) and preserves a
    /// mismatched same-name value.
    /// </summary>
    public static StartupSynchronizationResult Synchronize(
        bool enabled,
        StartupLaunchTarget launchTarget) =>
        Synchronize(
            enabled,
            launchTarget,
            new WindowsStartupRegistrationStore());

    internal static StartupSynchronizationResult Synchronize(
        bool enabled,
        StartupLaunchTarget launchTarget,
        IStartupRegistrationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!Enum.IsDefined(launchTarget.PathKind))
            throw new ArgumentOutOfRangeException(nameof(launchTarget));

        if (enabled && !launchTarget.IsInstallerOwned)
        {
            return new StartupSynchronizationResult(
                StartupSynchronizationStatus.UnmanagedTarget,
                launchTarget.PathKind);
        }

        string? expectedCommand = null;
        if (enabled)
        {
            expectedCommand = FormatLaunchCommand(launchTarget.ExecutablePath);
            if (expectedCommand.Length > MaximumLaunchCommandLength)
            {
                return new StartupSynchronizationResult(
                    StartupSynchronizationStatus.CommandTooLong,
                    launchTarget.PathKind);
            }
        }

        object? registeredValue;
        try
        {
            registeredValue = store.ReadValue();
        }
        catch (Exception ex)
        {
            return RegistryFailure(launchTarget, ex);
        }

        string? registeredCommand = registeredValue as string;
        bool registrationExists = registeredValue is not null;
        bool registrationMatches = IsExpectedLaunchCommand(
            registeredCommand,
            launchTarget.ExecutablePath);
        bool registrationOwned = IsOwnedLaunchCommand(
            registeredCommand,
            launchTarget.ExecutablePath);
        bool registrationSatisfiesPreference = enabled
            ? registrationMatches
            : registrationOwned;
        StartupRegistrationAction action = StartupRegistrationPolicy.Decide(
            enabled ? StartupPreference.Enabled : StartupPreference.Disabled,
            registrationExists,
            registrationSatisfiesPreference);

        try
        {
            if (action == StartupRegistrationAction.Remove)
            {
                store.DeleteValue();
                return new StartupSynchronizationResult(
                    StartupSynchronizationStatus.RegistrationRemoved,
                    launchTarget.PathKind);
            }

            if (action == StartupRegistrationAction.WriteExpected)
            {
                store.WriteValue(expectedCommand!);
                return new StartupSynchronizationResult(
                    StartupSynchronizationStatus.RegistrationWritten,
                    launchTarget.PathKind);
            }
        }
        catch (Exception ex)
        {
            return RegistryFailure(launchTarget, ex);
        }

        if (registrationSatisfiesPreference)
        {
            return new StartupSynchronizationResult(
                StartupSynchronizationStatus.AlreadySynchronized,
                launchTarget.PathKind);
        }

        return new StartupSynchronizationResult(
            registrationExists
                ? StartupSynchronizationStatus.ConflictingRegistrationPreserved
                : StartupSynchronizationStatus.NoRegistration,
            launchTarget.PathKind);
    }

    /// <summary>
    /// Reconciles a renamed application's Run value without taking ownership of an unrelated
    /// command. The new value is written before an owned legacy value is removed, so a cleanup
    /// interruption cannot silently disable the user's launch-at-sign-in preference.
    /// </summary>
    public static StartupSynchronizationResult SynchronizeBrandMigration(
        bool enabled,
        StartupLaunchTarget launchTarget,
        string currentValueName,
        string legacyValueName,
        string? legacyExecutablePath = null) =>
        SynchronizeBrandMigration(
            enabled,
            launchTarget,
            currentValueName,
            legacyValueName,
            legacyExecutablePath,
            new WindowsNamedStartupRegistrationStore());

    internal static StartupSynchronizationResult SynchronizeBrandMigration(
        bool enabled,
        StartupLaunchTarget launchTarget,
        string currentValueName,
        string legacyValueName,
        string? legacyExecutablePath,
        INamedStartupRegistrationStore store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentValueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyValueName);
        ArgumentNullException.ThrowIfNull(store);
        if (string.Equals(currentValueName, legacyValueName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Current and legacy startup value names must differ.");
        if (!Enum.IsDefined(launchTarget.PathKind))
            throw new ArgumentOutOfRangeException(nameof(launchTarget));
        if (enabled && !launchTarget.IsInstallerOwned)
        {
            return new StartupSynchronizationResult(
                StartupSynchronizationStatus.UnmanagedTarget,
                launchTarget.PathKind);
        }

        string? expectedCommand = null;
        if (enabled)
        {
            expectedCommand = FormatLaunchCommand(launchTarget.ExecutablePath);
            if (expectedCommand.Length > MaximumLaunchCommandLength)
            {
                return new StartupSynchronizationResult(
                    StartupSynchronizationStatus.CommandTooLong,
                    launchTarget.PathKind);
            }
        }

        object? currentValue;
        object? legacyValue;
        bool changed = false;
        try
        {
            currentValue = store.ReadValue(currentValueName);
            legacyValue = store.ReadValue(legacyValueName);
        }
        catch (Exception ex)
        {
            return RegistryFailure(launchTarget, ex);
        }

        bool currentExists = currentValue is not null;
        bool legacyExists = legacyValue is not null;
        string? currentCommand = currentValue as string;
        string? legacyCommand = legacyValue as string;
        string legacyOwnedExecutablePath = legacyExecutablePath ?? launchTarget.ExecutablePath;
        bool currentOwned = IsOwnedLaunchCommand(currentCommand, launchTarget.ExecutablePath);
        bool legacyOwned = IsOwnedLaunchCommand(legacyCommand, legacyOwnedExecutablePath);

        try
        {
            if (!enabled)
            {
                bool removed = false;
                if (currentOwned)
                {
                    store.DeleteValue(currentValueName);
                    removed = true;
                    changed = true;
                }
                if (legacyOwned)
                {
                    store.DeleteValue(legacyValueName);
                    removed = true;
                    changed = true;
                }
                if (removed)
                {
                    return new StartupSynchronizationResult(
                        StartupSynchronizationStatus.RegistrationRemoved,
                        launchTarget.PathKind);
                }

                return new StartupSynchronizationResult(
                    currentExists || legacyExists
                        ? StartupSynchronizationStatus.ConflictingRegistrationPreserved
                        : StartupSynchronizationStatus.NoRegistration,
                    launchTarget.PathKind);
            }

            bool currentExact = IsExpectedLaunchCommand(
                currentCommand,
                launchTarget.ExecutablePath);
            if (currentExists && !currentOwned)
            {
                return new StartupSynchronizationResult(
                    StartupSynchronizationStatus.ConflictingRegistrationPreserved,
                    launchTarget.PathKind);
            }
            if (legacyExists && !legacyOwned)
            {
                return new StartupSynchronizationResult(
                    StartupSynchronizationStatus.ConflictingRegistrationPreserved,
                    launchTarget.PathKind);
            }

            if (!currentExact)
            {
                store.WriteValue(currentValueName, expectedCommand!);
                changed = true;
            }
            if (legacyOwned)
            {
                store.DeleteValue(legacyValueName);
                changed = true;
            }

            return new StartupSynchronizationResult(
                changed
                    ? StartupSynchronizationStatus.RegistrationWritten
                    : StartupSynchronizationStatus.AlreadySynchronized,
                launchTarget.PathKind);
        }
        catch (Exception ex)
        {
            return changed
                ? new StartupSynchronizationResult(
                    StartupSynchronizationStatus.RegistrationPartiallySynchronized,
                    launchTarget.PathKind,
                    ex)
                : RegistryFailure(launchTarget, ex);
        }
    }

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is not null;
    }

    private static bool IsOwnedLaunchCommand(
        string? launchCommand,
        string executablePath)
    {
        string normalizedPath = NormalizeExecutablePath(executablePath);
        return TryParseLaunchCommand(
                launchCommand,
                StringComparison.OrdinalIgnoreCase,
                out string? registeredPath)
            && string.Equals(
                registeredPath,
                normalizedPath,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseLaunchCommand(
        string? launchCommand,
        StringComparison argumentComparison,
        out string? executablePath)
    {
        executablePath = null;
        if (string.IsNullOrWhiteSpace(launchCommand))
            return false;

        ReadOnlySpan<char> command = launchCommand.AsSpan().Trim();
        if (command.Length < 3 || command[0] != '"')
            return false;

        int closingQuote = command[1..].IndexOf('"');
        if (closingQuote < 0)
            return false;
        closingQuote++;

        ReadOnlySpan<char> path = command[1..closingQuote];
        ReadOnlySpan<char> arguments = command[(closingQuote + 1)..].Trim();
        if (path.IsEmpty || !arguments.Equals("--tray", argumentComparison))
            return false;

        executablePath = path.ToString();
        return true;
    }

    private static string NormalizeExecutablePath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        string normalizedPath = executablePath.Trim();
        if (normalizedPath.Contains('"'))
        {
            throw new ArgumentException(
                "The executable path cannot contain a quotation mark.",
                nameof(executablePath));
        }
        if (!Path.IsPathFullyQualified(normalizedPath))
        {
            throw new ArgumentException(
                "The executable path must be fully qualified.",
                nameof(executablePath));
        }
        if (!Path.GetExtension(normalizedPath).Equals(
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The executable path must identify a Windows .exe file.",
                nameof(executablePath));
        }

        return Path.GetFullPath(normalizedPath);
    }

    private static string FormatLaunchCommand(string executablePath)
    {
        string normalizedPath = NormalizeExecutablePath(executablePath);
        return $"\"{normalizedPath}\" --tray";
    }

    private static StartupSynchronizationResult RegistryFailure(
        StartupLaunchTarget launchTarget,
        Exception failure) =>
        new(
            StartupSynchronizationStatus.RegistryAccessFailed,
            launchTarget.PathKind,
            failure);

    private sealed class WindowsStartupRegistrationStore
        : IStartupRegistrationStore
    {
        public object? ReadValue()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(
                ValueName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        public void WriteValue(string launchCommand)
        {
            using RegistryKey key =
                Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(ValueName, launchCommand, RegistryValueKind.String);
        }

        public void DeleteValue()
        {
            using RegistryKey? key =
                Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private sealed class WindowsNamedStartupRegistrationStore
        : INamedStartupRegistrationStore
    {
        public object? ReadValue(string valueName)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(
                valueName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        public void WriteValue(string valueName, string launchCommand)
        {
            using RegistryKey key =
                Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(valueName, launchCommand, RegistryValueKind.String);
        }

        public void DeleteValue(string valueName)
        {
            using RegistryKey? key =
                Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}
