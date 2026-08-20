using System.Runtime.InteropServices;

namespace PerfRail.Interop;

/// <summary>
/// Makes the diagnostic command-line modes actually print something.
/// </summary>
/// <remarks>
/// <para>
/// PerfRail is a GUI-subsystem executable, which is what keeps a console window from
/// flashing up every time it starts. The cost is that Windows gives it no standard
/// output: run <c>PerfRail.exe --sample 5</c> from a terminal and every Console.WriteLine
/// goes nowhere, silently. PowerShell cannot capture it either - only an explicit
/// <c>Start-Process -RedirectStandardOutput</c> works, because that supplies a handle.
/// </para>
/// <para>
/// AttachConsole(ATTACH_PARENT_PROCESS) borrows the console of whatever launched us, so
/// the diagnostic modes behave like an ordinary command-line tool. Order matters:
/// .NET caches its console writers on first use, so this must run before anything
/// touches Console.
/// </para>
/// </remarks>
internal static partial class ConsoleAttach
{
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const int StdOutputHandle = -11;

    private static bool _attempted;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int nStdHandle);

    /// <summary>
    /// Attaches to the parent's console, if we do not already have somewhere to write.
    /// </summary>
    /// <remarks>
    /// Does nothing when there is no parent console - launched from Explorer, for
    /// instance - and output is discarded exactly as it was before.
    /// </remarks>
    public static void EnsureAttached()
    {
        if (_attempted)
        {
            return;
        }

        _attempted = true;

        // Critical: attaching to a console REPLACES the process's standard handles. If
        // the caller already redirected stdout to a file or a pipe, attaching throws that
        // redirection away and the output lands on the console instead - so a script
        // doing `PerfRail.exe --sample 5 > out.txt` gets an empty file. Only attach when
        // there is genuinely no output handle to begin with.
        nint existing = GetStdHandle(StdOutputHandle);
        if (existing != 0 && existing != -1)
        {
            return;
        }

        if (!AttachConsole(AttachParentProcess))
        {
            return;
        }

        // The writers .NET created before the attach point at nothing, so replace them.
        // AutoFlush because a diagnostic mode that exits without flushing prints nothing,
        // which is the exact failure this class exists to fix.
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);

        var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        Console.SetError(stderr);
    }
}
