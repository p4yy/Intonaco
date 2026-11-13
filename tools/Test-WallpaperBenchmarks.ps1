# CI tests; no wallpaper application or video playback is needed.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:TEMP ("IntonacoBenchTest-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir | Out-Null
try {
    $hash = 'A' * 64
    $files = @()
    $start = [DateTimeOffset]::UtcNow.AddMinutes(-5)
    $index = 0
    foreach ($app in @('Intonaco', 'Lively', 'AutoWall', 'WallpaperEngine')) {
        $samples = @()
        for ($i = 0; $i -lt 4; $i++) {
            $samples += [pscustomobject]@{
                App = $app
                Timestamp = $start.AddSeconds(10 * $i).ToString('o')
                Machine = 'CI synthetic PC / 16 logical CPUs'
                VideoFile = 'identical-video.mp4'
                VideoSha256 = $hash
                DisplayTag = '1920x1200 SDR 100%'
                PlaybackState = 'Playing'
                Processes = 2
                WorkingSetMB = 90 + $i + $index * 5
                PrivateMemoryMB = 110 + $i
                CpuPercent = 0.6
                VideoDecodePercent = ''
                Gpu3DPercent = ''
                GpuDedicatedMB = ''
                GpuSharedMB = ''
            }
        }
        $path = Join-Path $dir "$app.csv"
        $samples | Export-Csv -LiteralPath $path -NoTypeInformation -Encoding UTF8
        $files += $path
        $index++
    }
    $output = Join-Path $dir 'comparison.md'
    & "$PSScriptRoot/Summarize-WallpaperBenchmarks.ps1" -InputCsv $files -OutputMarkdown $output -MinSamples 4 -MinDurationSeconds 20
    if (-not (Test-Path -LiteralPath $output)) {
        throw 'Missing comparison Markdown.'
    }
    $report = Get-Content -LiteralPath $output -Raw
    foreach ($app in @('Intonaco', 'Lively', 'AutoWall', 'WallpaperEngine')) {
        if (-not $report.Contains("| $app |")) { throw "Summary is missing $app." }
    }
    if (-not $report.Contains('**Intonaco**')) {
        throw 'Synthetic lowest RAM winner not identified.'
    }
    if (-not $report.Contains('N/A')) {
        throw 'Missing GPU samples incorrectly treated as zero.'
    }

    # A different video can never establish superiority.
    $other = @(Import-Csv -LiteralPath $files[1])
    foreach ($sample in $other) { $sample.VideoSha256 = 'B' * 64 }
    $other | Export-Csv -LiteralPath $files[1] -NoTypeInformation -Encoding UTF8
    $rejected = $false
    try {
        & "$PSScriptRoot/Summarize-WallpaperBenchmarks.ps1" -InputCsv $files -OutputMarkdown (Join-Path $dir 'unfair.md') -MinSamples 4 -MinDurationSeconds 20
    }
    catch { $rejected = $_.Exception.Message -like '*UNFAIR COMPARISON*' }
    if (-not $rejected) { throw 'Mismatched video SHA-256 was not rejected.' }

    # Pause and play statistics cannot be ranked against each other.
    foreach ($sample in $other) {
        $sample.VideoSha256 = $hash
        $sample.PlaybackState = 'Paused'
    }
    $other | Export-Csv -LiteralPath $files[1] -NoTypeInformation -Encoding UTF8
    $rejected = $false
    try {
        & "$PSScriptRoot/Summarize-WallpaperBenchmarks.ps1" -InputCsv $files -OutputMarkdown (Join-Path $dir 'unfair-state.md') -MinSamples 4 -MinDurationSeconds 20
    }
    catch { $rejected = $_.Exception.Message -like '*UNFAIR COMPARISON*' }
    if (-not $rejected) { throw 'Playing vs paused was not rejected.' }
    Write-Host 'PASS: four competitors, missing GPU N/A, SHA and playback-state safeguards.'
}
finally {
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
