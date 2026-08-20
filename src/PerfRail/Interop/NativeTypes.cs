using System.Runtime.InteropServices;

namespace PerfRail.Interop;

/// <summary>
/// Win32 RECT. Four ints, sequential, nothing else.
/// </summary>
/// <remarks>
/// Do not substitute <see cref="System.Drawing.Rectangle"/>. It is the same size and
/// also blittable, so the substitution compiles, runs, and marshals - but Rectangle is
/// (X, Y, Width, Height) while RECT is (left, top, right, bottom). Every AppBar
/// rectangle would be silently wrong in a way no exception ever reports.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct RECT : IEquatable<RECT>
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;

    public readonly int Height => Bottom - Top;

    public readonly bool Equals(RECT other) =>
        Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;

    public override readonly bool Equals(object? obj) => obj is RECT other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);

    public static bool operator ==(RECT a, RECT b) => a.Equals(b);

    public static bool operator !=(RECT a, RECT b) => !a.Equals(b);

    public override readonly string ToString() =>
        $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}

[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}

/// <summary>
/// Win32 WINDOWPOS, as delivered by WM_WINDOWPOSCHANGING.
/// </summary>
/// <remarks>
/// On x64 the layout is hwnd(8) hwndInsertAfter(8) x(4) y(4) cx(4) cy(4) flags(4),
/// so <c>flags</c> sits at byte offset 32. The widely copied snippet that treats
/// lParam as int* and ORs into index 6 is writing to <c>cx</c> and corrupts the
/// window width. Marshal the struct instead of indexing raw memory.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct WINDOWPOS
{
    public nint hwnd;
    public nint hwndInsertAfter;
    public int x;
    public int y;
    public int cx;
    public int cy;
    public uint flags;
}
