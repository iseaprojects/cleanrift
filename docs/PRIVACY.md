# Privacy

CleanRift is local-only software.

## What CleanRift does not do

- No telemetry, analytics or usage tracking.
- No network requests for updates, ads or licensing.
- No account, registration or license key.
- No upload of program lists, traces, logs or machine information.

## Data stored on your computer

CleanRift stores its data under `%LOCALAPPDATA%\CleanRift`:

| File | Purpose |
| --- | --- |
| `preferences.xml` | Window, list and UI preferences |
| `state.json` | Last category, hidden entries, known install dates |
| `cache.dat` | Program list and size cache (CleanRift format) |
| `*_Traces.xml` | Install traces created by Install and Trace |
| `DisabledStartupItems.json` | Startup entries disabled from CleanRift |
| `error.log`, `elevation.log`, `instance.log`, `jumplist.log` | Diagnostics |

The HTML export contains the program list and the computer name. It is written
only to the location you choose.

## Registry

CleanRift reads uninstall, startup and Appx information from Windows. It writes
registry values only when you explicitly change a startup item (for example,
disabling a Run entry through `StartupApproved`). The installer creates the
usual uninstall entry for CleanRift itself.

## Third-party programs

When you uninstall a program, CleanRift runs that program's own uninstaller. The
third-party uninstaller may contact its vendor or collect data according to its
own privacy policy. CleanRift does not control that behavior.

## Removing your data

Close CleanRift and delete `%LOCALAPPDATA%\CleanRift`. The installer does not delete
this folder automatically, so traces and settings survive upgrades.
