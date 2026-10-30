using System.Text;
using Microsoft.Win32;

namespace CleanRift.Core;

/// <summary>
/// Deletes leftover items. Direct deletion is attempted first; anything refused
/// by Windows is replayed in one elevated instance, and files locked by running
/// processes are scheduled for deletion on the next reboot through
/// MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT).
/// </summary>
public sealed class RemovalEngine
{
    public event Action<string>? Progress;

    private readonly object _lock = new();
    private readonly JobResult _result = new();
    private readonly List<string> _log = new();

    public IReadOnlyList<string> Log
    {
        get { lock (_lock) return _log.ToList(); }
    }

    public JobResult Remove(LeftoverItem root, CancellationToken token)
    {
        var items = Collect(root);
        return RemoveItems(items, token);
    }

    public JobResult RemoveItems(IEnumerable<RemovalJobItem> items, CancellationToken token)
    {
        lock (_lock)
        {
            _result.Success = false;
            _result.Deleted = 0;
            _result.Failed = 0;
            _result.ScheduledForReboot = 0;
            _result.Error = "";
            _result.Errors.Clear();
            _log.Clear();
        }

        var pending = new List<RemovalJobItem>();
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            if (!PathSafety.IsSafeRemovalItem(item, out var error))
            {
                lock (_lock)
                {
                    _result.Failed++;
                    _result.Errors.Add((item?.Path ?? "(null)") + ": " + error);
                }
                continue;
            }

            pending.Add(item);
        }

        var needsElevation = new List<RemovalJobItem>();
        foreach (var item in pending)
        {
            token.ThrowIfCancellationRequested();
            bool ok;
            try
            {
                ok = DeleteDirect(item, out var accessDenied);
                if (ok)
                {
                    lock (_lock) _result.Deleted++;
                }
                else if (accessDenied)
                {
                    needsElevation.Add(item);
                }
                else
                {
                    Schedule(item, out var scheduled);
                    lock (_lock)
                    {
                        if (scheduled)
                            _result.ScheduledForReboot++;
                        else
                            _result.Failed++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _result.Failed++;
                    _result.Errors.Add(item.Path + ": " + ex.Message);
                }
            }
        }

        if (needsElevation.Count > 0)
        {
            if (Elevation.IsElevated)
            {
                foreach (var item in needsElevation)
                {
                    if (DeleteDirect(item, out _))
                    {
                        lock (_lock) _result.Deleted++;
                    }
                    else
                    {
                        Schedule(item, out var scheduled);
                        lock (_lock)
                        {
                            if (scheduled)
                                _result.ScheduledForReboot++;
                            else
                                _result.Failed++;
                        }
                    }
                }
            }
            else
            {
                Progress?.Invoke("Requesting administrator rights...");
                var elevated = Elevation.ExecuteRemoval(needsElevation);
                if (elevated == null || !elevated.Success)
                {
                    lock (_lock)
                    {
                        _result.Failed += needsElevation.Count;
                        _result.Errors.Add(elevated?.Error ?? "Elevation was cancelled");
                    }
                }
                else
                {
                    lock (_lock)
                    {
                        _result.Deleted += elevated.Deleted;
                        _result.Failed += elevated.Failed;
                        _result.ScheduledForReboot += elevated.ScheduledForReboot;
                        _result.Errors.AddRange(elevated.Errors);
                    }
                }
            }
        }

        lock (_lock)
            _result.Success = _result.Failed == 0;
        return _result;
    }

    public static List<RemovalJobItem> Collect(LeftoverItem root)
    {
        var result = new List<RemovalJobItem>();
        void Add(RemovalJobItem item)
        {
            if (PathSafety.IsSafeRemovalItem(item, out _))
                result.Add(item);
        }

        void Walk(LeftoverItem item)
        {
            // Container roots (File System / Registry) always descend.
            if (item.Kind == LeftoverKind.Folder && string.IsNullOrEmpty(item.FullPath))
            {
                foreach (var child in item.Children)
                    Walk(child);
                return;
            }

            if (!item.Checked)
            {
                foreach (var child in item.Children)
                    Walk(child);
                return;
            }

            switch (item.Kind)
            {
                case LeftoverKind.Folder when !string.IsNullOrWhiteSpace(item.FullPath):
                    if (AllChecked(item))
                        Add(new RemovalJobItem { Kind = "folder", Path = item.FullPath });
                    else
                        foreach (var child in item.Children)
                            Walk(child);
                    break;
                case LeftoverKind.File when !string.IsNullOrWhiteSpace(item.FullPath):
                    Add(new RemovalJobItem { Kind = "file", Path = item.FullPath });
                    break;
                case LeftoverKind.RegistryKey when !string.IsNullOrWhiteSpace(item.FullPath):
                    if (AllChecked(item))
                        Add(new RemovalJobItem { Kind = "key", Path = item.FullPath });
                    else
                        foreach (var child in item.Children)
                            Walk(child);
                    break;
                case LeftoverKind.RegistryValue when !string.IsNullOrWhiteSpace(item.FullPath):
                    Add(new RemovalJobItem
                    {
                        Kind = "value",
                        Path = item.FullPath,
                        ValueName = item.ValueName
                    });
                    break;
            }
        }

        Walk(root);
        return result;
    }

    private static bool AllChecked(LeftoverItem item)
    {
        foreach (var child in item.Children)
        {
            if (!child.Checked || !AllChecked(child))
                return false;
        }

        return true;
    }

    private bool DeleteDirect(RemovalJobItem item, out bool accessDenied)
    {
        accessDenied = false;
        switch (item.Kind)
        {
            case "file":
                return DeleteFile(item.Path, out accessDenied);
            case "folder":
                return DeleteFolder(item.Path, out accessDenied);
            case "key":
                return DeleteRegistryKey(item.Path, out accessDenied);
            case "value":
                return DeleteRegistryValue(item.Path, item.ValueName, out accessDenied);
            default:
                return false;
        }
    }

    private bool DeleteFile(string path, out bool accessDenied)
    {
        accessDenied = false;
        if (PathSafety.IsProtectedPath(path))
        {
            _log.Add("Refused to delete protected file: " + path);
            return false;
        }

        try
        {
            if (!File.Exists(path))
                return true;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Delete(path);
            _log.Add("Deleted file: " + path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            accessDenied = true;
            return false;
        }
        catch (Exception ex)
        {
            _log.Add("Failed to delete file: " + path + " (" + ex.Message + ")");
            return false;
        }
    }

    private bool DeleteFolder(string path, out bool accessDenied)
    {
        accessDenied = false;
        if (PathSafety.IsProtectedPath(path))
        {
            _log.Add("Refused to delete protected folder: " + path);
            return false;
        }

        try
        {
            if (!Directory.Exists(path))
                return true;
            DeleteTree(path);
            _log.Add("Deleted folder: " + path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            accessDenied = true;
            return false;
        }
        catch (Exception ex)
        {
            _log.Add("Failed to delete folder: " + path + " (" + ex.Message + ")");
            return false;
        }
    }

    /// <summary>
    /// Deletes a directory tree without following reparse points. A junction or
    /// symbolic link is removed as a link; its target is never traversed.
    /// </summary>
    private static void DeleteTree(string directory)
    {
        var attributes = File.GetAttributes(directory);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(directory, recursive: false);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var entryAttributes = File.GetAttributes(entry);
            if ((entryAttributes & FileAttributes.Directory) != 0)
            {
                if ((entryAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(entry, recursive: false);
                    continue;
                }

                DeleteTree(entry);
                continue;
            }

            if ((entryAttributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(entry, entryAttributes & ~FileAttributes.ReadOnly);
            File.Delete(entry);
        }

        var directoryAttributes = File.GetAttributes(directory);
        if ((directoryAttributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(directory, directoryAttributes & ~FileAttributes.ReadOnly);
        Directory.Delete(directory, recursive: false);
    }

    private bool DeleteRegistryKey(string fullPath, out bool accessDenied)
    {
        accessDenied = false;
        if (PathSafety.IsProtectedRegistryKey(fullPath, out var reason))
        {
            _log.Add("Refused to delete protected registry key: " + fullPath + " (" + reason + ")");
            return false;
        }

        var parsed = LeftoverScanner.ParseKeyPath(fullPath);
        if (parsed == null)
            return false;
        var (hive, subPath) = parsed.Value;
        var registryHive = hive == "HKEY_LOCAL_MACHINE" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        var deleted = false;
        var failed = false;
        var denied = false;

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(registryHive, view);
                using var key = baseKey.OpenSubKey(subPath);
                if (key != null)
                {
                    baseKey.DeleteSubKeyTree(subPath, throwOnMissingSubKey: false);
                    deleted = true;
                    _log.Add("Deleted registry key: " + fullPath);
                }
            }
            catch (UnauthorizedAccessException)
            {
                denied = true;
            }
            catch (Exception ex)
            {
                failed = true;
                _log.Add("Failed to delete registry key: " + fullPath + " (" + ex.Message + ")");
            }
        }

        // A key that is already absent in both views is a success: the goal
        // state has been reached.
        accessDenied = denied && !deleted;
        return deleted || (!failed && !denied);
    }

    private bool DeleteRegistryValue(string fullPath, string valueName, out bool accessDenied)
    {
        accessDenied = false;
        if (PathSafety.IsProtectedRegistryKey(fullPath, forValue: true, out var reason))
        {
            _log.Add("Refused to delete protected registry value: " + fullPath + " (" + reason + ")");
            return false;
        }

        var parsed = LeftoverScanner.ParseKeyPath(fullPath);
        if (parsed == null)
            return false;
        var (hive, subPath) = parsed.Value;
        var registryHive = hive == "HKEY_LOCAL_MACHINE" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        var deleted = false;
        var failed = false;
        var denied = false;

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(registryHive, view);
                using var key = baseKey.OpenSubKey(subPath, writable: true);
                if (key == null)
                    continue;
                if (key.GetValueNames().Any(n => string.Equals(n, valueName, StringComparison.OrdinalIgnoreCase)))
                {
                    key.DeleteValue(valueName, throwOnMissingValue: false);
                    deleted = true;
                    _log.Add("Deleted registry value: " + fullPath + "\\" + valueName);
                }
            }
            catch (UnauthorizedAccessException)
            {
                denied = true;
            }
            catch (Exception ex)
            {
                failed = true;
                _log.Add("Failed to delete registry value: " + fullPath + " (" + ex.Message + ")");
            }
        }

        // A value that no longer exists is a success.
        accessDenied = denied && !deleted;
        return deleted || (!failed && !denied);
    }

    private void Schedule(RemovalJobItem item, out bool scheduled)
    {
        scheduled = false;
        if (PathSafety.IsProtectedPath(item.Path))
        {
            _log.Add("Refused to schedule protected path for reboot: " + item.Path);
            return;
        }

        try
        {
            if (item.Kind == "file" && File.Exists(item.Path))
            {
                if (NativeMethods.MoveFileEx(item.Path, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT))
                {
                    scheduled = true;
                    _log.Add("Scheduled for reboot: " + item.Path);
                }
                return;
            }

            if (item.Kind == "folder" && Directory.Exists(item.Path))
            {
                var files = new List<string>();
                var dirs = new List<string>();
                CollectTree(item.Path, files, dirs);

                var allOk = true;
                foreach (var file in files)
                {
                    if (!NativeMethods.MoveFileEx(file, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT))
                        allOk = false;
                }

                foreach (var dir in dirs.OrderByDescending(d => d.Length))
                {
                    if (!NativeMethods.MoveFileEx(dir, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT))
                        allOk = false;
                }

                if (NativeMethods.MoveFileEx(item.Path, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT))
                {
                    scheduled = true;
                    _log.Add("Scheduled folder for reboot: " + item.Path);
                }
                else
                {
                    scheduled = allOk;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Add("Could not schedule for reboot: " + item.Path + " (" + ex.Message + ")");
        }
    }

    /// <summary>
    /// Enumerates a directory tree for reboot scheduling without following
    /// reparse points.
    /// </summary>
    private static void CollectTree(string directory, List<string> files, List<string> dirs)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                dirs.Add(entry);
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                    CollectTree(entry, files, dirs);
                continue;
            }

            files.Add(entry);
        }
    }

    public static bool IsProtected(string path) => PathSafety.IsProtectedPath(path);

    public static JobResult ApplyJob(RemovalJob job)
    {
        if (!PathSafety.ValidateJob(job, out var validationError))
        {
            return new JobResult
            {
                Success = false,
                Failed = 1,
                Error = validationError
            };
        }

        if (string.Equals(job.Kind, "startup", StringComparison.OrdinalIgnoreCase) &&
            job.Startup != null)
        {
            var ok = StartupManager.ApplyStartupJob(job.Startup, out var error, out var deleted);
            return new JobResult
            {
                Success = ok,
                Deleted = deleted ? 1 : 0,
                Failed = ok ? 0 : 1,
                Error = error
            };
        }

        var engine = new RemovalEngine();
        return engine.RemoveItems(job.Items, CancellationToken.None);
    }

    public static string BuildReport(IEnumerable<LeftoverItem> roots)
    {
        var builder = new StringBuilder();
        foreach (var root in roots)
        {
            foreach (var item in root.Flatten())
            {
                builder.AppendLine(item.Kind + "\t" + item.DetailText);
            }
        }

        return builder.ToString();
    }
}
