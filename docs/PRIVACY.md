# Intonaco Wallpaper — Privacy Policy

**Effective date: November 12, 2026**

This policy describes the desktop application **Intonaco Wallpaper**
for Windows, distributed by the developer and, if approved, through
the Microsoft Store under the registered publisher **Acev Apps Co.**

## Information handled by the app

Intonaco Wallpaper plays video files that **you select from your device**.
It keeps your chosen settings, recent wallpaper file paths and the most
recent wallpaper path in a local configuration file at:

`%LocalAppData%\Intonaco\settings.json`

For technical troubleshooting, the app writes local diagnostic and crash
logs in `%LocalAppData%\Intonaco`. Logs can contain video file paths,
desktop/Windows integration information and error details. When an MP4
needs a safe pixel-aspect metadata correction, the app may create a
local working copy in `%LocalAppData%\Intonaco\aspect-cache`. The
original video is not altered by that preparation step.

These files stay on your computer and are not automatically sent to
the developer by the app. A user who voluntarily attaches a log or
screenshot to a public GitHub issue should **review and redact** local
filenames, paths or any other sensitive information first.

## Collection, sharing and tracking

The current Intonaco Wallpaper application does **not** require an
account and does not implement developer-operated analytics, advertising
tracking, user profiling or upload of selected wallpapers to developer
servers. Wallpaper playback uses the locally selected video file.

The **Get live wallpapers** feature only copies a curated external
website address to the Windows clipboard; it does not download files,
load website content inside the app or open a browser automatically.

The optional About-window GitHub and donation links open in your browser
only when you select them. Those websites have their own terms and
privacy practices, which are outside this application's control.
Any optional third-party media backend that you install separately is
subject to that software's own practices.

Windows and the Microsoft Store may separately process device,
installation, diagnostic or Store-account information under Microsoft's
own privacy terms. That platform processing is distinct from any data
collected by Intonaco Wallpaper.

## Retention and your choices

Your recent wallpaper list can be cleared from inside the app. You can
change startup and playback preferences in its settings.

The app's installer deliberately preserves the local settings directory
during upgrades and uninstall. To remove the application's saved settings,
logs and cached copies yourself, quit Intonaco Wallpaper and delete
`%LocalAppData%\Intonaco`. This removes saved wallpaper preferences and
recent file paths; it does not delete your original wallpaper videos.

## Questions and support

For support or privacy questions, open an issue in the project's public
repository: https://github.com/p4yy/Intonaco/issues

Do not include personal data in public issues. This policy may be revised
if the app's behavior changes; revisions will be recorded in the source
repository.

Source repository: https://github.com/p4yy/Intonaco
