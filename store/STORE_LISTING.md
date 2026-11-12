# Intonaco Wallpaper — Microsoft Store listing copy

This is a standalone Microsoft Store listing. It must **not** point users
to the GitHub installer or require GitHub to install or update the app.

## Product
- **Reserved product name:** Intonaco Wallpaper (verify reservation in Partner Center)
- **Developer/publisher display:** Acev Dev Co **only if this is the verified Partner Center display name**
- **Category suggestion:** Personalization
- **Platform:** Windows desktop, x64
- **Application version:** 2.1.2.0 (Store MSIX version; once verified)
- **Pricing:** Set explicitly in Partner Center; do not assume free
- **Rating/content declaration:** Answer Partner Center's questionnaire accurately

## Short description
Bring your desktop to life with video wallpapers.

## Description
**Intonaco Wallpaper** brings video wallpapers to your Windows desktop
with a straightforward, native interface.

Choose a video from your computer, preview it, and apply it behind your
desktop icons. Adjust how it fits the screen, control playback, and
keep your preferred setup between sessions.

**Features**
- Play supported video files as desktop wallpapers.
- Preview wallpapers and quickly return to recently used files.
- Choose fit, playback speed, looping, mute, and volume.
- Decide whether animation pauses while another app is in focus.
- Resume your previous wallpaper and optionally start with Windows.
- Explore popular independent wallpaper-video websites using copyable links.

Intonaco uses local video files. The optional third-party catalog section
copies website addresses to your clipboard; it doesn't fetch, scrape,
automatically redirect, download, or distribute other creators' wallpapers.
Third-party websites have their own licensing terms.

Video formats, smoothness, and resource usage vary by Windows hardware and
the installed media capabilities. The standard Windows playback engine is
built in; an optional MPV engine may be used when separately installed.

## What's new in 2.1.2
- New compact section with copyable wallpaper source addresses.
- More reliable opening of the app from desktop and Start menu shortcuts.
- Cleaner wallpaper history layout for the narrow default window.
- Updated installer presentation and company metadata.

## Store submission requirements
1. Reserve *Intonaco Wallpaper* (the green availability check is not the reservation).
2. Open the product's **Product identity** in Partner Center.
3. Copy the *Package/Identity/Name* and complete *Package/Identity/Publisher* DN
   exactly. Do not infer them from the product name or publisher display.
4. Set *PublisherDisplayName* to the account-approved publisher text.
5. Run the separate Store MSIX workflow with these three values; **do not**
   upload its development-preview-identity artifact.
6. Test the resulting package on Windows 10/11, especially startup,
   single-instance shortcut activation, wallpaper playback, pause and tray.
7. Add **real** 16:9 screenshots of the actual software using wallpapers
   you are authorized to display, plus the exact official Intonaco app icon.
8. Set the support contact, privacy-policy URL if required, markets,
   category, pricing, age/content ratings and accessibility disclosures.
9. Confirm that Store package certification passes before announcing availability.

**Unverified:** The exact Partner Center identity and publisher details,
Store certification, package signing/re-signing and physical Windows QA.
The GitHub EXE/installer is not the Store submission.

**Publisher note:** An Inno Setup `AppPublisher` value and executable Company
metadata are NOT an Authenticode digital signature. Windows may show
"Unknown publisher" until a valid publisher certificate is used. Store
MSIX is re-signed as part of the Store certification process.
