using System.Diagnostics;
using System.Runtime.InteropServices;
using PerfRail.AppBar;
using PerfRail.Services;

namespace PerfRail;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
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

        // --dock docks the rail immediately instead of waiting for the tray toggle.
        // Used by the verification scripts, and the hook the future --autostart path
        // will reuse once the setting is persisted.
        bool dockOnStart = args.Any(a => string.Equals(a, "--dock", StringComparison.OrdinalIgnoreCase));

        Application.Run(new RailContext(dockOnStart));
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
