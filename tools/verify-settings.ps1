#requires -Version 5.1
<#
.SYNOPSIS
    Verifies settings persistence, corruption recovery, single-instance and startup state.

.DESCRIPTION
    Backs up any real settings file first and restores it at the end, so running this
    never costs you your own configuration.

    The startup ENABLE/DISABLE cycle is opt-in via -TestStartupWrite because it briefly
    registers PerfRail to launch at sign-in, which is a persistent change to your user
    profile. The read path is always checked.

.EXAMPLE
    pwsh -File tools/verify-settings.ps1
    pwsh -File tools/verify-settings.ps1 -TestStartupWrite
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [switch]$TestStartupWrite
)

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Wk {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct MI { public uint cb; public RECT mon; public RECT work; public uint f; }
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(int x, int y, uint f);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfoW(IntPtr h, ref MI mi);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    public static readonly IntPtr PMv2 = new IntPtr(-4);
    public static int Top() {
        MI mi = new MI(); mi.cb = (uint)Marshal.SizeOf(typeof(MI));
        GetMonitorInfoW(MonitorFromPoint(0, 0, 1), ref mi); return mi.work.T;
    }
}
'@
[void][Wk]::SetProcessDpiAwarenessContext([Wk]::PMv2)

if (-not $ExePath) {
    $root = Split-Path -Parent $PSScriptRoot
    $ExePath = Join-Path $root 'src\PerfRail\bin\Release\net10.0-windows10.0.26100.0\win-x64\PerfRail.exe'
}
if (-not (Test-Path $ExePath)) { throw "PerfRail.exe not found at $ExePath. Build first." }
if (Get-Process PerfRail -ErrorAction SilentlyContinue) { throw "PerfRail is already running. Close it first." }

$dir = Join-Path $env:LOCALAPPDATA 'PerfRail'
$cfg = Join-Path $dir 'settings.json'
$backup = Join-Path $env:TEMP ("perfrail-settings-backup-" + [Guid]::NewGuid().ToString('N') + ".json")

$script:Fail = 0
function Check($name, $ok, $detail) {
    if ($ok) { Write-Host "  [PASS] $name" -ForegroundColor Green }
    else { Write-Host "  [FAIL] $name -- $detail" -ForegroundColor Red; $script:Fail++ }
}

# Starts PerfRail, waits for it to settle, and reports whether it reserved a band.
function Test-Launch([string]$ExtraArgs = '') {
    $baseTop = [Wk]::Top()
    $p = if ($ExtraArgs) { Start-Process $ExePath -ArgumentList $ExtraArgs -PassThru }
         else { Start-Process $ExePath -PassThru }

    $docked = $false
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 8000) {
        if ($p.HasExited) { break }
        if ([Wk]::Top() -ne $baseTop) { $docked = $true; break }
        Start-Sleep -Milliseconds 150
    }
    Start-Sleep -Milliseconds 600

    [pscustomobject]@{ Process = $p; Docked = $docked; Exited = $p.HasExited }
}

function Stop-App($p) {
    if ($p -and -not $p.HasExited) {
        # The real shutdown path, not a kill: it is what releases the reserved band.
        Start-Process $ExePath -ArgumentList '--quit' -NoNewWindow -Wait
        [void]$p.WaitForExit(6000)
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    }
    Start-Sleep -Milliseconds 600
}

Write-Host "PerfRail settings and lifecycle verification" -ForegroundColor Cyan
Write-Host "config: $cfg`n"

if (Test-Path $cfg) {
    Copy-Item $cfg $backup -Force
    Write-Host "backed up your existing settings to $backup`n" -ForegroundColor Yellow
}

try {
    # ---- 1. First run with no config: docking must be off ------------------------
    Write-Host "1. first run, no config file" -ForegroundColor Cyan
    if (Test-Path $cfg) { Remove-Item $cfg -Force }
    $r = Test-Launch
    Check "starts without a config file" (-not $r.Exited) "process exited immediately"
    Check "rail is NOT docked by default" (-not $r.Docked) `
        "reserved screen space without being asked -- Store policy 10.2.8 requires consent first"
    Stop-App $r.Process

    # ---- 2. Saved preference is honoured ----------------------------------------
    Write-Host "`n2. saved preference" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    '{"version":1,"docked":true,"updateIntervalMs":1000,"barHeightDip":20,"barEdge":1,"showCpu":true,"showMemory":true,"showGpu":true,"showVram":true,"showCpuTemperature":false,"showGpuTemperature":false}' |
        Set-Content $cfg -Encoding UTF8
    $r = Test-Launch
    Check "docks when the saved preference says so" $r.Docked "ignored docked=true in settings.json"
    Stop-App $r.Process

    # ---- 3. --dock must not rewrite the user's preference ------------------------
    Write-Host "`n3. --dock is transient" -ForegroundColor Cyan
    '{"version":1,"docked":false,"updateIntervalMs":2000}' | Set-Content $cfg -Encoding UTF8
    $r = Test-Launch '--dock'
    Check "--dock docks the rail" $r.Docked "did not dock"
    Stop-App $r.Process
    $after = Get-Content $cfg -Raw | ConvertFrom-Json
    Check "--dock did not overwrite docked=false" ($after.docked -eq $false) `
        "a diagnostic flag rewrote the user's saved preference"
    Check "unrelated settings survived" ($after.updateIntervalMs -eq 2000) `
        "updateIntervalMs became $($after.updateIntervalMs), expected 2000"

    # ---- 4. Corrupt config must not stop the app --------------------------------
    Write-Host "`n4. corrupt config file" -ForegroundColor Cyan
    $corruptSide = "$cfg.corrupt"
    if (Test-Path $corruptSide) { Remove-Item $corruptSide -Force }
    '{ this is not valid json at all' | Set-Content $cfg -Encoding UTF8
    $r = Test-Launch
    Check "starts despite a malformed config" (-not $r.Exited) "process exited -- a bad config file must never be fatal"
    Check "falls back to defaults (not docked)" (-not $r.Docked) "docked with an unreadable config"
    Stop-App $r.Process
    Check "bad file kept aside as .corrupt" (Test-Path $corruptSide) `
        "the unreadable file was discarded instead of preserved for diagnosis"
    Check "a valid config was written in its place" `
        ((Test-Path $cfg) -and ((Get-Content $cfg -Raw | ConvertFrom-Json) -ne $null)) `
        "no readable config after recovery"
    if (Test-Path $corruptSide) { Remove-Item $corruptSide -Force }

    # ---- 5. Single instance ------------------------------------------------------
    Write-Host "`n5. single instance" -ForegroundColor Cyan
    $first = Start-Process $ExePath -PassThru
    Start-Sleep -Seconds 3
    $second = Start-Process $ExePath -PassThru
    Start-Sleep -Seconds 3
    Check "second launch exits on its own" $second.HasExited "a second instance stayed running"
    Check "second launch exits quietly (code 0)" `
        ($second.HasExited -and $second.ExitCode -eq 0) `
        "exit code $(if ($second.HasExited) { $second.ExitCode } else { 'n/a' })"
    Check "first instance is unaffected" (-not $first.HasExited) "the original instance died"
    Check "only one PerfRail process remains" `
        (@(Get-Process PerfRail -ErrorAction SilentlyContinue).Count -eq 1) "more than one process alive"
    Stop-App $first
    Stop-App $second

    # ---- 6. Startup state --------------------------------------------------------
    Write-Host "`n6. start with Windows" -ForegroundColor Cyan
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $existing = (Get-ItemProperty -Path $runKey -Name PerfRail -ErrorAction SilentlyContinue).PerfRail
    Check "not registered at startup by default" ($null -eq $existing) `
        "found a Run entry PerfRail never asked to create: $existing"

    if ($TestStartupWrite) {
        Write-Host "  running the write cycle (will be reverted)" -ForegroundColor Yellow
        $p = Start-Process $ExePath -ArgumentList '--startup-enable' -PassThru -Wait
        $val = (Get-ItemProperty -Path $runKey -Name PerfRail -ErrorAction SilentlyContinue).PerfRail
        Check "Enable() writes a Run value" ($null -ne $val) "no Run value after enabling"
        Check "Run value points at PerfRail.exe with --autostart" `
            ($val -like '*PerfRail.exe" --autostart') "value was: $val"

        Start-Process $ExePath -ArgumentList '--startup-disable' -PassThru -Wait | Out-Null
        $val2 = (Get-ItemProperty -Path $runKey -Name PerfRail -ErrorAction SilentlyContinue).PerfRail
        Check "Disable() removes the Run value" ($null -eq $val2) "left behind: $val2"
    }
    else {
        Write-Host "  (skipping the enable/disable cycle; pass -TestStartupWrite to run it)" -ForegroundColor DarkGray
    }
}
finally {
    Get-Process PerfRail -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500

    if (Test-Path $backup) {
        Copy-Item $backup $cfg -Force
        Remove-Item $backup -Force
        Write-Host "`nrestored your original settings" -ForegroundColor Yellow
    }
    elseif (Test-Path $cfg) {
        Remove-Item $cfg -Force
    }

    $finalTop = [Wk]::Top()
    Check "work area not left reserved" ($finalTop -eq 0) "rcWork.top is $finalTop"
}

if ($script:Fail) { Write-Host "`n$script:Fail failed" -ForegroundColor Red; exit 1 }
Write-Host "`nall checks passed" -ForegroundColor Green
