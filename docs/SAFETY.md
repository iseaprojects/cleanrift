# Safety, quarantine and undo

CleanRift keeps removed content recoverable by default.

## Quarantine

- Files and folders are moved to
  `%LOCALAPPDATA%\CleanRift\Quarantine\<batch>\items`.
- Registry keys are exported with `reg.exe`; registry values are stored in the
  batch manifest with their original type and data.
- A JSON manifest records the original path of every item.
- Existing paths are never overwritten during restore.
- Restores into protected Windows locations are rejected.
- Batches can be restored or deleted from **Tools → Quarantine and undo**.

## Automatic cleanup

- Preferences can delete batches older than 1–365 days (30 by default).
- A total size limit (2048 MB by default) removes the oldest batches first when
  the quarantine grows too large.
- Cleanup runs at startup and after saving Preferences.
- Batches younger than one day are never removed by the size limit, so the undo
  of a recent operation is preserved.
- Recent batches are also protected from the age rule until they are older than
  the configured retention period.

## Action history

- **Tools → Action history** lists removals, trace reverts, queue runs,
  quarantine restores and quarantine deletes.
- An action that produced a quarantine batch can be undone from the history.
- Restoring a batch marks the original removal as undone.
- Clearing the history does not delete quarantine batches.

## Registry backup

When “Back up removed registry keys and values” is enabled, every removed key
is exported from both the 64-bit and 32-bit registry views. A key that only
exists in the 32-bit view is therefore recoverable too.

## System Restore point

When enabled, CleanRift asks Windows to create a restore point before a removal
operation. System Restore can be disabled by policy; a failure is logged and
does not delete anything permanently by itself.

## Preview

Before deletion, the Removal Wizard and Force Removal show the number of files,
the number of registry items and the total size. The same dialog states whether
items will be quarantined or permanently deleted.

## Permanence

Disable quarantine in Preferences for a permanent delete. The confirmation
preview clearly warns when this is the case.
