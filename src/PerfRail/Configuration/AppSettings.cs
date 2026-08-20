using System.Text.Json.Serialization;

namespace PerfRail.Configuration;

/// <summary>Which screen edge the rail docks to.</summary>
/// <remarks>
/// Values match the Win32 ABE_* constants so the mapping stays trivial. Only
/// <see cref="Top"/> is wired up today; the rest exist so the setting does not have to
/// change shape when vertical rails land.
/// </remarks>
internal enum BarEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3,
}

/// <summary>
/// User-visible configuration, persisted as JSON.
/// </summary>
/// <remarks>
/// <para>
/// Note what is deliberately absent: "start with Windows". That is operating-system
/// state, not ours. Task Manager lets the user veto a startup entry without deleting
/// it, and once vetoed it cannot be re-enabled programmatically. A cached bool here
/// would confidently disagree with reality, so the setting is always read live from
/// <see cref="Services.IStartupService"/>.
/// </para>
/// <para>
/// Mutable with defaults on every property so a JSON file missing a key, or carrying a
/// key from a future version, still deserializes into something usable.
/// </para>
/// </remarks>
internal sealed class AppSettings
{
    /// <summary>Bumped when a migration is ever needed. Unknown values fall back to defaults.</summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Whether the rail reserves screen space. Off until the user asks for it.
    /// </summary>
    /// <remarks>
    /// Reserving work area shrinks every maximized window on the machine. Microsoft
    /// Store policy 10.2.8 treats that as modifying the Windows experience and requires
    /// consent, and Microsoft's own Command Palette Dock ships off by default for the
    /// same reason.
    /// </remarks>
    public bool Docked { get; set; }

    /// <summary>Sampling period in milliseconds. Clamped on load.</summary>
    public int UpdateIntervalMs { get; set; } = 1000;

    /// <summary>Bar thickness in device-independent pixels at 96 DPI. Clamped on load.</summary>
    public int BarHeightDip { get; set; } = 20;

    public BarEdge BarEdge { get; set; } = BarEdge.Top;

    public bool ShowCpu { get; set; } = true;

    public bool ShowMemory { get; set; } = true;

    public bool ShowGpu { get; set; } = true;

    public bool ShowVram { get; set; } = true;

    /// <summary>
    /// Off by default and currently inert: CPU die temperature needs ring-0 access
    /// through a signed kernel driver, which PerfRail does not ship.
    /// </summary>
    public bool ShowCpuTemperature { get; set; }

    /// <summary>
    /// On by default, because the cell hides itself when there is no reading.
    /// </summary>
    /// <remarks>
    /// Needs an NVIDIA or AMD card, whose driver ships a user-mode library that reports
    /// die temperature without elevation. Intel and everything else simply never produce
    /// a value, and an absent metric is dropped from the rail rather than shown empty.
    /// </remarks>
    public bool ShowGpuTemperature { get; set; } = true;

    /// <summary>
    /// On by default; the cell hides itself on a machine with no battery.
    /// </summary>
    public bool ShowBattery { get; set; } = true;

    /// <summary>Sampling periods offered in the UI.</summary>
    [JsonIgnore]
    public static int[] AllowedIntervalsMs => [500, 1000, 2000, 5000];

    /// <summary>
    /// Forces every value into a range the app can actually run with.
    /// </summary>
    /// <remarks>
    /// Applied after deserialization so a hand-edited or truncated file degrades to
    /// something sane instead of, say, a 0 ms timer that pins a core.
    /// </remarks>
    public void Normalize()
    {
        if (Array.IndexOf(AllowedIntervalsMs, UpdateIntervalMs) < 0)
        {
            UpdateIntervalMs = 1000;
        }

        BarHeightDip = Math.Clamp(BarHeightDip, 16, 48);

        if (!Enum.IsDefined(BarEdge))
        {
            BarEdge = BarEdge.Top;
        }

        // Only the top edge is implemented. Silently correcting is better than docking
        // somewhere the layout code cannot handle.
        if (BarEdge != BarEdge.Top)
        {
            BarEdge = BarEdge.Top;
        }
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsContext : JsonSerializerContext;
