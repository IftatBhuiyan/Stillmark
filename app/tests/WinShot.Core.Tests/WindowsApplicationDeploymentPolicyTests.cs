using System.Text;
using WinShot.App.Lifecycle;
using WinShot.Interop;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class WindowsApplicationDeploymentPolicyTests
{
    [Fact]
    public void NoPackageWithZeroLength_IsUnpackaged()
    {
        var native = new ScriptedPackageIdentityBoundary(
            NativeMethods.APPMODEL_ERROR_NO_PACKAGE,
            outputLength: 0);

        ApplicationDeploymentKind result =
            WindowsApplicationDeploymentDetector.Detect(native);

        Assert.Equal(ApplicationDeploymentKind.Unpackaged, result);
        Assert.Equal(1, native.CallCount);
        Assert.Equal(0u, native.ObservedInputLength);
        Assert.True(native.ObservedNullBuffer);
    }

    [Fact]
    public void NoPackageWithUnexpectedLength_IsUnknown()
    {
        var native = new ScriptedPackageIdentityBoundary(
            NativeMethods.APPMODEL_ERROR_NO_PACKAGE,
            outputLength: 1);

        ApplicationDeploymentKind result =
            WindowsApplicationDeploymentDetector.Detect(native);

        Assert.Equal(ApplicationDeploymentKind.Unknown, result);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(64u)]
    [InlineData(
        WindowsApplicationDeploymentDetector.MaximumPackageFullNameBufferCharacters)]
    public void InsufficientBufferWithValidRequiredLength_IsPackaged(uint outputLength)
    {
        var native = new ScriptedPackageIdentityBoundary(
            NativeMethods.ERROR_INSUFFICIENT_BUFFER,
            outputLength);

        ApplicationDeploymentKind result =
            WindowsApplicationDeploymentDetector.Detect(native);

        Assert.Equal(ApplicationDeploymentKind.Packaged, result);
        Assert.Equal(1, native.CallCount);
        Assert.True(native.ObservedNullBuffer);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(
        WindowsApplicationDeploymentDetector.MaximumPackageFullNameBufferCharacters + 1)]
    [InlineData(uint.MaxValue)]
    public void InsufficientBufferWithAmbiguousLength_IsUnknown(uint outputLength)
    {
        var native = new ScriptedPackageIdentityBoundary(
            NativeMethods.ERROR_INSUFFICIENT_BUFFER,
            outputLength);

        ApplicationDeploymentKind result =
            WindowsApplicationDeploymentDetector.Detect(native);

        Assert.Equal(ApplicationDeploymentKind.Unknown, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(unchecked((int)0x80004005))]
    public void OtherNativeResults_AreUnknown(int nativeResult)
    {
        var native = new ScriptedPackageIdentityBoundary(
            nativeResult,
            outputLength: 0);

        ApplicationDeploymentKind result =
            WindowsApplicationDeploymentDetector.Detect(native);

        Assert.Equal(ApplicationDeploymentKind.Unknown, result);
    }

    [Fact]
    public void NativeException_IsUnknownAndDoesNotEscape()
    {
        var native = new ThrowingPackageIdentityBoundary(
            new InvalidOperationException("synthetic package probe failure"));

        ApplicationDeploymentKind result =
            WindowsApplicationDeploymentDetector.Detect(native);

        Assert.Equal(ApplicationDeploymentKind.Unknown, result);
    }

    [Fact]
    public void MissingNativeBoundary_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => WindowsApplicationDeploymentDetector.Detect(null!));
    }

    [Theory]
    [InlineData((int)ApplicationDeploymentKind.Unpackaged)]
    [InlineData((int)ApplicationDeploymentKind.Unknown)]
    public void NonPackagedDeployment_KeepsConservativeRestartFlags(
        int deploymentKindValue)
    {
        uint flags = WindowsApplicationRestartPolicy.GetFlags(
            (ApplicationDeploymentKind)deploymentKindValue);

        Assert.Equal(
            NativeMethods.RESTART_NO_PATCH | NativeMethods.RESTART_NO_REBOOT,
            flags);
    }

    [Fact]
    public void PackagedDeployment_RemovesOnlyNoPatchRestartFlag()
    {
        uint flags = WindowsApplicationRestartPolicy.GetFlags(
            ApplicationDeploymentKind.Packaged);

        Assert.Equal(NativeMethods.RESTART_NO_REBOOT, flags);
        Assert.Equal(0u, flags & NativeMethods.RESTART_NO_PATCH);
    }

    [Fact]
    public void InvalidDeploymentKind_KeepsConservativeRestartFlags()
    {
        uint flags = WindowsApplicationRestartPolicy.GetFlags(
            (ApplicationDeploymentKind)int.MaxValue);

        Assert.Equal(
            NativeMethods.RESTART_NO_PATCH | NativeMethods.RESTART_NO_REBOOT,
            flags);
    }

    private sealed class ScriptedPackageIdentityBoundary
        : ICurrentPackageFullNameNativeBoundary
    {
        private readonly int _result;
        private readonly uint _outputLength;

        internal ScriptedPackageIdentityBoundary(
            int result,
            uint outputLength)
        {
            _result = result;
            _outputLength = outputLength;
        }

        internal int CallCount { get; private set; }
        internal uint ObservedInputLength { get; private set; }
        internal bool ObservedNullBuffer { get; private set; }

        public int GetCurrentPackageFullName(
            ref uint packageFullNameLength,
            StringBuilder? packageFullName)
        {
            CallCount++;
            ObservedInputLength = packageFullNameLength;
            ObservedNullBuffer = packageFullName is null;
            packageFullNameLength = _outputLength;
            return _result;
        }
    }

    private sealed class ThrowingPackageIdentityBoundary
        : ICurrentPackageFullNameNativeBoundary
    {
        private readonly Exception _failure;

        internal ThrowingPackageIdentityBoundary(Exception failure)
        {
            _failure = failure;
        }

        public int GetCurrentPackageFullName(
            ref uint packageFullNameLength,
            StringBuilder? packageFullName) =>
            throw _failure;
    }
}
