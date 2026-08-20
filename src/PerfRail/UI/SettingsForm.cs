using System.Diagnostics;
using System.Drawing;
using PerfRail.Configuration;
using PerfRail.Services;

namespace PerfRail.UI;

/// <summary>
/// The settings dialog. Applies changes immediately; the caller persists them.
/// </summary>
/// <remarks>
/// A normal, activatable window - unlike the rail, which must never take focus. All
/// layout is programmatic and auto-sized so it scales correctly at any DPI without a
/// designer file carrying baked-in pixel values.
/// </remarks>
internal sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly IStartupService _startup;
    private readonly Action _onChanged;

    private readonly ComboBox _interval = new();
    private readonly CheckBox _showCpu = new() { Text = "CPU usage", AutoSize = true };
    private readonly CheckBox _showMemory = new() { Text = "Memory usage", AutoSize = true };
    private readonly CheckBox _showGpu = new() { Text = "GPU usage", AutoSize = true };
    private readonly CheckBox _showVram = new() { Text = "Video memory", AutoSize = true };
    private readonly CheckBox _showGpuTemp = new() { Text = "GPU temperature", AutoSize = true };
    private readonly CheckBox _showBattery = new() { Text = "Battery", AutoSize = true };
    private readonly CheckBox _startWithWindows = new() { Text = "Start PerfRail when I sign in", AutoSize = true };
    private readonly Label _startupNote = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Visible = false };
    private readonly LinkLabel _startupSettingsLink = new()
    {
        Text = "Open Startup Apps settings",
        AutoSize = true,
        Visible = false,
    };

    /// <summary>
    /// Bold font for section headers, owned by this form.
    /// </summary>
    /// <remarks>
    /// Created once and disposed with the form. Every read of SystemFonts.DefaultFont
    /// allocates a NEW Font that the caller owns, unlike SystemBrushes and SystemPens
    /// which are process-wide singletons and must never be disposed.
    /// </remarks>
    private readonly Font _headerFont;

    private bool _suppressEvents;

    public SettingsForm(AppSettings settings, IStartupService startup, Action onChanged)
    {
        _settings = settings;
        _startup = startup;
        _onChanged = onChanged;

        using (Font baseFont = SystemFonts.DefaultFont)
        {
            _headerFont = new Font(baseFont, FontStyle.Bold);
        }

        Text = "PerfRail settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        BuildLayout();
        LoadFromSettings();
        WireEvents();
    }

    private void BuildLayout()
    {
        _interval.DropDownStyle = ComboBoxStyle.DropDownList;
        _interval.Width = 140;
        foreach (int ms in AppSettings.AllowedIntervalsMs)
        {
            _interval.Items.Add(FormatInterval(ms));
        }

        var intervalRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 12),
            WrapContents = false,
        };
        intervalRow.Controls.Add(new Label
        {
            Text = "Update every",
            AutoSize = true,
            Margin = new Padding(0, 6, 8, 0),
        });
        intervalRow.Controls.Add(_interval);

        var root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            WrapContents = false,
        };

        root.Controls.Add(Header("Sampling"));
        root.Controls.Add(intervalRow);

        root.Controls.Add(Header("Show on the rail"));
        root.Controls.Add(_showCpu);
        root.Controls.Add(_showMemory);
        root.Controls.Add(_showGpu);
        root.Controls.Add(_showVram);
        root.Controls.Add(_showGpuTemp);
        root.Controls.Add(_showBattery);
        root.Controls.Add(new Label
        {
            Text = string.Join(
                Environment.NewLine,
                "GPU temperature needs an NVIDIA or AMD card; the cell is hidden",
                "when no reading is available. CPU temperature is not supported:",
                "it can only be read by a kernel driver running as administrator."),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 4, 0, 12),
        });

        root.Controls.Add(Header("Startup"));
        root.Controls.Add(_startWithWindows);
        root.Controls.Add(_startupNote);
        root.Controls.Add(_startupSettingsLink);

        var close = new Button
        {
            Text = "Close",
            AutoSize = true,
            Margin = new Padding(0, 16, 0, 0),
            DialogResult = DialogResult.OK,
        };
        close.Click += (_, _) => Close();
        root.Controls.Add(close);

        AcceptButton = close;
        Controls.Add(root);
    }

    private Label Header(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = _headerFont,
        Margin = new Padding(0, 0, 0, 6),
    };

    private static string FormatInterval(int ms) =>
        ms < 1000 ? $"{ms} ms" : $"{ms / 1000} second{(ms == 1000 ? string.Empty : "s")}";

    private void LoadFromSettings()
    {
        _suppressEvents = true;

        _interval.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.AllowedIntervalsMs, _settings.UpdateIntervalMs));
        _showCpu.Checked = _settings.ShowCpu;
        _showMemory.Checked = _settings.ShowMemory;
        _showGpu.Checked = _settings.ShowGpu;
        _showVram.Checked = _settings.ShowVram;
        _showGpuTemp.Checked = _settings.ShowGpuTemperature;
        _showBattery.Checked = _settings.ShowBattery;

        _suppressEvents = false;

        RefreshStartupState();
    }

    private void WireEvents()
    {
        _interval.SelectedIndexChanged += (_, _) => Apply(s =>
            s.UpdateIntervalMs = AppSettings.AllowedIntervalsMs[_interval.SelectedIndex]);

        _showCpu.CheckedChanged += (_, _) => Apply(s => s.ShowCpu = _showCpu.Checked);
        _showMemory.CheckedChanged += (_, _) => Apply(s => s.ShowMemory = _showMemory.Checked);
        _showGpu.CheckedChanged += (_, _) => Apply(s => s.ShowGpu = _showGpu.Checked);
        _showVram.CheckedChanged += (_, _) => Apply(s => s.ShowVram = _showVram.Checked);
        _showGpuTemp.CheckedChanged += (_, _) => Apply(s => s.ShowGpuTemperature = _showGpuTemp.Checked);
        _showBattery.CheckedChanged += (_, _) => Apply(s => s.ShowBattery = _showBattery.Checked);

        _startWithWindows.CheckedChanged += OnStartWithWindowsChanged;

        _startupSettingsLink.LinkClicked += (_, _) => OpenStartupSettings();
    }

    private void Apply(Action<AppSettings> mutate)
    {
        if (_suppressEvents)
        {
            return;
        }

        mutate(_settings);
        _onChanged();
    }

    private void OnStartWithWindowsChanged(object? sender, EventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        _ = _startWithWindows.Checked ? _startup.Enable() : _startup.Disable();

        // Re-read rather than trusting the click. If the entry is vetoed in Task Manager
        // the request silently changes nothing, and an optimistic tick would be a lie.
        RefreshStartupState();
    }

    /// <summary>
    /// Sets the startup checkbox from the operating system's actual state.
    /// </summary>
    /// <remarks>
    /// Called whenever the dialog opens, not just on construction, because the user can
    /// change this in Task Manager while PerfRail is running.
    /// </remarks>
    private void RefreshStartupState()
    {
        StartupState state = _startup.GetState();

        _suppressEvents = true;
        _startWithWindows.Checked = state == StartupState.Enabled;
        _startWithWindows.Enabled = state is StartupState.Enabled or StartupState.Disabled;
        _suppressEvents = false;

        _startupNote.Text = state switch
        {
            StartupState.DisabledByUser =>
                "Turned off in Task Manager. Only you can turn it back on there.",
            StartupState.DisabledByPolicy =>
                "Startup entries are not available on this device.",
            StartupState.NotSupported =>
                "This build cannot register a startup entry.",
            _ => string.Empty,
        };

        _startupNote.Visible = _startupNote.Text.Length > 0;
        _startupSettingsLink.Visible = !_startWithWindows.Enabled;
    }

    private static void OpenStartupSettings()
    {
        try
        {
            // Works in both the packaged and unpackaged builds.
            Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Settings app unavailable; nothing useful to do about it.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Controls do not dispose the Font assigned to them, so the form has to.
            _headerFont.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);

        if (Visible)
        {
            RefreshStartupState();
        }
    }
}
