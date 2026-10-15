using WinShot.Core.Startup;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class InstalledLaunchPolicyTests
{
    private static readonly InstalledApplicationLayout InstalledLayout = new(
        @"C:\Users\Test\AppData\Local\WinShot",
        @"C:\Users\Test\AppData\Local\WinShot\WinShot.exe");
    private const string CurrentPayload =
        @"C:\Users\Test\AppData\Local\WinShot\current\WinShot.exe";

    [Theory]
    [InlineData(CurrentPayload)]
    [InlineData(@"C:\Users\Test\AppData\Local\WinShot\WinShot.exe")]
    public void CurrentLayout_WithStableLauncherAndCurrentPayload_IsDiscovered(
        string processPath)
    {
        var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            InstalledLayout.StableLauncherPath,
            CurrentPayload,
        };

        InstalledApplicationLayout? discovered =
            InstalledLaunchPolicy.DiscoverInstalledLayout(
                processPath,
                @"C:\Users\Test\AppData\Local",
                existingFiles.Contains);

        Assert.Equal(InstalledLayout, discovered);
        Assert.Equal(
            InstalledLayout.StableLauncherPath,
            InstalledLaunchPolicy.Resolve(processPath, discovered).ExecutablePath);
    }

    [Theory]
    [InlineData(
        @"C:\Users\Test\AppData\Local\WinShot\current\WinShot.exe",
        @"C:\Users\Test\AppData\Local\WinShot\WinShot.exe")]
    [InlineData(
        @"C:\Users\Test\AppData\Local\WinShot\WinShot.exe",
        @"C:\Users\Test\AppData\Local\WinShot\current\WinShot.exe")]
    public void CurrentLayout_MissingEitherRequiredExecutable_IsNotDiscovered(
        string processPath,
        string onlyExistingFile)
    {
        InstalledApplicationLayout? discovered =
            InstalledLaunchPolicy.DiscoverInstalledLayout(
                processPath,
                @"C:\Users\Test\AppData\Local",
                path => path.Equals(onlyExistingFile, StringComparison.OrdinalIgnoreCase));

        Assert.Null(discovered);
    }

    [Fact]
    public void IncompleteInstalledShape_ResolvesAsUnmanagedInsteadOfPayloadTarget()
    {
        InstalledApplicationLayout? discovered =
            InstalledLaunchPolicy.DiscoverInstalledLayout(
                CurrentPayload,
                @"C:\Users\Test\AppData\Local",
                path => path.Equals(
                    CurrentPayload,
                    StringComparison.OrdinalIgnoreCase));

        StartupLaunchTarget target =
            InstalledLaunchPolicy.Resolve(CurrentPayload, discovered);

        Assert.Null(discovered);
        Assert.Equal(ApplicationPathKind.Portable, target.PathKind);
        Assert.False(target.IsInstallerOwned);
        Assert.Equal(CurrentPayload, target.ExecutablePath);
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("v2.0.0-beta_1+build.7")]
    public void VersionedContractLayout_WithSafeSingleVersionSegment_IsDiscovered(
        string version)
    {
        string payload = Path.Combine(
            InstalledLayout.InstallRootPath,
            "versions",
            version,
            "WinShot.exe");
        var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            InstalledLayout.StableLauncherPath,
            payload,
        };

        InstalledApplicationLayout? discovered =
            InstalledLaunchPolicy.DiscoverInstalledLayout(
                payload,
                @"C:\Users\Test\AppData\Local",
                existingFiles.Contains);

        Assert.Equal(InstalledLayout, discovered);
        Assert.Equal(
            InstalledLayout.StableLauncherPath,
            InstalledLaunchPolicy.Resolve(payload, discovered).ExecutablePath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VersionedContractLayout_MissingEitherRequiredExecutable_IsNotDiscovered(
        bool stableLauncherExists)
    {
        string payload = Path.Combine(
            InstalledLayout.InstallRootPath,
            "versions",
            "1.2.3",
            "WinShot.exe");
        string onlyExistingFile = stableLauncherExists
            ? InstalledLayout.StableLauncherPath
            : payload;

        InstalledApplicationLayout? discovered =
            InstalledLaunchPolicy.DiscoverInstalledLayout(
                payload,
                @"C:\Users\Test\AppData\Local",
                path => path.Equals(onlyExistingFile, StringComparison.OrdinalIgnoreCase));

        Assert.Null(discovered);
    }

    [Fact]
    public void VersionedContractLayout_DifferentStableExecutableName_IsNotDiscovered()
    {
        string payload = Path.Combine(
            InstalledLayout.InstallRootPath,
            "versions",
            "1.2.3",
            "WinShot.exe");
        var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            payload,
            Path.Combine(InstalledLayout.InstallRootPath, "Other.exe"),
        };

        InstalledApplicationLayout? discovered =
            InstalledLaunchPolicy.DiscoverInstalledLayout(
                payload,
                @"C:\Users\Test\AppData\Local",
                existingFiles.Contains);

        Assert.Null(discovered);
    }

    [Theory]
    [InlineData(".hidden")]
    [InlineData("bad version")]
    [InlineData("1.0.0!")]
    [InlineData("1.0.")]
    [InlineData("CON")]
    [InlineData("NUL.release")]
    public void VersionedContractLayout_UnsafeVersionSegment_RemainsUnmanaged(
        string version)
    {
        string payload = Path.Combine(
            InstalledLayout.InstallRootPath,
            "versions",
            version,
            "WinShot.exe");

        InstalledApplicationLayout? discovered =
            InstalledLaunchPolicy.DiscoverInstalledLayout(
                payload,
                @"C:\Users\Test\AppData\Local",
                _ => true);

        Assert.Null(discovered);
    }

    [Fact]
    public void VersionedContractLayout_OverlongVersionSegment_RemainsUnmanaged()
    {
        string payload = Path.Combine(
            InstalledLayout.InstallRootPath,
            "versions",
            new string('1', 129),
            "WinShot.exe");

        InstalledApplicationLayout? discovered =
            InstalledLaunchPolicy.DiscoverInstalledLayout(
                payload,
                @"C:\Users\Test\AppData\Local",
                _ => true);

        Assert.Null(discovered);
    }

    [Fact]
    public void VersionedContractLayout_NestedPayload_RemainsUnmanaged()
    {
        string payload = Path.Combine(
            InstalledLayout.InstallRootPath,
            "versions",
            "1.2.3",
            "nested",
            "WinShot.exe");

        InstalledApplicationLayout? discovered =
            InstalledLaunchPolicy.DiscoverInstalledLayout(
                payload,
                @"C:\Users\Test\AppData\Local",
                _ => true);

        Assert.Null(discovered);
    }

    [Theory]
    [InlineData(@"D:\Portable\WinShot\current\WinShot.exe")]
    [InlineData(@"C:\Users\Test\AppData\Local\Vendor\WinShot\current\WinShot.exe")]
    [InlineData(@"C:\Users\Test\AppData\Local\WinShot\current-old\WinShot.exe")]
    public void InstalledLayout_NonExactShape_RemainsUnmanaged(string processPath)
    {
        InstalledApplicationLayout? discovered =
            InstalledLaunchPolicy.DiscoverInstalledLayout(
                processPath,
                @"C:\Users\Test\AppData\Local",
                _ => true);

        Assert.Null(discovered);
    }

    [Fact]
    public void InstalledVersionedPayload_ResolvesStableRootLauncher()
    {
        StartupLaunchTarget target = InstalledLaunchPolicy.Resolve(
            @"C:\Users\Test\AppData\Local\WinShot\current\WinShot.exe",
            InstalledLayout);

        Assert.Equal(ApplicationPathKind.InstalledPayload, target.PathKind);
        Assert.True(target.IsInstallerOwned);
        Assert.Equal(InstalledLayout.StableLauncherPath, target.ExecutablePath);
        Assert.Equal(
            "\"C:\\Users\\Test\\AppData\\Local\\WinShot\\WinShot.exe\" --tray",
            InstalledLaunchPolicy.BuildLaunchCommand(
                @"C:\Users\Test\AppData\Local\WinShot\current\WinShot.exe",
                InstalledLayout));
    }

    [Fact]
    public void StableRootProcess_RemainsStableLauncher()
    {
        StartupLaunchTarget target = InstalledLaunchPolicy.Resolve(
            InstalledLayout.StableLauncherPath,
            InstalledLayout);

        Assert.Equal(ApplicationPathKind.InstalledStableLauncher, target.PathKind);
        Assert.True(target.IsInstallerOwned);
        Assert.Equal(InstalledLayout.StableLauncherPath, target.ExecutablePath);
    }

    [Theory]
    [InlineData(
        @"C:\src\WinShot\src\WinShot.App\bin\x64\Debug\net8.0-windows\WinShot.exe",
        ApplicationPathKind.Development)]
    [InlineData(
        @"C:\src\WinShot\src\WinShot.App\bin\x64\Debug\net10.0-windows\WinShot.exe",
        ApplicationPathKind.Development)]
    [InlineData(
        @"D:\Portable\WinShot\WinShot.exe",
        ApplicationPathKind.Portable)]
    public void PathOutsideInstalledRoot_IsNotInstallerOwned(
        string processPath,
        ApplicationPathKind expectedKind)
    {
        StartupLaunchTarget target = InstalledLaunchPolicy.Resolve(
            processPath,
            InstalledLayout);

        Assert.Equal(expectedKind, target.PathKind);
        Assert.False(target.IsInstallerOwned);
        Assert.Equal(processPath, target.ExecutablePath);
    }

    [Fact]
    public void LookalikeSibling_IsNotClassifiedAsInstalled()
    {
        StartupLaunchTarget target = InstalledLaunchPolicy.Resolve(
            @"C:\Users\Test\AppData\Local\WinShot-Old\current\WinShot.exe",
            InstalledLayout);

        Assert.Equal(ApplicationPathKind.Portable, target.PathKind);
        Assert.False(target.IsInstallerOwned);
    }

    [Fact]
    public void StableLauncherOutsideInstallRoot_IsRejected()
    {
        var invalidLayout = new InstalledApplicationLayout(
            @"C:\Users\Test\AppData\Local\WinShot",
            @"C:\Users\Test\AppData\Local\Other\WinShot.exe");

        Assert.Throws<ArgumentException>(
            () => InstalledLaunchPolicy.Resolve(
                @"C:\Users\Test\AppData\Local\WinShot\current\WinShot.exe",
                invalidLayout));
    }

    [Theory]
    [InlineData(@"WinShot.exe")]
    [InlineData(@"..\WinShot.exe")]
    [InlineData(@"C:\src\WinShot\README.md")]
    public void InvalidProcessPath_IsRejected(string processPath)
    {
        Assert.Throws<ArgumentException>(
            () => InstalledLaunchPolicy.Resolve(processPath, InstalledLayout));
    }
}
