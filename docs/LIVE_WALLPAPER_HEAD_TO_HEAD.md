# Intonaco vs. Lively vs. AutoWall vs. Wallpaper Engine

A controlled **Windows 11 performance benchmark**, not a marketing claim.
The scripts do not install competitor apps, download media, modify colors,
reduce the source resolution, or manipulate process working sets.

## Requirements and fair conditions

Use PowerShell (pwsh preferred), and install/run competitor apps separately
on your own PC. Use identical original 4K files, monitor, scaling, HDR state,
audio setting, fullscreen/focus policy and actual playback state. Press
Win+D during each warmup so the wallpaper is visible.

Test both the Ichigo 4K HEVC clip and the demanding H.264 Wallpaper (62)
clip, each as a **separate comparison**. Never substitute a re-encoded
or 1080p copy.

## Record playing samples

Run at the Intonaco repository root (change the video path):

    $video = 'C:\Wallpapers\your-original-4k-video.mp4'
    $display = '1920x1200; 100pct scale; SDR; HDR off; one monitor'
    .\tools\Compare-WallpaperPerformance.ps1 -App Intonaco -VideoPath $video -DisplayTag $display -PlaybackState Playing -DurationSeconds 600 -WarmupSeconds 30 -IncludeGpuCounters -OutputCsv intonaco-playing.csv
    .\tools\Compare-WallpaperPerformance.ps1 -App Lively -VideoPath $video -DisplayTag $display -PlaybackState Playing -DurationSeconds 600 -WarmupSeconds 30 -IncludeGpuCounters -OutputCsv lively-playing.csv
    .\tools\Compare-WallpaperPerformance.ps1 -App AutoWall -VideoPath $video -DisplayTag $display -PlaybackState Playing -DurationSeconds 600 -WarmupSeconds 30 -IncludeGpuCounters -OutputCsv autowall-playing.csv
    .\tools\Compare-WallpaperPerformance.ps1 -App WallpaperEngine -VideoPath $video -DisplayTag $display -PlaybackState Playing -DurationSeconds 600 -WarmupSeconds 30 -IncludeGpuCounters -OutputCsv wallpaperengine-playing.csv

**Stop the current wallpaper app before starting the next.** The script
does not select the file in an app or change that app's pause state.
Confirm each app actually plays the identical video while measuring.
The SHA-256 records the named file but cannot prove an app has loaded it.

Use -RootProcessId 1234 to select an unusual player process when needed.
Automatic process rules include Intonaco.exe, Lively*, AutoWall*/weebp*,
and Wallpaper Engine's wallpaper32.exe, wallpaper64.exe,
webwallpaper32.exe, ui32.exe and ui64.exe. Helpers which outlive their
launcher can become orphaned: **check the whole Task Manager process list**
and make sure the benchmark includes a real mpv decoder if present.
An unrelated mpv instance must not be added to either total.
If a true video decoder or watchdog becomes orphaned, add it explicitly
with -ExtraProcessIds 1111,2222 (using its currently verified process IDs
from Task Manager). This prevents incorrectly treating a competitor's
separate playback process as free memory.

## Repeat the paused test

After each app truly pauses, repeat the command with
-PlaybackState Paused and a different output file name. Wait long
enough for each product's pause/deep-sleep implementation to work.
Visually verify the still frame remains displayed and Video Decode
activity stops. Never compare an app paused with another playing.

## Generate a same-source report

After at least two matching runs, create a Markdown report:

    .\tools\Summarize-WallpaperBenchmarks.ps1 -InputCsv @('intonaco-playing.csv', 'lively-playing.csv', 'autowall-playing.csv', 'wallpaperengine-playing.csv') -OutputMarkdown four-way-4k-results.md

The summary **refuses** mismatched video SHA-256, machine, display tag
or playback state. It reports mean, P95 and peak working-set RAM,
private committed memory, process-family CPU and optional GPU decode
and dedicated memory, sorted by measured average process RAM.
Missing GPU data appears as N/A, never an invented 0%.

The lowest measured RAM does not establish which application provides
the best frame smoothness, colors, stability or overall efficiency.

## Acceptance criteria before advertising Intonaco as lightest

1. At least two 10-minute runs per app per source video, one app at a time.
2. Include every real video-player and helper process, not just each GUI.
3. Preserve original resolution and BT.709/SDR/HDR colors and details.
4. Observe frame presentation, stutter, 4K motion, desktop glitches and
   pause/resume quality alongside Task Manager RAM.
5. Test long runs, 20 rapid wallpaper switches, and deep pause.
6. Check Video Decode, GPU 3D and dedicated GPU memory separately;
   GPU counter availability and attribution depend on drivers.
7. Under 100 MiB remains a target, not a guaranteed number on H.264.
   Do not use downscaling, transcodes or working-set trimming to fake it.

## Source documentation

- Lively: https://github.com/lively-community/lively/wiki/Performance
- AutoWall and its MPV/weebp architecture: https://github.com/SegoCode/AutoWall
- Wallpaper Engine executable names:
  https://help.wallpaperengine.io/functionality/cli.html

Windows CI tests the scripts with synthetic data, not the actual
GPU-dependent performance of any of these applications.
