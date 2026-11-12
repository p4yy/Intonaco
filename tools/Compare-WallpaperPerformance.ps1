# Compare TOTAL process-family usage, not just Lively.exe vs Intonaco.exe.
# Run separately with the SAME VIDEO, resolution, display, and 30+ sec steady state.
# Start this script and press Win+D during the warmup; foreground-only auto-pause
# can otherwise make either player look artificially efficient.
#
# .\tools\Compare-WallpaperPerformance.ps1 -App Lively -OutputCsv lively.csv
# .\tools\Compare-WallpaperPerformance.ps1 -App Intonaco -OutputCsv intonaco.csv
#
# Includes named Lively helpers (e.g. Lively.Watchdog) and child processes
# such as mpv, rather than comparing only the GUI processes. GPU Video Decode,
# 3D %, and dedicated GPU memory must also be recorded from Task Manager;
# Windows GPU counters do not reliably attribute these across drivers.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Intonaco', 'Lively')]
    [string]$App,
    [ValidateRange(5, 3600)]
    [int]$DurationSeconds = 30,
    [ValidateRange(0, 60)]
    [int]$WarmupSeconds = 5,
    [string]$OutputCsv
)

$ErrorActionPreference = 'Stop'

function Get-AppProcessSnapshot {
    param([string]$Name)

    # Query the process family once per sample. Most playback backends use
    # out-of-process decoders; these cannot be ignored in a fair comparison.
    $items = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name)
    $ids = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($item in $items) {
        $isRoot = if ($Name -eq 'Intonaco') {
            $item.Name -ieq 'Intonaco.exe'
        } else {
            $item.Name -like 'Lively*'
        }
        if ($isRoot) {
            [void]$ids.Add([int]$item.ProcessId)
        }
    }

    if ($ids.Count -eq 0) {
        throw "No $Name process is running. Start it before benchmarking."
    }

    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($item in $items) {
            if ($ids.Contains([int]$item.ParentProcessId) -and
                $ids.Add([int]$item.ProcessId)) {
                $changed = $true
            }
        }
    }

    $working = 0L
    $private = 0L
    $cpu = [TimeSpan]::Zero
    $active = 0

    foreach ($pidValue in $ids) {
        try {
            $process = Get-Process -Id $pidValue -ErrorAction Stop
            $process.Refresh()
            if ($process.HasExited) { continue }
            $working += $process.WorkingSet64
            $private += $process.PrivateMemorySize64
            $cpu = $cpu.Add($process.TotalProcessorTime)
            $active++
        }
        catch {
            # Short-lived helper exited between Win32_Process and Get-Process.
        }
    }

    [pscustomobject]@{
        ProcessCount = $active
        WorkingSetMB = [math]::Round($working / 1MB, 2)
        PrivateMemoryMB = [math]::Round($private / 1MB, 2)
        CpuSeconds = $cpu.TotalSeconds
    }
}

Write-Host "Preparing $App benchmark. Press Win+D during warmup to show the desktop."
if ($WarmupSeconds -gt 0) { Start-Sleep -Seconds $WarmupSeconds }
$previous = Get-AppProcessSnapshot -Name $App
$lastTime = Get-Date
$samples = @()
$intervalSeconds = 2
$iterations = [math]::Ceiling($DurationSeconds / $intervalSeconds)

for ($i = 0; $i -lt $iterations; $i++) {
    Start-Sleep -Seconds $intervalSeconds
    $now = Get-Date
    $current = Get-AppProcessSnapshot -Name $App
    $elapsed = ($now - $lastTime).TotalSeconds
    $cpuDelta = [math]::Max(0.0, $current.CpuSeconds - $previous.CpuSeconds)
    $cpuPercent = if ($elapsed -gt 0) {
        100.0 * $cpuDelta / ($elapsed * [Environment]::ProcessorCount)
    } else { 0.0 }

    $samples += [pscustomobject]@{
        App = $App
        Timestamp = $now
        Processes = $current.ProcessCount
        WorkingSetMB = $current.WorkingSetMB
        PrivateMemoryMB = $current.PrivateMemoryMB
        CpuPercent = [math]::Round($cpuPercent, 2)
    }
    $previous = $current
    $lastTime = $now
}

if ($OutputCsv) {
    $samples | Export-Csv -LiteralPath $OutputCsv -NoTypeInformation -Encoding UTF8
    Write-Host "Samples written to $OutputCsv"
}

$ram = $samples | Measure-Object WorkingSetMB -Average -Maximum
$private = $samples | Measure-Object PrivateMemoryMB -Average -Maximum
$cpu = $samples | Measure-Object CpuPercent -Average -Maximum

[pscustomobject]@{
    App = $App
    Samples = $samples.Count
    AvgWorkingSetMB = [math]::Round($ram.Average, 2)
    PeakWorkingSetMB = [math]::Round($ram.Maximum, 2)
    AvgPrivateMemoryMB = [math]::Round($private.Average, 2)
    PeakPrivateMemoryMB = [math]::Round($private.Maximum, 2)
    AvgCpuPercent = [math]::Round($cpu.Average, 2)
    PeakCpuPercent = [math]::Round($cpu.Maximum, 2)
    Note = 'Includes named helpers and child processes. Record GPU Video Decode and GPU memory separately.'
} | Format-List
