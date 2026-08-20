using System.Drawing;
using System.Windows.Forms;

namespace PerfRail.Rendering;

/// <summary>
/// Lays out and paints the rail's cells.
/// </summary>
/// <remarks>
/// <para>
/// Built around a list of cells rather than one concatenated string so that a metric
/// which cannot be read is simply absent, with no gap and no "N/A" placeholder.
/// </para>
/// <para>
/// Measurement is cached. The optimisation that actually matters is skipping
/// <c>Invalidate()</c> altogether when no formatted string changed - see
/// <see cref="SetCells"/>. Sub-rectangle invalidation is not worth it: a full
/// 2560x20 blit from a cached back buffer is about 200 KB and effectively free.
/// </para>
/// </remarks>
internal sealed class RailRenderer : IDisposable
{
    /// <summary>
    /// GDI flags used for both measuring and drawing.
    /// </summary>
    /// <remarks>
    /// NoPadding must be identical in both calls. TextRenderer adds a few pixels of
    /// padding by default, so measuring without it and drawing with it (or vice versa)
    /// makes the computed and painted widths disagree with no visible error.
    /// </remarks>
    private const TextFormatFlags TextFlags =
        TextFormatFlags.NoPadding
        | TextFormatFlags.SingleLine
        | TextFormatFlags.VerticalCenter
        | TextFormatFlags.NoPrefix;

    private readonly Theme _theme;
    private readonly List<RailCell> _cells = [];
    private readonly List<CellLayout> _layout = [];

    private Font? _font;
    private SolidBrush? _backgroundBrush;
    private Pen? _separatorPen;
    private Pen? _borderPen;
    private uint _dpi = 96;
    private bool _layoutDirty = true;

    public RailRenderer()
        : this(Theme.Dark)
    {
    }

    public RailRenderer(Theme theme) => _theme = theme;

    /// <summary>
    /// Replaces the cell list.
    /// </summary>
    /// <returns>
    /// True when something visible changed and the caller should repaint. At 1 Hz most
    /// samples produce identical formatted strings, so this returns false often.
    /// </returns>
    public bool SetCells(IReadOnlyList<RailCell> cells)
    {
        if (SameAsCurrent(cells))
        {
            return false;
        }

        bool structureChanged = cells.Count != _cells.Count;
        if (!structureChanged)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                if (cells[i].Label != _cells[i].Label || cells[i].WidestValue != _cells[i].WidestValue)
                {
                    structureChanged = true;
                    break;
                }
            }
        }

        _cells.Clear();
        _cells.AddRange(cells);

        if (structureChanged)
        {
            _layoutDirty = true;
        }

        return true;
    }

    public void UpdateDpi(uint dpi)
    {
        if (dpi == _dpi && _font is not null)
        {
            return;
        }

        _dpi = dpi == 0 ? 96 : dpi;

        _font?.Dispose();

        // GraphicsUnit.Pixel, never Point. Point sizes are converted through the
        // device context's DPI, which is the system DPI and therefore wrong on a
        // per-monitor-aware window sitting on a secondary display.
        _font = new Font(
            _theme.FontFamily,
            _theme.FontSizeDip * _dpi / 96f,
            FontStyle.Regular,
            GraphicsUnit.Pixel);

        _layoutDirty = true;
    }

    public void Paint(Graphics g, Rectangle bounds)
    {
        EnsureResources();

        g.FillRectangle(_backgroundBrush!, bounds);

        if (_cells.Count > 0)
        {
            EnsureLayout(g, bounds);
            PaintCells(g, bounds);
        }

        // Hairline along the inner edge so the rail reads as a distinct surface rather
        // than a black band bleeding into whatever is below it.
        int y = bounds.Bottom - 1;
        g.DrawLine(_borderPen!, bounds.Left, y, bounds.Right, y);
    }

    private void PaintCells(Graphics g, Rectangle bounds)
    {
        int separatorTop = bounds.Top + Scale(5f);
        int separatorBottom = bounds.Bottom - Scale(5f);

        for (int i = 0; i < _layout.Count; i++)
        {
            CellLayout slot = _layout[i];
            RailCell cell = _cells[i];

            TextRenderer.DrawText(
                g,
                cell.Label,
                _font,
                new Rectangle(slot.LabelX, bounds.Top, slot.LabelWidth, bounds.Height),
                _theme.Label,
                TextFlags);

            TextRenderer.DrawText(
                g,
                cell.Value,
                _font,
                new Rectangle(slot.ValueX, bounds.Top, slot.ValueWidth, bounds.Height),
                _theme.ValueColor(cell.Severity),
                TextFlags);

            if (i < _layout.Count - 1)
            {
                g.DrawLine(_separatorPen!, slot.SeparatorX, separatorTop, slot.SeparatorX, separatorBottom);
            }
        }
    }

    private void EnsureLayout(Graphics g, Rectangle bounds)
    {
        if (!_layoutDirty)
        {
            return;
        }

        _layout.Clear();

        int gap = Scale(_theme.LabelValueGapDip);
        int cellGap = Scale(_theme.CellGapDip);
        int x = bounds.Left + Scale(_theme.EdgePaddingDip);

        foreach (RailCell cell in _cells)
        {
            int labelWidth = Measure(g, cell.Label);

            // Reserve the widest value this cell can ever show, so the rail never
            // reflows when a value goes from "9%" to "10%".
            int valueWidth = Math.Max(Measure(g, cell.WidestValue), Measure(g, cell.Value));

            int labelX = x;
            int valueX = labelX + labelWidth + gap;
            x = valueX + valueWidth;

            int separatorX = x + cellGap;
            x = separatorX + cellGap;

            _layout.Add(new CellLayout(labelX, labelWidth, valueX, valueWidth, separatorX));
        }

        _layoutDirty = false;
    }

    private int Measure(Graphics g, string text) =>
        TextRenderer.MeasureText(g, text, _font, new Size(int.MaxValue, int.MaxValue), TextFlags).Width;

    private int Scale(float dip) => Math.Max(1, (int)Math.Round(dip * _dpi / 96f));

    private bool SameAsCurrent(IReadOnlyList<RailCell> cells)
    {
        if (cells.Count != _cells.Count)
        {
            return false;
        }

        for (int i = 0; i < cells.Count; i++)
        {
            if (!cells[i].Equals(_cells[i]))
            {
                return false;
            }
        }

        return true;
    }

    private void EnsureResources()
    {
        if (_font is null)
        {
            UpdateDpi(_dpi);
        }

        // Cached rather than created per paint. Note these are our own objects: never
        // dispose anything from SystemBrushes/Brushes/SystemPens, which are
        // process-wide singletons.
        _backgroundBrush ??= new SolidBrush(_theme.Background);
        _separatorPen ??= new Pen(_theme.Separator);
        _borderPen ??= new Pen(_theme.Border);
    }

    public void Dispose()
    {
        _font?.Dispose();
        _backgroundBrush?.Dispose();
        _separatorPen?.Dispose();
        _borderPen?.Dispose();

        _font = null;
        _backgroundBrush = null;
        _separatorPen = null;
        _borderPen = null;
    }

    private readonly record struct CellLayout(
        int LabelX,
        int LabelWidth,
        int ValueX,
        int ValueWidth,
        int SeparatorX);
}
