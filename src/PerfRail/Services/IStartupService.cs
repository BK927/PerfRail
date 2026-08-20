namespace PerfRail.Services;

/// <summary>
/// The real state of a "start with Windows" entry.
/// </summary>
/// <remarks>
/// Deliberately not a bool. Task Manager's Startup tab lets a user veto an entry without
/// removing it, and that veto cannot be overridden programmatically - Microsoft is
/// explicit that if a user disables a task, it cannot be re-enabled in code. A checkbox
/// backed by a bool would report success and change nothing.
/// </remarks>
internal enum StartupState
{
    /// <summary>Not registered. We can turn it on.</summary>
    Disabled,

    /// <summary>Registered and allowed to run.</summary>
    Enabled,

    /// <summary>Vetoed by the user in Task Manager. Only they can undo it.</summary>
    DisabledByUser,

    /// <summary>Blocked by policy, or unsupported on this platform.</summary>
    DisabledByPolicy,

    /// <summary>No mechanism available in this build.</summary>
    NotSupported,
}

/// <summary>
/// Registers PerfRail to start when the user signs in.
/// </summary>
/// <remarks>
/// Two implementations, chosen at runtime rather than at compile time. The registry Run
/// key genuinely does not work inside an MSIX package: the write succeeds and reads back
/// correctly, because the package's private hive is merged into HKCU for reads, but the
/// shell that enumerates Run at sign-in never sees it. The failure is completely silent
/// and a naive verification would report success.
/// </remarks>
internal interface IStartupService
{
    /// <summary>Reads the live state. Never cached.</summary>
    StartupState GetState();

    /// <summary>
    /// Attempts to enable, and returns the state that actually resulted.
    /// </summary>
    /// <remarks>
    /// Returns the post-attempt state rather than a success flag so the caller sets its
    /// checkbox from reality instead of from what it hoped would happen.
    /// </remarks>
    StartupState Enable();

    /// <summary>Disables, and returns the state that actually resulted.</summary>
    StartupState Disable();
}

internal static class StartupServiceFactory
{
    public static IStartupService Create() =>
        Interop.PackageIdentity.IsPackaged
            ? new MsixStartupService()
            : new RegistryStartupService();
}
