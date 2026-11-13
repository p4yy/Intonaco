<p align="center">
  <img src="docs/branding/intonaco-lockup-light.svg#gh-light-mode-only" width="360" alt="Intonaco Wallpaper" />
  <img src="docs/branding/intonaco-lockup-dark.svg#gh-dark-mode-only" width="360" alt="Intonaco Wallpaper" />
</p>

<p align="center">
  <a href="https://github.com/p4yy/Intonaco/actions/workflows/ci.yml"><img alt="Build and tests" src="https://github.com/p4yy/Intonaco/actions/workflows/ci.yml/badge.svg" /></a>
  <a href="https://github.com/p4yy/Intonaco/releases/tag/v2.2.0"><img alt="Download Intonaco Wallpaper" src="https://img.shields.io/badge/Download-v2.2.0-83DFCC" /></a>
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/badge/license-MIT-293B3A" /></a>
</p>

<p align="center">Native video wallpapers for Windows 10 and 11.</p>

**Intonaco Wallpaper** is a focused, open-source Windows app that plays a local video **behind your desktop icons**. It uses WPF and Windows Media Foundation instead of embedding a browser or shipping a separate media engine.

<p align="center">
  <a href="https://cdn.jsdelivr.net/gh/p4yy/Intonaco@main/docs/demo/intonaco-demo.mp4" title="Watch the Intonaco demo">
    <img src="docs/demo/intonaco-demo-preview.gif" width="720" alt="Demo of Intonaco displaying a live wallpaper behind desktop icons" />
  </a>
</p>

<p align="center">
  <a href="https://cdn.jsdelivr.net/gh/p4yy/Intonaco@main/docs/demo/intonaco-demo.mp4">Watch the demo with sound</a>
  · <a href="https://github.com/p4yy/Intonaco/releases">Downloads</a>
  · <a href="docs/VERIFICATION.md">Verification guide</a>
</p>

## What it does

- **Video on the desktop.** Uses the Windows WorkerW/Progman layers to stay behind your icons.
- **Local files.** Choose or drag and drop a video; revisit your last six wallpapers.
- **Quiet playback.** Tray controls for play, pause, volume and exit.
- **Deep pause.** The integrated GPU engine releases the video decoder after pausing while retaining the last full-quality desktop frame.
- **Clear controls.** A refreshed icon-led interface provides fit, speed, source-frame-rate playback, looping, focus-aware auto-pause and startup settings.
- **System-aware.** Responds to fullscreen applications, desktop focus, power events and Explorer restarts.

No accounts, wallpaper storefront, embedded browser, telemetry SDK or background service.

## Install

Download the current build from the [official Intonaco Wallpaper v2.2.0 release](https://github.com/p4yy/Intonaco/releases/tag/v2.2.0).

| Download | Purpose |
| --- | --- |
| [Standard installer](https://github.com/p4yy/Intonaco/releases/download/v2.2.0/Intonaco-Wallpaper-Setup.exe) | Windows 10/11 installation with Start menu shortcuts; .NET runtime included |
| [Portable EXE](https://github.com/p4yy/Intonaco/releases/download/v2.2.0/Intonaco-Wallpaper-Portable.exe) | Run directly without an installer |
| [Microsoft Store upload](https://github.com/p4yy/Intonaco/releases/download/v2.2.0/Intonaco-Wallpaper.msixupload) | Partner Center submission file; not a desktop installer |

Each GitHub release is published under its own versioned tag so its original publication date and download files stay together. Windows and Microsoft Store use the corresponding numeric version for updates.

## Native video engine and format support

Intonaco has its **own lightweight native media-preparation layer**. It recognizes container signatures, validates local files, and corrects nonstandard MP4 pixel-aspect metadata without re-encoding the video. Playback then uses **Windows Media Foundation**, retaining its available hardware-assisted decoding without shipping an additional codec engine.

**MLW compatibility:** Intonaco detects .mlw
files containing standard video bytes or ZIP-wrapped playable videos,
imports a local cached copy, and preserves the original. Protected or
proprietary .mlw files are not yet decoded; obtain an MP4 from the original
provider when possible.

**Common containers:** MP4, M4V, MOV, MKV, WebM, AVI, WMV, ASF, MPEG, MPEG-TS, M2TS and 3GP. Intonaco also allows opening uncommon video containers such as FLV, OGV, RMVB and MXF, but **those are not guaranteed to play**. Both container support and the video/audio codec must be available through Windows Media Foundation. MKV is a container, not a codec.

- **Standalone GPU engine:** Intonaco's integrated Direct3D 11 / Windows Media Foundation video renderer is selected by default. No external playback executable is required.
- **No quality loss:** normal playback reads the original file; the rare MP4 aspect-ratio repair modifies only metadata in a separate cached copy, leaving video samples and your original file unchanged.
- **Safe by default:** no automatic lossy transcode; unsupported codecs produce a clear explanation, and Intonaco can restore the last working wallpaper rather than leaving a black desktop.
- **Video-only input:** animated GIF, animated WebP and APNG are not yet supported by the integrated video renderer.

**Important:** Video decoding support depends on Windows codecs, media format and graphics drivers. For widest built-in compatibility, use **H.264 MP4**. You can choose the included **Native compatibility** or **Windows compatibility** renderer if the GPU renderer cannot play a file.

## Integrated video engine

**Intonaco GPU** is the default. It uses the original Direct3D 11 and Windows Media Foundation playback path with direct GPU frame presentation, deep pause and controlled decoder handoff during wallpaper changes. No media-player installation or separate decoding application is required.

**Native compatibility** and **Windows compatibility** remain available in Playback settings for graphics hardware or media configurations that need a fallback. Colors, video smoothness, and RAM usage depend on the source codec and Windows GPU driver. Some 4K H.264 files may use more than 100 MB of RAM while playing.

## Use

1. Open Intonaco Wallpaper and choose a video, or drop a video on the window.
   The default **still preview** uses a video thumbnail to reduce CPU/GPU
   usage while the desktop wallpaper animates. Select **Play live preview**
   if you explicitly want motion in the control window too.
2. Choose fit, volume, playback speed and looping. The original video frame rate is preserved.
3. Minimize to the system tray. The desktop wallpaper continues without the second preview decoder.
4. Pause from the tray when you want a still frame without changing your desktop.

**Default behavior:** wallpaper animation pauses when you switch to another app. In *Playback settings → When another app is active*, select **Keep playing** to change it. Manual pause always takes priority.

**Compatibility:** Settings and logs intentionally remain in `%LocalAppData%\Intonaco` through an uninstall or upgrade. The executable remains `Intonaco.exe` internally to keep older shortcuts and startup registrations working.

## Build and test

Requires Windows and the .NET 8 SDK.

```powershell
git clone https://github.com/p4yy/Intonaco.git
cd Intonaco
dotnet restore Intonaco.sln
dotnet build Intonaco.sln -c Release
dotnet test Intonaco.sln -c Release
.\build.ps1 -Publish
```

To build the installer, install Inno Setup 6 and run `.\build.ps1 -Installer`.

The native Windows UI and media pipeline are tested in [GitHub Actions](.github/workflows/ci.yml). Desktop layering, actual codec behavior, power events and GPU use must additionally be verified on Windows hardware as described in [docs/VERIFICATION.md](docs/VERIFICATION.md).

## Performance

Resource use depends on codec, resolution, GPU, monitor configuration and Windows version. Intonaco does **not** claim a universal RAM or CPU figure. To compare performance on your computer, measure the same clip while settings are visible, hidden in the tray, paused and stopped:

```powershell
.\tools\Measure-IntonacoPerformance.ps1 -Label "Playing" -DurationSeconds 20
.\tools\Measure-IntonacoPerformance.ps1 -Label "Paused" -DurationSeconds 20
```

**Deep pause:** after the first GPU-presented frame, pausing releases the Media Foundation decoder while retaining the still image on the DXGI swap chain. The original video is not downscaled or re-encoded. Memory is source/driver dependent; do not assume every 4K file stays under 100 MB.

## Microsoft Store

The official GitHub release includes an independently built and validated **unsigned** `.msixupload` file with
the registered **Acev Apps Co.** package identity. Upload it in Microsoft
Partner Center for certification; building the file does not publish the app.
See the [Store submission checklist](docs/MICROSOFT_STORE.md).

## Project

**Find live wallpapers:** The app's collapsed **Discover wallpapers** section contains six independent sources. Press **Copy link** to copy a source address, paste it into your preferred browser, download a compatible local video, and open that file in Intonaco Wallpaper. The app does not automatically redirect, stream, scrape, or download third-party content. Always check the content's license.

[Source](src/Intonaco) · [Tests](tests/Intonaco.Tests) · [Changelog](CHANGELOG.md) · [Brand](docs/branding) · [Demo](docs/demo) · [Issues](https://github.com/p4yy/Intonaco/issues)

Intonaco is MIT-licensed. It uses Windows Media Foundation and [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) for tray integration.
