# Verification checklist

Intonaco is built and unit-tested on Windows by GitHub Actions on every push
(see `.github/workflows/ci.yml`). The checks below **require an interactive
Windows desktop session** and therefore cannot run on a CI agent. They are
tracked here — and in the
[verification issue](https://github.com/p4yy/Intonaco/issues) — so they
are never silently skipped. If you run any of them, please report results
(with Windows build, GPU, and video details) on that issue or via PR.

## Automated (CI, every push)

- [x] `dotnet restore` + `dotnet build -c Release` succeeds on `windows-latest`
- [x] `dotnet test -c Release` — settings normalization/recents, player
      playback options, fullscreen-detector and frame-rate-limiter tests pass
- [x] Hosted media-lifecycle test: a generated video opens, Pause
      keeps the source open and the media element visible, Resume does
      not reopen or seek, and Stop releases the source
- [x] Source-level regression tests pin that Pause never captures,
      closes, collapses or replaces the rendering surface; the preview
      is paused in place rather than photographed
- [x] Unit tests for both background animation modes, JSON persistence
      across restarts, and manual pause overriding focus events
- [x] Framework-dependent, self-contained single-file and installer
      ReadyToRun publishes succeed
- [x] Inno Setup builds, then silently installs to a test folder,
      creates Start Menu/desktop shortcuts, launches the installed app,
      reinstalls over it, uninstalls it, removes the test Run value and
      shortcuts, and leaves a sentinel in `%LocalAppData%\Intonaco`
      untouched

## Manual (interactive Windows 10/11 session)

### Desktop integration

- [ ] Apply an H.264 MP4: the video renders behind desktop icons, and icons
      remain visible, selectable and double-clickable
- [ ] On Windows 11 24H2+/26H2, the fresh `%LocalAppData%\Intonaco\debug.log`
      shows the `0x052C (0xD, 1)` split request when no WorkerW layer existed,
      the Worker's appearance, `verified=True` with alpha 255, and Progman
      children ordered `SHELLDLL_DefView` → player → system WorkerW
- [ ] Desktop right-click menu (on empty wallpaper area) still works
- [ ] Win+D (Show desktop) keeps the wallpaper playing
- [ ] Second monitor / mixed-DPI setup: note which monitors are covered
      (WorkerW normally spans the primary monitor)

### Playback

- [ ] 1080p and 4K H.264 files loop seamlessly with no black flash
- [ ] HEVC file plays when the HEVC Video Extension is installed
- [ ] Play/Pause/Stop/Mute/volume from the main window and from the tray menu
- [ ] Clicking the manual Pause control on the left stops animation
      immediately with **no black borders, black blocks, empty gaps or
      flicker**; the last frame stays byte-for-byte on the same surface,
      with identical size, position and fit. `debug.log` says
      `Wallpaper paused in place … media surface kept`
- [ ] Resume continues from that exact frame with no reopen, seek,
      black flash or first-frame jump (repeat 20 times)
- [ ] Losing focus / switching to another app does not produce black
      borders in either animation mode, and returning resumes (Mode A)
      or keeps animating (Mode B) without resetting the background
- [ ] Repeat Pause → Resume at least 20 times and switch wallpapers
      several times: no crash, stuck black frame, steadily growing
      private memory or handle count
- [ ] Change Fill/Fit/Stretch while paused: the frozen frame re-fits
      on the live surface without starting the animation or black bars
- [ ] Preview in the main window stays muted and stops on Stop; while
      paused it freezes in place without a bitmap swap or black bands
- [ ] Dropping a supported video onto the window applies it; dropping a
      non-video shows a friendly error
- [ ] Applying a video adds it to Recent wallpapers, and a recent item reapplies
      it in one click
- [ ] Fill/Fit/Stretch changes both the preview and actual desktop immediately
- [ ] Playback speed and Loop playback changes are honored by preview and
      desktop playback
- [ ] Moving the volume slider while muted unmutes and updates the percentage

### v1.2.0 native interface and branding

- [ ] The new cat/image mark appears in the EXE properties, taskbar button,
      window title bar, system tray and app header; it remains recognizable
      at 100%, 125% and 150% display scaling
- [ ] The Playing on desktop/Paused/Ready/No wallpaper header status matches
      the actual desktop state
- [ ] Volume can be dragged smoothly, clicked to a position and adjusted by
      keyboard; the percentage and mute state stay in sync
- [ ] Dark, Light and System themes render readable Settings-style cards,
      controls and ComboBox popups; the title-bar caption follows the app
      surface and selected theme
- [ ] The scrollable window remains usable at its minimum size and common
      Windows display-scaling values (100%, 125%, 150%)
- [ ] Open location reveals the current video in File Explorer, and Open logs
      opens `%LocalAppData%\Intonaco`

### Power / shell events

- [ ] Lock (Win+L) pauses; unlock resumes
- [ ] Sleep pauses; wake reattaches (wallpaper visible again within ~3 s)
- [ ] Kill `explorer.exe` in Task Manager and restart it: the wallpaper
      reattaches automatically (`TaskbarCreated` handling)
- [ ] Resolution change / monitor disconnect re-fits the wallpaper

### v2.0.0 functional checklist (plan Section 4)

- [ ] Wallpaper fills the monitor completely with **no black bars** while
      playing *and* while paused (check a 21:9 source on a 16:9 display and
      a portrait source on a landscape display)
- [ ] Pausing does **not** replace the video: the same `MediaElement`
      keeps presenting the last frame and no second layer appears. CPU /
      GPU Video Decode fall to idle because the media clock is stopped;
      `debug.log` shows `Wallpaper paused in place … media surface kept`
- [ ] Setting the frame rate to **30 fps** lowers GPU/CPU utilisation versus
      **60 fps** for the same clip; `debug.log` shows
      `Frame-rate gate engaged: target 30 fps (interval 33.3 ms)` and a
      `measured presentation` value close to the target
- [ ] Switching focus to Discord, a browser or a game auto-pauses the
      wallpaper within ~1.5 s (`Auto-pause: foreground is 'chrome' …`)
- [ ] Returning focus to the desktop / Explorer auto-resumes it
      (`Auto-resume: foreground returned to desktop …`)
- [ ] Shell overlays (Start menu, Search, Action Center, lock screen) do
      **not** cause a pause/resume flicker
- [ ] "Start with Windows" creates
      `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Intonaco` with
      `"<path>\Intonaco.exe" --minimized`; disabling the toggle deletes it
- [ ] Launching with `--minimized` (or `-m`) starts silently in the system
      tray with no window flash and the wallpaper already playing
- [ ] Tray context menu works: Show Intonaco, Play/Pause, Choose video…,
      Mute, Settings, Exit
- [ ] Last wallpaper path, volume, fit, frame rate, speed and loop setting
      all restore automatically after a reboot
- [ ] An invalid/unsupported video shows a friendly toast
      ("Unable to play video: Unsupported codec or format…") and the app
      keeps running; a `.flv`/`.rmvb` file is rejected before the decoder
      is even created
- [ ] Windows 11 Fluent dark theme is applied (no neon/glow): window
      `#1E1E1E`, cards `#272727`, subtle white-alpha borders, `#60CDFF`
      accent only on the primary button, active switches and slider fill

### v2.0.2 manual pause, focus modes and restart persistence

- [ ] With **Pause when app loses focus** (default), switch to a browser or
      game: animation updates stop within ~1.5 s and the background stays
      fully rendered; returning to the desktop/Explorer resumes smoothly
- [ ] With **Keep animating when unfocused**, the wallpaper keeps animating
      while another app is used; returning to Intonaco does not reset or
      re-seek it
- [ ] Changing between the two modes while played, manually paused and
      focus-paused in turn: the manual pause is never resumed by a mode
      change or by a focus event
- [ ] Manual Pause/Resume works identically in both modes, including from
      the tray menu
- [ ] Quit and relaunch Intonaco (and reboot at least once): the chosen
      mode is still selected in **Background animation when unfocused**
- [ ] A 21:9 clip on a 16:9 desktop and a portrait clip on a landscape
      desktop show no black bars while playing, manually paused, resumed,
      focus-paused and unfocused-animating

### Fullscreen detection

- [ ] Launching a fullscreen game pauses the wallpaper within ~2 s
- [ ] Exiting the game resumes it
- [ ] A merely *maximized* browser window does **not** pause playback when
      only the legacy fullscreen behaviour is wanted — note that with
      "Pause when other apps are focused" enabled (the v2 default) a
      maximized browser *does* pause, which is the intended Lively-like
      behaviour; turn the toggle off to restore v1 behaviour

### v2.0.0 installer

- [ ] Run `Intonaco-v2.0.0-win-x64-setup.exe` as a standard user: setup
      completes without an administrator prompt, creates the Start Menu
      shortcut, respects the optional desktop-shortcut choice, and the
      post-install launch option opens the current interface
- [ ] Installed app path, icon, version and uninstall entry are correct
      in File Explorer and Windows Settings → Apps
- [ ] Apply a wallpaper with the installed build; minimize to tray,
      pause/resume from the tray, enable Start with Windows, sign out/in
      if practical, and confirm the installed path still launches
- [ ] Run the same installer again over the existing installation:
      settings, recents and Start with Windows survive; the app still
      launches from both shortcuts
- [ ] Uninstall from Settings: program files, shortcuts and the
      `HKCU\...\Run\Intonaco` value are removed, while
      `%LocalAppData%\Intonaco\settings.json` remains for a future install

### Performance measurement

Record Task Manager / Performance Monitor values during the same 1080p
H.264 playback in each state. The helper script samples process RAM/CPU
for repeatable comparisons:

```powershell
.\tools\Measure-IntonacoPerformance.ps1 -Label "v2.0 playing main window" -DurationSeconds 20
# Minimize Intonaco to the tray, wait 10 seconds, sample again.
.\tools\Measure-IntonacoPerformance.ps1 -Label "v2.0 playing tray" -DurationSeconds 20
# Pause from the tray, wait 10 seconds, sample again.
.\tools\Measure-IntonacoPerformance.ps1 -Label "v2.0 paused" -DurationSeconds 20
# Focus another app (browser/game) with auto-pause on, wait 10 seconds, sample.
.\tools\Measure-IntonacoPerformance.ps1 -Label "v2.0 auto-paused (other app focused)" -DurationSeconds 20
```

- [ ] Record average/max private memory and working set for playing
      with the settings window visible, playing hidden in tray, paused,
      stopped idle, and fullscreen-paused; compare the same sequence
      with v1.2.0 where practical (v1.2.0 was reported around 100–150 MB
      playing and about 150 MB after Pause on the reporter's PC)
- [ ] Average CPU % and GPU Video Decode % are materially lower when
      the preview window is hidden than when it is visible, and fall to
      idle noise after Pause/fullscreen pause
- [ ] Confirm pause/resume has no visible stutter, black borders or frame jump; compare private bytes and CPU in the performance samples above

### Release hygiene

- [ ] Capture a genuine release screenshot of the main window and live desktop;
      do not use mockups as evidence of a tested build
- [ ] Confirm the published installer and zips run from a clean folder
      on a machine without the .NET SDK (installer/self-contained) /
      with only the runtime (framework-dependent)
- [ ] Confirm `SHA256SUMS.txt` matches the setup EXE, portable EXE and
      both zips in the release

## Known limitations

- **Frame-rate setting is a WPF presentation hint, not a decoder limit.**
  In Windows mode, `Timeline.DesiredFrameRate` is applied without a
  continuous `CompositionTarget.Rendering` polling hook. The underlying
  decoder can still consume resources at the source frame cadence; do not
  claim measured FPS or CPU savings based on the setting alone.
  MPV uses the source cadence and disables this Windows-mode control.
  Benchmark with real clips and hardware for resource-use claims.
- Pause keeps the Media Foundation source open so the last decoded frame
  stays on the native video surface. Decoder buffers therefore remain
  allocated while paused, although the media clock is stopped and no new
  frames are decoded or presented. This is deliberate: closing the source
  or swapping it for a WPF bitmap is what produced the black borders.
  Stop fully releases the source.
- In the default Windows backend, supported codecs and containers depend
  on the installed Media Foundation components (for example, HEVC may need
  additional Windows codec support). Intonaco recognizes some uncommon
  container extensions without promising decoding. The optional MPV backend
  supports additional containers through a separately installed MPV build.
- Foreground auto-pause polls `GetForegroundWindow()` every 1.2 s, so a
  focus change can take up to ~1.2 s to suspend playback (and the same to
  resume). Borderless/fullscreen-exclusive games that do not take the
  foreground window may not be detected; the legacy fullscreen-coverage
  detector runs alongside it to cover that case.
- The WorkerW technique relies on the undocumented `0x052C` Progman message.
  It has been stable since Windows 7, but Microsoft could change it in a
  future release.


## Native media engine smoke test

Automated tests cover file preflight, container signature identification and
error classification. **Actual video decoding and hardware performance must
be verified on Windows hardware** with the installed Media Foundation codecs.

1. Test H.264 MP4 natively, including the settings preview closing after
   minimizing Intonaco to the system tray.
2. Test H.264 and HEVC MKV, VP9 WebM, AVI, MOV, WMV, MPEG transport streams,
   and any additional containers users request. Record which codec/Windows
   combinations work; recognition alone is not proof of decoder support.
3. Test an FLV, RMVB, OGV or MXF sample where native Windows lacks a suitable
   decoder. The failure must be actionable and must not claim universal
   native compatibility.
4. Select a corrupt or audio-only file while a working wallpaper is already
   playing. Verify playback fails safely and the last successfully opened
   wallpaper is restored instead of leaving an unusable desktop.
5. Test the supplied Satoru 3840×2160 MP4 with oversized pixel-aspect
   metadata: verify it renders at the correct aspect ratio and its source
   file remains byte-for-byte unchanged.
6. Test genuine anamorphic video: it must preserve display aspect ratio and
   not be misclassified as the malformed 4K source.
7. Start a metadata-correction operation and choose a different wallpaper,
   or press Stop. Confirm the latest requested wallpaper wins.
8. Monitor CPU, RAM and GPU with identical clips while settings are
   visible, minimized, playing and paused. Windows mode must launch no
   external player process; MPV mode should run exactly one MPV process.
   Confirm the MPV mode does not decode a second UI-preview copy.
9. Confirm drag-and-drop, file picker and recent-wallpaper list are
   consistent, and that unsupported image animation formats are not
   advertised as playable videos.
10. Review packaged files: no FFmpeg or MPV executable or codec libraries
    should be included. Verify the download size against the Windows-only build.

## Optional MPV backend smoke test

This is a separate Windows hardware test, **not** a claim that automated CI
can verify video playback on the desktop.

1. Without MPV, confirm the default Windows engine starts normally and
   choosing the optional MPV engine shows an actionable installation hint.
2. Install an MPV build with all its required DLLs, select **MPV (optional)**
   and apply H.264 MKV and a VP9/AV1 WebM clip. Verify the video appears
   behind Explorer desktop icons rather than in a floating window.
3. Check that cover/fill and fit preserve aspect ratio on 1080p, 4K, and
   ultrawide monitors. Confirm no flicker or stretched output after resize.
4. Pause manually, resume, seek, mute/unmute, change volume and speed, stop,
   loop and apply another file. No duplicate MPV subprocess should remain.
5. Switch MPV → Windows → MPV while a wallpaper is active. Verify each
   engine releases the previous one and settings persist across restart.
6. Minimize settings to tray. Confirm the optional MPV backend never starts
   a second Windows MediaElement decoder for the settings preview.
7. Restart Explorer; confirm that desktop attachment and paused state can
   recover without leaving an orphan MPV process.
8. If an MPV executable is removed or crashes, confirm that Intonaco shows
   a useful error, does not crash itself, and retains or restores a known
   working wallpaper when possible.
9. Compare working-set, GPU usage, startup time and energy consumption with
   the **same** video and hardware in both engines. Do not claim either
   is universally lighter without measurement.

