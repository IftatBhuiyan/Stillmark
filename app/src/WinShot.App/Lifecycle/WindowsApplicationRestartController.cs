using System.Runtime.InteropServices;
using WinShot.Core.Update;

namespace WinShot.App.Lifecycle;

internal interface IWindowsApplicationRestartNativeBoundary
{
    int Register(string commandLine, uint flags);

    int Unregister();
}

internal sealed class WindowsApplicationRestartNativeBoundary
    : IWindowsApplicationRestartNativeBoundary
{
    public int Register(string commandLine, uint flags) =>
        WinShot.Interop.NativeMethods.RegisterApplicationRestart(
            commandLine,
            flags);

    public int Unregister() =>
        WinShot.Interop.NativeMethods.UnregisterApplicationRestart();
}

/// <summary>
/// Owns the process-wide Windows Application Restart registration. Native calls and cached state
/// are serialized so normal lifecycle work and an asynchronous update handoff cannot race.
/// </summary>
internal sealed class WindowsApplicationRestartController
    : IApplicationRestartController
{
    private const string RestartCommandLine = "--tray --recovered";

    private readonly object _gate = new();
    private readonly IWindowsApplicationRestartNativeBoundary _native;
    private readonly uint _restartFlags;
    private readonly Action<Exception> _logFailure;
    private bool _isRegistered;

    internal WindowsApplicationRestartController(Action<Exception> logFailure)
        : this(
            new WindowsApplicationRestartNativeBoundary(),
            WindowsApplicationDeploymentDetector.Detect(),
            logFailure)
    {
    }

    internal WindowsApplicationRestartController(
        IWindowsApplicationRestartNativeBoundary native,
        Action<Exception> logFailure)
        : this(
            native,
            ApplicationDeploymentKind.Unknown,
            logFailure)
    {
    }

    internal WindowsApplicationRestartController(
        IWindowsApplicationRestartNativeBoundary native,
        ApplicationDeploymentKind deploymentKind,
        Action<Exception> logFailure)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _restartFlags = WindowsApplicationRestartPolicy.GetFlags(deploymentKind);
        _logFailure = logFailure
            ?? throw new ArgumentNullException(nameof(logFailure));
    }

    public bool IsRegistered
    {
        get
        {
            lock (_gate)
                return _isRegistered;
        }
    }

    public bool TryRegister()
    {
        Exception? failure = null;
        lock (_gate)
        {
            if (_isRegistered)
                return true;

            try
            {
                int result = _native.Register(
                    RestartCommandLine,
                    _restartFlags);
                if (result < 0)
                {
                    failure = CreateHResultFailure(
                        result,
                        "Windows Application Restart registration failed.");
                }
                else
                {
                    _isRegistered = true;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        if (failure is not null)
            LogFailure(failure);
        return failure is null;
    }

    public bool TryUnregister()
    {
        Exception? failure = null;
        lock (_gate)
        {
            if (!_isRegistered)
                return true;

            try
            {
                int result = _native.Unregister();
                if (result < 0)
                {
                    failure = CreateHResultFailure(
                        result,
                        "Windows Application Restart unregistration failed.");
                }
                else
                {
                    _isRegistered = false;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        if (failure is not null)
            LogFailure(failure);
        return failure is null;
    }

    private static Exception CreateHResultFailure(
        int hResult,
        string fallbackMessage) =>
        Marshal.GetExceptionForHR(hResult, new IntPtr(-1))
        ?? new COMException(fallbackMessage, hResult);

    private void LogFailure(Exception failure)
    {
        try
        {
            _logFailure(failure);
        }
        catch
        {
            // A diagnostic sink must not change recovery registration state or escape this
            // best-effort process-lifecycle boundary.
        }
    }
}
