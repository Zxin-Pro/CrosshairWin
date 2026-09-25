using System.Threading;

namespace CrosshairWin.Services;

/// <summary>
/// Enforces a single running instance using a named mutex, and lets a second
/// launch signal the first one to show itself.
/// </summary>
internal sealed class SingleInstanceService : IDisposable
{
    // "Local\" keeps the mutex scoped to the current session, so two different
    // Windows users can each run their own copy without fighting.
    private const string MutexName = @"Local\CrosshairWin.SingleInstance.v1";
    private const string EventName = @"Local\CrosshairWin.Activate.v1";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _registeredWait;
    private bool _ownsMutex;

    /// <summary>Raised on a thread-pool thread when another instance asks us to show.</summary>
    public event Action? ActivationRequested;

    /// <summary>
    /// Attempts to become the primary instance.
    /// Returns true when this process owns the mutex; false when another instance exists.
    /// </summary>
    public bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: false, MutexName);

            // Wait with a zero timeout: we must not block startup.
            _ownsMutex = _mutex.WaitOne(0, exitContext: false);

            if (_ownsMutex)
            {
                _activateEvent = new EventWaitHandle(
                    false, EventResetMode.AutoReset, EventName);

                // Listen for activation signals from subsequently launched copies.
                _registeredWait = ThreadPool.RegisterWaitForSingleObject(
                    _activateEvent,
                    (_, _) => ActivationRequested?.Invoke(),
                    state: null,
                    millisecondsTimeOutInterval: Timeout.Infinite,
                    executeOnlyOnce: false);
            }

            return _ownsMutex;
        }
        catch (AbandonedMutexException)
        {
            // The previous instance crashed without releasing. We are now the owner.
            _ownsMutex = true;
            return true;
        }
        catch
        {
            // Any unexpected failure: degrade to "allow start" rather than blocking the app.
            _ownsMutex = false;
            return true;
        }
    }

    /// <summary>
    /// Called by a secondary instance to ask the running one to come to the foreground.
    /// </summary>
    public static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(EventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch
        {
            // If signalling fails the user simply sees nothing happen; not fatal.
        }
    }

    public void Dispose()
    {
        try
        {
            _registeredWait?.Unregister(null);
        }
        catch
        {
            // Best effort.
        }

        try
        {
            _activateEvent?.Dispose();
        }
        catch
        {
            // Best effort.
        }

        try
        {
            if (_ownsMutex)
                _mutex?.ReleaseMutex();

            _mutex?.Dispose();
        }
        catch
        {
            // Best effort.
        }

        _registeredWait = null;
        _activateEvent = null;
        _mutex = null;
    }
}
