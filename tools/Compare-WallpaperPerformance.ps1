# Compare TOTAL process-family usage, not just Lively.exe vs Intonaco.exe.
# Run separately with the SAME VIDEO, resolution, display, and 30+ sec steady state.
# Start this script and press Win+D during the warmup; foreground-only auto-pause
# can otherwise make either player look artificially efficient.
#
# .\tools\Compare-WallpaperPerformance.ps1 -App Lively -OutputCsv lively.csv
# .\tools\Compare-WallpaperPerformance.ps1 -App Intonaco -OutputCsv intonaco.csv
# .\tools\Compare-WallpaperPerformance.ps1 -App AutoWall -OutputCsv autowall.csv
# .\tools\Compare-WallpaperPerformance.ps1 -App WallpaperEngine -OutputCsv engine.csv
# Pass -VideoPath with the exact same original video in EACH app, -DisplayTag
# (resolution, scaling, HDR, monitor) and -PlaybackState to make runs comparable.
# -RootProcessId identifies uncommon launchers and prevents attributing other
# unrelated mpv.exe instances to the tested app.
# Test apps separately with identical videos; do not run both wallpaper
# engines at once because one may be paused/obscured by the other.
#
# Includes named Lively helpers (e.g. Lively.Watchdog) and child processes
# such as mpv, rather than comparing only the GUI processes. GPU Video Decode,
# 3D %, and dedicated GPU memory must also be recorded from Task Manager;
# Windows GPU counters do not reliably attribute these across drivers.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Intonaco', 'Lively', 'AutoWall', 'WallpaperEngine')]
    [string]$App,
    [ValidateRange(5, 3600)]
    [int]$DurationSeconds = 30,
    [ValidateRange(0, 60)]
    [int]$WarmupSeconds = 15,
    [ValidateRange(1, 10)]
    [int]$SampleIntervalSeconds = 2,
    [string]$VideoPath,
    [string]$DisplayTag,
    [ValidateSet('Playing', 'Paused')]
    [string]$PlaybackState = 'Playing',
    [ValidateRange(0, 2147483647)]
    [int]$RootProcessId = 0,
    [switch]$IncludeGpuCounters,
    [string]$OutputCsv,
    [string]$OutputJson,
    [ValidateRange(1, 100000)]
    [double]$RamTargetMiB = 100,
    [switch]$EnforceRamTarget
)

$ErrorActionPreference = 'Stop'

function Get-AppProcessSnapshot {
    param([string]$Name, [int]$ExplicitRoot = 0)

    # Query the process family once per sample. Most playback backends use
    # out-of-process decoders; these cannot be ignored in a fair comparison.
    $items = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name)
    $ids = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($item in $items) {
        $isRoot = if ($ExplicitRoot -gt 0) {
            [int]$item.ProcessId -eq $ExplicitRoot
        } elseif ($Name -eq 'Intonaco') {
            $item.Name -ieq 'Intonaco.exe'
        } elseif ($Name -eq 'Lively') {
            $item.Name -like 'Lively*'
        } elseif ($Name -eq 'AutoWall') {
            # AutoWall can keep weebp running even when its GUI exits.
            # mpv is included only when it descends from a known root.
            $item.Name -like 'AutoWall*' -or
            $item.Name -like 'weebp*'
        } else {
            # Official Wallpaper Engine binaries: UI, video/scene player,
            # web player. Do NOT include the entire Steam process tree.
            $item.Name -in @('wallpaper32.exe', 'wallpaper64.exe',
                'webwallpaper32.exe', 'ui32.exe', 'ui64.exe')
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
        ProcessIds = @($ids | ForEach-Object { [int]$_ })
    }
}

# GPU engine counters are optional and vary by Windows version, driver and
# language. Unavailable counters remain blank, never an invented zero.
function Get-GpuSample {
    param([int[]]$FamilyPids)
    $gpu = [ordered]@{
        VideoDecode = $null
        ThreeD = $null
        DedicatedMiB = $null
        SharedMiB = $null
    }
    if ($FamilyPids.Count -eq 0) { return [pscustomobject]$gpu }
    $pidSet = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($familyPid in $FamilyPids) { [void]$pidSet.Add($familyPid) }
    try {
        $set = Get-Counter -Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction Stop
        $decode = 0.0; $threeD = 0.0
        $matched = $false
        foreach ($sample in $set.CounterSamples) {
            if ($sample.InstanceName -match 'pid_(\d+)_.*engtype_([^_]+)' -and
                $pidSet.Contains([int]$Matches[1])) {
                $matched = $true
                if ($Matches[2] -ieq 'VideoDecode') { $decode += [double]$sample.CookedValue }
                if ($Matches[2] -ieq '3D') { $threeD += [double]$sample.CookedValue }
            }
        }
        if ($matched) {
            $gpu.VideoDecode = [math]::Round($decode, 2)
            $gpu.ThreeD = [math]::Round($threeD, 2)
        }
    }
    catch { } # Counter category may not exist on this GPU/Windows locale.

    foreach ($kind in @(
        @{ Property = 'DedicatedMiB'; Counter = '\GPU Process Memory(*)\Dedicated Usage' },
        @{ Property = 'SharedMiB'; Counter = '\GPU Process Memory(*)\Shared Usage' })) {
        try {
            $set = Get-Counter -Counter $kind.Counter -ErrorAction Stop
            $total = 0.0
            $matched = $false
            foreach ($sample in $set.CounterSamples) {
                if ($sample.InstanceName -match 'pid_(\d+)' -and
                    $pidSet.Contains([int]$Matches[1])) {
                    $total += [double]$sample.CookedValue
                    $matched = $true
                }
            }
            if ($matched) {
                $gpu[$kind.Property] = [math]::Round($total / 1MB, 2)
            }
        }
        catch { }
    }
    return [pscustomobject]$gpu
}

$videoHash = ''
$videoName = ''
if ($VideoPath) {
    if (-not (Test-Path -LiteralPath $VideoPath -PathType Leaf)) {
        throw "Test video not found: $VideoPath"
    }
    $videoHash = (Get-FileHash -LiteralPath $VideoPath -Algorithm SHA256).Hash
    $videoName = [IO.Path]::GetFileName($VideoPath)
}

$machineId = "$env:COMPUTERNAME / $([Environment]::ProcessorCount) logical CPUs"
Write-Host "Preparing $App ($PlaybackState); video=$videoName; SHA256=$videoHash."
Write-Host "Press Win+D during warmup. Compare identical files and identical display/HDR conditions."
if ($RootProcessId) { Write-Host "Tracking explicit root process ID $RootProcessId and its children." }
if ($WarmupSeconds -gt 0) { Start-Sleep -Seconds $WarmupSeconds }
$previous = Get-AppProcessSnapshot -Name $App -ExplicitRoot $RootProcessId
$lastTime = Get-Date
$samples = @()
$intervalSeconds = $SampleIntervalSeconds
$iterations = [math]::Ceiling($DurationSeconds / $intervalSeconds)

for ($i = 0; $i -lt $iterations; $i++) {
    Start-Sleep -Seconds $intervalSeconds
    $now = Get-Date
    $current = Get-AppProcessSnapshot -Name $App -ExplicitRoot $RootProcessId
    $elapsed = ($now - $lastTime).TotalSeconds
    $cpuDelta = [math]::Max(0.0, $current.CpuSeconds - $previous.CpuSeconds)
    $cpuPercent = if ($elapsed -gt 0) {
        100.0 * $cpuDelta / ($elapsed * [Environment]::ProcessorCount)
    } else { 0.0 }

    $gpuSample = if ($IncludeGpuCounters) {
        Get-GpuSample -FamilyPids $current.ProcessIds
    } else { $null }
    $videoDecodePercent = $null
    $gpu3DPercent = $null
    $gpuDedicatedMB = $null
    $gpuSharedMB = $null
    if ($gpuSample) {
        $videoDecodePercent = $gpuSample.VideoDecode
        $gpu3DPercent = $gpuSample.ThreeD
        $gpuDedicatedMB = $gpuSample.DedicatedMiB
        $gpuSharedMB = $gpuSample.SharedMiB
    }
    $samples += [pscustomobject]@{
        App = $App
        Timestamp = $now.ToString('o')
        Machine = $machineId
        VideoFile = $videoName
        VideoSha256 = $videoHash
        DisplayTag = $DisplayTag
        PlaybackState = $PlaybackState
        Processes = $current.ProcessCount
        WorkingSetMB = $current.WorkingSetMB
        PrivateMemoryMB = $current.PrivateMemoryMB
        CpuPercent = [math]::Round($cpuPercent, 2)
        VideoDecodePercent = $videoDecodePercent
        Gpu3DPercent = $gpu3DPercent
        GpuDedicatedMB = $gpuDedicatedMB
        GpuSharedMB = $gpuSharedMB
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

$summary = [pscustomobject]@{
    App = $App
    PlaybackState = $PlaybackState
    Machine = $machineId
    DisplayTag = $DisplayTag
    VideoFile = $videoName
    VideoSha256 = $videoHash
    RootProcessId = $RootProcessId
    Samples = $samples.Count
    AvgWorkingSetMB = [math]::Round($ram.Average, 2)
    PeakWorkingSetMB = [math]::Round($ram.Maximum, 2)
    AvgPrivateMemoryMB = [math]::Round($private.Average, 2)
    PeakPrivateMemoryMB = [math]::Round($private.Maximum, 2)
    AvgCpuPercent = [math]::Round($cpu.Average, 2)
    PeakCpuPercent = [math]::Round($cpu.Maximum, 2)
    RamTargetMiB = $RamTargetMiB
    PeakTotalRamWithinTarget = ($ram.Maximum -lt $RamTargetMiB)
    Note = 'Includes tracked roots and descendants. GPU counters can be missing or driver-dependent; visual quality and FPS need separate checks.'
}
$summary | Format-List

if ($OutputJson) {
    [pscustomobject]@{ Summary = $summary; Samples = @($samples) } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputJson -Encoding UTF8
    Write-Host "Summary + full samples written to $OutputJson"
}

if ($EnforceRamTarget -and $ram.Maximum -ge $RamTargetMiB) {
    throw "FAIL: $App peak TOTAL working set $([math]::Round($ram.Maximum, 2)) MiB is not below $RamTargetMiB MiB. Do not claim the memory target has been met."
}
