CLEANRIFT 1.0 BETA 1
==================

CleanRift removes programs completely, helps review the files and registry
entries they leave behind, traces new installations and manages startup items.

CleanRift is an independent project. It is not affiliated with, endorsed by or
sponsored by any third-party software vendor.


REQUIREMENTS
------------
- Windows 10 or Windows 11, 64-bit.
- The portable build includes the .NET runtime; no separate installation is
  required.


STARTING THE APP
----------------
Run CleanRift.exe. The application asks for administrator rights (UAC) because
uninstallers, the registry and protected folders require them.

Options:
  Preferences > General
      "Use Windows Task Scheduler..." creates a scheduled task so future starts
      can skip the UAC confirmation. You can turn it off at any time.

Command line switches:
  /install_from_task_bar   open the Install and Trace page
  /startup_from_task_bar   open the Startup page
  /store_apps_from_task_bar open the Microsoft Store page
  --job <file>             apply an elevated removal/startup job


DATA AND PRIVACY
----------------
CleanRift stores its settings, traces and cache in:

  %LOCALAPPDATA%\CleanRift

Files:
  preferences.xml          window, list and UI preferences
  state.json               small UI state (last category, hidden items)
  cache.dat                program list and size cache (CleanRift format)
  *_Traces.xml             install traces
  error.log, elevation.log, instance.log, jumplist.log
  DisabledStartupItems.json

CleanRift does not send telemetry and does not contact a server. It only opens
local files and the programs you explicitly ask it to run.

If you enable the optional SkipUAC scheduled task, turn it off in Preferences
before uninstalling. The uninstaller also tries to delete the task, but a
per-user uninstall may not have permission to remove an elevated task.

Removing a program may delete files and registry keys. Back up important data
before using Force Removal or the leftovers wizard. Deleted data cannot be
recovered by CleanRift.


BETA NOTES
----------
This is beta software. Known limitations:
- Software update groups are not implemented; the related preferences are
  disabled.
- Explorer integration, the Control Panel icon, Total Commander navigation and
  the recent-items toolbar are not available in this beta.
- The real-time install monitor driver used by some commercial tools is not
  included; tracing compares snapshots before and after an installation.
- The dark theme uses WinForms controls; the closed state of native combo boxes
  can still be painted by Windows in a light color.

Report bugs with the steps to reproduce and, if possible, the files in
%LOCALAPPDATA%\CleanRift (remove any private paths first).


LICENSE
-------
MIT License. See LICENSE.txt.

Copyright (c) 2026 CleanRift contributors.
