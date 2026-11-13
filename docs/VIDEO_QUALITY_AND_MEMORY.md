# Video quality and memory: diagnostic contract

## Why this matters

A user's 3840×2160 HEVC/H.265 Main video at 30 fps and SDR BT.709
(primaries, matrix, transfer, TV/limited range) played behind a 1920×1200
Windows desktop at far more than 700 MiB working set in the **Windows**
backend. A 45-second diagnostic sample showed 920.3 MiB process working set,
1137.3 MiB private committed bytes, and only 6.4 MiB live managed .NET
objects. The latter does **not** explain the native decoder footprint.

4K decoding and WPF/Media Foundation presentation can allocate multiple
decoded frames and graphics surfaces; working set also includes shared/native
code and cached pages. The exact 100 MiB target must be measured on the
target PC. There is no safe guarantee of sub-100 MiB total RAM when decoding
and presenting full-resolution 4K, especially with WPF's MediaElement.

## Quality fidelity requirements

- Never silently transcode, reduce bit depth, reduce resolution, clamp
  colors to a made-up range, stretch aspect ratio or increase saturation.
- Respect the source's color primaries, matrix, range and transfer; prefer
  calibrated display color management when available.
- On a 1920×1200 (16:10) desktop, a 3840×2160 (16:9) source set to **Fill**
  will necessarily crop the sides. Set **Fit** to preserve the entire frame.
  This is separate from video color conversion.
- **Windows backend:** WPF MediaElement uses Windows Media Foundation and
  OS/driver-decided video output. It does not expose a reliable custom
  BT.709 → calibrated-display color pipeline, or a frame-rate cap. If the
  colors look faded or differ from the source in this mode, do not claim
  the problem has been fixed by an animation property.
- **Optional MPV backend:** Uses gpu-next/gpu, source metadata and automatic
  system ICC profile detection when supported. Does not modify contrast,
  brightness or saturation. MPV is not bundled with the app. Actual color
  accuracy depends on the active display profile, HDR settings and
  graphics driver.

## Verify, rather than disguise, improvements

1. Play the same local file in Intonaco and in a known-good player, with
   the same display, Windows HDR/color profile, scaling/fit and decoder
   settings. Compare at the same timecode. Take screenshots from both.
2. Inspect the file, if ffprobe is available:
   `ffprobe -v error -select_streams v:0 -show_entries stream=codec_name,width,height,pix_fmt,color_range,color_space,color_transfer,color_primaries,avg_frame_rate -of default=noprint_wrappers=1 <FILE>`
3. In `%LocalAppData%\Intonaco\debug.log`, find the latest
   `Memory sample:` line. Compare `appWorkingSetMiB`, `appPrivateMiB`,
   `managedLiveMiB` and any MPV helper working set. A tiny managed live heap
   alongside huge private memory suggests a **native** hotspot but does not
   by itself prove an OS leak or identify which allocation dominates.
4. Compare the *complete process families*, including MPV and watchdogs,
   with `tools/Compare-WallpaperPerformance.ps1`. Record video-decode GPU,
   dedicated GPU memory and stutter separately.
5. Never use `EmptyWorkingSet`, forced GC, splitting out a helper process,
   or downscaling the source only to make Task Manager's single-process number
   look smaller. Those are not proof of lower **total** RAM or preserved quality.

## Known bug fixed

Old builds called `Timeline.DesiredFrameRateProperty` on a
`MediaElement`, throwing an invalid cast. This property applies to WPF
animation timelines, not the Media Foundation decoder. It neither limited
video FPS nor lowered video decoder memory. Those calls have been removed,
and the related UI is disabled instead of advertising an ineffective cap.

## Next architectural milestone

To truly control video memory and color in the default Windows backend,
replace the WPF MediaElement desktop presentation with a dedicated,
instrumented video pipeline (e.g., Media Foundation decoding into Direct3D
surfaces, controlled surface queue and color-aware rendering). Test on the
user's Windows 11 desktop arrangement and graphics driver before shipping.
That is a significant engine change, not something guaranteed by the
incremental fixes in this branch.
