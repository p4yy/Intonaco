<p align="center">
  <img src="docs/branding/intonaco-lockup-light.svg#gh-light-mode-only" width="360" alt="Intonaco — video wallpapers for Windows" />
  <img src="docs/branding/intonaco-lockup-dark.svg#gh-dark-mode-only" width="360" alt="Intonaco — video wallpapers for Windows" />
</p>

<p align="center">
  <a href="https://github.com/p4yy/Intonaco/actions/workflows/ci.yml"><img alt="Build and tests" src="https://github.com/p4yy/Intonaco/actions/workflows/ci.yml/badge.svg" /></a>
  <a href="https://github.com/p4yy/Intonaco/releases"><img alt="Latest release" src="https://img.shields.io/github/v/release/p4yy/Intonaco?display_name=tag" /></a>
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/badge/license-MIT-293B3A" /></a>
</p>

<p align="center"><strong>Your video. Your desktop. Nothing extra.</strong></p>

Intonaco is a focused, open-source Windows app that plays a local video **behind your desktop icons**. It uses WPF and Windows Media Foundation instead of embedding a browser or shipping a separate media engine.

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
- **Respectful of resources.** Pausing stops the media clock without losing the last frame. Hiding the settings window releases its separate live-preview decoder.
- **Practical controls.** Fit, speed, preferred frame rate, looping, auto-pause and startup behavior.
- **System-aware.** Responds to fullscreen applications, desktop focus, power events and Explorer restarts.

No accounts, wallpaper storefront, embedded browser, telemetry SDK or background service.

## Install

Download an installer or portable build from [GitHub Releases](https://github.com/p4yy/Intonaco/releases).

| Download | Requirements |
| --- | --- |
| Windows installer | Windows 10 1903+ or Windows 11; includes the required runtime |
| Portable self-contained EXE / zip | No separate .NET installation needed |
| Framework-dependent zip | .NET 8 Desktop Runtime |

Playback uses the video codecs installed on Windows. **H.264 MP4** is the most reliable choice; WMV, AVI and MOV depend on system codecs. MKV and WebM require compatible codecs. A GPU supporting hardware video decode is recommended.

## Use

1. Open Intonaco and choose a video, or drop a video on the window.
2. Choose fit, volume, preferred frame rate and looping.
3. Minimize to the system tray. The desktop wallpaper continues without the second preview decoder.
4. Pause from the tray when you want a still frame without changing your desktop.

**Default behavior:** wallpaper animation pauses when you switch to another app. In *Playback settings → When another app is active*, select **Keep playing** to change it. Manual pause always takes priority.

Settings and logs remain in `%LocalAppData%\Intonaco` through an uninstall or upgrade.

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

**Paused video stays open by design.** The original MediaElement retains its native frame to prevent black borders and flicker. A full garbage collection cannot release that native decoder, so the app avoids a blocking collection on pause.

## Project

[Source](src/Intonaco) · [Tests](tests/Intonaco.Tests) · [Changelog](CHANGELOG.md) · [Brand](docs/branding) · [Demo](docs/demo) · [Issues](https://github.com/p4yy/Intonaco/issues)

Intonaco is MIT-licensed. It uses Windows Media Foundation and [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) for tray integration.
