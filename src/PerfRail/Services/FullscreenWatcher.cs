using System.Runtime.InteropServices;
using PerfRail.Interop;

namespace PerfRail.Services;

/// <summary>
/// Notices when a window takes over an entire monitor, so the rail can get out of the way.
/// </summary>
/// <remarks>
/// <para>
/// ABN_FULLSCREENAPP only fires for exclusive-fullscreen applications. Modern games
/// overwhelmingly run borderless windowed fullscreen, which is just an ordinary top-level
/// window sized to the monitor: the shell sends no notification, ever. A topmost rail
/// would sit on top of the game with no event to tell it to move.
/// </para>
/// <para>
/// This is event-driven rather than polled. SetWinEventHook delivers foreground and
/// window-move events to the UI thread's message loop, so there is no timer and no cost
/// while nothing is happening - which is the objection to the usual "check the foreground
/// window every second" approach.
/// </para>
/// </remarks>
internal sealed partial class FullscreenWatcher : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;

    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;

    /// <summary>Kept in a field so the GC cannot collect the delegate the OS holds.</summary>
    private readonly WinEventProc _callback;

    private readonly List<nint> _hooks = [];

    private nint _locationHook;
    private bool _isFullscreen;
    private bool _disposed;

    public FullscreenWatcher()
    {
        _callback = OnWinEvent;

        // Foreground changes catch alt-tabbing into a game; minimize-end catches
        // restoring one. Both are rare, so these hooks are system-wide.
        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, 0);
        Hook(EVENT_SYSTEM_MINIMIZEEND, EVENT_SYSTEM_MINIMIZEEND, 0, 0);

        RebindLocationHook();
    }

    /// <summary>Raised only when the answer changes, never on every event.</summary>
    public event Action<bool>? FullscreenChanged;

    public bool IsFullscreen => _isFullscreen;

    /// <summary>Re-evaluates immediately, for example right after the rail is docked.</summary>
    public void Refresh()
    {
        RebindLocationHook();
        Evaluate();
    }

    private void Hook(uint min, uint max, uint process, uint thread)
    {
        nint hook = SetWinEventHook(
            min, max, 0, _callback, process, thread, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        if (hook != 0)
        {
            _hooks.Add(hook);
        }
    }

    /// <summary>
    /// Re-scopes the move/resize hook to whichever window currently has focus.
    /// </summary>
    /// <remarks>
    /// A window that already has focus can switch itself to fullscreen, which a
    /// foreground hook alone never sees. Watching for that needs
    /// EVENT_OBJECT_LOCATIONCHANGE - but system-wide, that event fires for every caret
    /// blink, menu highlight and child object on the desktop, and each one is a
    /// cross-process callback. Measured here, a global hook cost about 0.3% CPU on its
    /// own, over half the app's entire budget, purely to discard events.
    ///
    /// Scoping the hook to the foreground window's thread keeps the one case that
    /// matters and drops the rest at the source rather than in our callback.
    /// </remarks>
    private void RebindLocationHook()
    {
        if (_locationHook != 0)
        {
            UnhookWinEvent(_locationHook);
            _locationHook = 0;
        }

        nint foreground = GetForegroundWindow();
        if (foreground == 0)
        {
            return;
        }

        uint thread = GetWindowThreadProcessId(foreground, out uint process);
        if (thread == 0)
        {
            return;
        }

        _locationHook = SetWinEventHook(
            EVENT_OBJECT_LOCATIONCHANGE,
            EVENT_OBJECT_LOCATIONCHANGE,
            0,
            _callback,
            process,
            thread,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    private void OnWinEvent(
        nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // LOCATIONCHANGE is chatty: it fires for carets, menu items and every child
        // object. Only whole-window moves of the window that currently has focus can
        // change the answer.
        if (eventType == EVENT_OBJECT_LOCATIONCHANGE)
        {
            if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF || hwnd != GetForegroundWindow())
            {
                return;
            }
        }

        if (eventType is EVENT_SYSTEM_FOREGROUND or EVENT_SYSTEM_MINIMIZEEND)
        {
            RebindLocationHook();
        }

        Evaluate();
    }

    private void Evaluate()
    {
        bool fullscreen = IsForegroundWindowFullscreen();

        if (fullscreen == _isFullscreen)
        {
            return;
        }

        _isFullscreen = fullscreen;
        FullscreenChanged?.Invoke(fullscreen);
    }

    /// <summary>
    /// True when the foreground window covers its entire monitor.
    /// </summary>
    /// <remarks>
    /// The desktop and the shell are excluded explicitly: both legitimately span the whole
    /// screen at all times, and treating them as fullscreen would sink the rail whenever
    /// the user clicked the desktop.
    /// </remarks>
    private static bool IsForegroundWindowFullscreen()
    {
        nint foreground = GetForegroundWindow();
        if (foreground == 0 || IsShellWindow(foreground))
        {
            return false;
        }

        if (!GetWindowRect(foreground, out RECT window))
        {
            return false;
        }

        if (!User32.TryGetMonitorRect(foreground, out RECT monitor))
        {
            return false;
        }

        // "Covers" rather than "equals": a borderless-fullscreen window's rect can extend
        // slightly beyond the monitor.
        return window.Left <= monitor.Left
            && window.Top <= monitor.Top
            && window.Right >= monitor.Right
            && window.Bottom >= monitor.Bottom;
    }

    private static bool IsShellWindow(nint hwnd)
    {
        if (hwnd == GetShellWindow() || hwnd == GetDesktopWindow())
        {
            return true;
        }

        Span<char> buffer = stackalloc char[64];
        int length;

        unsafe
        {
            fixed (char* raw = buffer)
            {
                length = GetClassName(hwnd, raw, buffer.Length);
            }
        }

        if (length <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> className = buffer[..length];
        return className.SequenceEqual("Progman")
            || className.SequenceEqual("WorkerW")
            || className.SequenceEqual("Shell_TrayWnd")
            || className.SequenceEqual("Shell_SecondaryTrayWnd");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_locationHook != 0)
        {
            UnhookWinEvent(_locationHook);
            _locationHook = 0;
        }

        foreach (nint hook in _hooks)
        {
            UnhookWinEvent(hook);
        }

        _hooks.Clear();
    }

    private delegate void WinEventProc(
        nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        WinEventProc pfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint hWinEventHook);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetDesktopWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassName(nint hWnd, char* lpClassName, int nMaxCount);
}
