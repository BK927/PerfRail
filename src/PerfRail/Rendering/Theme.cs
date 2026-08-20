using System.Drawing;

namespace PerfRail.Rendering;

/// <summary>
/// Colours and metrics for the rail.
/// </summary>
/// <remarks>
/// Deliberately a mutable instance rather than constants, so a future settings screen
/// can swap palettes without the renderer knowing anything about themes. The defaults
/// are a dark, low-contrast set: a monitoring bar that is quiet at a glance and only
/// becomes noticeable when a value is abnormal.
/// </remarks>
internal sealed class Theme
{
    public static Theme Dark { get; } = new();

    /// <summary>Very dark neutral, not pure black, so the rail reads as a surface.</summary>
    public Color Background { get; init; } = Color.FromArgb(255, 22, 22, 24);

    /// <summary>Hairline along the inner edge, separating the rail from the desktop.</summary>
    public Color Border { get; init; } = Color.FromArgb(255, 44, 44, 48);

    /// <summary>Labels are dimmer than values: the number is what you scan for.</summary>
    public Color Label { get; init; } = Color.FromArgb(255, 138, 138, 146);

    public Color ValueNormal { get; init; } = Color.FromArgb(255, 226, 226, 232);

    public Color ValueWarning { get; init; } = Color.FromArgb(255, 232, 186, 96);

    public Color ValueCritical { get; init; } = Color.FromArgb(255, 232, 106, 96);

    public Color Separator { get; init; } = Color.FromArgb(255, 58, 58, 64);

    public string FontFamily { get; init; } = "Segoe UI";

    /// <summary>Font size in device-independent pixels, scaled by DPI at render time.</summary>
    public float FontSizeDip { get; init; } = 12f;

    /// <summary>Gap between a label and its value, in DIP.</summary>
    public float LabelValueGapDip { get; init; } = 5f;

    /// <summary>Gap on each side of a separator, in DIP.</summary>
    public float CellGapDip { get; init; } = 11f;

    /// <summary>Padding at the left and right ends of the rail, in DIP.</summary>
    public float EdgePaddingDip { get; init; } = 10f;

    public Color ValueColor(MetricSeverity severity) => severity switch
    {
        MetricSeverity.Warning => ValueWarning,
        MetricSeverity.Critical => ValueCritical,
        _ => ValueNormal,
    };
}
