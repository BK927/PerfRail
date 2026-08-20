using PerfRail.Interop;

namespace PerfRail.Sensors.Vendor;

/// <summary>
/// A vendor-specific way to read a GPU's die temperature.
/// </summary>
/// <remarks>
/// <para>
/// There is no operating-system API for GPU temperature, so each vendor needs its own
/// library. All of them are user-mode and ship with the graphics driver, which is what
/// makes this the one temperature PerfRail can report without administrator rights or a
/// kernel driver - and therefore the one that can exist in the Microsoft Store build too.
/// </para>
/// <para>
/// Implementations must load their library dynamically and fail silently. A machine with
/// an NVIDIA card has no AMD library and vice versa, so "the library is missing" is the
/// normal case for at least one of them on every machine.
/// </para>
/// </remarks>
internal interface IGpuTemperatureSource : IDisposable
{
    /// <summary>Vendor this source can read.</summary>
    GpuVendor Vendor { get; }

    /// <summary>Short description of what was loaded, for <c>--gpu-info</c>.</summary>
    string Status { get; }

    /// <summary>
    /// Reads the GPU die temperature in degrees Celsius, or null when unavailable.
    /// </summary>
    double? ReadCelsius();
}

/// <summary>
/// Picks and owns the temperature source for a given adapter.
/// </summary>
internal static class GpuTemperatureSourceFactory
{
    /// <summary>
    /// Returns a source for this adapter's vendor, or null when none applies.
    /// </summary>
    /// <remarks>
    /// Intel is deliberately absent. IGCL only covers Arc and Alder Lake-P or newer, and
    /// integrated Intel graphics - which is what most Intel machines have - expose no die
    /// temperature at all. Adding it would mean shipping a code path that reports nothing
    /// on the overwhelming majority of the hardware it targets.
    /// </remarks>
    public static IGpuTemperatureSource? Create(GraphicsAdapter adapter) => adapter.Vendor switch
    {
        GpuVendor.Nvidia => NvmlTemperatureSource.TryCreate(),
        GpuVendor.Amd => AdlTemperatureSource.TryCreate(),
        _ => null,
    };

    /// <summary>
    /// Tries every vendor library regardless of which adapter is selected, and reports
    /// what happened. For <c>--gpu-info</c> and for support requests.
    /// </summary>
    /// <remarks>
    /// The per-adapter path above short-circuits on vendor, so on an Intel machine the
    /// NVIDIA and AMD loaders are never entered at all. This exercises them, which is the
    /// only way to confirm on such a machine that a missing library degrades quietly
    /// instead of throwing.
    /// </remarks>
    public static IEnumerable<(string Vendor, string Result)> ProbeAll()
    {
        yield return ("nvidia", Describe(NvmlTemperatureSource.TryCreate()));
        yield return ("amd", Describe(AdlTemperatureSource.TryCreate()));
    }

    private static string Describe(IGpuTemperatureSource? source)
    {
        if (source is null)
        {
            return "library not present";
        }

        using (source)
        {
            double? celsius = source.ReadCelsius();
            return celsius is { } c
                ? $"{source.Status}, reads {c:F0} C"
                : $"{source.Status}, but no reading";
        }
    }
}
