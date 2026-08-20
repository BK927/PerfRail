using System.Runtime.InteropServices;
using PerfRail.Interop;

namespace PerfRail.AppBar;

/// <summary>
/// APPBARDATA, as passed to SHAppBarMessage.
/// </summary>
/// <remarks>
/// <para>
/// x64 layout is 48 bytes: cbSize@0, (4 bytes padding), hWnd@8, uCallbackMessage@16,
/// uEdge@20, rc@24, lParam@40.
/// </para>
/// <para>
/// Deliberately hand-written with no <c>Pack</c>. CsWin32 / win32metadata emit this
/// struct with <c>Pack = 1</c>, which is correct for x86 (36 bytes) and catastrophic on
/// x64: it packs to 44 bytes and puts hWnd at offset 4, so the shell reads garbage for
/// every field. The size assertion in <see cref="AppBarHost"/> exists to catch exactly
/// this if anyone ever swaps in a generated version.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct APPBARDATA
{
    public uint cbSize;
    public nint hWnd;
    public uint uCallbackMessage;
    public uint uEdge;
    public RECT rc;
    public nint lParam;
}

internal static partial class AppBarInterop
{
    /// <summary>Expected <c>sizeof(APPBARDATA)</c> on x64.</summary>
    public const int ExpectedAppBarDataSize = 48;

    // ---- ABM_* : messages we send to the shell ---------------------------
    public const uint ABM_NEW = 0x00000000;
    public const uint ABM_REMOVE = 0x00000001;
    public const uint ABM_QUERYPOS = 0x00000002;
    public const uint ABM_SETPOS = 0x00000003;
    public const uint ABM_GETSTATE = 0x00000004;
    public const uint ABM_WINDOWPOSCHANGED = 0x00000009;

    // ---- ABN_* : notifications the shell sends back ----------------------
    public const int ABN_STATECHANGE = 0x0000;
    public const int ABN_POSCHANGED = 0x0001;
    public const int ABN_FULLSCREENAPP = 0x0002;
    public const int ABN_WINDOWARRANGE = 0x0003;

    // ---- ABE_* : screen edges --------------------------------------------
    public const uint ABE_LEFT = 0;
    public const uint ABE_TOP = 1;
    public const uint ABE_RIGHT = 2;
    public const uint ABE_BOTTOM = 3;

    /// <summary>
    /// Sends an AppBar message to the shell.
    /// </summary>
    /// <remarks>
    /// This is a synchronous cross-process round trip: shell32 locates Shell_TrayWnd,
    /// packs the struct into shared memory and WM_COPYDATAs it across. Every call must
    /// therefore happen on the UI thread, and the UI thread must never be blocked
    /// elsewhere - if Explorer's SendMessage back to us times out we are treated as hung.
    /// </remarks>
    [LibraryImport("shell32.dll")]
    public static partial nuint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
}
