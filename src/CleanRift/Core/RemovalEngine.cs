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
        var pending = new List<RemovalJobItem>();
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
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
                        result.Add(new RemovalJobItem { Kind = "folder", Path = item.FullPath });
                    else
                        foreach (var child in item.Children)
                            Walk(child);
                    break;
                case LeftoverKind.File when !string.IsNullOrWhiteSpace(item.FullPath):
                    result.Add(new RemovalJobItem { Kind = "file", Path = item.FullPath });
                    break;
                case LeftoverKind.RegistryKey when !string.IsNullOrWhiteSpace(item.FullPath):
                    if (AllChecked(item))
                        result.Add(new RemovalJobItem { Kind = "key", Path = item.FullPath });
                    else
                        foreach (var child in item.Children)
                            Walk(child);
                    break;
                case LeftoverKind.RegistryValue when !string.IsNullOrWhiteSpace(item.FullPath):
                    result.Add(new RemovalJobItem
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
        if (IsProtected(path))
        {
            _log.Add("Refused to delete protected folder: " + path);
            return false;
        }

        try
        {
            if (!Directory.Exists(path))
                return true;
            ClearAttributes(path);
            Directory.Delete(path, recursive: true);
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

    private static void ClearAttributes(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private bool DeleteRegistryKey(string fullPath, out bool accessDenied)
    {
        accessDenied = false;
        var parsed = LeftoverScanner.ParseKeyPath(fullPath);
        if (parsed == null)
            return false;
        var (hive, subPath) = parsed.Value;
        var registryHive = hive == "HKEY_LOCAL_MACHINE" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        var deleted = false;
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
                _log.Add("Failed to delete registry key: " + fullPath + " (" + ex.Message + ")");
            }
        }

        accessDenied = denied && !deleted;
        return deleted;
    }

    private bool DeleteRegistryValue(string fullPath, string valueName, out bool accessDenied)
    {
        accessDenied = false;
        var parsed = LeftoverScanner.ParseKeyPath(fullPath);
        if (parsed == null)
            return false;
        var (hive, subPath) = parsed.Value;
        var registryHive = hive == "HKEY_LOCAL_MACHINE" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        var deleted = false;
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
                _log.Add("Failed to delete registry value: " + fullPath + " (" + ex.Message + ")");
            }
        }

        accessDenied = denied && !deleted;
        return deleted;
    }

    private void Schedule(RemovalJobItem item, out bool scheduled)
    {
        scheduled = false;
        try
        {
            if (item.Kind == "file" && File.Exists(item.Path))
            {
                NativeMethods.MoveFileEx(item.Path, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
                var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (error == 0)
                {
                    scheduled = true;
                    _log.Add("Scheduled for reboot: " + item.Path);
                }
                return;
            }

            if (item.Kind == "folder" && Directory.Exists(item.Path))
            {
                var files = Directory.EnumerateFiles(item.Path, "*", SearchOption.AllDirectories).ToList();
                var dirs = Directory.EnumerateDirectories(item.Path, "*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length).ToList();
                var allOk = true;
                foreach (var file in files)
                {
                    if (!NativeMethods.MoveFileEx(file, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT))
                        allOk = false;
                }

                foreach (var dir in dirs)
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

    public static bool IsProtected(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\');
            if (full.Length <= 3)
                return true;
            var appDir = AppContext.BaseDirectory.TrimEnd('\\');
            if (full.Equals(appDir, StringComparison.OrdinalIgnoreCase))
                return true;

            foreach (var root in LeftoverScanner.ProtectedPaths)
            {
                if (string.IsNullOrWhiteSpace(root))
                    continue;
                if (full.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windows) &&
                full.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            return true;
        }

        return false;
    }

    public static JobResult ApplyJob(RemovalJob job)
    {
        if (job.Kind == "startup" && job.Startup != null)
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
        var result = engine.RemoveItems(job.Items, CancellationToken.None);
        result.Errors.AddRange(engine.Log);
        return result;
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
