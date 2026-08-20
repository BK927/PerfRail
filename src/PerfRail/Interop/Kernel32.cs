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

/// <summary>Battery and mains state, as filled by GetSystemPowerStatus.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEM_POWER_STATUS
{
    /// <summary>0 offline, 1 online, 255 unknown.</summary>
    public byte ACLineStatus;

    /// <summary>Bit flags; 128 means the machine has no battery at all.</summary>
    public byte BatteryFlag;

    /// <summary>0-100, or 255 when Windows cannot tell.</summary>
    public byte BatteryLifePercent;

    public byte SystemStatusFlag;

    public uint BatteryLifeTime;

    public uint BatteryFullLifeTime;
}

internal static partial class Kernel32
{
    public const byte AcLineOnline = 1;

    /// <summary>BATTERY_FLAG_NO_BATTERY.</summary>
    public const byte BatteryFlagNoSystemBattery = 128;

    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemPowerStatus", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatusNative(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    /// <summary>
    /// Reads battery and mains state. No elevation, no dependency, one syscall.
    /// </summary>
    public static bool TryGetPowerStatus(out SYSTEM_POWER_STATUS status) =>
        GetSystemPowerStatusNative(out status);

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
