using System.Globalization;
using PerfRail.Configuration;
using PerfRail.Sensors;

namespace PerfRail.Rendering;

/// <summary>
/// Thresholds at which a metric stops being unremarkable.
/// </summary>
/// <param name="Warning">Percentage at or above which the value is highlighted.</param>
/// <param name="Critical">Percentage at or above which it demands attention.</param>
internal readonly record struct MetricThresholds(double Warning, double Critical)
{
    public MetricSeverity Classify(double percent) => percent switch
    {
        _ when percent >= Critical => MetricSeverity.Critical,
        _ when percent >= Warning => MetricSeverity.Warning,
        _ => MetricSeverity.Normal,
    };
}

/// <summary>
/// Turns a <see cref="HardwareSnapshot"/> into the cells the rail draws.
/// </summary>
/// <remarks>
/// This is where the rail adapts: a metric that reads null is not added at all, so on a
/// machine with no visible GPU the rail is simply <c>CPU 14% | RAM 53%</c> with no gap
/// and no "N/A" placeholder taking up space.
/// </remarks>
internal static class RailCellBuilder
{
    // Settings will own these once the configuration layer lands; kept here rather than
    // inlined so there is a single place to move.
    private static readonly MetricThresholds CpuThresholds = new(85, 95);
    private static readonly MetricThresholds MemoryThresholds = new(85, 95);
    private static readonly MetricThresholds GpuThresholds = new(85, 95);
    private static readonly MetricThresholds VramThresholds = new(85, 95);

    /// <summary>Shown while a rate metric is still waiting for its second sample.</summary>
    private const string Pending = "--%";

    /// <summary>Reserved width for a percentage cell, so digits changing never reflows.</summary>
    private const string WidestPercent = "100%";

    /// <summary>
    /// Fills <paramref name="into"/> with the cells for this snapshot.
    /// </summary>
    /// <remarks>
    /// Writes into a caller-owned list so the 1 Hz update does not allocate a new one
    /// every tick.
    /// </remarks>
    public static void Build(in HardwareSnapshot snapshot, AppSettings settings, List<RailCell> into)
    {
        into.Clear();

        // CPU and memory come from syscalls that cannot fail to be available, so these
        // depend only on whether the user wants to see them. CPU shows a placeholder
        // until it has two samples to compare.
        if (settings.ShowCpu)
        {
            into.Add(Percent("CPU", snapshot.CpuUsage, CpuThresholds, showPendingWhenNull: true));
        }

        if (settings.ShowMemory)
        {
            into.Add(Percent("RAM", snapshot.MemoryUsagePercent, MemoryThresholds, showPendingWhenNull: true));
        }

        // From here down a cell needs BOTH the user's consent and an actual reading.
        // Absent means absent: no gap, no "N/A" holding a slot open.
        if (settings.ShowGpu && snapshot.GpuUsage is { } gpu)
        {
            into.Add(Percent("GPU", gpu, GpuThresholds, showPendingWhenNull: false));
        }

        if (settings.ShowVram && snapshot.VramUsagePercent is { } vram)
        {
            into.Add(Percent("VRAM", vram, VramThresholds, showPendingWhenNull: false));
        }

        if (settings.ShowGpuTemperature && snapshot.GpuTemperatureCelsius is { } gpuTemp)
        {
            into.Add(new RailCell(
                "GPU",
                FormatTemperature(gpuTemp),
                MetricSeverity.Normal,
                "100°C"));
        }
    }

    private static RailCell Percent(
        string label,
        double? value,
        MetricThresholds thresholds,
        bool showPendingWhenNull)
    {
        if (value is not { } percent)
        {
            return new RailCell(label, showPendingWhenNull ? Pending : string.Empty, WidestPercent);
        }

        return new RailCell(
            label,
            FormatPercent(percent),
            thresholds.Classify(percent),
            WidestPercent);
    }

    /// <summary>
    /// Formats a percentage with no decimals.
    /// </summary>
    /// <remarks>
    /// A monitoring bar updating once a second is read at a glance, and a flickering
    /// decimal place is noise. Rounding rather than truncating so 99.6% does not read
    /// as 99% while the machine is pinned.
    /// </remarks>
    private static string FormatPercent(double percent) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Math.Clamp(Math.Round(percent), 0, 100):F0}%");

    private static string FormatTemperature(double celsius) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(celsius):F0}°C");
}
