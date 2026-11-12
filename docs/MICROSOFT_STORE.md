# Microsoft Store submission checklist

Intonaco is a WPF/.NET 8 Windows desktop app. GitHub's official installer
and portable EXE are **distribution builds, not certified Store packages**.
A successful Windows CI run is not a substitute for actual desktop QA or
Microsoft Store certification.

## Choose a packaging path

### Recommended: MSIX submission

- Create a Windows Application Packaging Project or an MSIX package with the
  Microsoft MSIX Packaging Tool.
- Use the app identity, publisher and reserved display name supplied in Partner
  Center. Do not invent a production package identity.
- Validate the package with the Windows App Certification Kit and perform
  installation/upgrade/uninstall testing.
- The Microsoft Store re-signs submitted MSIX/AppX packages, so no separately
  purchased CA certificate is required **for Store distribution**.
- The current GitHub CI produces Inno Setup EXE installers and portable builds,
  **not** an MSIX package; this step is outstanding.

### Alternative: existing Inno Setup EXE installer

- Submit a permanent, version-specific HTTPS **direct download** of the
  `Intonaco-vX.Y.Z-win-x64-setup.exe` release asset. Do not use a moving
  `latest` URL, and never silently replace the bytes behind a submitted URL.
- Microsoft requires the installer **and every included PE binary** to be
  authenticode-signed with a certificate chaining to a trusted CA. Our current
  CI does **not** sign any of those binaries; **this is a blocking prerequisite**.
- The installer must be fully offline and must support silent installation.
  CI tests Inno Setup with `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-`.
  Confirm those switches and the uninstall path on a clean Windows machine.
- Provide the x64 architecture, English language and installer parameters to
  Partner Center. Re-test silent upgrade and uninstall after signing.
- Store submission and certification happen in Partner Center; pushing a
  GitHub release does not publish it to the Store.

## Real-device acceptance checklist

- [ ] Windows 10 (1903 or newer) and Windows 11, on ordinary user accounts.
- [ ] Small, normal and maximized windows; screen reader, keyboard focus,
      100% / 125% / 150% and mixed-DPI monitor layouts.
- [ ] Use a real 1080p video, 4K video, portrait source, letterboxed source,
      malformed MP4 pixel-aspect metadata, and an unsupported codec.
- [ ] Pause/resume, seek, loop, volume, app focus changes, tray minimize/restore,
      Explorer restart, screen lock, sleep/wake, and startup after login.
- [ ] Run measured performance tests with identical clips in active,
      background, paused and stopped states; compare private bytes, CPU and GPU.
- [ ] Optional MPV installed/not installed, device-specific video embedding,
      IPC controls, crash handling and absence of orphan player processes.
- [ ] Confirm the app logo and real UI screenshots, privacy policy/claims,
      publisher links, support channel and third-party notices are correct.
- [ ] Submit only a signed and tested EXE installer, or a Store-validated MSIX
      package. Do not call a passing CI artifact Store-ready.

## Official Microsoft guidance

- [MSI/EXE app package requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements)
- [Uploading MSI/EXE installers](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/upload-app-packages)
- [Distributing a Win32 app through the Microsoft Store](https://learn.microsoft.com/en-us/windows/apps/distribute-through-store/how-to-distribute-your-win32-app-through-microsoft-store)
- [MSIX vs EXE signing requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/package-version-numbering)
