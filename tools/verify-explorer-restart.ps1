#requires -Version 5.1
<#
.SYNOPSIS
    Verifies PerfRail survives an Explorer restart and re-reserves its band.

.DESCRIPTION
    AppBar registrations live inside explorer.exe. When Explorer dies, every
    registration dies with it, and the shell broadcasts "TaskbarCreated" once the new
    instance is ready. An AppBar that does not listen for that broadcast keeps painting
    a bar that reserves nothing, and windows start opening underneath it.

    DISRUPTIVE: this kills explorer.exe. Open File Explorer windows will close and the
    taskbar disappears for a few seconds. Windows restarts the shell automatically.

.EXAMPLE
    pwsh -File tools/verify-explorer-restart.ps1
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [int]$RecoverTimeoutMs = 30000
)

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class Work
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public uint cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(int x, int y, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfoW(IntPtr h, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

    public static readonly IntPtr PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    public static int WorkTop()
    {
        MONITORINFO mi = new MONITORINFO();
        mi.cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO));
        GetMonitorInfoW(MonitorFromPoint(0, 0, 1), ref mi);
        return mi.rcWork.Top;
    }
}
'@

[void][Work]::SetProcessDpiAwarenessContext([Work]::PER_MONITOR_AWARE_V2)

if (-not $ExePath) {
    $root = Split-Path -Parent $PSScriptRoot
    $ExePath = Join-Path $root 'src\PerfRail\bin\Release\net10.0-windows10.0.26100.0\win-x64\PerfRail.exe'
}
if (-not (Test-Path $ExePath)) { throw "PerfRail.exe not found at $ExePath. Build first." }
if (Get-Process PerfRail -ErrorAction SilentlyContinue) { throw "PerfRail is already running. Close it first." }

function Wait-For([scriptblock]$Condition, [int]$TimeoutMs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if (& $Condition) { return [int]$sw.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 200
    }
    return -1
}

$fail = 0
function Check($name, $ok, $detail) {
    if ($ok) { Write-Host "  [PASS] $name" -ForegroundColor Green }
    else { Write-Host "  [FAIL] $name -- $detail" -ForegroundColor Red; $script:fail++ }
}

Write-Host "PerfRail Explorer-restart resilience" -ForegroundColor Cyan
Write-Host "WARNING: this kills explorer.exe. Open File Explorer windows will close.`n" -ForegroundColor Yellow

$baseTop = [Work]::WorkTop()
$proc = Start-Process -FilePath $ExePath -ArgumentList '--dock' -PassThru

$dockMs = Wait-For { [Work]::WorkTop() -ne $baseTop } 20000
Check "docked before the restart" ($dockMs -ge 0) "never reserved anything"
if ($dockMs -lt 0) { Stop-Process -Id $proc.Id -Force; exit 1 }

$dockedTop = [Work]::WorkTop()
Write-Host ("  reserved: rcWork.top = {0} (was {1}), after {2} ms`n" -f $dockedTop, $baseTop, $dockMs)

try {
    Write-Host "killing explorer.exe ..." -ForegroundColor Yellow
    Stop-Process -Name explorer -Force

    # Explorer's death alone should drop the reservation.
    $dropped = Wait-For { [Work]::WorkTop() -eq $baseTop } 10000
    Write-Host ("  reservation dropped with the shell after {0} ms" -f $dropped)

    $shellBack = Wait-For { Get-Process explorer -ErrorAction SilentlyContinue } 30000
    if ($shellBack -lt 0) {
        Write-Host "  explorer did not restart on its own; starting it" -ForegroundColor Yellow
        Start-Process explorer.exe
        $shellBack = Wait-For { Get-Process explorer -ErrorAction SilentlyContinue } 30000
    }
    Check "explorer restarted" ($shellBack -ge 0) "shell never came back"

    Check "PerfRail survived" (-not $proc.HasExited) "process exited when the shell died"

    $recovered = Wait-For { [Work]::WorkTop() -eq $dockedTop } $RecoverTimeoutMs
    Check "re-reserved the band after the restart" ($recovered -ge 0) `
        "rcWork.top is $([Work]::WorkTop()), expected $dockedTop -- the rail is painting over a band it no longer owns"

    if ($recovered -ge 0) { Write-Host ("  recovered {0} ms after the shell returned" -f $recovered) }
}
finally {
    Write-Host "`ncleanup" -ForegroundColor Cyan
    if (-not $proc.HasExited) {
        [void]$proc.CloseMainWindow()
        Start-Sleep -Milliseconds 1200
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    }
    Start-Sleep -Milliseconds 800
    Check "work area restored on exit" ([Work]::WorkTop() -eq $baseTop) "leaked: rcWork.top = $([Work]::WorkTop()), expected $baseTop"
}

if ($fail) { Write-Host "`n$fail failed" -ForegroundColor Red; exit 1 }
Write-Host "`nall checks passed" -ForegroundColor Green
