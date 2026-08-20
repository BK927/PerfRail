using Microsoft.Win32;

namespace PerfRail.Services;

/// <summary>
/// Start-with-Windows for the unpackaged build, via the per-user Run key.
/// </summary>
/// <remarks>
/// HKCU rather than HKLM: no administrator rights, and the setting belongs to the user
/// whose desktop the rail appears on rather than to the machine.
/// </remarks>
internal sealed class RegistryStartupService : IStartupService
{
    private const string ValueName = "PerfRail";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Where Task Manager records a user's veto.
    /// </summary>
    /// <remarks>
    /// Disabling a startup entry in Task Manager does NOT delete the Run value. It writes
    /// a blob here whose first byte has the low bit set when the entry is disabled. Code
    /// that only checks whether the Run value exists reports "enabled" for an entry
    /// Windows will never launch. Read-only: writing here would be overriding a choice
    /// the user made deliberately.
    /// </remarks>
    private const string ApprovedKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>
    /// The command Windows will run at sign-in.
    /// </summary>
    /// <remarks>
    /// Environment.ProcessPath, not Assembly.Location - the latter returns an empty
    /// string under single-file publish, which is how the release build ships. Quoted
    /// because the path routinely contains spaces. --autostart lets the app tell an
    /// automatic launch apart from a manual one.
    /// </remarks>
    private static string Command => $"\"{Environment.ProcessPath}\" --autostart";

    public StartupState GetState()
    {
        try
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string value || value.Length == 0)
            {
                return StartupState.Disabled;
            }

            // Self-heal a stale path, for example after the user moved the folder.
            if (!string.Equals(value, Command, StringComparison.OrdinalIgnoreCase))
            {
                Write();
            }

            return IsVetoedByUser() ? StartupState.DisabledByUser : StartupState.Enabled;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return StartupState.DisabledByPolicy;
        }
    }

    public StartupState Enable()
    {
        // Rewriting the Run value achieves nothing while the veto stands, and would make
        // the UI claim a success the user will not see at next sign-in.
        if (GetState() == StartupState.DisabledByUser)
        {
            return StartupState.DisabledByUser;
        }

        try
        {
            Write();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return StartupState.DisabledByPolicy;
        }

        return GetState();
    }

    public StartupState Disable()
    {
        try
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return StartupState.DisabledByPolicy;
        }

        return GetState();
    }

    private static void Write()
    {
        using RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        run.SetValue(ValueName, Command, RegistryValueKind.String);
    }

    private static bool IsVetoedByUser()
    {
        using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return approved?.GetValue(ValueName) is byte[] blob
            && blob.Length > 0
            && (blob[0] & 0x01) != 0;
    }
}
