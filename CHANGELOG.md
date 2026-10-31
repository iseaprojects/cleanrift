# Changelog

## 1.0.0-beta.2 — 2026-10-30

- Added quarantine with restore, registry backups, System Restore points and a
  clear pre-removal summary (see `docs/SAFETY.md`).
- Added optional Explorer “Install and trace” context menu, silent uninstall,
  uninstall queue, live tracker events and startup impact.
- Added the Quarantine and undo window and a per-item uninstall queue.

## Audit fixes and real-time tracker

- Install Tracker now watches filesystem changes in real time and captures
  registry changes with an ETW kernel session; the final snapshot remains the
  authoritative registry fallback. See `docs/TRACKER.md`.
- Tracks Start Menu, Desktop, LocalLow and newly created custom install
  folders; pre-existing paths are never recorded, so updates stay safe.
- Filesystem-only final snapshot when the live filesystem monitor covered every
  root, which makes tracing faster.
- Hardened elevated removal jobs, protected paths/registry trees and command
  execution (see `docs/AUDIT-2026-10.md`).
- Fixed Store-app uninstall quoting, process/GDI leaks, scan cancellation,
  tray/close behavior, multi-monitor restore and trace identity collisions.
- Added automated tests and made settings/cache/trace writes atomic.

## 1.0.0-beta.1 — 2026-10-30

First public beta.

### Added

- Program discovery from HKCU/HKLM uninstall keys (32/64-bit), MSI installer
  data and Microsoft Store packages.
- Standard uninstall, Removal Wizard for leftovers, Force Removal and batch
  Force Removal.
- Install and Trace with real-time filesystem events, registry snapshot diff
  and trace XML.
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
- Tracing uses real-time filesystem events plus a registry snapshot diff; it
  does not load a kernel driver.
