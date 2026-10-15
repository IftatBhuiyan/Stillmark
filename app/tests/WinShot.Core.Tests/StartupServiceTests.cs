using WinShot.Core.Startup;
using Xunit;

namespace WinShot.Core.Tests;

public class StartupServiceTests
{
    private static readonly StartupLaunchTarget InstalledTarget = new(
        ApplicationPathKind.InstalledStableLauncher,
        @"C:\Users\Test\AppData\Local\WinShot\WinShot.exe");

    [Fact]
    public void BuildLaunchCommand_QuotesExecutableAndUsesTrayMode()
    {
        string command = StartupService.BuildLaunchCommand(
            @"C:\Program Files\WinShot\WinShot.exe");

        Assert.Equal("\"C:\\Program Files\\WinShot\\WinShot.exe\" --tray", command);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\WinShot\\WinShot.exe\" --tray")]
    public void IsExpectedLaunchCommand_AcceptsEquivalentQuotedCommand(string command)
    {
        Assert.True(StartupService.IsExpectedLaunchCommand(
            command,
            @"C:\Program Files\WinShot\WinShot.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C:\\Program Files\\WinShot\\WinShot.exe --tray")]
    [InlineData("\"C:\\Other\\WinShot.exe\" --tray")]
    [InlineData("\"C:\\Program Files\\WinShot\\WinShot.exe\"")]
    [InlineData("\"C:\\Program Files\\WinShot\\WinShot.exe\" --TRAY")]
    [InlineData(" \"C:\\Program Files\\WinShot\\WinShot.exe\" --tray")]
    [InlineData("\"C:\\Program Files\\WinShot\\WinShot.exe\"  --tray")]
    [InlineData("\"C:\\Program Files\\WinShot\\WinShot.exe\" --tray ")]
    [InlineData("\"C:\\Program Files\\WinShot\\WinShot.exe\" --tray --extra")]
    public void IsExpectedLaunchCommand_RejectsMalformedOrMismatchedCommand(string? command)
    {
        Assert.False(StartupService.IsExpectedLaunchCommand(
            command,
            @"C:\Program Files\WinShot\WinShot.exe"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\Bad\"Path\\WinShot.exe")]
    [InlineData("WinShot.exe")]
    [InlineData("..\\WinShot.exe")]
    [InlineData("C:\\Program Files\\WinShot\\README.md")]
    public void BuildLaunchCommand_RejectsInvalidExecutablePath(string executablePath)
    {
        Assert.Throws<ArgumentException>(() => StartupService.BuildLaunchCommand(executablePath));
    }

    [Fact]
    public void BuildLaunchCommand_AcceptsExactly260Characters()
    {
        string command = StartupService.BuildLaunchCommand(
            CreateExecutablePathForCommandLength(260));

        Assert.Equal(260, command.Length);
    }

    [Fact]
    public void BuildLaunchCommand_RejectsMoreThan260Characters()
    {
        string path = CreateExecutablePathForCommandLength(261);

        Assert.Throws<ArgumentException>(
            () => StartupService.BuildLaunchCommand(path));
    }

    [Theory]
    [InlineData(ApplicationPathKind.Development)]
    [InlineData(ApplicationPathKind.Portable)]
    public void EnabledUnmanagedTarget_DoesNotReadOrMutateRegistry(
        ApplicationPathKind pathKind)
    {
        var store = new RecordingStartupStore
        {
            ReadFailure = new InvalidOperationException(
                "An unmanaged target must not inspect startup state."),
        };
        var target = new StartupLaunchTarget(
            pathKind,
            @"C:\Portable\WinShot.exe");

        StartupSynchronizationResult result =
            StartupService.Synchronize(enabled: true, target, store);

        Assert.Equal(StartupSynchronizationStatus.UnmanagedTarget, result.Status);
        Assert.Equal(pathKind, result.TargetKind);
        Assert.True(result.RequiresUserAttention);
        Assert.False(result.RegistryChanged);
        Assert.Equal(0, store.ReadCount);
        Assert.Empty(store.WrittenCommands);
        Assert.Equal(0, store.DeleteCount);
    }

    [Fact]
    public void EnabledInstalledTarget_MissingRegistrationWritesStableCommand()
    {
        var store = new RecordingStartupStore();

        StartupSynchronizationResult result =
            StartupService.Synchronize(
                enabled: true,
                InstalledTarget,
                store);

        Assert.Equal(
            StartupSynchronizationStatus.RegistrationWritten,
            result.Status);
        Assert.True(result.RegistryChanged);
        Assert.Equal(
            "\"C:\\Users\\Test\\AppData\\Local\\WinShot\\WinShot.exe\" --tray",
            Assert.Single(store.WrittenCommands));
        Assert.Equal(0, store.DeleteCount);
    }

    [Fact]
    public void EnabledInstalledTarget_MismatchedOrWrongTypeValueIsRepaired()
    {
        var mismatched = new RecordingStartupStore
        {
            Value = "\"C:\\Old\\WinShot.exe\" --tray",
        };
        var wrongType = new RecordingStartupStore { Value = 42 };

        StartupSynchronizationResult mismatchedResult =
            StartupService.Synchronize(true, InstalledTarget, mismatched);
        StartupSynchronizationResult wrongTypeResult =
            StartupService.Synchronize(true, InstalledTarget, wrongType);

        Assert.Equal(
            StartupSynchronizationStatus.RegistrationWritten,
            mismatchedResult.Status);
        Assert.Equal(
            StartupSynchronizationStatus.RegistrationWritten,
            wrongTypeResult.Status);
        Assert.Single(mismatched.WrittenCommands);
        Assert.Single(wrongType.WrittenCommands);
    }

    [Fact]
    public void EnabledInstalledTarget_ExactRegistrationIsNotRewritten()
    {
        var store = new RecordingStartupStore
        {
            Value =
                "\"C:\\Users\\Test\\AppData\\Local\\WinShot\\WinShot.exe\" --tray",
        };

        StartupSynchronizationResult result =
            StartupService.Synchronize(true, InstalledTarget, store);

        Assert.Equal(
            StartupSynchronizationStatus.AlreadySynchronized,
            result.Status);
        Assert.False(result.RegistryChanged);
        Assert.Empty(store.WrittenCommands);
        Assert.Equal(0, store.DeleteCount);
    }

    [Fact]
    public void EnabledInstalledTarget_RepairsNonCanonicalTrayCasing()
    {
        var store = new RecordingStartupStore
        {
            Value =
                "\"C:\\Users\\Test\\AppData\\Local\\WinShot\\WinShot.exe\" --TRAY",
        };

        StartupSynchronizationResult result =
            StartupService.Synchronize(true, InstalledTarget, store);

        Assert.Equal(
            StartupSynchronizationStatus.RegistrationWritten,
            result.Status);
        Assert.Equal(
            "\"C:\\Users\\Test\\AppData\\Local\\WinShot\\WinShot.exe\" --tray",
            Assert.Single(store.WrittenCommands));
    }

    [Fact]
    public void EnabledInstalledTarget_RepairsNonCanonicalWhitespace()
    {
        var store = new RecordingStartupStore
        {
            Value =
                " \"C:\\Users\\Test\\AppData\\Local\\WinShot\\WinShot.exe\"  --tray ",
        };

        StartupSynchronizationResult result =
            StartupService.Synchronize(true, InstalledTarget, store);

        Assert.Equal(
            StartupSynchronizationStatus.RegistrationWritten,
            result.Status);
        Assert.Equal(
            "\"C:\\Users\\Test\\AppData\\Local\\WinShot\\WinShot.exe\" --tray",
            Assert.Single(store.WrittenCommands));
    }

    [Fact]
    public void DisabledUnmanagedTarget_RemovesOnlyItsExactCommand()
    {
        var target = new StartupLaunchTarget(
            ApplicationPathKind.Development,
            @"C:\src\WinShot\bin\Debug\WinShot.exe");
        var store = new RecordingStartupStore
        {
            Value = "\"C:\\src\\WinShot\\bin\\Debug\\WinShot.exe\" --tray",
        };

        StartupSynchronizationResult result =
            StartupService.Synchronize(false, target, store);

        Assert.Equal(
            StartupSynchronizationStatus.RegistrationRemoved,
            result.Status);
        Assert.True(result.RegistryChanged);
        Assert.Equal(1, store.DeleteCount);
        Assert.Empty(store.WrittenCommands);
    }

    [Theory]
    [InlineData(null, StartupSynchronizationStatus.NoRegistration)]
    [InlineData(
        "\"C:\\Other\\WinShot.exe\" --tray",
        StartupSynchronizationStatus.ConflictingRegistrationPreserved)]
    public void DisabledTarget_PreservesMissingOrMismatchedRegistration(
        object? registeredValue,
        StartupSynchronizationStatus expectedStatus)
    {
        var store = new RecordingStartupStore { Value = registeredValue };

        StartupSynchronizationResult result =
            StartupService.Synchronize(false, InstalledTarget, store);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(
            registeredValue is not null,
            result.RequiresUserAttention);
        Assert.False(result.RegistryChanged);
        Assert.Equal(0, store.DeleteCount);
        Assert.Empty(store.WrittenCommands);
    }

    [Fact]
    public void DisabledTarget_PreservesWrongTypeRegistration()
    {
        var store = new RecordingStartupStore { Value = 42 };

        StartupSynchronizationResult result =
            StartupService.Synchronize(false, InstalledTarget, store);

        Assert.Equal(
            StartupSynchronizationStatus.ConflictingRegistrationPreserved,
            result.Status);
        Assert.True(result.RequiresUserAttention);
        Assert.False(result.RegistryChanged);
        Assert.Equal(0, store.DeleteCount);
        Assert.Empty(store.WrittenCommands);
    }

    [Fact]
    public void DisabledInstalledTarget_RemovesOwnedNonCanonicalTrayCasing()
    {
        var store = new RecordingStartupStore
        {
            Value =
                "\"C:\\Users\\Test\\AppData\\Local\\WinShot\\WinShot.exe\" --TRAY",
        };

        StartupSynchronizationResult result =
            StartupService.Synchronize(false, InstalledTarget, store);

        Assert.Equal(
            StartupSynchronizationStatus.RegistrationRemoved,
            result.Status);
        Assert.Equal(1, store.DeleteCount);
        Assert.Empty(store.WrittenCommands);
    }

    [Fact]
    public void EnabledOverlongInstalledTarget_DoesNotInspectOrMutateRegistry()
    {
        var store = new RecordingStartupStore
        {
            ReadFailure = new InvalidOperationException(
                "An overlong command must fail before registry access."),
        };
        var target = new StartupLaunchTarget(
            ApplicationPathKind.InstalledStableLauncher,
            CreateExecutablePathForCommandLength(261));

        StartupSynchronizationResult result =
            StartupService.Synchronize(true, target, store);

        Assert.Equal(StartupSynchronizationStatus.CommandTooLong, result.Status);
        Assert.True(result.RequiresUserAttention);
        Assert.Equal(0, store.ReadCount);
        Assert.Empty(store.WrittenCommands);
        Assert.Equal(0, store.DeleteCount);
    }

    [Fact]
    public void DisabledExactOverlongTarget_CanRemoveLegacyOwnedCommand()
    {
        string path = CreateExecutablePathForCommandLength(261);
        var target = new StartupLaunchTarget(
            ApplicationPathKind.InstalledStableLauncher,
            path);
        var store = new RecordingStartupStore
        {
            Value = $"\"{path}\" --tray",
        };

        StartupSynchronizationResult result =
            StartupService.Synchronize(false, target, store);

        Assert.Equal(
            StartupSynchronizationStatus.RegistrationRemoved,
            result.Status);
        Assert.Equal(1, store.DeleteCount);
    }

    [Fact]
    public void RegistryReadFailure_IsReturnedWithoutMutation()
    {
        var expected = new UnauthorizedAccessException("synthetic read failure");
        var store = new RecordingStartupStore { ReadFailure = expected };

        StartupSynchronizationResult result =
            StartupService.Synchronize(true, InstalledTarget, store);

        Assert.Equal(
            StartupSynchronizationStatus.RegistryAccessFailed,
            result.Status);
        Assert.Same(expected, result.Failure);
        Assert.True(result.RequiresUserAttention);
        Assert.Empty(store.WrittenCommands);
        Assert.Equal(0, store.DeleteCount);
    }

    [Fact]
    public void RegistryWriteFailure_IsReturnedWithoutClaimingMutation()
    {
        var expected = new UnauthorizedAccessException("synthetic write failure");
        var store = new RecordingStartupStore { WriteFailure = expected };

        StartupSynchronizationResult result =
            StartupService.Synchronize(true, InstalledTarget, store);

        Assert.Equal(
            StartupSynchronizationStatus.RegistryAccessFailed,
            result.Status);
        Assert.Same(expected, result.Failure);
        Assert.False(result.RegistryChanged);
        Assert.Equal(1, store.WriteCount);
    }

    [Fact]
    public void RegistryDeleteFailure_IsReturnedWithoutClaimingMutation()
    {
        var expected = new UnauthorizedAccessException("synthetic delete failure");
        var store = new RecordingStartupStore
        {
            Value =
                "\"C:\\Users\\Test\\AppData\\Local\\WinShot\\WinShot.exe\" --tray",
            DeleteFailure = expected,
        };

        StartupSynchronizationResult result =
            StartupService.Synchronize(false, InstalledTarget, store);

        Assert.Equal(
            StartupSynchronizationStatus.RegistryAccessFailed,
            result.Status);
        Assert.Same(expected, result.Failure);
        Assert.False(result.RegistryChanged);
        Assert.Equal(1, store.DeleteCount);
    }

    [Fact]
    public void Synchronize_RejectsUnknownPathKindAndMissingStore()
    {
        var invalidTarget = new StartupLaunchTarget(
            (ApplicationPathKind)99,
            @"C:\WinShot.exe");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => StartupService.Synchronize(
                true,
                invalidTarget,
                new RecordingStartupStore()));
        Assert.Throws<ArgumentNullException>(
            () => StartupService.Synchronize(
                true,
                InstalledTarget,
                null!));
    }

    private static string CreateExecutablePathForCommandLength(
        int commandLength)
    {
        const string prefix = @"C:\";
        const string suffix = ".exe";
        const int launchSyntaxLength = 9; // opening/closing quotes plus " --tray"
        int stemLength =
            commandLength - prefix.Length - suffix.Length - launchSyntaxLength;
        Assert.True(stemLength > 0);
        return prefix + new string('a', stemLength) + suffix;
    }

    private sealed class RecordingStartupStore : IStartupRegistrationStore
    {
        public object? Value { get; set; }
        public Exception? ReadFailure { get; set; }
        public Exception? WriteFailure { get; set; }
        public Exception? DeleteFailure { get; set; }
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }
        public int DeleteCount { get; private set; }
        public List<string> WrittenCommands { get; } = [];

        public object? ReadValue()
        {
            ReadCount++;
            if (ReadFailure is not null)
                throw ReadFailure;
            return Value;
        }

        public void WriteValue(string launchCommand)
        {
            WriteCount++;
            if (WriteFailure is not null)
                throw WriteFailure;
            WrittenCommands.Add(launchCommand);
            Value = launchCommand;
        }

        public void DeleteValue()
        {
            DeleteCount++;
            if (DeleteFailure is not null)
                throw DeleteFailure;
            Value = null;
        }
    }
}
