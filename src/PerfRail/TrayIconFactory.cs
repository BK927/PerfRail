using System.Drawing;
using System.Drawing.Drawing2D;
using PerfRail.Interop;

namespace PerfRail;

/// <summary>
/// Builds the tray icon at runtime.
/// </summary>
/// <remarks>
/// Drawn rather than shipped as an .ico so the repository carries no binary asset while
/// the visual identity is still unsettled. Replace with a designed icon before release;
/// the tray icon is the app's only persistent visual presence when the rail is undocked.
/// </remarks>
internal static class TrayIconFactory
{
    public static Icon Create()
    {
        const int size = 32;

        using var bitmap = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.None;
            g.Clear(Color.Transparent);

            // A rail: one bright band at the top edge, with three descending bars below
            // suggesting a readout.
            using var rail = new SolidBrush(Color.FromArgb(255, 226, 226, 232));
            g.FillRectangle(rail, 2, 3, size - 4, 5);

            using var bars = new SolidBrush(Color.FromArgb(255, 128, 172, 232));
            g.FillRectangle(bars, 4, 20, 5, 9);
            g.FillRectangle(bars, 13, 14, 5, 15);
            g.FillRectangle(bars, 22, 17, 5, 12);
        }

        // Icon.FromHandle does not own the HICON, so the bitmap's icon handle has to be
        // cloned into a managed Icon and the original destroyed.
        nint hIcon = bitmap.GetHicon();
        try
        {
            using var unowned = Icon.FromHandle(hIcon);
            return (Icon)unowned.Clone();
        }
        finally
        {
            User32.DestroyIcon(hIcon);
        }
    }
}
