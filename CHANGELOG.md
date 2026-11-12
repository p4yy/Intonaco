# Changelog

All notable changes to Intonaco are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.1.2] — 2026-11-12

A cleaner wallpaper-discovery workflow and more reliable desktop shortcut activation.

### Added
- Compact, expandable **Get live wallpapers** section with six curated
  third-party live wallpaper and video source websites.
- Copy-only site actions: no automatic redirects, embedded browser,
  scraping, download tracking or additional video decoder.
- Strict local HTTPS allowlist and tests for the source-copy feature.
- Branded Inno Setup welcome panel, generated from the **official** logo
  at installer-build time, with publisher metadata **Acev Dev Co**.

### Fixed
- Preserve second-launch restore requests received before the main WPF
  window has loaded.
- Defer a secondary-shortcut restore until pending minimize and layout
  events settle, using the same UI restoration path as the tray shortcut.

### Improved
- Single-column recent-wallpaper rows fit the compact default width
  without crowding.
- The wallpaper directory is collapsed by default for less visual clutter;
  opening or copying sources does not load remote content.
- Both GitHub EXE/installer and a separate Store preview MSIX are built
  from the updated code; the Store upload requires exact Partner Center
  package identity and publisher values.
- Windows executable metadata now lists **Acev Dev Co**. A trusted
  Authenticode signature is still required for Windows to show this
  company as the *verified* publisher rather than "Unknown publisher".

## [2.1.1] — 2026-11-12

A focused Windows startup and window-restoration update.

### Fixed
- Opening Intonaco manually no longer inherits the stored Start minimized preference,
  so Start menu and desktop shortcut launches bring up the control window.
- Restore a minimized/hidden WPF window to Normal **before** showing and
  activating it, avoiding a race that could leave an unpainted black window.
- Refresh the video preview after window layout on restore; the desktop player
  remains independent.
- Respect the Start minimized preference for actual Windows sign-in launches
  and update existing startup entries that were registered with stale flags.

### Improved
- Default window is a narrow, tall 620-DIP control panel like the original
  compact design; height is capped to available screen work area, with full
  resizing, maximizing and scrolling still supported.
- Regression checks cover normal/automatic startup flags and default sizing.
- Separate, preview-only MSIX packaging support for **Intonaco Wallpaper**.
  Store-ready identity and publisher must come from Partner Center; real
  Windows device validation and Store certification are still required.

## [2.1.0] — 2026-11-12

A native-interface refresh with safer video compatibility and leaner UI activity.

### Added
- Optional, separately installed MPV video backend, selected in Playback settings.
  Windows Media Foundation remains the default; no MPV/FFmpeg dependencies
  are included with the app.
- Real vector GitHub, PayPal and Ko-fi icons in About, credited to Simple Icons.
- Native media preparation and conservative metadata-only MP4 aspect-ratio
  correction using a separate cached copy, without re-encoding or modifying
  the original video.

### Improved
- Responsive 16:9 preview and readable typography at larger window sizes,
  with a centered, width-constrained settings layout.
- The secondary WPF preview decoder closes as soon as the control window is
  minimized, hidden, or loses activation; it reopens only when the user
  returns. The desktop player remains independently active.
- Less frequent desktop placement polling while playing (10 seconds).
  Explorer restart notifications still request immediate reattachment.
- Cancel stale preparation work, retain the last functioning wallpaper on
  unsupported media, and avoid forced full garbage collection on pause.
- Reopening Intonaco from another shortcut restores the existing window.

### Verification
- Windows Actions build, unit tests, published portable binaries, and installer
  smoke test are required for release. Actual desktop layering, MPV embedding,
  codec compatibility, pause-frame fidelity, DPI configurations, and energy
  use require manual tests on physical Windows devices before Store submission.
- No guaranteed frame-rate cap or universal codec compatibility is claimed.

## [2.0.2] — 2026-10-27

Critical rendering fix plus a dedicated background-animation setting.

### Fixed

- **Black borders/blocks on manual Pause — root cause removed.** Manual
  pause and focus auto-pause both called `WallpaperPlayer.Pause()`, which
  since v1.3.0 did much more than stop animation updates: it photographed
  the `MediaElement` with `RenderTargetBitmap`, then called `Stop()`,
  `Source = null`, `Close()` and `Visibility = Collapsed`, replacing the
  live video with a separate `Image`. WPF MediaElement video is presented
  by Media Foundation into a native surface that is **not** part of the
  managed visual tree, so that snapshot was not a faithful copy of the
  desktop (black bands/blocks were baked into it), and closing the source
  removed the only surface holding the real frame. The 2.0.0/2.0.1
  container-geometry and aspect-crop fixes could not fix pixels that were
  captured wrong in the first place. Focus loss appeared fine only because
  the desktop is covered by the other application and auto-resume happens
  before it is visible again — **focus loss was never the cause**.
- Pause now stops *only the media clock*: source, decoded frame, element
  geometry, Stretch, size and position are untouched. The last rendered
  frame remains visible on the same surface, so there is nothing to
  letterbox, clear or repaint. This restores the v1.0.0 pause behaviour
  while keeping every later feature (FPS cap, preview, auto-pause, fit,
  speed, loop, tray, recovery).
- Resume is a single `Play()` on that same paused clock: no reopen, no
  seek, no decoder warm-up, no frame swap — no flicker, black gaps or
  jump to a different state.
- The in-app preview is paused in place too, instead of being photographed
  into a bitmap and closed. Its decoder is still released when the window
  is hidden/minimized/stopped, and `MediaOpened`/`MediaEnded` are now
  wired so a reopened preview syncs to the desktop's position.
- Explorer-restart recovery restores a paused wallpaper by seeking the
  live surface at the saved position (`PauseAt`), not by carrying a bitmap
  captured against a destroyed player window.
- `WallpaperPlayer.Position` reports the captured paused position while
  paused, so recovery cannot jump to a different frame.

### Added

- **Background animation when unfocused** — a dedicated, clearly labelled
  two-mode setting in the Wallpaper settings card, persisted in
  `%LocalAppData%\Intonaco\settings.json`:
  - **Pause when app loses focus (Mode A, default)** — focus/fullscreen
    detection stops animation updates while another application is
    focused and resumes them when the desktop/Explorer returns. The
    background stays fully rendered throughout.
  - **Keep animating when unfocused (Mode B)** — both auto-pause detectors
    are disabled so the wallpaper keeps animating while the user works in
    another application; returning does not reset or re-seek it.
- Manual pause is now tracked separately (`_pausedByUser` and the new
  `PlaybackFocusPolicy`). Focus events, system events and mode changes can
  never auto-resume a wallpaper the user paused explicitly, in either
  mode; the manual control always works.
- `BackgroundAnimationMode` is the single source of truth with migration
  from v1 (`pauseOnFullscreen`) and v2.0.x (`pauseWhenUnfocused`) files;
  the legacy flags are kept in sync for older builds. Invalid/hand-edited
  values fall back to the safe default.

### Changed

- Pause no longer closes the desktop decoder (it stops the clock, so no
  frames are decoded or presented); `Stop()` remains the full teardown.
  README, verification checklist and performance notes now describe the
  real architecture instead of the static-frame suspension.

### Tests

- `PauseRenderingTests` pins the root cause at source level (Pause must
  not capture, close, collapse or replace the surface; Resume must not
  reopen; the preview must pause in place) and asserts the host remains
  full-bleed with no visible secondary layer.
- Hosted media-lifecycle test now proves Pause keeps the same `Uri`/
  visible element, stops the clock and that Resume does not raise
  `MediaOpened` again.
- `BackgroundAnimationModeTests` covers the default mode, Mode B, JSON
  restart persistence, migration, invalid values and manual-pause
  independence in both modes.

## [2.0.1] — 2026-10-27

Hotfix: the paused-wallpaper black bars that 2.0.0 claimed to fix were still
reproducible. 2.0.0 hardened the *container* geometry (window/host margin,
padding, alignment, clipping), but the bars were never a container problem.

### Fixed

- **Black bars on the paused wallpaper (actually fixed this time).** The paused
  wallpaper is a `RenderTargetBitmap` photograph of the `MediaElement`, so any
  letterbox it was showing at that instant became real black pixels *inside the
  bitmap*. The static layer then displayed that bitmap with `Stretch.Fill`,
  which scaled the baked-in bars to full screen. Container geometry cannot
  remove bars that are part of the image, which is why 2.0.0 did not fix it.
  The frozen frame is now:
  - captured after `_host.UpdateLayout()` / `MediaElement.UpdateLayout()`, so a
    suspend triggered by a focus change, fullscreen transition, display resize
    or DPI change can no longer photograph a stale, wrong-sized layout;
  - captured with the media element forced to `Stretch.UniformToFill` (except in
    the user-selected "Fit frame" mode, where the letterbox is deliberate and is
    now identical before and after pausing instead of changing on pause);
  - centre-cropped to the desktop host's aspect ratio by the new
    `WallpaperPlayer.CentreCropToAspect`, applied both at capture and again in
    `SetPausedFrame` — the single choke point every path funnels through,
    including `PauseAt` during explorer-restart recovery, which carries a frame
    captured against the *previous* desktop host;
  - shown by a paused layer that covers rather than letterboxes
    (`Stretch.UniformToFill`, zero margin, stretch alignment, re-asserted on
    every frame), and the host grid and media element are now explicitly
    zero-margin/stretch so the crop target is trustworthy.
- Capture geometry is written to `%LocalAppData%\Intonaco\debug.log`
  (`Paused frame captured 1920x1080 (host …, media …, video …, fit …,
  captured-as cover, host aspect 1.778)`), so a mismatch can be diagnosed from
  the log instead of guessed at.

### Added

- `tests/Intonaco.Tests/PausedFrameGeometryTests.cs` — regression tests that
  reproduce the defect without a video decoder: a letterboxed bitmap wider than
  the desktop is cropped so the black bands are physically gone from the pixels,
  cropping stays centred (a wallpaper must not jump sideways on pause), matching
  frames are returned untouched, an unknown host aspect is ignored, and the
  paused/host/media layers pin cover geometry with zero inset.

## [2.0.0] — 2026-10-27

Major Performance & UI Overhaul. The complete fix & overhaul plan implementation.

### Fixed

- **Black bars on paused wallpaper:** video and static snapshot now always render with `Stretch.UniformToFill` (`object-fit: cover`) filling 100% of the monitor. Wallpaper host window has zero margin/padding, black background, and clipped bounds — no letterboxing in play or pause.
- **Pause now truly reduces CPU/RAM:** pausing captures the current frame to a bitmap, replaces the video element with a static image, calls `video.pause()`, clears `src` and `load()` to release the Media Foundation decoder, and cancels all animation loops. Resume recreates the decoder and seeks to the saved position.
- **Frame-rate throttling now affects resources:** `TargetFps` sets `Timeline.DesiredFrameRate` on the MediaElement and engages a `CompositionTarget.Rendering` guard (`FrameRateLimiter`). Switching 60 fps → 30 fps measurably drops GPU/CPU composition cost.
- **Auto-pause when other apps are in focus:** new `ForegroundDetector` polls the foreground window every 1.2s via `GetForegroundWindow` + process name. If the foreground is not `explorer` (desktop) or `Intonaco` itself, the wallpaper auto-pauses (snapshot & decoder unload) and auto-resumes on return to desktop. Toggle enabled by default, migrated from legacy fullscreen-only setting.
- **Start with Windows now works:** implements `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` registry entry with `"path" --minimized` on enable and deletion on disable, verified via `StartupManager.IsRegistered`. Silent boot parses `--minimized`/`-m` flags and starts hidden in the tray with wallpaper playing.
- **Start minimized & Resume last wallpaper:** silent-start logic hides the main UI completely, starts in the system tray, and `AutoPlay` restores `LastWallpaperPath` plus volume/fit/fps/loop settings from `%LocalAppData%\Intonaco\settings.json`.
- **Unsupported video errors:** `MediaFailed` now maps HRESULT codec errors to a friendly toast — “Unable to play video: Unsupported codec or format. Please use standard MP4 (H.264) or WebM files.” — via tray balloon and dialog, without crashing. Drag-drop validates extensions and shows a clear error.

### Changed

- **Complete Windows 11 UI overhaul (no AI slop):** new Fluent Dark palette — `Background #1E1E1E`, `Card #272727`, `Hover #323232`, subtle `rgba(255,255,255,0.07)` borders, Windows Blue `#60CDFF` accent used sparingly for toggles/primary buttons. Typography `Segoe UI Variable`, 8px card radius, 4px button radius, 32px control height. Light theme updated to matching WinUI light surfaces.
- **Player window hardened** against black bars and placement drift; ensures layered placement verification and no redirection bitmap handling remains unchanged but visually correct.
- **Background timers gated** by need: foreground/fullscreen polling runs only while applied and playing (or paused awaiting resume), placement check throttled to 5s playing / 15s paused.

### Added

- New `ForegroundDetector` service and `PauseWhenUnfocused` setting (migrates legacy `PauseOnFullscreen`).
- Frame-rate limiter integration with measurable resource impact.
- Enhanced error handling and user notifications for unsupported codecs.
- Updated verification checklist and release automation for v2.0.0.

## [1.3.0] — 2026-10-26

Performance and installation release. The user-verified desktop
integration from v1.0.3 remains unchanged.

### Added

- **Real Windows installer:** `Intonaco-v1.3.0-win-x64-setup.exe` is a
  self-contained, per-user Inno Setup installer. It needs no .NET or
  administrator account, creates a Start Menu shortcut, offers an
  optional desktop shortcut, registers a normal uninstaller, and can
  launch Intonaco after setup. Upgrades, reinstalls and uninstall keep
  `%LocalAppData%\Intonaco` settings and logs intact.
- Reproducible installer tooling under `installer/`, including an
  automated silent install → launch → reinstall → uninstall smoke test
  that runs in CI and again before a release is published.
- `tools/Measure-IntonacoPerformance.ps1` for repeatable RAM/CPU samples,
  plus transition-only process footprint entries in `debug.log`.

### Changed

- **Pause now behaves like a static wallpaper.** Intonaco captures the
  current desktop frame, displays that one bitmap, then closes the
  Media Foundation source so decoder, source-reader and frame-queue
  buffers are released. Resume reopens the same file, seeks to the
  paused position, and removes the static frame only after new frames
  render. The same suspension is used for user, tray, fullscreen and
  system-event pauses; Fit/display changes can quietly recapture the
  static frame and suspend again.
- **The in-app preview no longer decodes when it cannot help.** It now
  closes its separate media pipeline when playback pauses, the main
  window is minimized/hidden in the tray, playback stops, or the app
  exits. While paused it reuses the desktop player's static frame.
- Background work is gated by need: fullscreen polling runs only while
  playing (or while waiting to resume from a fullscreen pause), the
  desktop-placement check runs only while a wallpaper is applied, and
  its interval relaxes from 5 seconds to 15 seconds for a static frame.
- Volume changes are saved after a short debounce instead of writing
  settings for every slider movement; the pending save is flushed when
  the window hides or exits.
- The installer payload uses a loose self-contained ReadyToRun publish,
  workstation GC settings are explicit, and self-contained outputs omit
  unused runtime satellite-language assemblies.

### Fixed

- Pausing previously left both the desktop `MediaElement` pipeline and
  the independent preview pipeline allocated; the preview could keep
  decoding even though the wallpaper said “Paused.”
- A system pause/resume event could claim ownership of a wallpaper the
  user had already paused manually.
- The README's old, unrealistically low RAM estimates were replaced with
  a qualitative state table and a repeatable measurement procedure.

### Added tests

- A hosted WPF media-lifecycle test generates a deterministic video and
  proves Play opens a pipeline, Pause captures a frame and closes that
  pipeline, Resume reopens it, and Stop releases both source and frame.
- Paused-frame bounds, player host composition, timer idempotence and
  process-snapshot tests, plus the installer smoke test described above.

## [1.2.0] — 2026-10-26

Brand and interface refinement release. The user-verified desktop
integration from v1.0.3 remains unchanged.

### Added

- **New Intonaco identity:** the supplied navy/cyan cat-and-image mark is
  now the application icon used by the EXE, Windows taskbar, window title
  bar and system tray, and appears in the app header.
- A proper multi-resolution Windows `.ico` (16, 20, 24, 32, 48, 64, 128 and
  256 px) generated from the transparent logo master.
- Light and dark full-logo lockups under `docs/branding/`, plus a
  reproducible `tools/generate-icon.py` asset pipeline.

### Changed

- Reworked the main window from the decorative v1.1 presentation into a
  quieter **Windows 11 Settings-style layout**: neutral `#F3F3F3`/`#202020`
  surfaces, 8 px cards, 4 px controls, 32 px control heights, standard
  separators, Segoe UI Variable typography and plain setting labels.
- Removed accent gradients, oversized radii, hand cursors, colored recent
  tiles, the status pill and the preview gradient footer.
- Recent wallpapers now use compact two-column list rows, so all six are
  visible without taking over the page.
- Toggle switches now use the Windows settings arrangement—label and
  description on the left, compact 40×20 switch on the right.
- The title-bar caption and text colors now match the app surface on
  Windows 11, in addition to immersive dark mode.
- The interface uses the logo's navy/cyan only for the mark and primary
  action; the rest of the app stays neutral like Windows Settings.

### Fixed

- **Volume slider:** rebuilt with a 32 px interaction area, a continuous
  4 px filled track, a 22 px thumb with hover/drag states,
  click-to-position behavior, 1% snapping and consistent centering in the
  playback row.
- Replaced the legacy visible scrollbar appearance with a thin Windows
  11-style scrollbar.

## [1.1.0] — 2026-10-26

A creativity and polish release built on the user-verified v1.0.3 desktop
engine. No changes were made to the raised-desktop attachment path.

### Added

- **Drag-and-drop wallpapers:** drop an MP4, WebM, MKV, MOV, AVI, WMV or
  M4V anywhere on the main window to apply it immediately, with a clear
  drop overlay while dragging.
- **Recent wallpapers:** the last six applied videos are remembered with
  file name, size and folder, ready for one-click reuse.
- **Wallpaper fit modes:** Fill (crop), Fit (letterbox) and Stretch now
  apply live to both the in-app preview and the desktop player.
- **Playback speed and looping controls:** choose 0.5×, 0.75×, 1.0×,
  1.25× or 1.5× playback, and optionally stop instead of looping.
- **Startup preferences in the UI:** Start minimized and Resume last
  wallpaper are now visible toggles alongside Start with Windows.
- **Quick file and diagnostics actions:** reveal the current video in
  File Explorer or open the Intonaco log folder from the footer.
- The file card now reports the video's natural resolution and duration
  when Windows Media Foundation provides them.

### Changed

- Rebuilt the main window as a cleaner Fluent-inspired, scrollable
  layout with the app identity in the header, a live **Desktop live /
  Paused / Ready / No video** status pill, and clearer grouping for
  wallpaper, playback and app preferences.
- The preview is larger and carries a status chip; the current file,
  metadata, playback and volume controls are aligned into one compact
  card.
- Refreshed both dark and light themes with softer blue-gray surfaces,
  an accent-gradient primary action, larger radii, subtle borders and
  more consistent typography and spacing.
- Combo boxes and their drop-down popups are now fully themed in both
  appearances instead of using the default white Windows popup.
- The volume slider now has a filled accent track, a clearly visible
  thumb and a live percentage label.
- Moving the volume slider while muted unmutes playback, matching the
  behavior users expect from media controls.
- The native Windows title bar now follows Intonaco's light/dark theme
  through DWM immersive dark mode.
- Settings are normalized on load/save so fit, speed, theme, volume,
  frame-rate and recent-wallpaper values stay canonical and bounded.

### Added tests

- Settings normalization, recent-wallpaper de-duplication/capping, and
  player playback-speed/fit/loop defaults.

## [1.0.3] — 2026-10-26

Fix for v1.0.2 still leaving the static wallpaper on Windows 11 26H2,
OS Build 26300.9711. On-device diagnostics showed the player had the
correct Progman parent, full-screen rect and DefView → player z-order,
but two required conditions were missing: Explorer had never created a
WorkerW wallpaper layer, and WPF had removed the player's
`WS_EX_LAYERED` bit. v1.0.2 nevertheless logged “Raised attach
succeeded.”

### Fixed

- **Request the raised-desktop split when no actual wallpaper layer
  exists.** A Progman with `WS_EX_NOREDIRECTIONBITMAP` and a
  `SHELLDLL_DefView` child is now treated as raised-capable rather than
  already split. If neither a full-screen classic WorkerW nor a
  full-screen Progman-child WorkerW exists, Intonaco sends the single
  `0x052C (0xD, 1)` request and polls for up to two seconds for
  Explorer to create the layer. It still does not send an `lParam=0`
  variant on a raised desktop.
- **Keep `WS_EX_LAYERED` on the WPF player.** WPF's `HwndTarget`
  clears that bit in `WM_STYLECHANGING` whenever
  `UsesPerPixelOpacity` is false, even when application code has just
  set it with `SetWindowLongPtr`; `SetLayeredWindowAttributes` then
  fails with Win32 error 87. Intonaco now installs a narrow
  `HwndSource` style guard before enabling raised layering. The guard
  restores the bit in `STYLESTRUCT.styleNew`, while keeping the normal
  hardware-rendered, non-per-pixel WPF path and constant alpha `0xFF`.
- **No more false raised-attach success.** Raised placement is verified
  end-to-end before “Playing”: parent, visibility, coverage,
  `WS_EX_LAYERED`, alpha read back as 255 with `LWA_ALPHA`, exact
  position directly below `SHELLDLL_DefView`, and no full-screen system
  WorkerW above the player. The 5-second placement check and reattach
  path reassert and re-verify the layered state too.
- Tiny Explorer WorkerW helper windows can no longer masquerade as a
  classic full-screen wallpaper layer.
- `debug.log` now records split-request results, waits for the WorkerW
  layer, style-guard installation, ex-style readbacks and layered
  alpha/flags, making the build 26300 acceptance state directly
  inspectable.

### Added

- Regression tests for treating a raised-but-unsplit desktop as having
  no wallpaper layer, and for the layered ex-style guard transform.

## [1.0.2] — 2026-10-26

Fix for v1.0.1 reporting "Playing" while the actual desktop never
changed (reported on Windows 11 26H2, OS Build 26300.9711, with the
same 1920x1080 MP4).

### Fixed

- **Windows 11 24H2+ "raised desktop" support.** On these builds
  Progman is created with `WS_EX_NOREDIRECTIONBITMAP`, the icon view
  is a layered child of Progman, and Explorer's wallpaper is a WorkerW
  child below it — a plain child window parented there (v1.0.1's
  approach) is never composited by DWM: present and playing, but
  invisible. Following Microsoft's guidance to wallpaper apps,
  Intonaco now detects the raised desktop from the window tree and
  Progman's extended style and makes the player a `WS_EX_LAYERED`
  child of Progman with constant alpha `0xFF`
  (`SetLayeredWindowAttributes`, so WPF/Media Foundation presents stay
  hardware accelerated), z-ordered directly under `SHELLDLL_DefView`
  and above the system WorkerW. The classic top-level-WorkerW path is
  unchanged for Windows 10 / Windows 11 23H2 and earlier.
- The `0x052C` desktop-split request is now sent once as `(0xD, 1)`
  and only when the window tree shows no wallpaper layer yet — extra
  variants sent by v1.0.1 could undo the layer that was just created.
- A full-screen WorkerW under Progman is no longer confused with
  Explorer's tiny helper WorkerW windows (size-filtered).
- Attach now *verifies* visibility (parent, `IsWindowVisible`, and that
  the player actually covers its parent) before reporting success, and
  `%LocalAppData%\Intonaco\debug.log` records the OS build, the
  detected layout, Progman's child z-order and the player's final
  styles/rect for bug reports.

### Added

- Periodic placement self-check (every 5 s while a wallpaper is
  applied): if Explorer reshuffles its desktop windows — e.g. a
  wallpaper change recreates the system WorkerW above the player —
  size and z-order are re-asserted automatically.
- Unit tests for desktop-layout classification.

## [1.0.1] — 2026-10-26

Hotfix for "Failed to attach to desktop" on current Windows 11 builds
(reported against v1.0.0 with a 1920x1080 MP4; restarting Explorer did
not help because the lookup itself was too narrow).

### Fixed

- **Desktop attach (`DesktopIntegration`) rewritten for modern Explorer
  layouts.** v1.0.0 tried only two lookups with a single `0x052C` spawn
  variant. v1.0.1 sends all known spawn variants, enumerates top-level
  windows, and tries candidates in order — classic shell-host sibling,
  WorkerW child of Progman (newer Windows 11 / Spotlight layouts),
  WorkerW after Progman, any icon-less WorkerW, and finally Progman
  itself with the player Z-ordered to the bottom — retrying briefly
  while Explorer creates the WorkerW asynchronously, and verifying
  `SetParent` actually took effect before reporting success.
- Player window conversion now clears `WS_POPUP` (mutually exclusive
  with `WS_CHILD`) and uses `Get/SetWindowLongPtr` on 64-bit Windows.
- A failed attach no longer leaves the black player window floating on
  screen; it is closed, and the real failure reason is shown.
- Footer version now comes from the assembly version (was hardcoded).

### Added

- Diagnostic logging to `%LocalAppData%\Intonaco\debug.log`: every
  attach attempt records the Progman/WorkerW windows found, the
  candidates tried and Win32 errors — attach this file to bug reports.
- Direct single-file download in releases:
  `Intonaco-v<version>-win-x64.exe` (self-contained, no unzip needed),
  included in `SHA256SUMS.txt`.
- Unit tests for WorkerW candidate ordering.

## [1.0.0] — 2026-10-26

First official release.

### Added

- Complete WPF application on .NET 8 with a Windows 11-style dark theme UI
  (Segoe UI Variable, card layout, Segoe MDL2 icon glyphs).
- Real desktop integration via the WorkerW technique
  (`DesktopIntegration`): sends `0x052C` to Progman, locates the WorkerW
  sibling of the SHELLDLL_DefView host, and parents the borderless
  `PlayerWindow` behind the desktop icons.
- Video wallpaper playback via WPF `MediaElement` / Windows Media Foundation
  with DXVA2 hardware decoding (`WallpaperPlayer`) — no libvlc, eliminating
  ~120 MB of native binaries compared to the original AutoWall approach.
- Play / pause / stop / mute / volume controls, muted by default, with
  seamless looping and a live (muted) in-window preview.
- `FullscreenDetector`: 2-second polling of the foreground window against its
  monitor rect; automatically pauses playback for fullscreen games/apps.
- `SystemEventMonitor`: pauses on sleep/lock, resumes after wake/unlock
  (with a short display-stabilization delay), and re-fits the wallpaper on
  display changes.
- Explorer restart recovery via the `TaskbarCreated` broadcast message.
- System tray icon (Hardcodet.NotifyIcon.Wpf) with Show / Play-Pause / Mute /
  Exit menu, minimize-to-tray, and double-click to restore.
- JSON settings persistence in `%LocalAppData%\Intonaco\settings.json`
  (last wallpaper, volume, mute, FPS limit, pause-on-fullscreen, theme,
  start-with-Windows), auto-play of the last wallpaper on launch, and
  `--minimized` start support.
- Start-with-Windows registration under `HKCU\...\Run` (`StartupManager`).
- Configurable frame-rate limit (15/24/30/60 fps, `FrameRateLimiter`).
- Single-instance enforcement via a named mutex; PerMonitorV2 DPI-aware
  application manifest.
- Light theme with a System/Light/Dark selector that follows the Windows
  app theme (including live switching on system theme changes).
- Multi-resolution application icon (`16–256 px` `.ico`) generated from the
  SVG source in `src/Intonaco/Assets/` by `tools/generate-icon.py`.
- xUnit test suite (settings defaults/clamping, player state machine and
  clamping, fullscreen detector lifecycle, frame-rate limiter).
- GitHub Actions CI (build + test + publish artifacts on Windows) and a
  tag-driven release workflow producing framework-dependent and
  self-contained single-file zips with `SHA256SUMS.txt`.
- Unhandled-exception logging to `%LocalAppData%\Intonaco\crash.log`.
- Manual Windows verification checklist in `docs/VERIFICATION.md`.

### Fixed (during the initial hardening pass)

- The second-instance path no longer calls `ReleaseMutex` on a mutex owned
  by the first process (threw `ApplicationException` on exit).
- FPS setting now persists: settings events are suppressed during XAML
  initialization so the default 30 fps selection no longer overwrites the
  saved value on every launch, and saved FPS/volume/mute are pushed into the
  player at startup.
- The Stopped state handler no longer blanks the file name (an async
  `BeginInvoke` race made it clear the name of a just-applied wallpaper).
- Explorer-restart/wake recovery now fully re-applies the wallpaper with a
  fresh player window when the old WorkerW (and its child window) was
  destroyed, instead of silently failing to reattach.
- `DesktopIntegration.Attach` no longer propagates handle-creation
  exceptions from destroyed windows; it reports failure so callers can
  recover.
- Test project compiles (missing `using System;`) and WPF-touching tests run
  on an STA thread with a Dispatcher via `Xunit.StaFact` (`[WpfFact]`),
  instead of failing on xUnit's default MTA threads.
- Solution file uses `Any CPU` configurations that match the SDK-style
  projects (the original `x64`-only mappings did not build with
  `dotnet build`).
- `RuntimeIdentifier`/`SelfContained` moved out of the app project into the
  publish commands (`build.ps1`, CI) so `dotnet build`/`dotnet test` work
  RID-agnostically.
- README: fixed the placeholder clone URL, an unclosed code fence, and the
  flattened Usage/Performance tables.

[1.0.3]: https://github.com/77worldd/Intonaco/releases/tag/v1.0.3
[1.0.2]: https://github.com/77worldd/Intonaco/releases/tag/v1.0.2
[1.0.1]: https://github.com/77worldd/Intonaco/releases/tag/v1.0.1
[1.0.0]: https://github.com/77worldd/Intonaco/releases/tag/v1.0.0
