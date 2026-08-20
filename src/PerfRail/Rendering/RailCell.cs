namespace PerfRail.Rendering;

/// <summary>
/// How much attention a value should attract. Normal is the overwhelming majority and
/// must stay visually quiet.
/// </summary>
internal enum MetricSeverity
{
    Normal,
    Warning,
    Critical,
}

/// <summary>
/// One metric shown on the rail, for example <c>CPU</c> / <c>14%</c>.
/// </summary>
/// <param name="Label">Short caption, e.g. "CPU".</param>
/// <param name="Value">Formatted value, e.g. "14%". Never a placeholder for missing
/// data - a metric that cannot be read is omitted from the list entirely.</param>
/// <param name="Severity">Drives colour only, never layout.</param>
/// <param name="WidestValue">
/// The widest string this cell will ever display, e.g. "100%". The cell reserves this
/// width so the rail does not reflow every time a digit count changes.
/// </param>
internal readonly record struct RailCell(
    string Label,
    string Value,
    MetricSeverity Severity,
    string WidestValue)
{
    public RailCell(string label, string value, string widestValue)
        : this(label, value, MetricSeverity.Normal, widestValue)
    {
    }
}
