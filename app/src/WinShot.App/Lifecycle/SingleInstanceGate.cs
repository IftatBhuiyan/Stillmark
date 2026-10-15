namespace WinShot.App.Lifecycle;

/// <summary>
/// Owns both generations of the resident-process identity during the brand transition. Acquiring
/// the legacy mutex first prevents an older WinShot resident and WinShot from becoming
/// primary at the same time. Both handoff events remain signalable in either direction.
/// </summary>
internal sealed class SingleInstanceGate : IDisposable
{
    private readonly EventWaitHandle _currentSignal;
    private readonly EventWaitHandle _legacySignal;
    private readonly Mutex? _currentMutex;
    private readonly Mutex? _legacyMutex;
    private bool _disposed;

    internal SingleInstanceGate(
        string currentMutexName,
        string legacyMutexName,
        string currentSignalName,
        string legacySignalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentMutexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyMutexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSignalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacySignalName);
        if (string.Equals(currentMutexName, legacyMutexName, StringComparison.Ordinal)
            || string.Equals(currentSignalName, legacySignalName, StringComparison.Ordinal))
        {
            throw new ArgumentException("Current and legacy single-instance names must differ.");
        }

        EventWaitHandle? currentSignal = null;
        EventWaitHandle? legacySignal = null;
        Mutex? currentMutex = null;
        Mutex? legacyMutex = null;
        try
        {
            currentSignal = new EventWaitHandle(false, EventResetMode.AutoReset, currentSignalName);
            legacySignal = new EventWaitHandle(false, EventResetMode.AutoReset, legacySignalName);
            legacyMutex = new Mutex(true, legacyMutexName, out bool legacyCreated);
            bool isPrimary = false;
            if (legacyCreated)
            {
                currentMutex = new Mutex(true, currentMutexName, out bool currentCreated);
                isPrimary = currentCreated;
            }

            if (!isPrimary)
            {
                currentMutex?.Dispose();
                legacyMutex.Dispose();
                currentMutex = null;
                legacyMutex = null;
            }

            _currentSignal = currentSignal;
            _legacySignal = legacySignal;
            _currentMutex = currentMutex;
            _legacyMutex = legacyMutex;
            IsPrimary = isPrimary;
        }
        catch
        {
            currentMutex?.Dispose();
            legacyMutex?.Dispose();
            currentSignal?.Dispose();
            legacySignal?.Dispose();
            throw;
        }
    }

    internal bool IsPrimary { get; }

    internal WaitHandle[] HandoffSignals => [_currentSignal, _legacySignal];

    internal void SignalExistingInstance()
    {
        ThrowIfDisposed();
        _currentSignal.Set();
        _legacySignal.Set();
    }

    internal void WakeListener() => SignalExistingInstance();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _currentSignal.Dispose();
        _legacySignal.Dispose();
        if (IsPrimary)
        {
            _currentMutex?.Dispose();
            _legacyMutex?.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
