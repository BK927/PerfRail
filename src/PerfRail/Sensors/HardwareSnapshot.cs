namespace PerfRail.Sensors;

/// <summary>
/// One sample of everything PerfRail can currently read.
/// </summary>
/// <remarks>
/// <para>
/// Every optional metric is nullable, and null means "not available on this machine"
/// rather than zero. That distinction is the whole point: a monitoring bar that renders
/// a confident <c>0 °C</c> because a sensor returned nothing is worse than one that
/// shows nothing at all. The renderer drops null metrics from the layout entirely.
/// </para>
/// <para>
/// A readonly record struct so the 1 Hz sampler can publish a new snapshot without
/// allocating, and so a torn read is impossible - the whole value is replaced at once.
/// </para>
/// </remarks>
/// <param name="CpuUsage">Total CPU utilisation, 0-100, or null before the second sample.</param>
/// <param name="MemoryUsedBytes">Physical memory in use.</param>
/// <param name="MemoryTotalBytes">Total physical memory installed.</param>
/// <param name="GpuUsage">Busiest GPU engine, 0-100. Null when no WDDM adapter is visible.</param>
/// <param name="VramUsedBytes">Dedicated video memory in use.</param>
/// <param name="VramTotalBytes">Dedicated video memory available to the adapter.</param>
/// <param name="CpuTemperatureCelsius">
/// Always null today. CPU die temperature comes from model-specific registers that need
/// ring-0 access via a signed kernel driver, which needs administrator rights to install
/// and start. PerfRail runs as a standard user and installs no driver.
/// </param>
/// <param name="GpuTemperatureCelsius">
/// Null unless a vendor path is available. There is no OS-level API for this; NVIDIA
/// (NVML) and AMD (ADL) each need their own user-mode library.
/// </param>
/// <param name="BatteryPercent">Charge level 0-100, or null on a machine with no battery.</param>
/// <param name="BatteryCharging">True when running on mains power.</param>
internal readonly record struct HardwareSnapshot(
    double? CpuUsage,
    ulong? MemoryUsedBytes,
    ulong? MemoryTotalBytes,
    double? GpuUsage,
    ulong? VramUsedBytes,
    ulong? VramTotalBytes,
    double? CpuTemperatureCelsius,
    double? GpuTemperatureCelsius,
    double? BatteryPercent,
    bool? BatteryCharging)
{
    /// <summary>A snapshot with nothing read yet.</summary>
    public static HardwareSnapshot Empty => default;

    public double? MemoryUsagePercent =>
        MemoryUsedBytes is { } used && MemoryTotalBytes is { } total && total > 0
            ? used * 100.0 / total
            : null;

    public double? VramUsagePercent =>
        VramUsedBytes is { } used && VramTotalBytes is { } total && total > 0
            ? used * 100.0 / total
            : null;
}
