using System.Runtime.InteropServices;

namespace PerfRail.Interop;

/// <summary>
/// Physical and virtual memory counters, as filled by GlobalMemoryStatusEx.
/// </summary>
/// <remarks>
/// dwLength must be set before the call or the function fails with
/// ERROR_INVALID_PARAMETER. It is set inside <see cref="Kernel32.TryGetMemoryStatus"/>
/// so no caller can forget.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct MEMORYSTATUSEX
{
    public uint dwLength;

    /// <summary>Physical memory in use, 0-100. Physical load, not commit charge.</summary>
    public uint dwMemoryLoad;

    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;
}

internal static partial class Kernel32
{
    /// <summary>
    /// System-wide idle, kernel and user times as 100-nanosecond FILETIME values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// lpKernelTime ALREADY INCLUDES lpIdleTime. The correct utilisation over an
    /// interval is therefore:
    /// </para>
    /// <code>
    /// total = dKernel + dUser
    /// busy  = total - dIdle
    /// </code>
    /// <para>
    /// The widely repeated <c>(dKernel - dIdle + dUser) / (dKernel + dUser)</c> double
    /// counts idle and roughly doubles the reading on a quiet machine.
    /// </para>
    /// <para>
    /// Above 64 logical processors this reports only the calling thread's processor
    /// group. Acceptable for now; revisit with NtQuerySystemInformation if PerfRail ever
    /// needs to be correct on such machines.
    /// </para>
    /// </remarks>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusExNative(ref MEMORYSTATUSEX lpBuffer);

    public static bool TryGetMemoryStatus(out MEMORYSTATUSEX status)
    {
        status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusExNative(ref status);
    }
}
