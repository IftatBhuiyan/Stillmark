using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WinShot.App.Lifecycle;
using WinShot.Interop;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class WindowsApplicationRestartControllerTests
{
    private const int EFail = unchecked((int)0x80004005);

    [Fact]
    public void Register_UsesExactCommandAndFlags_AndIsIdempotent()
    {
        var native = new ScriptedNativeBoundary();
        native.RegisterResults.Enqueue(0);
        var failures = new List<Exception>();
        var controller = new WindowsApplicationRestartController(
            native,
            failures.Add);

        Assert.True(controller.TryRegister());
        Assert.True(controller.TryRegister());

        Assert.True(controller.IsRegistered);
        Assert.Empty(failures);
        Assert.Single(native.RegisterCalls);
        RegisterCall call = native.RegisterCalls.Single();
        Assert.Equal("--tray --recovered", call.CommandLine);
        Assert.Equal(
            NativeMethods.RESTART_NO_PATCH | NativeMethods.RESTART_NO_REBOOT,
            call.Flags);
    }

    [Fact]
    public void Register_PackagedDeploymentAllowsPatchRestartButNotRebootRestart()
    {
        var native = new ScriptedNativeBoundary();
        native.RegisterResults.Enqueue(0);
        var controller = new WindowsApplicationRestartController(
            native,
            ApplicationDeploymentKind.Packaged,
            _ => { });

        Assert.True(controller.TryRegister());

        RegisterCall call = Assert.Single(native.RegisterCalls);
        Assert.Equal("--tray --recovered", call.CommandLine);
        Assert.Equal(NativeMethods.RESTART_NO_REBOOT, call.Flags);
        Assert.Equal(0u, call.Flags & NativeMethods.RESTART_NO_PATCH);
    }

    [Theory]
    [InlineData((int)ApplicationDeploymentKind.Unpackaged)]
    [InlineData((int)ApplicationDeploymentKind.Unknown)]
    public void Register_NonPackagedDeploymentKeepsConservativeFlags(
        int deploymentKindValue)
    {
        var native = new ScriptedNativeBoundary();
        native.RegisterResults.Enqueue(0);
        var controller = new WindowsApplicationRestartController(
            native,
            (ApplicationDeploymentKind)deploymentKindValue,
            _ => { });

        Assert.True(controller.TryRegister());

        RegisterCall call = Assert.Single(native.RegisterCalls);
        Assert.Equal(
            NativeMethods.RESTART_NO_PATCH | NativeMethods.RESTART_NO_REBOOT,
            call.Flags);
    }

    [Fact]
    public void Register_FailingHResultLeavesUnregisteredAndCanRetry()
    {
        _ = Marshal.GetHRForException(
            new InvalidOperationException("synthetic ambient COM error information"));
        try
        {
            var native = new ScriptedNativeBoundary();
            native.RegisterResults.Enqueue(EFail);
            native.RegisterResults.Enqueue(0);
            var failures = new List<Exception>();
            var controller = new WindowsApplicationRestartController(
                native,
                failures.Add);

            Assert.False(controller.TryRegister());
            Assert.False(controller.IsRegistered);
            Exception failure = Assert.Single(failures);
            Assert.Equal(EFail, failure.HResult);

            Assert.True(controller.TryRegister());
            Assert.True(controller.IsRegistered);
            Assert.Equal(2, native.RegisterCalls.Count);
        }
        finally
        {
            _ = Marshal.GetExceptionForHR(EFail);
        }
    }

    [Fact]
    public void Register_NativeExceptionLeavesUnregisteredAndIsLogged()
    {
        var expected = new InvalidOperationException("synthetic native failure");
        var native = new ScriptedNativeBoundary();
        native.RegisterFailures.Enqueue(expected);
        var failures = new List<Exception>();
        var controller = new WindowsApplicationRestartController(
            native,
            failures.Add);

        Assert.False(controller.TryRegister());

        Assert.False(controller.IsRegistered);
        Assert.Same(expected, Assert.Single(failures));
    }

    [Fact]
    public void Register_NonNegativeHResultIsSuccess()
    {
        var native = new ScriptedNativeBoundary();
        native.RegisterResults.Enqueue(1);
        var controller = new WindowsApplicationRestartController(
            native,
            _ => { });

        Assert.True(controller.TryRegister());

        Assert.True(controller.IsRegistered);
    }

    [Fact]
    public void Unregister_IsIdempotentAfterSuccess()
    {
        var native = new ScriptedNativeBoundary();
        native.RegisterResults.Enqueue(0);
        native.UnregisterResults.Enqueue(0);
        var controller = new WindowsApplicationRestartController(
            native,
            _ => { });
        Assert.True(controller.TryRegister());

        Assert.True(controller.TryUnregister());
        Assert.True(controller.TryUnregister());

        Assert.False(controller.IsRegistered);
        Assert.Equal(1, native.UnregisterCallCount);
    }

    [Fact]
    public void Unregister_FailingHResultConservativelyRemainsRegisteredAndCanRetry()
    {
        var native = new ScriptedNativeBoundary();
        native.RegisterResults.Enqueue(0);
        native.UnregisterResults.Enqueue(EFail);
        native.UnregisterResults.Enqueue(0);
        var failures = new List<Exception>();
        var controller = new WindowsApplicationRestartController(
            native,
            failures.Add);
        Assert.True(controller.TryRegister());

        Assert.False(controller.TryUnregister());
        Assert.True(controller.IsRegistered);
        Exception failure = Assert.Single(failures);
        Assert.Equal(EFail, failure.HResult);

        Assert.True(controller.TryUnregister());
        Assert.False(controller.IsRegistered);
        Assert.Equal(2, native.UnregisterCallCount);
    }

    [Fact]
    public void Unregister_NativeExceptionConservativelyRemainsRegistered()
    {
        var expected = new InvalidOperationException("synthetic native failure");
        var native = new ScriptedNativeBoundary();
        native.RegisterResults.Enqueue(0);
        native.UnregisterFailures.Enqueue(expected);
        var failures = new List<Exception>();
        var controller = new WindowsApplicationRestartController(
            native,
            failures.Add);
        Assert.True(controller.TryRegister());

        Assert.False(controller.TryUnregister());

        Assert.True(controller.IsRegistered);
        Assert.Same(expected, Assert.Single(failures));
    }

    [Fact]
    public void ThrowingLoggerCannotEscapeOrChangeConservativeState()
    {
        var native = new ScriptedNativeBoundary();
        native.RegisterResults.Enqueue(EFail);
        var controller = new WindowsApplicationRestartController(
            native,
            _ => throw new InvalidOperationException("synthetic logger failure"));

        Assert.False(controller.TryRegister());

        Assert.False(controller.IsRegistered);
    }

    [Fact]
    public async Task ConcurrentSuccessfulRegistrationInvokesNativeExactlyOnce()
    {
        var native = new ScriptedNativeBoundary();
        native.RegisterResults.Enqueue(0);
        var controller = new WindowsApplicationRestartController(
            native,
            _ => { });
        const int callerCount = 32;
        using var start = new ManualResetEventSlim();

        Task<bool>[] attempts = Enumerable
            .Range(0, callerCount)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return controller.TryRegister();
            }))
            .ToArray();
        start.Set();
        bool[] results = await Task.WhenAll(attempts);

        Assert.All(results, Assert.True);
        Assert.True(controller.IsRegistered);
        Assert.Single(native.RegisterCalls);
    }

    [Fact]
    public void ConcurrentRegisterNativeCallsAreSerializedByOneLock()
    {
        using var firstNativeCallEntered = new ManualResetEventSlim();
        using var releaseFirstNativeCall = new ManualResetEventSlim();
        using var secondAttemptStarted = new ManualResetEventSlim();
        var native = new BlockingFailureNativeBoundary(
            firstNativeCallEntered,
            releaseFirstNativeCall);
        var controller = new WindowsApplicationRestartController(
            native,
            _ => { });

        var first = new Thread(() => controller.TryRegister());
        var second = new Thread(() =>
        {
            secondAttemptStarted.Set();
            controller.TryRegister();
        });
        first.Start();
        Assert.True(firstNativeCallEntered.Wait(TimeSpan.FromSeconds(5)));
        second.Start();
        Assert.True(secondAttemptStarted.Wait(TimeSpan.FromSeconds(5)));

        Assert.False(
            SpinWait.SpinUntil(
                () => Volatile.Read(ref native.RegisterCallCount) > 1,
                TimeSpan.FromMilliseconds(200)));

        releaseFirstNativeCall.Set();
        Assert.True(first.Join(TimeSpan.FromSeconds(5)));
        Assert.True(second.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, native.RegisterCallCount);
        Assert.Equal(1, native.MaximumConcurrentNativeCalls);
        Assert.False(native.ReleaseTimedOut);
    }

    [Fact]
    public void ConcurrentUnregisterNativeCallsAreSerializedAndRemainConservative()
    {
        using var firstNativeCallEntered = new ManualResetEventSlim();
        using var releaseFirstNativeCall = new ManualResetEventSlim();
        using var secondAttemptStarted = new ManualResetEventSlim();
        var native = new BlockingUnregisterFailureNativeBoundary(
            firstNativeCallEntered,
            releaseFirstNativeCall);
        var controller = new WindowsApplicationRestartController(
            native,
            _ => { });
        Assert.True(controller.TryRegister());

        var first = new Thread(() => controller.TryUnregister());
        var second = new Thread(() =>
        {
            secondAttemptStarted.Set();
            controller.TryUnregister();
        });
        first.Start();
        Assert.True(firstNativeCallEntered.Wait(TimeSpan.FromSeconds(5)));
        second.Start();
        Assert.True(secondAttemptStarted.Wait(TimeSpan.FromSeconds(5)));

        Assert.False(
            SpinWait.SpinUntil(
                () => Volatile.Read(ref native.UnregisterCallCount) > 1,
                TimeSpan.FromMilliseconds(200)));

        releaseFirstNativeCall.Set();
        Assert.True(first.Join(TimeSpan.FromSeconds(5)));
        Assert.True(second.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, native.UnregisterCallCount);
        Assert.Equal(1, native.MaximumConcurrentNativeCalls);
        Assert.False(native.ReleaseTimedOut);
        Assert.True(controller.IsRegistered);
    }

    [Fact]
    public void ConstructorRejectsMissingDependencies()
    {
        var native = new ScriptedNativeBoundary();

        Assert.Throws<ArgumentNullException>(
            () => new WindowsApplicationRestartController(
                null!,
                _ => { }));
        Assert.Throws<ArgumentNullException>(
            () => new WindowsApplicationRestartController(
                native,
                null!));
        Assert.Throws<ArgumentNullException>(
            () => new WindowsApplicationRestartController(
                null!,
                ApplicationDeploymentKind.Packaged,
                _ => { }));
        Assert.Throws<ArgumentNullException>(
            () => new WindowsApplicationRestartController(
                native,
                ApplicationDeploymentKind.Packaged,
                null!));
    }

    private sealed record RegisterCall(string CommandLine, uint Flags);

    private sealed class ScriptedNativeBoundary
        : IWindowsApplicationRestartNativeBoundary
    {
        public ConcurrentQueue<int> RegisterResults { get; } = new();
        public ConcurrentQueue<int> UnregisterResults { get; } = new();
        public ConcurrentQueue<Exception> RegisterFailures { get; } = new();
        public ConcurrentQueue<Exception> UnregisterFailures { get; } = new();
        public ConcurrentQueue<RegisterCall> RegisterCalls { get; } = new();
        public int UnregisterCallCount;

        public int Register(string commandLine, uint flags)
        {
            RegisterCalls.Enqueue(new RegisterCall(commandLine, flags));
            if (RegisterFailures.TryDequeue(out Exception? failure))
                throw failure;
            return RegisterResults.TryDequeue(out int result)
                ? result
                : 0;
        }

        public int Unregister()
        {
            Interlocked.Increment(ref UnregisterCallCount);
            if (UnregisterFailures.TryDequeue(out Exception? failure))
                throw failure;
            return UnregisterResults.TryDequeue(out int result)
                ? result
                : 0;
        }
    }

    private sealed class BlockingFailureNativeBoundary
        : IWindowsApplicationRestartNativeBoundary
    {
        private readonly ManualResetEventSlim _firstNativeCallEntered;
        private readonly ManualResetEventSlim _releaseFirstNativeCall;
        private int _activeNativeCalls;
        private int _maximumConcurrentNativeCalls;
        private int _releaseTimedOut;
        public int RegisterCallCount;

        internal BlockingFailureNativeBoundary(
            ManualResetEventSlim firstNativeCallEntered,
            ManualResetEventSlim releaseFirstNativeCall)
        {
            _firstNativeCallEntered = firstNativeCallEntered;
            _releaseFirstNativeCall = releaseFirstNativeCall;
        }

        public int MaximumConcurrentNativeCalls =>
            Volatile.Read(ref _maximumConcurrentNativeCalls);

        public bool ReleaseTimedOut =>
            Volatile.Read(ref _releaseTimedOut) != 0;

        public int Register(string commandLine, uint flags)
        {
            int active = Interlocked.Increment(ref _activeNativeCalls);
            UpdateMaximum(active);
            int call = Interlocked.Increment(ref RegisterCallCount);
            try
            {
                if (call == 1)
                {
                    _firstNativeCallEntered.Set();
                    if (!_releaseFirstNativeCall.Wait(TimeSpan.FromSeconds(5)))
                        Volatile.Write(ref _releaseTimedOut, 1);
                }
                return EFail;
            }
            finally
            {
                Interlocked.Decrement(ref _activeNativeCalls);
            }
        }

        public int Unregister() => 0;

        private void UpdateMaximum(int active)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref _maximumConcurrentNativeCalls);
                if (active <= observed)
                    return;
            }
            while (Interlocked.CompareExchange(
                ref _maximumConcurrentNativeCalls,
                active,
                observed) != observed);
        }
    }

    private sealed class BlockingUnregisterFailureNativeBoundary
        : IWindowsApplicationRestartNativeBoundary
    {
        private readonly ManualResetEventSlim _firstNativeCallEntered;
        private readonly ManualResetEventSlim _releaseFirstNativeCall;
        private int _activeNativeCalls;
        private int _maximumConcurrentNativeCalls;
        private int _releaseTimedOut;
        public int UnregisterCallCount;

        internal BlockingUnregisterFailureNativeBoundary(
            ManualResetEventSlim firstNativeCallEntered,
            ManualResetEventSlim releaseFirstNativeCall)
        {
            _firstNativeCallEntered = firstNativeCallEntered;
            _releaseFirstNativeCall = releaseFirstNativeCall;
        }

        public int MaximumConcurrentNativeCalls =>
            Volatile.Read(ref _maximumConcurrentNativeCalls);

        public bool ReleaseTimedOut =>
            Volatile.Read(ref _releaseTimedOut) != 0;

        public int Register(string commandLine, uint flags) => 0;

        public int Unregister()
        {
            int active = Interlocked.Increment(ref _activeNativeCalls);
            UpdateMaximum(active);
            int call = Interlocked.Increment(ref UnregisterCallCount);
            try
            {
                if (call == 1)
                {
                    _firstNativeCallEntered.Set();
                    if (!_releaseFirstNativeCall.Wait(TimeSpan.FromSeconds(5)))
                        Volatile.Write(ref _releaseTimedOut, 1);
                }
                return EFail;
            }
            finally
            {
                Interlocked.Decrement(ref _activeNativeCalls);
            }
        }

        private void UpdateMaximum(int active)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref _maximumConcurrentNativeCalls);
                if (active <= observed)
                    return;
            }
            while (Interlocked.CompareExchange(
                ref _maximumConcurrentNativeCalls,
                active,
                observed) != observed);
        }
    }
}
