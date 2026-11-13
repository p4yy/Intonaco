# Compare controlled Intonaco, Lively, AutoWall, Wallpaper Engine runs.
# A matching source SHA-256, playback state, machine and display are mandatory.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateCount(2, 8)]
    [string[]]$InputCsv,
    [string]$OutputMarkdown = 'wallpaper-performance-comparison.md',
    [ValidateRange(2, 1000)]
    [int]$MinSamples = 15,
    [ValidateRange(0, 3600)]
    [int]$MinDurationSeconds = 60
)
$ErrorActionPreference = 'Stop'
$cult = [System.Globalization.CultureInfo]::InvariantCulture
function Read-Number {
    param([object]$Value)
    $n = 0.0
    if ([string]::IsNullOrWhiteSpace([string]$Value) -or
        -not [double]::TryParse([string]$Value,
            [System.Globalization.NumberStyles]::Float,
            $cult, [ref]$n)) {
        return $null
    }
    return $n
}

$groups = @()
foreach ($path in $InputCsv) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Benchmark CSV missing: $path"
    }
    $samples = @(Import-Csv -LiteralPath $path)
    if ($samples.Count -lt $MinSamples) {
        throw "Insufficient samples in $path ($($samples.Count) < $MinSamples)."
    }
    $first = $samples[0]
    foreach ($field in @('App', 'Machine', 'VideoSha256',
        'DisplayTag', 'PlaybackState', 'WorkingSetMB',
        'PrivateMemoryMB', 'CpuPercent', 'Timestamp')) {
        if (-not ($first.PSObject.Properties.Name -contains $field)) {
            throw "CSV $path is missing required field $field. Re-run the latest benchmark."
        }
    }
    foreach ($field in @('Machine', 'VideoSha256', 'DisplayTag', 'PlaybackState')) {
        if ([string]::IsNullOrWhiteSpace([string]$first.$field)) {
            throw "CSV $path missing $field. Supply -VideoPath and -DisplayTag when recording."
        }
    }
    if ($first.VideoSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
        throw "Invalid source video fingerprint in $path."
    }

    $values = @()
    $privates = @()
    $cpus = @()
    $gpuDecode = @()
    $gpu3D = @()
    $gpuDedicated = @()
    $gpuShared = @()
    foreach ($sample in $samples) {
        foreach ($field in @('App', 'Machine', 'VideoSha256',
            'DisplayTag', 'PlaybackState')) {
            if ([string]$sample.$field -cne [string]$first.$field) {
                throw "CSV $path contains mixed $field values."
            }
        }
        $ram = Read-Number $sample.WorkingSetMB
        $private = Read-Number $sample.PrivateMemoryMB
        $cpu = Read-Number $sample.CpuPercent
        if ($null -eq $ram -or $null -eq $private -or $null -eq $cpu) {
            throw "Invalid RAM/CPU values in $path."
        }
        $values += $ram; $privates += $private; $cpus += $cpu

        $g = Read-Number $sample.VideoDecodePercent
        if ($null -ne $g) { $gpuDecode += $g }
        $g = Read-Number $sample.Gpu3DPercent
        if ($null -ne $g) { $gpu3D += $g }
        $g = Read-Number $sample.GpuDedicatedMB
        if ($null -ne $g) { $gpuDedicated += $g }
        $g = Read-Number $sample.GpuSharedMB
        if ($null -ne $g) { $gpuShared += $g }
    }

    $start = [DateTimeOffset]::Parse($samples[0].Timestamp, $cult)
    $end = [DateTimeOffset]::Parse($samples[-1].Timestamp, $cult)
    $duration = ($end - $start).TotalSeconds
    if ($duration -lt $MinDurationSeconds) {
        throw "Benchmark $path ran only $([math]::Round($duration, 1)) seconds; required at least $MinDurationSeconds."
    }
    $r = $values | Measure-Object -Average -Maximum
    $p = $privates | Measure-Object -Average -Maximum
    $c = $cpus | Measure-Object -Average -Maximum
    $ordered = @($values | Sort-Object)
    $p95Index = [int][math]::Max(0, [math]::Ceiling($ordered.Count * 0.95) - 1)

    $avgDecode = $null
    $avg3D = $null
    $peakDedicated = $null
    $peakShared = $null
    if ($gpuDecode.Count -eq $samples.Count) {
        $avgDecode = [math]::Round(($gpuDecode | Measure-Object -Average).Average, 2)
    }
    if ($gpu3D.Count -eq $samples.Count) {
        $avg3D = [math]::Round(($gpu3D | Measure-Object -Average).Average, 2)
    }
    if ($gpuDedicated.Count -eq $samples.Count) {
        $peakDedicated = [math]::Round(($gpuDedicated | Measure-Object -Maximum).Maximum, 2)
    }
    if ($gpuShared.Count -eq $samples.Count) {
        $peakShared = [math]::Round(($gpuShared | Measure-Object -Maximum).Maximum, 2)
    }
    $groups += [pscustomobject]@{
        App = $first.App
        Machine = $first.Machine
        VideoSha256 = $first.VideoSha256
        VideoFile = $first.VideoFile
        DisplayTag = $first.DisplayTag
        PlaybackState = $first.PlaybackState
        Samples = $samples.Count
        Seconds = $duration
        AvgRam = [math]::Round($r.Average, 2)
        P95Ram = [math]::Round([double]$ordered[$p95Index], 2)
        PeakRam = [math]::Round($r.Maximum, 2)
        AvgPrivate = [math]::Round($p.Average, 2)
        PeakPrivate = [math]::Round($p.Maximum, 2)
        AvgCpu = [math]::Round($c.Average, 2)
        PeakCpu = [math]::Round($c.Maximum, 2)
        AvgVideoDecode = $avgDecode
        AvgGpu3D = $avg3D
        PeakDedicatedGpu = $peakDedicated
        PeakSharedGpu = $peakShared
    }
}

$reference = $groups[0]
foreach ($g in $groups) {
    foreach ($field in @('Machine', 'VideoSha256', 'DisplayTag', 'PlaybackState')) {
        if ([string]$g.$field -cne [string]$reference.$field) {
            throw "UNFAIR COMPARISON: $($reference.App) and $($g.App) have different $field. No ranking generated."
        }
    }
}
if (@($groups.App | Select-Object -Unique).Count -ne $groups.Count) {
    throw "Duplicate app results. Supply one complete run per app for a direct comparison."
}

$ranked = @($groups | Sort-Object AvgRam, App)
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# Live-wallpaper alternatives — controlled performance comparison')
$lines.Add('')
$lines.Add("- **Video:** $($reference.VideoFile)")
$lines.Add("- **SHA-256:** $($reference.VideoSha256)")
$lines.Add("- **Machine:** $($reference.Machine)")
$lines.Add("- **Display / HDR / scale:** $($reference.DisplayTag)")
$lines.Add("- **Playback state:** $($reference.PlaybackState)")
$lines.Add("- **Lowest average process-family working set:** **$($ranked[0].App)** ($($ranked[0].AvgRam) MiB). This is NOT a claim of best overall quality or FPS.")
$lines.Add('')
$lines.Add('| App | Avg RAM (MiB) | P95 RAM | Peak RAM | Avg private (MiB) | Peak private | Avg CPU % | Peak GPU dedicated (MiB) | GPU decode % | Samples |')
$lines.Add('|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|')
foreach ($g in $ranked) {
    $gpu = if ($null -eq $g.PeakDedicatedGpu) { 'N/A' } else { [string]$g.PeakDedicatedGpu }
    $decode = if ($null -eq $g.AvgVideoDecode) { 'N/A' } else { [string]$g.AvgVideoDecode }
    $lines.Add("| $($g.App) | $($g.AvgRam) | $($g.P95Ram) | $($g.PeakRam) | $($g.AvgPrivate) | $($g.PeakPrivate) | $($g.AvgCpu) | $gpu | $decode | $($g.Samples) |")
}
$lines.Add('')
$lines.Add('**Interpretation limits:** A RAM winner is not automatically the smoothest or most color-accurate wallpaper app. Independently verify original resolution, frame presentation, HDR/SDR and color fidelity, glitches, UI responsiveness and long-term stability on the same GPU. GPU counters are optional and driver-specific; missing GPU readings are N/A rather than 0%. Working-set RAM and private committed memory are distinct from GPU residency. Process trees may miss orphaned helpers after a launcher exits; verify the included processes manually.')
$lines.Add('')
$lines.Add('**Method:** One wallpaper engine at a time, identical SHA-256 source, machine, display and playback state. Sampler includes selected process roots and descendants, not unrelated Steam or MPV instances. Run at least 10 minutes and repeat trials before marketing superlative claims.')

$lines | Set-Content -LiteralPath $OutputMarkdown -Encoding UTF8
Write-Host "Controlled comparison generated: $OutputMarkdown"
Write-Host "Lowest average process-family RAM: $($ranked[0].App) $($ranked[0].AvgRam) MiB"
$ranked | Select-Object App, AvgRam, P95Ram, PeakRam, AvgPrivate, AvgCpu, AvgVideoDecode, PeakDedicatedGpu | Format-Table -AutoSize
