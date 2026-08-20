#requires -Version 5.1
<#
.SYNOPSIS
    Checks PerfRail's CPU and RAM readings against independent sources.

.DESCRIPTION
    PerfRail reads CPU from GetSystemTimes and memory from GlobalMemoryStatusEx. This
    script compares those against sources that share none of that code path:

      CPU  -> the "\Processor Information(_Total)\% Processor Time" performance counter
      RAM  -> Win32_OperatingSystem's TotalVisibleMemorySize / FreePhysicalMemory

    Agreement between two independent paths is evidence the formula is right. In
    particular it catches the classic GetSystemTimes mistake - forgetting that kernel
    time already includes idle time, which roughly doubles the reading on a quiet
    machine and would show up here as PerfRail reading far above the counter.

    A load phase then confirms the reading actually tracks reality rather than being a
    plausible-looking constant.

.EXAMPLE
    pwsh -File tools/verify-telemetry.ps1
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [int]$Samples = 6,

    # Two independent sampling methods over slightly different windows will not agree
    # exactly, especially on a machine that is not idle.
    [double]$CpuTolerance = 12.0,
    [double]$RamTolerancePct = 2.0
)

$ErrorActionPreference = 'Stop'

if (-not $ExePath) {
    $root = Split-Path -Parent $PSScriptRoot
    $ExePath = Join-Path $root 'src\PerfRail\bin\Release\net10.0-windows10.0.26100.0\win-x64\PerfRail.exe'
}
if (-not (Test-Path $ExePath)) { throw "PerfRail.exe not found at $ExePath. Build first." }

$script:Fail = 0
function Check($name, $ok, $detail) {
    if ($ok) { Write-Host "  [PASS] $name" -ForegroundColor Green }
    else { Write-Host "  [FAIL] $name -- $detail" -ForegroundColor Red; $script:Fail++ }
}

# Runs PerfRail's headless sampler while simultaneously reading the performance
# counter, so both cover the same wall-clock window.
function Measure-Phase([string]$Label) {
    $out = [System.IO.Path]::GetTempFileName()
    $proc = Start-Process -FilePath $ExePath -ArgumentList "--sample $Samples" `
        -RedirectStandardOutput $out -NoNewWindow -PassThru

    $counter = (Get-Counter '\Processor Information(_Total)\% Processor Time' `
        -SampleInterval 1 -MaxSamples $Samples).CounterSamples.CookedValue

    $proc.WaitForExit(30000) | Out-Null
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }

    # The leading comma matters: without it PowerShell unrolls each split result into
    # the pipeline and $rows becomes a flat list of fields, so $row[1] silently indexes
    # a character instead of a column.
    $rows = @(Get-Content $out | Select-Object -Skip 1 | Where-Object { $_ -match '\S' } |
        ForEach-Object { , ($_ -split "`t") })
    Remove-Item $out -ErrorAction SilentlyContinue

    # Field 1 is cpu_pct and is empty on the first row by design: CPU utilisation is a
    # rate and has no value until there are two samples to difference.
    $cpu = @($rows | ForEach-Object { $_[1] } | Where-Object { $_ -ne '' } | ForEach-Object { [double]$_ })
    $ramPct = @($rows | ForEach-Object { $_[2] } | Where-Object { $_ -ne '' } | ForEach-Object { [double]$_ })
    $ramUsed = @($rows | ForEach-Object { $_[3] } | Where-Object { $_ -ne '' } | ForEach-Object { [double]$_ })
    $ramTotal = @($rows | ForEach-Object { $_[4] } | Where-Object { $_ -ne '' } | ForEach-Object { [double]$_ })

    [pscustomobject]@{
        Label       = $Label
        RailCpu     = ($cpu | Measure-Object -Average).Average
        CounterCpu  = ($counter | Measure-Object -Average).Average
        RailRamPct  = ($ramPct | Measure-Object -Average).Average
        RailRamUsed = ($ramUsed | Measure-Object -Average).Average
        RailRamTotal= ($ramTotal | Measure-Object -Average).Average
        CpuCount    = $cpu.Count
        FirstCpuBlank = ($rows[0][1] -eq '')
    }
}

Write-Host "PerfRail telemetry verification" -ForegroundColor Cyan
Write-Host "exe: $ExePath`n"

# ---- Phase 1: whatever the machine is doing now ----------------------------------
Write-Host "phase 1: baseline" -ForegroundColor Cyan
$idle = Measure-Phase 'baseline'
Write-Host ("  PerfRail CPU {0:N1}%   counter {1:N1}%" -f $idle.RailCpu, $idle.CounterCpu)
Write-Host ("  PerfRail RAM {0:N1}%   {1:N2} / {2:N2} GB" -f `
    $idle.RailRamPct, ($idle.RailRamUsed / 1GB), ($idle.RailRamTotal / 1GB))

Check "first CPU sample is blank, not a fake 0%" $idle.FirstCpuBlank `
    "the sampler reported a value before it had an interval to measure"
Check "CPU agrees with the performance counter" `
    ([Math]::Abs($idle.RailCpu - $idle.CounterCpu) -le $CpuTolerance) `
    ("PerfRail {0:N1}% vs counter {1:N1}%, difference {2:N1} exceeds {3}" -f `
        $idle.RailCpu, $idle.CounterCpu, [Math]::Abs($idle.RailCpu - $idle.CounterCpu), $CpuTolerance)

# ---- RAM against an independent source -------------------------------------------
$os = Get-CimInstance Win32_OperatingSystem
$cimTotal = $os.TotalVisibleMemorySize * 1KB
$cimUsed = ($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) * 1KB
$cimPct = $cimUsed * 100.0 / $cimTotal
Write-Host ("  Win32_OperatingSystem: {0:N1}%   {1:N2} / {2:N2} GB" -f $cimPct, ($cimUsed / 1GB), ($cimTotal / 1GB))

Check "RAM percentage agrees with Win32_OperatingSystem" `
    ([Math]::Abs($idle.RailRamPct - $cimPct) -le $RamTolerancePct) `
    ("PerfRail {0:N1}% vs CIM {1:N1}%" -f $idle.RailRamPct, $cimPct)

# TotalVisibleMemorySize excludes memory the firmware reserves, so it is legitimately a
# little below ullTotalPhys. A gap beyond a few percent means the wrong field is in use.
$totalGap = [Math]::Abs($idle.RailRamTotal - $cimTotal) / $cimTotal * 100
Check "total physical memory is within 5% of CIM" ($totalGap -le 5) `
    ("PerfRail {0:N2} GB vs CIM {1:N2} GB ({2:N1}% apart)" -f `
        ($idle.RailRamTotal / 1GB), ($cimTotal / 1GB), $totalGap)

# ---- Phase 2: under load ----------------------------------------------------------
Write-Host "`nphase 2: under load" -ForegroundColor Cyan
$cores = [Math]::Max(1, [Environment]::ProcessorCount / 2)
Write-Host ("  spinning {0} of {1} logical processors" -f $cores, [Environment]::ProcessorCount)

$load = @()
for ($i = 0; $i -lt $cores; $i++) {
    $load += Start-Process -FilePath 'powershell.exe' `
        -ArgumentList '-NoProfile', '-Command', '$e=(Get-Date).AddSeconds(60); while((Get-Date) -lt $e){}' `
        -WindowStyle Hidden -PassThru
}

try {
    Start-Sleep -Seconds 2
    $busy = Measure-Phase 'load'
    Write-Host ("  PerfRail CPU {0:N1}%   counter {1:N1}%" -f $busy.RailCpu, $busy.CounterCpu)
}
finally {
    foreach ($p in $load) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
}

Check "CPU rose under load" (($busy.RailCpu - $idle.RailCpu) -ge 15) `
    ("baseline {0:N1}% -> load {1:N1}%, expected a rise of at least 15 points" -f $idle.RailCpu, $busy.RailCpu)
Check "CPU still agrees with the counter under load" `
    ([Math]::Abs($busy.RailCpu - $busy.CounterCpu) -le $CpuTolerance) `
    ("PerfRail {0:N1}% vs counter {1:N1}%" -f $busy.RailCpu, $busy.CounterCpu)
Check "CPU stays within 0-100" (($busy.RailCpu -le 100) -and ($busy.RailCpu -ge 0)) `
    ("reported {0:N1}%" -f $busy.RailCpu)

if ($script:Fail) { Write-Host "`n$script:Fail failed" -ForegroundColor Red; exit 1 }
Write-Host "`nall checks passed" -ForegroundColor Green
