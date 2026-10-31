# Install Tracker

CleanRift's “Install and Trace” is a user-mode real-time tracker. It does not
load a kernel driver and does not require a reboot.

## How it works

1. The setup program is selected.
2. CleanRift scans installed programs and takes a baseline snapshot. The
   baseline includes Program Files, ProgramData, AppData/Local,
   AppData/Roaming, AppData/LocalLow, Public Documents, Start Menu and Desktop.
3. Two real-time monitors start:
   - filesystem: `FileSystemWatcher` (`ReadDirectoryChangesW`) records
     created/renamed paths and drops files deleted during setup;
   - registry: an ETW kernel registry session records new keys and values.
     Events are filtered to the setup process tree and the standard MSI
     workers (`msiexec`, `TrustedInstaller`, `TiWorker`, `dism`).
4. The setup program runs normally. CleanRift waits for its process tree.
5. The monitors are stopped. If the filesystem monitor covered every root and
   did not overflow, the final filesystem snapshot is skipped. The final
   registry snapshot is still taken and merged with the ETW events as the
   authoritative fallback.
6. If a monitor failed or overflowed, CleanRift performs the full final
   snapshot and merges it with whatever the live monitors observed.
7. A newly created install directory outside the standard roots (for example
   `D:\Apps\Product`) is added when its creation time proves it is new.

## Safety rules

- Paths that existed in the baseline are never recorded as created. Updating an
  existing installation cannot cause the update to delete pre-existing files.
- CleanRift's own data, temporary files, reparse points, shared Windows folders
  and known cache directories are ignored.
- Desktop and Start Menu are tracked only for `.lnk`/`.url` files. A document
  the user saves there during setup is not an installed file.
- Registry removal targets remain validated again by the removal engine.

## Fallback

`FileSystemWatcher` has a fixed buffer. On a busy machine the buffer can
overflow. CleanRift records the overflow in the log and falls back to the final
snapshot for filesystem changes. The trace is still produced; only transient
changes that no longer exist at the end may be missing.

## Limitations

- Events are attributed by path and time, not by process ID. A different
  program writing to the same roots during the installation window can appear
  in the trace. The existing filters remove the common noise, but a busy system
  is still a busy system.
- Registry events are captured with an ETW kernel session when administrator
  rights are available. The final registry snapshot is still taken as the
  authoritative fallback, so a dropped ETW event cannot hide a change.
- A kernel minifilter would give exact process attribution and cannot be
  bypassed by buffer limits, but it requires driver signing and installation.
  That is intentionally out of scope for this beta.
