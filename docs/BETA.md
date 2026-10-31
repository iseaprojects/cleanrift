# CleanRift 1.0 Beta 2 — scope and known limitations

## What is included

- Program scan: HKCU/HKLM uninstall keys in both registry views, MSI
  `Installer\UserData`, and Microsoft Store packages.
- Standard uninstall, optional silent uninstall and a sequential uninstall
  queue.
- Standard uninstall plus the Removal Wizard for leftover files, folders and
  registry entries.
- Force Removal, including batch mode.
- Quarantine with undo, registry backups, optional System Restore points and a
  clear pre-removal preview (see `docs/SAFETY.md`).
- Install and Trace with real-time filesystem events, an ETW registry session,
  a live event list and a snapshot fallback (see `docs/TRACKER.md`).
- Optional Explorer “Install and trace” context menu for setup files.
- Startup manager: Run/RunOnce, StartupApproved, scheduled tasks and an
  estimated startup-impact column.
- HTML export of programs and startup items.
- Light, dark and automatic themes; English and Spanish UI.
- Single instance, automatic elevation, optional SkipUAC task and jump list.

## Disabled in this beta

These controls are visible in Preferences but read-only, with an explanatory
note:

- Check for updates and beta versions.
- Control Panel icon.
- Total Commander navigation.
- Software update groups (sub-items).
- Recent-items popup and recent-items toolbar button.

## Known limitations

- **No kernel driver.** Filesystem changes are captured with
  `ReadDirectoryChangesW` and registry changes with an ETW kernel session. If
  either monitor is unavailable, the before/after snapshot is used as the
  fallback.
- **Quarantine uses disk space** until the batch is deleted from Tools.
- **System Restore may be disabled** by policy; removal continues if the
  restore point cannot be created.
- **Unsigned build.** Windows SmartScreen may warn until the executable is
  code-signed.
- **x64 only.** The published build targets Windows 10/11 x64.
- **No auto-update.** Check the project page for new builds.
- **No crash uploader.** Errors are written to
  `%LOCALAPPDATA%\CleanRift\error.log`.
- **Native dark controls.** The closed state of some native WinForms controls
  (notably combo boxes) can still be painted by Windows in a light color.
- **Store packages** are removed through `Remove-AppxPackage`; per-user and
  provisioned packages can behave differently.
- **Driver-based monitoring** used by some commercial tools is not included.
- **Accessibility** has not been fully tested. Window position is restored
  across the current virtual desktop, including secondary monitors.

## Safety notes for testers

- **Working name.** “CleanRift” has not been trademark-cleared. Verify the name,
  domain and any conflict in your jurisdiction before a public launch.
- Use a virtual machine or a disposable Windows installation for destructive
  tests.
- Export the program list to HTML before testing Force Removal.
- Keep a backup of `%LOCALAPPDATA%\CleanRift` if you want to preserve traces and
  settings.
- Test uninstallers that require a reboot separately; CleanRift reports
  scheduled-for-reboot items but does not reboot the machine.
- Do not run CleanRift and another uninstaller on the same program at the same
  time.

## Reporting a bug

Include:

1. Windows version and build.
2. CleanRift version (`1.0 Beta 2`).
3. Steps to reproduce.
4. Whether the app was portable or installed.
5. Relevant lines from `%LOCALAPPDATA%\CleanRift\error.log` with private paths
   removed.

Do not attach full registry exports or personal data.
