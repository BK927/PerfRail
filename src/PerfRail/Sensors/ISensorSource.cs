namespace PerfRail.Sensors;

/// <summary>
/// A source of hardware readings.
/// </summary>
/// <remarks>
/// <para>
/// The seam that keeps vendor-specific machinery out of the rest of PerfRail. No type
/// from any sensor library crosses this boundary: sources fill in a
/// <see cref="HardwareSnapshot"/> and nothing else.
/// </para>
/// <para>
/// A source that cannot read something leaves it null. It must not throw, and it must
/// not substitute zero - a metric that reads 0 because a sensor failed is worse than a
/// metric that is simply absent from the rail.
/// </para>
/// </remarks>
internal interface ISensorSource : IDisposable
{
    /// <summary>Short identifier used in logs.</summary>
    string Name { get; }

    /// <summary>
    /// False when this source found nothing usable on this machine. The service keeps
    /// unavailable sources loaded but stops calling them.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Adds this source's readings to the snapshot. Called on a background thread.
    /// </summary>
    void Contribute(ref HardwareSnapshot snapshot);
}
