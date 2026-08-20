using System.Drawing;
using Microsoft.Win32;
using PerfRail.Rendering;

namespace PerfRail;

/// <summary>
/// Application root. Owns the tray icon, the rail's lifetime, and the single shutdown path.
/// </summary>
/// <remarks>
/// There is no MainForm: the rail is created on demand and destroyed when undocked, so an
/// undocked PerfRail holds no window at all.
/// </remarks>
internal sealed class RailContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _dockItem;
    private readonly Icon _trayIcon;

    private RailForm? _rail;
    private int _shutdownStarted;

    public RailContext(bool dockOnStart = false)
    {
        _trayIcon = TrayIconFactory.Create();

        _dockItem = new ToolStripMenuItem("Show rail")
        {
            CheckOnClick = true,
            Checked = false,
        };
        _dockItem.CheckedChanged += OnDockCheckedChanged;

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("PerfRail") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_dockItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Shutdown()));

        _tray = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "PerfRail",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // Every path that can end this process has to reach exactly one Shutdown().
        // Leaving the AppBar registered means the desktop stays permanently short by the
        // height of the bar until Explorer restarts.
        SystemEvents.SessionEnding += OnSessionEnding;
        Application.ApplicationExit += OnApplicationExit;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        if (dockOnStart)
        {
            // CheckedChanged drives Dock(), so this is the same path a tray click takes.
            _dockItem.Checked = true;
        }
    }

    /// <summary>
    /// Docks the rail, reserving screen space.
    /// </summary>
    /// <remarks>
    /// Deliberately off until the user asks for it. Reserving work area shrinks every
    /// maximized window on the machine, which Microsoft Store policy 10.2.8 treats as
    /// modifying the Windows experience and requires consent for. Microsoft's own
    /// Command Palette Dock, which uses the same AppBar API, ships off by default too.
    /// </remarks>
    private void Dock()
    {
        if (_rail is not null)
        {
            return;
        }

        _rail = new RailForm();

        // The rail can also disappear without going through Undock (an external
        // WM_CLOSE, for instance). Without this the field would keep pointing at a
        // disposed form and the menu would still claim the rail is shown.
        _rail.FormClosed += OnRailClosed;

        _rail.UpdateCells(PlaceholderCells());
        _rail.Show();
    }

    private void OnRailClosed(object? sender, FormClosedEventArgs e)
    {
        _rail = null;

        if (_dockItem.Checked)
        {
            // Reflect reality without re-entering Undock through the click handler.
            _dockItem.CheckedChanged -= OnDockCheckedChanged;
            _dockItem.Checked = false;
            _dockItem.CheckedChanged += OnDockCheckedChanged;
        }
    }

    private void Undock()
    {
        if (_rail is null)
        {
            return;
        }

        RailForm rail = _rail;
        _rail = null;
        rail.FormClosed -= OnRailClosed;

        // Disposing destroys the handle, and RailForm releases the reserved band in
        // OnHandleDestroyed.
        rail.Close();
        rail.Dispose();
    }

    private void OnDockCheckedChanged(object? sender, EventArgs e)
    {
        if (_dockItem.Checked)
        {
            Dock();
        }
        else
        {
            Undock();
        }
    }

    /// <summary>
    /// Static cells shown until real telemetry lands in M2.
    /// </summary>
    private static IReadOnlyList<RailCell> PlaceholderCells() =>
    [
        new RailCell("CPU", "--%", "100%"),
        new RailCell("RAM", "--%", "100%"),
    ];

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => Shutdown();

    private void OnApplicationExit(object? sender, EventArgs e) => ReleaseResources();

    private void OnProcessExit(object? sender, EventArgs e) => ReleaseResources();

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e) => ReleaseResources();

    /// <summary>
    /// Orderly exit: release the band and the tray icon, then end the message loop.
    /// </summary>
    private void Shutdown()
    {
        ReleaseResources();
        ExitThread();
    }

    /// <summary>
    /// Idempotent teardown. Safe from any of the shutdown paths, in any order, on any thread.
    /// </summary>
    private void ReleaseResources()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        SystemEvents.SessionEnding -= OnSessionEnding;
        Application.ApplicationExit -= OnApplicationExit;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;

        Undock();

        // Dispose(false) does not send NIM_DELETE, which leaves a ghost icon in the tray
        // until the user hovers over it.
        _tray.Visible = false;
        _tray.Dispose();
        _trayIcon.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseResources();
        }

        base.Dispose(disposing);
    }
}
