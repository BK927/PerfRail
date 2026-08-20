using Windows.ApplicationModel;

namespace PerfRail.Services;

/// <summary>
/// Start-with-Windows for the packaged (Store) build, via windows.startupTask.
/// </summary>
/// <remarks>
/// <para>
/// The registry Run key is not an option inside a package. HKCU writes are
/// copy-on-written into a private per-app hive, so the value is written successfully and
/// reads back correctly to us, while the shell that enumerates Run at sign-in never sees
/// it. Verification code that checks its own write would report success for something
/// that silently never runs.
/// </para>
/// <para>
/// This compiles unconditionally rather than behind an #if. The project targets a single
/// OS-versioned TFM precisely so both implementations always build: a
/// configuration-dependent target framework is how a Store package ends up shipping the
/// registry branch with a startup toggle that does nothing.
/// </para>
/// <para>
/// The manifest declares the task with <c>Enabled="false"</c>. Declaring it enabled would
/// be one-way: the manifest can turn startup on, but only <c>Disable()</c> can turn it
/// back off, so the Store build would ship with startup forced on and no way to change
/// it - breaking the promise that both builds behave identically, in the direction that
/// makes the paid one worse.
/// </para>
/// </remarks>
internal sealed class MsixStartupService : IStartupService
{
    /// <summary>Must match the TaskId in Package.appxmanifest byte for byte.</summary>
    private const string TaskId = "PerfRailStartup";

    public StartupState GetState()
    {
        try
        {
            return Map(GetTask().State);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // No such task declared in the manifest.
            return StartupState.NotSupported;
        }
    }

    public StartupState Enable()
    {
        try
        {
            StartupTask task = GetTask();

            // Neither a user veto in Task Manager nor a policy block can be overridden,
            // and RequestEnableAsync would return the unchanged state anyway.
            if (task.State is StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy)
            {
                return Map(task.State);
            }

            // No consent dialog appears for a packaged desktop app; the prompt is a
            // UWP-only behaviour.
            StartupTaskState result = task.RequestEnableAsync().AsTask().GetAwaiter().GetResult();
            return Map(result);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return StartupState.NotSupported;
        }
    }

    public StartupState Disable()
    {
        try
        {
            GetTask().Disable();
            return Map(GetTask().State);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return StartupState.NotSupported;
        }
    }

    private static StartupTask GetTask() => StartupTask.GetAsync(TaskId).AsTask().GetAwaiter().GetResult();

    private static StartupState Map(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => StartupState.Enabled,
        StartupTaskState.Disabled => StartupState.Disabled,
        StartupTaskState.DisabledByUser => StartupState.DisabledByUser,

        // Also what platforms without startup-task support report, so the message shown
        // for this state has to cover both readings.
        StartupTaskState.DisabledByPolicy => StartupState.DisabledByPolicy,
        _ => StartupState.NotSupported,
    };
}
