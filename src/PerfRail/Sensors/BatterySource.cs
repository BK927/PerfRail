using PerfRail.Interop;

namespace PerfRail.Sensors;

/// <summary>
/// Battery charge and mains status.
/// </summary>
/// <remarks>
/// <para>
/// One syscall, no dependency, no elevation. Worth noting given how much of this app's
/// design is shaped by what needs administrator rights: battery state is simply public
/// information that Windows hands to anyone who asks.
/// </para>
/// <para>
/// A machine with no battery reports so explicitly, and the cell disappears from the rail
/// rather than showing a placeholder - the same rule every optional metric follows.
/// </para>
/// </remarks>
internal sealed class BatterySource : ISensorSource
{
    private bool _noBattery;

    public string Name => "battery";

    /// <summary>
    /// False once Windows has said this machine has no battery.
    /// </summary>
    /// <remarks>
    /// Latched rather than re-checked: a desktop does not grow a battery, and there is no
    /// reason to keep asking every second.
    /// </remarks>
    public bool IsAvailable => !_noBattery;

    public void Contribute(ref HardwareSnapshot snapshot)
    {
        if (!Kernel32.TryGetPowerStatus(out SYSTEM_POWER_STATUS status))
        {
            return;
        }

        if ((status.BatteryFlag & Kernel32.BatteryFlagNoSystemBattery) != 0)
        {
            _noBattery = true;
            return;
        }

        // 255 means Windows cannot tell, which is different from 0% and must not be
        // rendered as an almost-flat battery.
        if (status.BatteryLifePercent > 100)
        {
            return;
        }

        snapshot = snapshot with
        {
            BatteryPercent = status.BatteryLifePercent,
            BatteryCharging = status.ACLineStatus == Kernel32.AcLineOnline,
        };
    }

    public void Dispose()
    {
    }
}
