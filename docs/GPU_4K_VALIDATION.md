# Intonaco GPU test protocol — 4K playback

This is a **test build**, not a validated below-100-MiB release.
Preserve the original source resolution, colors, bit depth, aspect ratio,
playback timing and smoothness. Do not manipulate the working set to pass.

## Test machine

Test both previously supplied wallpapers, one at a time, on the same GPU,
monitor, display profile and Windows HDR setting:

- **4K HEVC/H.265 wallpaper:** 3840×2160, 30 fps, SDR BT.709.
- **Wallpaper (62)(1).mp4:** 3840×2160, H.264 High, ~23.98 fps,
  short loop and B-frames.

The measurements on the last experimental engine were inconsistent:
HEVC ~277–298 MiB while playing in one later recording,
H.264 ~173 MiB playing. Pause reduced RAM to approximately 92–114 MiB.
These are not guaranteed baselines across different Windows hardware.

## Checklist

1. Save any work and keep *Start with Windows* disabled. Select
   **Playback → Video engine → Intonaco GPU (test)**; do not use the legacy
   Native or Windows renderer when measuring GPU engine changes.
2. Play one test video. Verify full-resolution output and faithful colors,
   correct fit/crop, and desktop icon interaction. Wait for at least
   60 seconds before sampling.
3. Record the Intonaco process-family RAM, CPU, GPU **Video Decode**, GPU
   **3D**, and **Dedicated GPU Memory** separately. GPU percentage in
   Task Manager may reflect NVIDIA Container or recording tools instead.
4. Pause at a visible frame for 5 seconds. Confirm the exact same still
   frame remains and Task Manager GPU decode activity approaches zero.
   A paused application still requires memory for its process and frame.
5. Resume the short-loop H.264 video, watch for black flashes, video
   seeking to the wrong position, frame skips, or runaway RAM after several
   loops. Test the 4K HEVC video as well.
6. Let each file play for 10 minutes. Record steady-state and peak RAM,
   CPU and GPU. Compare **Intonaco, Lively and AutoWall separately** with
   exactly the same wallpaper and display settings; include **all** playback
   helpers/players in each process family. AutoWall may use mpv/weebp
   subprocesses that must not be excluded from its totals.
7. Open `%LocalAppData%\Intonaco\debug.log` and check
   `GPU playback sample:` for **framePresentationFps**, RAM and pause
   state. FPS is the **actual number of frames submitted to DXGI** over
   15 seconds; it is not the decoded source FPS or an invented FPS limit.
   `GPU warning: no new frame presented for 15 seconds` indicates a
   renderer or codec problem and should be reported.
8. Run the Windows PowerShell process-family checks with **only the
   named wallpaper engine running** (not all three at once):
   `./tools/Compare-WallpaperPerformance.ps1 -App Intonaco -DurationSeconds 120 -EnforceRamTarget -OutputCsv intonaco.csv`
   `./tools/Compare-WallpaperPerformance.ps1 -App Lively -DurationSeconds 120 -OutputCsv lively.csv`
   `./tools/Compare-WallpaperPerformance.ps1 -App AutoWall -DurationSeconds 120 -OutputCsv autowall.csv`
   A failed Intonaco RAM gate means the total process-family working set
   did not remain below 100 MiB; it does not mean there was a crash.
   Dedicated GPU memory must still be checked separately.
9. Pause **immediately after selecting a new wallpaper**, before any frame
   has been drawn. The GPU engine should present one original-quality frame,
   freeze it, then enter deep pause; never leave the desktop permanently
   black. Resume and switch several times without UI stalls.

## Acceptance gates

- No system freeze, crash or desktop attachment failure.
- Visually correct colors and detail on both videos; no hidden downscaling.
- No sustained stutter, and approximately source-native frame presentation
  when unobstructed.
- Pause does not continue video decoding; same full-quality frozen frame.
- Compare real peak *total* process-family RAM and GPU memory against
  Lively. **Under 100 MiB is a target, not a verified fact for both files.**
- Driver-specific GPU memory and video-decode allocations cannot be
  certified from a cloud Windows build agent. Do not merge this PR based
  on build success alone.

## Implementation notes

- The native GPU loop now learns a conservative polling cadence from media
  presentation timestamps. This reduces redundant polling for 24 FPS clips,
  while polling faster for 60 FPS. It never drops, resamples, recolors or
  transcodes video intentionally.
- A paused frame is not discarded during a resize; resizing waits until
  resume to avoid a black frozen wallpaper.
- The .NET status timer checks at a slower rate after deep pause, while
  restoring fast status checks on resume.
- Telemetry samples process RAM and actual DXGI presentation rate with
  negligible overhead; it never changes working-set accounting.
