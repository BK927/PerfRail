using System.Runtime.InteropServices;
using PerfRail.AppBar;
using PerfRail.Interop;
using PerfRail.Rendering;
using PerfRail.Services;

namespace PerfRail;

/// <summary>
/// The monitoring strip itself: a borderless, non-activating, custom-painted window
/// whose position is dictated entirely by the shell.
/// </summary>
internal sealed class RailForm : Form
{
    private readonly AppBarHost _appBar;
    private readonly RailRenderer _renderer;
    private readonly FullscreenWatcher _fullscreen;
    private readonly LoggingService _log;

    public RailForm(LoggingService log)
    {
        _log = log;

        // These MUST be set before the handle exists. Their setters call
        // RecreateHandle() when IsHandleCreated is true, and a new HWND would silently
        // orphan the ABM_NEW registration: the bar keeps painting while reserving nothing.
        //
        // ShowInTaskbar = false is not optional. WinForms ORs WS_EX_APPWINDOW into
        // CreateParams when it is true (the default), and WS_EX_APPWINDOW forces a
        // taskbar button, overriding WS_EX_NOACTIVATE.
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;

        // ShowWithoutActivation is checked after WindowState in Form.ShowParams, so a
        // non-Normal state would silently discard it.
        WindowState = FormWindowState.Normal;
        StartPosition = FormStartPosition.Manual;

        // No child controls exist, so there is nothing to auto-scale. Leaving
        // AutoScaleDimensions unset keeps the scale factor pinned at (1,1) and makes
        // PerformAutoScale a no-op at load, at handle creation, and on font change.
        AutoScaleMode = AutoScaleMode.None;

        // Z-order is owned by AppBarHost, which raises the rail to HWND_TOPMOST and
        // drops it to HWND_BOTTOM when a full-screen app appears. Setting TopMost here
        // as well would have WinForms issue its own competing SetWindowPos calls.
        //
        // Topmost is necessary, not cosmetic. Reserving work area keeps other windows'
        // client areas off the band, but not their DWM extended frames: a maximized
        // window's rect starts about 11 px above its visible edge at 150% scaling and
        // its drop shadow lands on the rail. Measured on this machine, the bottom third
        // of the bar was darkened and the text cut through.
        TopMost = false;

        ControlBox = false;
        MinimizeBox = false;
        MaximizeBox = false;
        Text = "PerfRail";

        SetStyle(
            ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.Opaque,
            true);
        SetStyle(ControlStyles.Selectable, false);

        _renderer = new RailRenderer();
        _appBar = new AppBarHost(this);
        _appBar.DpiChanged += OnBarDpiChanged;

        _appBar.RegistrationFailed += OnRegistrationFailed;
        _appBar.Repositioned += OnRepositioned;

        _fullscreen = new FullscreenWatcher();
        _fullscreen.FullscreenChanged += OnFullscreenChanged;
    }

    /// <summary>Raised whenever the shell approves a new reserved rectangle.</summary>
    public event Action<RECT>? Repositioned
    {
        add => _appBar.Repositioned += value;
        remove => _appBar.Repositioned -= value;
    }

    public bool IsDocked => _appBar.IsRegistered;

    public RECT ReservedRect => _appBar.ReservedRect;

    /// <summary>Replaces the displayed cells and repaints only if anything changed.</summary>
    public void UpdateCells(IReadOnlyList<RailCell> cells)
    {
        if (_renderer.SetCells(cells))
        {
            Invalidate();
        }
    }

    /// <summary>
    /// Suppresses activation when the window is shown.
    /// </summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;

            // NOACTIVATE keeps us out of the activation chain entirely; TOOLWINDOW is
            // not a focus style, it only keeps us out of ALT+TAB.
            cp.ExStyle |= User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        _renderer.UpdateDpi(CurrentDpi());
        _appBar.Register();

        // A game may already be running when the rail is docked.
        _fullscreen.Refresh();
        _appBar.SetStandAside(_fullscreen.IsFullscreen);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        // Last chance to release the band while the HWND still exists.
        _appBar.Unregister();
        base.OnHandleDestroyed(e);
    }

    /// <summary>
    /// Opts out of the non-linear DPI size calculation.
    /// </summary>
    /// <remarks>
    /// Returning false keeps scaling linear and skips a FontHandleWrapper allocation
    /// plus a GDI text-metric call on every DPI change. Our size comes from the shell
    /// regardless.
    /// </remarks>
    protected override bool OnGetDpiScaledSize(int deviceDpiOld, int deviceDpiNew, ref Size desiredSize) =>
        false;

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // Cancelling stops ContainerControl.ScaleContainerForDpi from calling
        // SetWindowPos with its own suggested rectangle, which would fight the AppBar
        // band. DeviceDpi has already been updated by this point, so it stays correct.
        e.Cancel = true;

        // Windows can re-fire WM_DPICHANGED recursively unless the handler issues its
        // own SetWindowPos, so ApplyPosition must run here rather than being deferred.
        _appBar.ApplyPosition();

        base.OnDpiChanged(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (_appBar.ProcessMessage(ref m))
        {
            return;
        }

        if (m.Msg == User32.WM_WINDOWPOSCHANGING)
        {
            // Snapshot before default handling, which rewrites the height, then let
            // AppBarHost restore the geometry it asked for.
            WINDOWPOS requested = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
            base.WndProc(ref m);
            _appBar.AfterWindowPosChanging(m.LParam, requested);
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e) => _renderer.Paint(e.Graphics, ClientRectangle);

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Opaque + full repaint in OnPaint. Painting the background here would only
        // add a flash.
    }

    private void OnBarDpiChanged(uint dpi) => _renderer.UpdateDpi(dpi);

    private void OnFullscreenChanged(bool isFullscreen)
    {
        _log.Info(isFullscreen
            ? "full-screen app detected, moving the rail behind it"
            : "full-screen app gone, restoring the rail");

        _appBar.SetStandAside(isFullscreen);
    }

    private void OnRegistrationFailed(int attempt) =>
        _log.Warn($"AppBar registration refused (attempt {attempt}); Explorer may be restarting");

    private void OnRepositioned(RECT rect) => _log.Info($"rail band set to {rect}");

    private uint CurrentDpi()
    {
        uint dpi = User32.GetDpiForWindow(Handle);
        return dpi == 0 ? (uint)DeviceDpi : dpi;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fullscreen.FullscreenChanged -= OnFullscreenChanged;
            _fullscreen.Dispose();

            _appBar.RegistrationFailed -= OnRegistrationFailed;
            _appBar.Repositioned -= OnRepositioned;
            _appBar.DpiChanged -= OnBarDpiChanged;
            _appBar.Dispose();
            _renderer.Dispose();
        }

        base.Dispose(disposing);
    }
}
