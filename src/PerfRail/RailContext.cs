using System.Diagnostics;
using System.Drawing;
using Microsoft.Win32;
using PerfRail.Configuration;
using PerfRail.Rendering;
using PerfRail.Sensors;
using PerfRail.Services;
using PerfRail.UI;

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
    private readonly LoggingService _log = new();
    private readonly SettingsService _settingsService = new();
    private readonly AppSettings _settings;
    private readonly IStartupService _startup = StartupServiceFactory.Create();

    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _dockItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly Icon _trayIcon;
    private readonly TelemetryService _telemetry;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly List<RailCell> _cells = [];

    private RailForm? _rail;
    private SettingsForm? _settingsForm;
    private bool _restoringState;
    private int _shutdownStarted;

    public RailContext(bool forceDock = false, bool openSettings = false)
    {
        _log.Info($"PerfRail {typeof(RailContext).Assembly.GetName().Version?.ToString(3)} starting "
            + $"({(Interop.PackageIdentity.IsPackaged ? "packaged" : "standalone")}, "
            + $"DPI mode {Application.HighDpiMode})");

        _settingsService.Failed += OnSettingsFailed;
        _settings = _settingsService.Load();

        _telemetry = new TelemetryService(
            [new CpuMemorySource(), new PdhGpuSource()],
            TimeSpan.FromMilliseconds(_settings.UpdateIntervalMs));
        _telemetry.SourceFailed += OnSourceFailed;
        _telemetry.Start();

        _trayIcon = TrayIconFactory.Create();

        _dockItem = new ToolStripMenuItem("Show rail") { CheckOnClick = true };
        _dockItem.CheckedChanged += OnDockCheckedChanged;

        _pauseItem = new ToolStripMenuItem("Pause monitoring") { CheckOnClick = true };
        _pauseItem.CheckedChanged += OnPauseCheckedChanged;

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("PerfRail") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_dockItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripMenuItem("Settings...", null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("About PerfRail", null, (_, _) => ShowAbout()));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Shutdown()));

        _tray = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "PerfRail",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // The UI pulls the latest snapshot on its own timer rather than the sampler
        // pushing into the UI thread. Nothing crosses a thread boundary, so there is no
        // window where a sample arrives at a form that is already being disposed.
        _uiTimer = new System.Windows.Forms.Timer { Interval = _settings.UpdateIntervalMs };
        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();

        // Every path that can end this process has to reach exactly one Shutdown().
        // Leaving the AppBar registered means the desktop stays permanently short by the
        // height of the bar until Explorer restarts.
        SystemEvents.SessionEnding += OnSessionEnding;
        Application.ApplicationExit += OnApplicationExit;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // CheckedChanged drives Dock(), so this is the same path a tray click takes.
        // Guarded because restoring saved state is not a user decision: without this,
        // a --dock launch would silently rewrite the user's saved preference.
        _restoringState = true;
        _dockItem.Checked = forceDock || _settings.Docked;
        _restoringState = false;

        if (openSettings)
        {
            ShowSettings();
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

        _rail = new RailForm(_log);

        // The rail can also disappear without going through Undock (an external
        // WM_CLOSE, for instance). Without this the field would keep pointing at a
        // disposed form and the menu would still claim the rail is shown.
        _rail.FormClosed += OnRailClosed;
        _rail.Show();

        // Paint real values immediately instead of waiting for the next tick.
        PushSnapshotToRail();
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

        _log.Info(_dockItem.Checked ? "rail docked" : "rail undocked");

        if (!_restoringState && _settings.Docked != _dockItem.Checked)
        {
            _settings.Docked = _dockItem.Checked;
            _settingsService.Save(_settings);
        }
    }

    private void OnPauseCheckedChanged(object? sender, EventArgs e) =>
        _telemetry.IsPaused = _pauseItem.Checked;

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_settings, _startup, OnSettingsChanged);
        _settingsForm.FormClosed += (_, _) =>
        {
            _settingsForm = null;
            _settingsService.Save(_settings);
        };
        _settingsForm.Show();
    }

    /// <summary>
    /// Applies a settings change live, without a restart.
    /// </summary>
    private void OnSettingsChanged()
    {
        if (_uiTimer.Interval != _settings.UpdateIntervalMs)
        {
            _uiTimer.Interval = _settings.UpdateIntervalMs;
            _telemetry.Interval = TimeSpan.FromMilliseconds(_settings.UpdateIntervalMs);
        }

        PushSnapshotToRail();
    }

    private void ShowAbout()
    {
        string version = typeof(RailContext).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        string packaging = Interop.PackageIdentity.IsPackaged ? "Microsoft Store build" : "standalone build";

        MessageBox.Show(
            $"PerfRail {version}\r\n{packaging}\r\n\r\n"
                + "A lightweight hardware monitor that reserves its own strip of screen edge "
                + "using the Windows AppBar API, so it never covers your applications.\r\n\r\n"
                + "Runs as a standard user. Temperatures are not shown because reading them "
                + "requires a kernel driver and administrator rights.\r\n\r\n"
                + "https://github.com/BK927/PerfRail",
            "About PerfRail",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void OnUiTick(object? sender, EventArgs e) => PushSnapshotToRail();

    private void PushSnapshotToRail()
    {
        if (_rail is null)
        {
            return;
        }

        RailCellBuilder.Build(_telemetry.Current, _settings, _cells);

        // UpdateCells repaints only when a formatted string actually changed, which at
        // 1 Hz is most of the time a no-op.
        _rail.UpdateCells(_cells);
    }

    private void OnSourceFailed(string source, Exception ex) =>
        _log.Error($"sensor '{source}' threw and has been disabled for this run", ex);

    private void OnSettingsFailed(string stage, Exception ex) =>
        _log.Error($"settings {stage} failed", ex);

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => Shutdown();

    private void OnApplicationExit(object? sender, EventArgs e) => ReleaseResources();

    private void OnProcessExit(object? sender, EventArgs e) => ReleaseResources();

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e) => ReleaseResources();

    /// <summary>Ends the app through the normal path. Safe to call from any thread.</summary>
    public void RequestShutdown() => Shutdown();

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

        _uiTimer.Stop();
        _uiTimer.Tick -= OnUiTick;
        _uiTimer.Dispose();

        // Cancels and joins the sampler before its sources are disposed.
        _telemetry.SourceFailed -= OnSourceFailed;
        _telemetry.Dispose();

        _settingsForm?.Close();
        Undock();

        // Dispose(false) does not send NIM_DELETE, which leaves a ghost icon in the tray
        // until the user hovers over it.
        _tray.Visible = false;
        _tray.Dispose();
        _trayIcon.Dispose();

        _settingsService.Failed -= OnSettingsFailed;

        _log.Info("shutdown complete, reserved area released");
        _log.Dispose();
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
