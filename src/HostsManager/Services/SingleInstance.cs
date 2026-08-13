using System.Threading;

namespace HostsManager.Services;

/// <summary>
/// Keeps one instance of the app running at a time.
/// <para>
/// This matters beyond tidiness: two instances would each hold their own copy of the
/// hosts file and their own drift hash, so whichever saved second would be blocked, or
/// worse, be working from a stale view of a file the other one had already rewritten.
/// </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\HostsManager.Instance";
    private const string ActivateEventName = @"Local\HostsManager.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateSignal;
    private readonly CancellationTokenSource _cancellation = new();
    private Thread? _listener;

    private SingleInstance(Mutex mutex, EventWaitHandle activateSignal)
    {
        _mutex = mutex;
        _activateSignal = activateSignal;
    }

    /// <summary>Raised when another launch asks this instance to come to the front.</summary>
    public event Action? ActivationRequested;

    /// <summary>
    /// Returns true and the owning handle if this is the first instance. Returns false
    /// if another instance is already running, having first asked it to surface.
    /// </summary>
    public static bool TryAcquire(out SingleInstance? instance, bool privateInstance = false)
    {
        instance = null;

        // A private rehearsal edits a workspace fixture and deliberately runs beside the
        // installed app. Its separate names prevent either copy from activating or
        // suppressing the other while preserving one-instance protection within each mode.
        var suffix = privateInstance ? ".Private" : "";
        var mutex = new Mutex(initiallyOwned: true, MutexName + suffix, out var isFirst);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName + suffix);

        if (!isFirst)
        {
            mutex.Dispose();
            signal.Set();      // ask the running instance to show itself
            signal.Dispose();
            return false;
        }

        instance = new SingleInstance(mutex, signal);
        instance.StartListening();
        return true;
    }

    private void StartListening()
    {
        _listener = new Thread(() =>
        {
            var handles = new WaitHandle[] { _activateSignal, _cancellation.Token.WaitHandle };
            while (!_cancellation.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(handles) == 0) ActivationRequested?.Invoke();
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstanceListener",
        };

        _listener.Start();
    }

    public void Dispose()
    {
        _cancellation.Cancel();

        // Wait for the listener to leave its wait loop before the handles go away.
        // Disposing them underneath a thread that is about to call WaitAny would throw
        // ObjectDisposedException on a background thread, which takes the process down
        // with a fault instead of a clean exit.
        _listener?.Join(TimeSpan.FromSeconds(2));
        _listener = null;

        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }

        _mutex.Dispose();
        _activateSignal.Dispose();
        _cancellation.Dispose();
    }
}
