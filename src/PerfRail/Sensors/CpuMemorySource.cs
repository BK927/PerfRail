using PerfRail.Interop;

namespace PerfRail.Sensors;

/// <summary>
/// The metrics PerfRail can always read: total CPU utilisation and physical memory.
/// </summary>
/// <remarks>
/// Two syscalls per sample, no allocation in the steady state, no performance-counter
/// infrastructure. <c>System.Diagnostics.PerformanceCounter</c> is deliberately avoided:
/// it drags in the whole PDH stack, allocates heavily, returns 0 from the first
/// <c>NextValue()</c>, and is the usual reason a "lightweight" monitor idles at 2-3%.
/// </remarks>
internal sealed class CpuMemorySource : ISensorSource
{
    private long _prevIdle;
    private long _prevKernel;
    private long _prevUser;
    private bool _hasPrevious;

    public string Name => "cpu+memory";

    public bool IsAvailable => true;

    public void Contribute(ref HardwareSnapshot snapshot)
    {
        snapshot = snapshot with
        {
            CpuUsage = SampleCpu(),
        };

        if (Kernel32.TryGetMemoryStatus(out MEMORYSTATUSEX memory))
        {
            snapshot = snapshot with
            {
                // "In use" as Task Manager reports it. Deliberately physical memory, not
                // commit charge: those diverge badly on machines with a large page file.
                MemoryUsedBytes = memory.ullTotalPhys - memory.ullAvailPhys,
                MemoryTotalBytes = memory.ullTotalPhys,
            };
        }
    }

    /// <summary>
    /// CPU utilisation across the interval since the previous call.
    /// </summary>
    /// <returns>
    /// 0-100, or null on the very first call: utilisation is a rate, and there is no
    /// interval to measure yet. The rail shows a placeholder rather than a fake 0%.
    /// </returns>
    private double? SampleCpu()
    {
        if (!Kernel32.GetSystemTimes(out long idle, out long kernel, out long user))
        {
            return null;
        }

        if (!_hasPrevious)
        {
            (_prevIdle, _prevKernel, _prevUser, _hasPrevious) = (idle, kernel, user, true);
            return null;
        }

        long deltaIdle = idle - _prevIdle;
        long deltaKernel = kernel - _prevKernel;
        long deltaUser = user - _prevUser;

        (_prevIdle, _prevKernel, _prevUser) = (idle, kernel, user);

        // Kernel time already includes idle time, so the denominator is kernel + user
        // and busy time is that total minus idle.
        long total = deltaKernel + deltaUser;
        if (total <= 0)
        {
            // Sampled faster than the clock ticks, or the counters moved backwards.
            // Report nothing rather than a spike.
            return null;
        }

        long busy = total - deltaIdle;
        return Math.Clamp(busy * 100.0 / total, 0.0, 100.0);
    }

    public void Dispose()
    {
    }
}
