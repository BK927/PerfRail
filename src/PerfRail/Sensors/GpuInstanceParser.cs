using System.Globalization;
using System.Text.RegularExpressions;
using PerfRail.Interop;

namespace PerfRail.Sensors;

/// <summary>
/// Parses "GPU Engine" performance-counter instance names and aggregates their values.
/// </summary>
/// <remarks>
/// Instance names look like
/// <c>pid_1092_luid_0x00000000_0x0000CE58_phys_0_eng_0_engtype_3D</c>.
/// </remarks>
internal static partial class GpuInstanceParser
{
    /// <summary>
    /// Matches a GPU Engine instance name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine type is <c>.*</c> on purpose, and is never matched against a list of
    /// known values. It is a driver-supplied friendly name, not an enumeration: NVIDIA
    /// emits things like <c>Compute_0</c>, <c>VideoEncode</c>, <c>VR</c> and
    /// <c>Security</c>, and names can contain spaces ("GDI Render"), digits and
    /// underscores. A whitelist built from one machine's output silently drops half the
    /// engines on somebody else's GPU. It can also be empty, hence <c>*</c> not <c>+</c>.
    /// </para>
    /// <para>
    /// For the same reason the name is never split on '_': the engine type contains them.
    /// </para>
    /// <para>
    /// PDH returns instance names lowercased, so matching is case-insensitive.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^pid_(?<pid>\d+)_luid_0x(?<hi>[0-9A-Fa-f]{8})_0x(?<lo>[0-9A-Fa-f]{8})_phys_(?<phys>\d+)_eng_(?<eng>\d+)_engtype_(?<type>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstancePattern();

    /// <summary>
    /// Aggregates per-engine utilisation for one adapter.
    /// </summary>
    /// <param name="items">Instance name and value pairs from a single collect.</param>
    /// <param name="luid">The adapter to report on. Everything else is ignored.</param>
    /// <returns>Utilisation 0-100, or null when the adapter contributed no usable instance.</returns>
    /// <remarks>
    /// <para>
    /// Values are summed per engine across every process, then the BUSIEST engine wins.
    /// That is what Task Manager reports, and it is not interchangeable with summing
    /// everything: a naive total over all engines reads around 19% on an idle machine,
    /// because every engine's small contribution piles up.
    /// </para>
    /// <para>
    /// Engines are keyed by (phys, eng) rather than by type name, since one adapter can
    /// expose several nodes sharing a type.
    /// </para>
    /// </remarks>
    public static double? Aggregate(IEnumerable<(string Name, double Value)> items, LUID luid)
    {
        Dictionary<(int Phys, int Eng), double> perEngine = [];

        foreach ((string name, double value) in items)
        {
            Match match = InstancePattern().Match(name);
            if (!match.Success)
            {
                continue;
            }

            if (!TryParseLuid(match, out LUID instanceLuid) || !Equals(instanceLuid, luid))
            {
                continue;
            }

            int phys = int.Parse(match.Groups["phys"].ValueSpan, CultureInfo.InvariantCulture);
            int eng = int.Parse(match.Groups["eng"].ValueSpan, CultureInfo.InvariantCulture);

            perEngine.TryGetValue((phys, eng), out double running);
            perEngine[(phys, eng)] = running + value;
        }

        if (perEngine.Count == 0)
        {
            return null;
        }

        double busiest = 0;
        foreach (double engineTotal in perEngine.Values)
        {
            if (engineTotal > busiest)
            {
                busiest = engineTotal;
            }
        }

        return Math.Clamp(busiest, 0, 100);
    }

    /// <summary>
    /// Builds the "GPU Adapter Memory" instance name for an adapter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Format is <c>luid_0xHHHHHHHH_0xLLLLLLLL_phys_0</c>, high part first, each as eight
    /// uppercase hex digits.
    /// </para>
    /// <para>
    /// phys is hard-coded to 0 on purpose: it is the linked-adapter member index, and it
    /// is 0 for every distinct adapter on a normal machine. Using it to tell two GPUs
    /// apart does not work - that is what the LUID is for.
    /// </para>
    /// </remarks>
    public static string AdapterMemoryInstanceName(LUID luid) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"luid_0x{luid.HighPart:X8}_0x{luid.LowPart:X8}_phys_0");

    private static bool TryParseLuid(Match match, out LUID luid)
    {
        luid = default;

        if (!uint.TryParse(match.Groups["hi"].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint high)
            || !uint.TryParse(match.Groups["lo"].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint low))
        {
            return false;
        }

        luid = new LUID { HighPart = unchecked((int)high), LowPart = low };
        return true;
    }

    private static bool Equals(LUID a, LUID b) =>
        a.LowPart == b.LowPart && a.HighPart == b.HighPart;
}
