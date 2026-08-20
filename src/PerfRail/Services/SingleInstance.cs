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

    private Mutex? _mutex;

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

    public void Dispose()
    {
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
