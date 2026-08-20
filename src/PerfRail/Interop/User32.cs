using System.Runtime.InteropServices;

namespace PerfRail.Interop;

internal static partial class User32
{
    // ---- Window messages -------------------------------------------------
    public const int WM_QUERYENDSESSION = 0x0011;
    public const int WM_ENDSESSION = 0x0016;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_WINDOWPOSCHANGING = 0x0046;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_DPICHANGED = 0x02E0;

    /// <summary>
    /// Return value for WM_MOUSEACTIVATE: do not activate, but DO deliver the click.
    /// </summary>
    /// <remarks>
    /// MA_NOACTIVATEANDEAT (4) also suppresses activation but swallows the message,
    /// which means a right-click never reaches us and the context menu never opens.
    /// </remarks>
    public const int MA_NOACTIVATE = 3;

    // ---- Extended window styles ------------------------------------------
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    // ---- SetWindowPos ----------------------------------------------------
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    public static readonly nint HWND_TOP = 0;
    public static readonly nint HWND_BOTTOM = 1;
    public static readonly nint HWND_TOPMOST = -1;
    public static readonly nint HWND_NOTOPMOST = -2;

    // ---- Monitors --------------------------------------------------------
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(
        nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial uint RegisterWindowMessage(string lpString);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    /// <summary>
    /// DPI of the monitor the window is currently on. Valid only after the handle exists.
    /// </summary>
    /// <remarks>
    /// Not interchangeable with <c>Graphics.DpiX/DpiY</c>, which resolve to
    /// GetDeviceCaps(LOGPIXELSX) - documented as identical for every monitor, so it is
    /// right on the primary display and wrong on every other one.
    /// </remarks>
    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    /// <summary>
    /// Reads the current monitor rectangle in physical pixels.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>Screen.Bounds</c>. Screen caches rcMonitor in a readonly field
    /// and caches the whole AllScreens array statically. Worse, <c>Screen.WorkingArea</c>
    /// is self-referential once we are registered - our own reservation shrinks it, so
    /// repositioning from it walks the bar down the screen on every update.
    /// </remarks>
    public static bool TryGetMonitorRect(nint hwnd, out RECT monitorRect)
    {
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        nint monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != 0 && GetMonitorInfo(monitor, ref info))
        {
            monitorRect = info.rcMonitor;
            return true;
        }

        monitorRect = default;
        return false;
    }
}
