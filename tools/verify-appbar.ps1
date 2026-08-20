#requires -Version 5.1
<#
.SYNOPSIS
    Objectively verifies PerfRail's AppBar behaviour. No visual inspection involved.

.DESCRIPTION
    An AppBar either reserved screen space or it did not, and the shell will tell you
    which: GetMonitorInfo's rcWork shrinks by exactly the height of the reserved band.
    This script measures that, plus the window styles and focus behaviour that decide
    whether PerfRail is actually non-intrusive.

    Deliberately reads rcWork through GetMonitorInfo rather than
    [Windows.Forms.Screen]::PrimaryScreen.WorkingArea, which caches its value.

.EXAMPLE
    pwsh -File tools/verify-appbar.ps1
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [int]$SettleMs = 700,
    [int]$DockTimeoutMs = 20000
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class Probe
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public uint cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(int x, int y, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();

    /// DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.
    /// Without this the script is DPI-virtualized and every rectangle it reads comes back
    /// scaled, so a 30 px band on a 150% display reads as 20 and the comparisons against
    /// PerfRail (which IS per-monitor aware) happen in two different coordinate spaces.
    public static readonly IntPtr PER_MONITOR_AWARE_V2 = new IntPtr(-4);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfoW(IntPtr h, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] public static extern int GetWindowLongW(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    // CharSet.Unicode is mandatory on the W entry points. Without it the default is
    // Ansi, the string arrives as bytes the function reads as UTF-16, and the lookup
    // silently finds nothing.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);

    public delegate bool EnumProc(IntPtr h, IntPtr p);

    /// Finds the visible top-level window owned by a specific process.
    /// Preferred over FindWindow(NULL, "PerfRail"): it cannot match some unrelated
    /// window with the same caption, and it sidesteps PowerShell coercing a null class
    /// name into an empty string, which matches nothing.
    public static IntPtr VisibleWindowOf(uint targetPid, string caption)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid != targetPid || !IsWindowVisible(h)) return true;

            StringBuilder sb = new StringBuilder(256);
            GetWindowTextW(h, sb, sb.Capacity);
            if (sb.ToString() == caption) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_APPWINDOW  = 0x00040000;
    public const int WS_EX_TOPMOST    = 0x00000008;

    public static MONITORINFO PrimaryMonitor()
    {
        // MONITOR_DEFAULTTOPRIMARY for the point (0,0).
        IntPtr h = MonitorFromPoint(0, 0, 1);
        MONITORINFO mi = new MONITORINFO();
        mi.cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO));
        GetMonitorInfoW(h, ref mi);
        return mi;
    }
}
'@

function Get-Work {
    $mi = [Probe]::PrimaryMonitor()
    [pscustomobject]@{
        MonTop = $mi.rcMonitor.Top
        MonH   = $mi.rcMonitor.Bottom - $mi.rcMonitor.Top
        Top    = $mi.rcWork.Top
        H      = $mi.rcWork.Bottom - $mi.rcWork.Top
    }
}

$script:Pass = 0
$script:Fail = 0

function Check($name, $condition, $detail) {
    if ($condition) {
        Write-Host ("  [PASS] {0}" -f $name) -ForegroundColor Green
        $script:Pass++
    }
    else {
        Write-Host ("  [FAIL] {0} -- {1}" -f $name, $detail) -ForegroundColor Red
        $script:Fail++
    }
}

if (-not $ExePath) {
    $root = Split-Path -Parent $PSScriptRoot
    $ExePath = Join-Path $root 'src\PerfRail\bin\Release\net10.0-windows10.0.26100.0\win-x64\PerfRail.exe'
}
if (-not (Test-Path $ExePath)) { throw "PerfRail.exe not found at $ExePath. Build first." }

# Match PerfRail's DPI awareness before reading any rectangle.
[void][Probe]::SetProcessDpiAwarenessContext([Probe]::PER_MONITOR_AWARE_V2)
$dpi = [Probe]::GetDpiForWindow([Probe]::GetDesktopWindow())
if ($dpi -eq 0) { $dpi = 96 }
$scalePct = [Math]::Round($dpi * 100 / 96)
$expectedBand = [Math]::Max(18, [Math]::Round(20 * $dpi / 96))

Write-Host "PerfRail AppBar verification" -ForegroundColor Cyan
Write-Host ("display: {0} DPI ({1}% scaling) -> 20 DIP is {2} px" -f $dpi, $scalePct, $expectedBand)
Write-Host "exe: $ExePath`n"

# Refuse to run against a stale instance: it would hold the single-instance mutex and
# the launch below would exit silently, making every check look like a failure.
if (Get-Process PerfRail -ErrorAction SilentlyContinue) {
    throw "PerfRail is already running. Close it first."
}

$before = Get-Work
Write-Host ("baseline   rcWork top={0} height={1}  (monitor height {2})" -f $before.Top, $before.H, $before.MonH)

$foregroundBefore = [Probe]::GetForegroundWindow()

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process -FilePath $ExePath -ArgumentList '--dock' -PassThru

# Poll rather than sleeping a fixed amount. A cold .NET start plus WinForms
# initialisation is seconds on a laptop, and a fixed wait that expires early reports
# "reserved nothing" for an AppBar that simply had not registered yet.
$dockLatencyMs = -1
while ($sw.ElapsedMilliseconds -lt $DockTimeoutMs) {
    if ($proc.HasExited) { break }
    if ((Get-Work).Top -ne $before.Top) { $dockLatencyMs = $sw.ElapsedMilliseconds; break }
    Start-Sleep -Milliseconds 100
}

try {
    if ($proc.HasExited) { throw "PerfRail exited immediately with code $($proc.ExitCode)." }

    # Let the position settle past the 200 ms debounce before measuring anything.
    Start-Sleep -Milliseconds $SettleMs

    $after = Get-Work
    Write-Host ("docked     rcWork top={0} height={1}   (after {2} ms)`n" -f $after.Top, $after.H, $dockLatencyMs)

    $reserved = $after.Top - $before.Top
    $shrunk   = $before.H - $after.H

    Write-Host "AppBar reservation" -ForegroundColor Cyan
    Check "rcWork.top moved down" ($reserved -gt 0) "top unchanged at $($after.Top); the shell reserved nothing"
    Check "rcWork height shrank by the same amount" ($shrunk -eq $reserved) "top moved $reserved but height shrank $shrunk"

    Check "docked within $DockTimeoutMs ms" ($dockLatencyMs -ge 0) "never reserved anything"
    Check "reserved band is exactly 20 DIP" ($reserved -eq $expectedBand) "reserved $reserved px, expected $expectedBand px at $scalePct% scaling"

    Write-Host "`nWindow styles" -ForegroundColor Cyan
    $hwnd = [IntPtr]::Zero
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ([DateTime]::UtcNow -lt $deadline) {
        $hwnd = [Probe]::VisibleWindowOf([uint32]$proc.Id, 'PerfRail')
        if ($hwnd -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 100
    }
    Check "rail window found" ($hwnd -ne [IntPtr]::Zero) "no top-level window titled PerfRail"

    if ($hwnd -ne [IntPtr]::Zero) {
        $ex = [Probe]::GetWindowLongW($hwnd, [Probe]::GWL_EXSTYLE)
        Check "WS_EX_NOACTIVATE set"   (($ex -band [Probe]::WS_EX_NOACTIVATE) -ne 0) "window can take focus"
        Check "WS_EX_TOOLWINDOW set"   (($ex -band [Probe]::WS_EX_TOOLWINDOW) -ne 0) "would appear in ALT+TAB"
        Check "WS_EX_APPWINDOW clear"  (($ex -band [Probe]::WS_EX_APPWINDOW)  -eq 0) "forces a taskbar button, overriding NOACTIVATE"
        Check "WS_EX_TOPMOST clear"    (($ex -band [Probe]::WS_EX_TOPMOST)    -eq 0) "topmost would draw over borderless-fullscreen apps"

        $r = New-Object Probe+RECT
        [void][Probe]::GetWindowRect($hwnd, [ref]$r)
        $winH = $r.Bottom - $r.Top
        Check "window height equals reserved band" ($winH -eq $reserved) "window is ${winH}px but the band is ${reserved}px"
        Check "window sits at the reserved top" ($r.Top -eq $before.Top) "window top is $($r.Top), expected $($before.Top)"
    }

    Write-Host "`nNon-interference" -ForegroundColor Cyan
    $foregroundAfter = [Probe]::GetForegroundWindow()
    Check "did not steal foreground" ($foregroundAfter -ne $hwnd) "PerfRail took the foreground"

    Write-Host "`nIdle stability (proves the re-entrancy guard)" -ForegroundColor Cyan
    $cpu1 = (Get-Process -Id $proc.Id).TotalProcessorTime
    $w1 = Get-Work
    Start-Sleep -Seconds 10
    $cpu2 = (Get-Process -Id $proc.Id).TotalProcessorTime
    $w2 = Get-Work
    $cpuMs = ($cpu2 - $cpu1).TotalMilliseconds

    # The budget is a share of the whole machine, so normalise by logical processor
    # count rather than comparing raw milliseconds - the same absolute figure means
    # very different things on 4 cores and on 32.
    $cpuPct = $cpuMs / (10 * 1000 * [Environment]::ProcessorCount) * 100
    Write-Host ("  CPU over 10 s idle: {0:N0} ms = {1:N3}% of {2} logical processors" -f `
        $cpuMs, $cpuPct, [Environment]::ProcessorCount)

    Check "rcWork stable while idle" ($w1.Top -eq $w2.Top -and $w1.H -eq $w2.H) "band moved from top=$($w1.Top) to top=$($w2.Top) with no input: feedback loop"
    Check "idle CPU under the 0.5% budget" ($cpuPct -lt 0.5) ("used {0:N3}%" -f $cpuPct)

    # A runaway ABN_POSCHANGED loop would show up as CPU far above what one repaint per
    # second can account for, so keep a separate ceiling well below the budget.
    Check "no sign of a reposition loop" ($cpuPct -lt 0.35) ("used {0:N3}%, higher than 1 Hz repainting explains" -f $cpuPct)

    $ws = [Math]::Round((Get-Process -Id $proc.Id).WorkingSet64 / 1MB, 1)
    $pb = [Math]::Round((Get-Process -Id $proc.Id).PrivateMemorySize64 / 1MB, 1)
    Write-Host ("  working set {0} MB, private bytes {1} MB" -f $ws, $pb)
    Check "private bytes under 100 MB" ($pb -lt 100) "$pb MB exceeds the budget"
}
finally {
    Write-Host "`nShutdown" -ForegroundColor Cyan

    if (-not $proc.HasExited) {
        # Graceful close first: this is the path that must call ABM_REMOVE.
        [void]$proc.CloseMainWindow()
        Start-Sleep -Milliseconds 1200
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; Start-Sleep -Milliseconds 800 }
    }

    $restored = Get-Work
    Write-Host ("restored   rcWork top={0} height={1}" -f $restored.Top, $restored.H)
    Check "work area fully restored" ($restored.Top -eq $before.Top -and $restored.H -eq $before.H) `
        "leaked reservation: top=$($restored.Top) height=$($restored.H), expected top=$($before.Top) height=$($before.H)"
}

Write-Host ("`n{0} passed, {1} failed" -f $script:Pass, $script:Fail) -ForegroundColor $(if ($script:Fail) { 'Red' } else { 'Green' })
if ($script:Fail) { exit 1 }
