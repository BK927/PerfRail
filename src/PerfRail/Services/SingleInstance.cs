namespace PerfRail.Services;

/// <summary>
/// Ensures only one PerfRail runs per interactive session.
/// </summary>
/// <remarks>
/// The mutex is deliberately in the <c>Local\</c> namespace, not <c>Global\</c>.
/// Creating a global object requires SeCreateGlobalPrivilege, which a standard user does
/// not have, so a <c>Global\</c> mutex would throw exactly on the machines PerfRail is
/// built for. Per-session is also the correct scope: the rail is a per-desktop UI element,
/// and two users logged on at once should each get their own.
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\PerfRail.SingleInstance";

    /// <summary>Signalled by another process running <c>--quit</c>.</summary>
    /// <remarks>
    /// Gives uninstallers, update scripts and the verification suite a way to end
    /// PerfRail through the ordinary shutdown path. That path is the one that calls
    /// ABM_REMOVE, and a killed process leaves the desktop permanently short by the
    /// height of the bar until Explorer restarts - so being able to exercise it
    /// automatically matters more here than in most apps.
    /// </remarks>
    private const string QuitEventName = @"Local\PerfRail.Quit";

    private Mutex? _mutex;
    private EventWaitHandle? _quitEvent;
    private RegisteredWaitHandle? _quitRegistration;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// Attempts to claim the single-instance slot.
    /// </summary>
    /// <returns>
    /// The holder when this process is the first instance, or <c>null</c> when another
    /// instance already owns it. A second launch should exit silently: the user almost
    /// certainly just double-clicked twice, and an error dialog would be noise.
    /// </returns>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);

        if (createdNew)
        {
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// Asks a running instance to shut down. Returns false when none is running.
    /// </summary>
    public static bool RequestQuit()
    {
        if (!EventWaitHandle.TryOpenExisting(QuitEventName, out EventWaitHandle? handle))
        {
            return false;
        }

        using (handle)
        {
            handle.Set();
        }

        return true;
    }

    /// <summary>
    /// Invokes <paramref name="onQuit"/> when another process requests shutdown.
    /// </summary>
    /// <remarks>
    /// The callback arrives on a thread-pool thread, so it is marshalled onto the UI
    /// thread here: the shutdown path touches the AppBar, and every SHAppBarMessage call
    /// has to come from the thread that owns the window.
    /// </remarks>
    public void ListenForQuit(Action onQuit)
    {
        SynchronizationContext ui = SynchronizationContext.Current
            ?? throw new InvalidOperationException("ListenForQuit must be called from the UI thread.");

        _quitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEventName);
        _quitRegistration = ThreadPool.RegisterWaitForSingleObject(
            _quitEvent,
            (_, _) => ui.Post(_ => onQuit(), null),
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);
    }

    public void Dispose()
    {
        _quitRegistration?.Unregister(null);
        _quitRegistration = null;
        _quitEvent?.Dispose();
        _quitEvent = null;

        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not the owning thread. Nothing to release; disposing still frees the handle.
        }

        _mutex.Dispose();
        _mutex = null;
    }
}
