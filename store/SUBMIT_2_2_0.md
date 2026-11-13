# Intonaco Wallpaper 2.2.0 — Microsoft Store submission

**Status:** Package prepared and validated by GitHub Actions. **Not submitted or certified by Microsoft.**

The repository stores the Partner Center identity supplied for this app:
- Product: **Intonaco Wallpaper**
- Publisher: **Acev Apps Co.**
- Package identity: `AcevAppsCo.IntonacoWallpaper`
- Publisher DN: `CN=44221EAF-7759-44DD-9DA8-F00E45DF7108`
- Product Store ID: `9PBBDCR0C6LH`
- Version: **2.2.0.0**
- Architecture: **Windows x64**
- Package format: **`Intonaco-Wallpaper.msixupload`**, unsigned before Store submission

## Upload this file, not the GitHub EXE

After the official release workflow passes, download:
https://github.com/p4yy/Intonaco/releases/download/Intonaco-Wallpaper/Intonaco-Wallpaper.msixupload

Open **https://partner.microsoft.com/dashboard** and select the existing
**Intonaco Wallpaper** app/product identified by `9PBBDCR0C6LH`, or
complete the product reservation if your Partner Center account still requires it.
Start the app's submission or update, then open **Packages** and upload the
`.msixupload` file. If Partner Center reports a publisher/identity mismatch,
STOP and compare the exact Package Identity and Publisher against the app's
Product identity page; do not invent a new Publisher DN or bypass the check.

Microsoft automatically signs and distributes accepted MSIX packages for the
Store. The unsigned .msixupload should not be given to customers as a sideload
installer. The official GitHub Setup EXE is a separate unsigned installation
method and is **not** a suitable Store EXE submission without Authenticode
signatures and Store-compliant silent installation.

## Ready-to-copy product details

**App name:** Intonaco Wallpaper

**Short description:** Bring your Windows desktop to life with smooth, native video wallpapers.

**Category:** Personalization (confirm in Partner Center)

**Description:**

Intonaco Wallpaper turns your favorite local videos into live Windows desktop
backgrounds, displayed behind your desktop icons.

The built-in Intonaco GPU renderer uses Direct3D 11 and Windows Media Foundation.
Choose an MP4 or another Windows-supported video file, adjust screen fit and
playback speed, and return to your recent wallpapers in a clean, icon-led interface.

- Native video wallpaper playback with no external media player required
- Original video resolution and colors, subject to codec and GPU support
- Deep pause retains the displayed frame while releasing decoder resources
- Playback, loop, mute, volume and fit controls
- Automatically pause when another app is active, or keep playing
- Recent wallpaper history and optional start-with-Windows behavior
- System tray controls for quick access

Videos stay on your computer. Intonaco does not include a wallpaper download
service or upload your video files. Playback performance depends on video format,
codec and device hardware.

**What's new in version 2.2.0:**
Redesigned icon-led interface, integrated GPU playback by default, improved
wallpaper switching, and deep-pause resource management.

**Privacy policy:** https://github.com/p4yy/Intonaco/blob/main/docs/PRIVACY.md

**Support:** https://github.com/p4yy/Intonaco/issues

**Website/source:** https://github.com/p4yy/Intonaco

## You still need to complete these Partner Center fields

- Pricing and availability, distribution countries/markets and visibility
- Properties, accurate hardware/video requirements, accessibility disclosures
- Age/content rating questionnaire
- Official app logo (derived from `src/Intonaco/Assets/intonaco.ico`)
- **Real application screenshots** with wallpapers you own or are licensed to
  display. Do not upload simulated screenshots, third-party copyrighted imagery
  without permission or preview-only mockups.
- Correct publisher contact and support information
- Package validation on Partner Center; certification notes and review
- **Submit for certification**, then wait for Microsoft approval

## Verification

GitHub Actions compiles the x64 native GPU renderer, runs .NET tests, builds
a self-contained Store application, packs a 2.2.0.0 MSIX, verifies actual
manifest identity, checks embedded Intonaco.exe and logo resources, and wraps
a single MSIX in the `.msixupload` container.

That pipeline does **not** reserve a new product, sign for sideloading, upload to
Partner Center, execute on your physical GPU, verify screenshots/licensing, or
guarantee Store approval. Microsoft may ask for further information or fixes.

Official guidance:
- https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/create-app-submission
- https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/upload-app-packages
- https://learn.microsoft.com/en-us/windows/apps/publish/get-started
