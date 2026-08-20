#requires -Version 5.1
<#
.SYNOPSIS
    Checks PerfRail's GPU and VRAM readings against the raw performance counters.

.DESCRIPTION
    PerfRail aggregates "\GPU Engine(*)\Utilization Percentage" itself: it sums each
    engine's instances across every process, then reports the BUSIEST engine, which is
    what Task Manager shows. This script recomputes that independently with Get-Counter
    and compares.

    That aggregation is easy to get subtly wrong in a way that still looks plausible.
    Summing every engine instead of taking the maximum reads around 19% on a completely
    idle machine; filtering engine types against a whitelist drops half the engines on an
    NVIDIA card. Both would pass a glance at the screen.

.EXAMPLE
    pwsh -File tools/verify-gpu.ps1
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [int]$Samples = 8,

    # GPU load is spiky and the two samplers cover slightly different windows, so this
    # tolerance is necessarily wider than the CPU one.
    [double]$GpuTolerance = 20.0
)

$ErrorActionPreference = 'Stop'

if (-not $ExePath) {
    $root = Split-Path -Parent $PSScriptRoot
    $ExePath = Join-Path $root 'src\PerfRail\bin\Release\net10.0-windows10.0.26100.0\win-x64\PerfRail.exe'
}
if (-not (Test-Path $ExePath)) { throw "PerfRail.exe not found at $ExePath. Build first." }

$script:Fail = 0
$script:Skip = 0
function Check($name, $ok, $detail) {
    if ($ok) { Write-Host "  [PASS] $name" -ForegroundColor Green }
    else { Write-Host "  [FAIL] $name -- $detail" -ForegroundColor Red; $script:Fail++ }
}
function Skip($name, $why) {
    Write-Host "  [SKIP] $name -- $why" -ForegroundColor DarkGray; $script:Skip++
}

Write-Host "PerfRail GPU verification" -ForegroundColor Cyan
Write-Host "exe: $ExePath`n"

# ---- 1. Adapter selection ---------------------------------------------------------
Write-Host "1. adapter selection" -ForegroundColor Cyan
# PerfRail is a GUI-subsystem executable, so PowerShell's & operator does not set up a
# pipe for it and captures nothing. An explicit redirect supplies the handle.
$infoFile = Join-Path $env:TEMP 'perfrail-gpu-info.txt'
Start-Process $ExePath -ArgumentList '--gpu-info' -RedirectStandardOutput $infoFile -NoNewWindow -Wait
$info = Get-Content $infoFile
$info | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }

if ($info -match 'no DXGI adapters') {
    Skip "adapter checks" "DXGI reports no adapters on this machine"
    $selected = $null
}
else {
    $rows = @($info | Select-Object -Skip 1 | Where-Object { $_ -match '^luid_' } | ForEach-Object { , ($_ -split "`t") })
    $selectedLine = $info | Where-Object { $_ -match '^selected\t' }
    $selectedName = if ($selectedLine) { ($selectedLine -split "`t")[1] } else { '' }

    Check "at least one adapter reported" ($rows.Count -gt 0) "DXGI enumeration returned nothing"
    Check "an adapter was selected" ($selectedName -ne '') "no adapter chosen"

    $selectedRow = $rows | Where-Object { $_[4] -eq $selectedName } | Select-Object -First 1
    Check "selected adapter is not a software adapter" `
        ($selectedRow -and $selectedRow[1] -eq 'no') `
        "chose '$selectedName', which DXGI flags as a software adapter"

    $softwareRows = @($rows | Where-Object { $_[1] -eq 'yes' })
    if ($softwareRows.Count -gt 0) {
        Check "software adapters were available but skipped" `
            ($selectedRow[1] -eq 'no') "did not skip $($softwareRows.Count) software adapter(s)"
    }

    $selected = $selectedRow
}

# ---- 2. The selected LUID must exist in the counter namespace ---------------------
Write-Host "`n2. LUID joins to the counters" -ForegroundColor Cyan
if (-not $selected) {
    Skip "LUID join" "no adapter selected"
}
else {
    $luid = $selected[0]
    $memInstances = (Get-Counter '\GPU Adapter Memory(*)\Shared Usage' -ErrorAction SilentlyContinue).CounterSamples.InstanceName
    Check "selected LUID appears in GPU Adapter Memory instances" `
        ($memInstances -contains $luid.ToLowerInvariant()) `
        "built '$luid' but the counters expose: $($memInstances -join ', ')"
}

# ---- 3. Concurrent comparison -----------------------------------------------------
Write-Host "`n3. readings vs the raw counters" -ForegroundColor Cyan
$out = Join-Path $env:TEMP 'perfrail-gpu-samples.tsv'
$proc = Start-Process -FilePath $ExePath -ArgumentList "--sample $Samples" `
    -RedirectStandardOutput $out -NoNewWindow -PassThru

# Recompute Task Manager's algorithm independently: sum each engine's instances across
# processes, then take the busiest engine.
$counterMax = @()
for ($i = 0; $i -lt $Samples; $i++) {
    $s = Get-Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction SilentlyContinue
    if ($s) {
        $perEngine = $s.CounterSamples |
            Group-Object { if ($_.InstanceName -match '_phys_(\d+)_eng_(\d+)_') { "$($Matches[1])/$($Matches[2])" } else { '?' } } |
            ForEach-Object { ($_.Group | Measure-Object CookedValue -Sum).Sum }
        $counterMax += ($perEngine | Measure-Object -Maximum).Maximum
    }
    Start-Sleep -Milliseconds 900
}

$proc.WaitForExit(30000) | Out-Null
if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }

$rows = @(Get-Content $out | Select-Object -Skip 1 | Where-Object { $_ -match '\S' } |
    ForEach-Object { , ($_ -split "`t") })

$gpuVals = @($rows | ForEach-Object { $_[5] } | Where-Object { $_ -ne '' } | ForEach-Object { [double]$_ })
$vramUsed = @($rows | ForEach-Object { $_[6] } | Where-Object { $_ -ne '' } | ForEach-Object { [double]$_ })
$vramTotal = @($rows | ForEach-Object { $_[7] } | Where-Object { $_ -ne '' } | ForEach-Object { [double]$_ })

if ($gpuVals.Count -eq 0) {
    Skip "GPU comparison" "PerfRail reported no GPU values; expected on a machine with no WDDM adapter"
}
else {
    $railAvg = ($gpuVals | Measure-Object -Average).Average
    $counterAvg = ($counterMax | Measure-Object -Average).Average
    Write-Host ("  PerfRail GPU avg {0:N1}%   independent max-per-engine avg {1:N1}%" -f $railAvg, $counterAvg)

    Check "first GPU sample is blank, not a fake 0%" ($rows[0][5] -eq '') `
        "reported a rate before the counter had been primed with a second collect"
    Check "GPU stays within 0-100" (($gpuVals | Where-Object { $_ -lt 0 -or $_ -gt 100 }).Count -eq 0) `
        "values outside range: $($gpuVals -join ', ')"
    Check "GPU agrees with the independent aggregation" `
        ([Math]::Abs($railAvg - $counterAvg) -le $GpuTolerance) `
        ("PerfRail {0:N1}% vs {1:N1}%, difference {2:N1} exceeds {3}" -f `
            $railAvg, $counterAvg, [Math]::Abs($railAvg - $counterAvg), $GpuTolerance)

    # Summing every engine instead of taking the busiest inflates an idle reading.
    # If both are near zero this proves nothing, so only assert when there is signal.
    if ($counterAvg -lt 5) {
        Check "idle GPU is not inflated by summing engines" ($railAvg -lt 10) `
            ("reads {0:N1}% while the busiest engine averages {1:N1}% -- looks like a sum, not a max" -f $railAvg, $counterAvg)
    }
}

# ---- 4. Video memory ---------------------------------------------------------------
Write-Host "`n4. video memory" -ForegroundColor Cyan
if ($vramTotal.Count -eq 0) {
    Skip "VRAM checks" "PerfRail reported no video memory"
}
else {
    $total = ($vramTotal | Select-Object -First 1)
    $usedAvg = ($vramUsed | Measure-Object -Average).Average
    Write-Host ("  PerfRail VRAM {0:N2} / {1:N2} GB" -f ($usedAvg / 1GB), ($total / 1GB))

    Check "VRAM total is non-zero" ($total -gt 0) "reported 0, so a percentage would be meaningless"
    Check "VRAM used is below the total" ($usedAvg -le $total) `
        ("used {0:N2} GB exceeds total {1:N2} GB" -f ($usedAvg / 1GB), ($total / 1GB))
    Check "VRAM used is non-zero" ($usedAvg -gt 0) `
        "always 0 -- the classic symptom of measuring per-process video memory instead of the adapter's"

    if ($selected) {
        $dedicated = [double]$selected[2]
        $shared = [double]$selected[3]
        $expected = if ($dedicated -lt 512MB) { $shared } else { $dedicated }
        Check "VRAM total matches the pool DXGI reports" ($total -eq $expected) `
            ("reported {0:N0} bytes, expected {1:N0}" -f $total, $expected)

        $counterName = if ($dedicated -lt 512MB) { 'Shared Usage' } else { 'Dedicated Usage' }
        $sample = (Get-Counter "\GPU Adapter Memory($($selected[0]))\$counterName" -ErrorAction SilentlyContinue).CounterSamples.CookedValue
        if ($sample) {
            $gap = [Math]::Abs($usedAvg - $sample) / [Math]::Max($sample, 1) * 100
            Write-Host ("  counter '$counterName' now: {0:N2} GB (PerfRail avg differs by {1:N1}%)" -f ($sample / 1GB), $gap)
            Check "VRAM used tracks the counter within 30%" ($gap -le 30) `
                ("PerfRail {0:N0} vs counter {1:N0}" -f $usedAvg, $sample)
        }
    }
}

Write-Host ""
if ($script:Fail) { Write-Host "$script:Fail failed, $script:Skip skipped" -ForegroundColor Red; exit 1 }
Write-Host "all checks passed ($script:Skip skipped)" -ForegroundColor Green
