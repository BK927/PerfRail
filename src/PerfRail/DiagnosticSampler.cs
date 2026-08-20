using System.Globalization;
using PerfRail.Interop;
using PerfRail.Sensors;
using PerfRail.Sensors.Vendor;

namespace PerfRail;

/// <summary>
/// Headless sampling mode: prints readings to stdout and exits.
/// </summary>
/// <remarks>
/// <para>
/// Reachable as <c>PerfRail.exe --sample [count]</c>. No window, no tray icon and no
/// AppBar registration, so it is safe to run alongside a docked instance.
/// </para>
/// <para>
/// Exists so the numbers on the rail can be checked against an independent source
/// instead of being read off the screen, and so a bug report can carry actual values
/// rather than a screenshot.
/// </para>
/// </remarks>
internal static class DiagnosticSampler
{
    public static void Run(int count, TimeSpan interval)
    {
        using var telemetry = new TelemetryService(
            [new CpuMemorySource(), new PdhGpuSource(), new BatterySource()], interval);
        telemetry.SourceFailed += (name, ex) =>
            Console.Error.WriteLine($"sensor '{name}' disabled: {ex.Message}");

        telemetry.Start();

        Console.WriteLine("sample\tcpu_pct\tram_pct\tram_used_bytes\tram_total_bytes\tgpu_pct" + "\tvram_used_bytes\tvram_total_bytes\tcpu_temp_c\tgpu_temp_c\tbattery_pct\tcharging");

        for (int i = 0; i < count; i++)
        {
            // Wait first: CPU utilisation is a rate and has nothing to report until it
            // has two samples to difference.
            Thread.Sleep(interval);

            HardwareSnapshot s = telemetry.Current;

            Console.WriteLine(string.Join(
                '\t',
                (i + 1).ToString(CultureInfo.InvariantCulture),
                Num(s.CpuUsage),
                Num(s.MemoryUsagePercent),
                Num(s.MemoryUsedBytes),
                Num(s.MemoryTotalBytes),
                Num(s.GpuUsage),
                Num(s.VramUsedBytes),
                Num(s.VramTotalBytes),
                Num(s.CpuTemperatureCelsius),
                Num(s.GpuTemperatureCelsius),
                Num(s.BatteryPercent),
                s.BatteryCharging is { } charging ? (charging ? "1" : "0") : string.Empty));

            Console.Out.Flush();
        }
    }

    /// <summary>
    /// Prints the graphics adapters DXGI reports and which one PerfRail would use.
    /// </summary>
    /// <remarks>
    /// Reachable as <c>PerfRail.exe --gpu-info</c>. Adapter selection is the part of GPU
    /// reporting most likely to be wrong on hardware the author cannot test, so it is
    /// worth being able to see it without a debugger.
    /// </remarks>
    public static void PrintGpuInfo()
    {
        List<GraphicsAdapter> adapters = Dxgi.EnumerateAdapters();

        if (adapters.Count == 0)
        {
            Console.WriteLine("no DXGI adapters (normal in some remote and virtualised sessions)");
            return;
        }

        Console.WriteLine("luid	software	dedicated_bytes	shared_bytes	description");
        foreach (GraphicsAdapter a in adapters)
        {
            Console.WriteLine(string.Join(
                '	',
                GpuInstanceParser.AdapterMemoryInstanceName(a.Luid),
                a.IsSoftware ? "yes" : "no",
                a.DedicatedVideoMemoryBytes.ToString(CultureInfo.InvariantCulture),
                a.SharedSystemMemoryBytes.ToString(CultureInfo.InvariantCulture),
                a.Description));
        }

        using var source = new PdhGpuSource();
        HardwareSnapshot probe = HardwareSnapshot.Empty;
        source.Contribute(ref probe);

        Console.WriteLine();
        Console.WriteLine($"selected	{source.AdapterDescription}");
        Console.WriteLine($"temperature	{source.TemperatureStatus}");
        Console.WriteLine($"reading	{(probe.GpuTemperatureCelsius is { } c ? $"{c:F0} C" : "unavailable")}");

        Console.WriteLine();
        Console.WriteLine("vendor temperature libraries:");
        foreach ((string vendor, string result) in GpuTemperatureSourceFactory.ProbeAll())
        {
            Console.WriteLine($"  {vendor}	{result}");
        }
    }

    /// <summary>
    /// Formats a value, or an empty field when the metric is unavailable.
    /// </summary>
    /// <remarks>
    /// Empty rather than 0, matching the rule the whole snapshot follows: unavailable
    /// and zero are different facts and must not be confused downstream.
    /// </remarks>
    private static string Num(double? value) =>
        value is { } v ? v.ToString("F2", CultureInfo.InvariantCulture) : string.Empty;

    private static string Num(ulong? value) =>
        value is { } v ? v.ToString(CultureInfo.InvariantCulture) : string.Empty;
}
