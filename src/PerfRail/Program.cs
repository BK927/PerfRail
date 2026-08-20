using System.Diagnostics;
using System.Runtime.InteropServices;
using PerfRail.AppBar;
using PerfRail.Interop;
using PerfRail.Services;

namespace PerfRail;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Headless diagnostics: no window, no tray icon, no AppBar registration, and no
        // single-instance claim, so it can run alongside a docked instance.
        if (TryGetSampleCount(args, out int sampleCount))
        {
            DiagnosticSampler.Run(sampleCount, TimeSpan.FromSeconds(1));
            return;
        }

        // Every mode below writes to stdout, and a GUI-subsystem process has none until
        // it borrows the caller's console. Must happen before anything touches Console.
        if (args.Length > 0)
        {
            ConsoleAttach.EnsureAttached();
        }

        if (args.Any(a => string.Equals(a, "--gpu-info", StringComparison.OrdinalIgnoreCase)))
        {
            DiagnosticSampler.PrintGpuInfo();
            return;
        }

        // Startup registration from the command line. Headless like --sample: useful
        // for scripting and for checking what Windows actually thinks the state is,
        // which is not always what PerfRail last asked for.
        if (TryHandleStartupCommand(args))
        {
            return;
        }

        using SingleInstance? instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            // Already running. Exit quietly rather than showing a dialog, and above all
            // without registering a second AppBar.
            return;
        }

        ApplicationConfiguration.Initialize();

        if (!VerifyEnvironment())
        {
            return;
        }

        // --dock forces the rail on regardless of the saved setting, for the
        // verification scripts. Without it the saved preference decides, which is what
        // an --autostart launch relies on.
        bool forceDock = args.Any(a => string.Equals(a, "--dock", StringComparison.OrdinalIgnoreCase));

        // Lets a shortcut go straight to settings instead of making the user find the
        // tray icon first.
        bool openSettings = args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase));

        Application.Run(new RailContext(forceDock, openSettings));
    }

    /// <summary>
    /// Handles <c>--startup-status</c>, <c>--startup-enable</c> and
    /// <c>--startup-disable</c>. Returns true when one of them ran.
    /// </summary>
    /// <remarks>
    /// Every branch reports the state the operating system ends up in, not the action
    /// requested. Enabling cannot override a veto set in Task Manager, so "enabled" and
    /// "we asked for enabled" are different facts.
    /// </remarks>
    private static bool TryHandleStartupCommand(string[] args)
    {
        IStartupService startup = StartupServiceFactory.Create();

        foreach (string arg in args)
        {
            StartupState? state = arg.ToLowerInvariant() switch
            {
                "--startup-status" => startup.GetState(),
                "--startup-enable" => startup.Enable(),
                "--startup-disable" => startup.Disable(),
                _ => null,
            };

            if (state is { } resulting)
            {
                Console.WriteLine(resulting);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses <c>--sample [count]</c>. Defaults to 5 samples when no count follows.
    /// </summary>
    private static bool TryGetSampleCount(string[] args, out int count)
    {
        count = 0;

        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--sample", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsed) && parsed > 0)
            {
                count = parsed;
            }
            else
            {
                count = 5;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Verifies the two runtime assumptions the whole AppBar design rests on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DPI awareness is checked rather than trusted. The manifest declares PerMonitorV2
    /// and the SDK property agrees, but the manifest only reaches the executable through
    /// an apphost PE-resource copy that has known SDK failure modes, launching via
    /// <c>dotnet PerfRail.dll</c> picks up dotnet.exe's manifest instead, and a per-user
    /// compatibility override under AppCompatFlags\Layers outranks all of it and needs no
    /// admin rights to set. Under anything less than PerMonitorV2, every rectangle handed
    /// to SHAppBarMessage is virtualized and the bar lands wrong on any scaled monitor.
    /// </para>
    /// <para>
    /// The struct size check catches a Pack=1 APPBARDATA, which marshals without error
    /// and corrupts every field.
    /// </para>
    /// </remarks>
    private static bool VerifyEnvironment()
    {
        int actualSize = Marshal.SizeOf<APPBARDATA>();
        if (actualSize != AppBarInterop.ExpectedAppBarDataSize)
        {
            Fail(
                $"APPBARDATA marshals to {actualSize} bytes but must be "
                + $"{AppBarInterop.ExpectedAppBarDataSize} on x64.\n\n"
                + "The AppBar API would silently misbehave, so PerfRail will not start.");
            return false;
        }

        if (Application.HighDpiMode != HighDpiMode.PerMonitorV2)
        {
            Fail(
                $"PerfRail requires per-monitor DPI awareness but is running as "
                + $"{Application.HighDpiMode}.\n\n"
                + "This usually means a compatibility override is set for PerfRail.exe, or "
                + "the app was launched through dotnet.exe instead of PerfRail.exe.\n\n"
                + "Positioning would be wrong on any display that is not at 100% scaling, "
                + "so PerfRail will not start.");
            return false;
        }

        Debug.WriteLine($"[PerfRail] HighDpiMode={Application.HighDpiMode}, APPBARDATA={actualSize} bytes");
        return true;
    }

    private static void Fail(string message) =>
        MessageBox.Show(message, "PerfRail", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
