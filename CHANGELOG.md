# Changelog

## 1.0.0-beta.1 — 2026-10-30

First public beta.

### Added

- Program discovery from HKCU/HKLM uninstall keys (32/64-bit), MSI installer
  data and Microsoft Store packages.
- Standard uninstall, Removal Wizard for leftovers, Force Removal and batch
  Force Removal.
- Install and Trace with snapshot comparison and trace XML.
- Startup manager for Run/RunOnce, StartupApproved and scheduled tasks.
- HTML export for programs and startup items.
- Light, dark and automatic themes.
- English and Spanish UI generated from CleanRift's own translation sources.
- Single-instance handling, automatic elevation, optional SkipUAC task and
  taskbar jump list.
- Portable build and Inno Setup installer script.
- MIT license and project documentation.

### Changed

- Independent CleanRift identity: teal accent, vector icon set, own data folder,
  registry/task/mutex names and report templates.
- Data moved to `%LOCALAPPDATA%\CleanRift`; no other product's profile is read or
  written.
- Cache uses the `CLEANRIFT1` format and trace files use `CleanRiftTraces`.

### Known issues

- Update groups, Explorer integration, Control Panel icon, Total Commander
  navigation and recent-items toolbar are disabled in this beta.
- The build is unsigned.
- Native dark combo boxes can still paint a light closed state.
- Tracing uses snapshots rather than a real-time kernel driver.
