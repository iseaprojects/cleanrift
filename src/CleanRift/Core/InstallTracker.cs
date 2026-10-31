using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace CleanRift.Core;

public sealed class InstallSnapshot
{
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ValueData { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime TakenUtc { get; set; } = DateTime.UtcNow;
    public bool Truncated { get; set; }

    public int Count => Files.Count + Folders.Count + Keys.Count + Values.Count;
}

public sealed class SnapshotDiff
{
    public List<string> Files { get; } = new();
    public List<string> Folders { get; } = new();
    public List<string> RegistryKeys { get; } = new();
    public List<(string Key, string Name)> RegistryValues { get; } = new();

    public bool IsEmpty => Files.Count == 0 && Folders.Count == 0 && RegistryKeys.Count == 0 &&
                           RegistryValues.Count == 0;

    public int Count => Files.Count + Folders.Count + RegistryKeys.Count + RegistryValues.Count;
}

/// <summary>
/// Install and Trace implementation. Filesystem changes are captured in real
/// time by <see cref="LiveFileMonitor"/> while the setup program runs, and
/// before/after snapshots provide the registry difference and a fallback for
/// roots that cannot be watched. The result is stored as trace data consumed
/// by the Removal Wizard. No kernel driver is loaded.
/// </summary>
public sealed class InstallTracker
{
    private const int MaxEntriesPerRoot = 300_000;
    private const int MaxFileSystemEntries = 1_500_000;
    private const int MaxRegistryEntries = 300_000;

    public event Action<string>? Log;

    public InstallSnapshot TakeSnapshot(CancellationToken token, TimeSpan? maxDuration = null,
        bool includeFiles = true)
    {
        var snapshot = new InstallSnapshot();
        var deadline = DateTime.UtcNow + (maxDuration ?? TimeSpan.FromSeconds(90));
        var started = DateTime.UtcNow;

        // Registry first: it is fast and contains the uninstall entries the
        // wizard needs. The file system walk is much slower and may be
        // truncated on huge profiles; that must not hide registry changes.
        foreach (var (hive, view, path) in RegistryRoots())
        {
            if (token.IsCancellationRequested || DateTime.UtcNow > deadline)
            {
                snapshot.Truncated = true;
                break;
            }

            SnapshotRegistry(hive, view, path, snapshot, token, deadline, depth: 0);
        }

        if (includeFiles)
        {
            foreach (var root in FileRoots())
            {
                if (token.IsCancellationRequested || DateTime.UtcNow > deadline)
                {
                    snapshot.Truncated = true;
                    break;
                }

                Log?.Invoke("Indexing " + root);
                SnapshotDirectory(root, snapshot, token, deadline);
                Log?.Invoke($"  {root}: {snapshot.Files.Count} files, {snapshot.Folders.Count} folders so far");
            }
        }

        snapshot.TakenUtc = DateTime.UtcNow;
        Log?.Invoke((includeFiles ? "Snapshot" : "Registry-only snapshot") +
                    $" finished in {(int)(DateTime.UtcNow - started).TotalSeconds}s: " +
                    $"{snapshot.Files.Count} files, {snapshot.Folders.Count} folders, " +
                    $"{snapshot.Keys.Count} keys, {snapshot.Values.Count} values");
        return snapshot;
    }

    public static IEnumerable<string> FileRoots()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            string.IsNullOrWhiteSpace(userProfile)
                ? null
                : Path.Combine(userProfile, "AppData", "LocalLow")
        };

        return list.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<(string Hive, RegistryView View, string Path)> RegistryRoots()
    {
        yield return ("HKEY_LOCAL_MACHINE", RegistryView.Registry64, "SOFTWARE");
        yield return ("HKEY_LOCAL_MACHINE", RegistryView.Registry32, "SOFTWARE");
        yield return ("HKEY_CURRENT_USER", RegistryView.Registry64, "SOFTWARE");
        yield return ("HKEY_CURRENT_USER", RegistryView.Registry32, "SOFTWARE");
        yield return ("HKEY_LOCAL_MACHINE", RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        yield return ("HKEY_LOCAL_MACHINE", RegistryView.Registry32,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        yield return ("HKEY_CURRENT_USER", RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        yield return ("HKEY_CURRENT_USER", RegistryView.Registry32,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        yield return ("HKEY_LOCAL_MACHINE", RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
    }

    private static readonly string[] SkipRegistryPrefixes =
    {
        "Classes", "Microsoft", "Policies", "Clients", "RegisteredApplications",
        "WOW6432Node\\Classes", "WOW6432Node\\Microsoft", "WOW6432Node\\Policies"
    };

    private static readonly string[] AllowedRegistryPrefixes =
    {
        @"Microsoft\Windows\CurrentVersion\Uninstall",
        @"Microsoft\Windows\CurrentVersion\App Paths"
    };

    /// <summary>
    /// Normalizes a registry path relative to a SOFTWARE root so the skip rules
    /// also apply to nested paths (SOFTWARE\Microsoft, SOFTWARE\WOW6432Node\...).
    /// </summary>
    private static string NormalizeRegistryPath(string path)
    {
        var normalized = path;
        if (normalized.StartsWith("SOFTWARE\\", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["SOFTWARE\\".Length..];
        else if (normalized.Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase))
            normalized = "";
        if (normalized.StartsWith("WOW6432Node\\", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["WOW6432Node\\".Length..];
        return normalized;
    }

    private static bool SkipRegistryPath(string path)
    {
        var normalized = NormalizeRegistryPath(path);
        if (normalized.Length == 0)
            return false;

        var allowedBranch = false;
        foreach (var allowed in AllowedRegistryPrefixes)
        {
            if (normalized.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(allowed + "\\", StringComparison.OrdinalIgnoreCase))
                return false;
            if (allowed.StartsWith(normalized + "\\", StringComparison.OrdinalIgnoreCase))
                allowedBranch = true;
        }

        // Never prune a branch that leads to an allowed subtree (the parent of
        // Microsoft\Windows\CurrentVersion\Uninstall must still be visited).
        if (allowedBranch)
            return false;

        foreach (var prefix in SkipRegistryPrefixes)
        {
            if (normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void SnapshotDirectory(string root, InstallSnapshot snapshot, CancellationToken token,
        DateTime deadline)
    {
        // Folders that never hold an actual installation and can grow without
        // bound (browser/Store caches). Skipping them keeps the snapshot small
        // and, more importantly, leaves budget for the interesting roots.
        var skipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", "Package Cache", "WindowsApps", "$Recycle.Bin", "Temp",
            "Packages", "Microsoft", "Google", "Mozilla", "Cache", "Caches",
            "Code Cache", "GPUCache", "ShaderCache", ".git", "CleanRift"
        };

        var rootEntries = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            if (token.IsCancellationRequested || DateTime.UtcNow > deadline ||
                snapshot.Files.Count + snapshot.Folders.Count >= MaxFileSystemEntries)
            {
                snapshot.Truncated = true;
                return;
            }

            if (rootEntries >= MaxEntriesPerRoot)
            {
                snapshot.Truncated = true;
                return;
            }

            var current = stack.Pop();
            snapshot.Folders.Add(current);
            rootEntries++;

            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    snapshot.Files.Add(file);
                    rootEntries++;
                    if (rootEntries >= MaxEntriesPerRoot ||
                        snapshot.Files.Count + snapshot.Folders.Count >= MaxFileSystemEntries)
                    {
                        snapshot.Truncated = true;
                        return;
                    }
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(current))
                {
                    var name = Path.GetFileName(dir);
                    if (skipNames.Contains(name))
                        continue;
                    try
                    {
                        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                            continue;
                    }
                    catch
                    {
                        continue;
                    }

                    stack.Push(dir);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private void SnapshotRegistry(string hive, RegistryView view, string path, InstallSnapshot snapshot,
        CancellationToken token, DateTime deadline, int depth)
    {
        if (depth > 10 || skipRegistryDepth(path))
            return;
        if (token.IsCancellationRequested || DateTime.UtcNow > deadline ||
            snapshot.Keys.Count + snapshot.Values.Count >= MaxRegistryEntries)
        {
            snapshot.Truncated = true;
            return;
        }

        try
        {
            var registryHive = hive == "HKEY_LOCAL_MACHINE" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            using var baseKey = RegistryKey.OpenBaseKey(registryHive, view);
            using var key = baseKey.OpenSubKey(path);
            if (key == null)
                return;

            var full = hive + "\\" + path;
            snapshot.Keys.Add(full);

            foreach (var valueName in key.GetValueNames())
            {
                var id = full + "\u0001" + valueName;
                snapshot.Values.Add(id);
                try
                {
                    snapshot.ValueData[id] = HashValue(key.GetValue(valueName));
                }
                catch
                {
                    snapshot.ValueData[id] = "";
                }

                if (snapshot.Keys.Count + snapshot.Values.Count >= MaxRegistryEntries)
                {
                    snapshot.Truncated = true;
                    return;
                }
            }

            foreach (var sub in key.GetSubKeyNames())
            {
                var subPath = path + "\\" + sub;
                if (SkipRegistryPath(subPath))
                    continue;
                SnapshotRegistry(hive, view, subPath, snapshot, token, deadline, depth + 1);
            }
        }
        catch
        {
            // ignore inaccessible key
        }
    }

    private static bool skipRegistryDepth(string path) => SkipRegistryPath(path);

    private static string HashValue(object? value)
    {
        return value switch
        {
            null => "",
            byte[] bytes => Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 64))),
            string[] array => string.Join("\u0002", array),
            _ => value.ToString() ?? ""
        };
    }

    public SnapshotDiff Diff(InstallSnapshot before, InstallSnapshot after)
    {
        var diff = new SnapshotDiff();

        foreach (var folder in after.Folders)
        {
            if (before.Folders.Contains(folder))
                continue;
            if (IsNoisePath(folder))
                continue;
            diff.Folders.Add(folder);
        }

        foreach (var file in after.Files)
        {
            if (before.Files.Contains(file) || IsNoisePath(file))
                continue;
            diff.Files.Add(file);
        }

        foreach (var key in after.Keys)
        {
            if (before.Keys.Contains(key) || IsNoiseRegistry(key) ||
                PathSafety.IsProtectedRegistryKey(key, out _))
                continue;
            diff.RegistryKeys.Add(key);
        }

        foreach (var value in after.Values)
        {
            var existed = before.Values.Contains(value);
            if (existed && before.ValueData.TryGetValue(value, out var oldHash) &&
                after.ValueData.TryGetValue(value, out var newHash) && oldHash == newHash)
                continue;
            if (IsNoiseRegistry(value))
                continue;

            var valueKey = value.Split('\u0001', 2);
            if (valueKey.Length == 2 &&
                PathSafety.IsProtectedRegistryKey(valueKey[0], forValue: true, out _))
                continue;
            var split = value.Split('\u0001', 2);
            if (split.Length != 2)
                continue;
            diff.RegistryValues.Add((split[0], split[1]));
        }

        // Drop child files/folders whose parent folder is itself new: the folder
        // item removes the whole tree anyway, but keeping the children gives the
        // wizard a richer preview.

        return diff;
    }

    private static readonly string[] NoiseSegments =
    {
        "node_modules", "Package Cache", "WindowsApps", "$Recycle.Bin", "Temp",
        "Packages", "Microsoft", "Google", "Mozilla", "Cache", "Caches",
        "Code Cache", "GPUCache", "ShaderCache", ".git", "CleanRift",
        "Common Files", "Windows Defender", "Windows NT", "System32", "SysWOW64"
    };

    private static readonly string[] ShellRoots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
    };

    public static bool IsNoisePath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return true;

            var full = Path.GetFullPath(path);
            var appDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
            if (full.StartsWith(appDir + "\\", StringComparison.OrdinalIgnoreCase) ||
                full.Equals(appDir, StringComparison.OrdinalIgnoreCase))
                return true;

            // Never include CleanRift's own data folder in a trace: it contains
            // preferences, cache, logs and the trace files themselves.
            var traceDir = Path.GetFullPath(TraceStore.TraceDirectory).TrimEnd('\\');
            if (full.StartsWith(traceDir + "\\", StringComparison.OrdinalIgnoreCase) ||
                full.Equals(traceDir, StringComparison.OrdinalIgnoreCase))
                return true;

            if (full.StartsWith(Path.GetTempPath().TrimEnd('\\') + "\\",
                    StringComparison.OrdinalIgnoreCase))
                return true;

            if (full.Contains(@"\Microsoft\Windows\Explorer\", StringComparison.OrdinalIgnoreCase))
                return true;

            // Desktop and Start Menu are tracked only for shortcuts. A document
            // the user creates there while setup runs is not an installed file.
            var shellRoot = ShellRoots.FirstOrDefault(root =>
                !string.IsNullOrWhiteSpace(root) &&
                (full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
                 full.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)));
            if (shellRoot != null && !Directory.Exists(full) &&
                !full.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) &&
                !full.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                return true;

            if (PathSafety.IsProtectedPath(full))
                return true;

            var parts = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var part in parts)
            {
                if (NoiseSegments.Contains(part, StringComparer.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // If the path cannot be inspected, keep it out of the trace.
            return true;
        }

        return false;
    }

    private static bool IsNoiseRegistry(string path)
    {
        var lowered = path.ToLowerInvariant();
        if (lowered.Contains(@"\software\microsoft\windows\currentversion\explorer\recentdocs"))
            return true;
        if (lowered.Contains(@"\software\microsoft\windows\currentversion\explorer\userassist"))
            return true;
        if (lowered.Contains(@"\software\microsoft\windows\currentversion\search"))
            return true;
        if (lowered.Contains(@"\software\microsoft\windows\currentversion\cloudstore"))
            return true;
        if (lowered.Contains(@"\software\microsoft\windows\currentversion\contentdeliverymanager"))
            return true;
        if (lowered.Contains(@"\software\microsoft\windows\currentversion\run"))
            return false;
        return false;
    }

    /// <summary>
    /// Adds a newly created install directory that lives outside the usual
    /// snapshot roots (for example D:\Apps\Product). Existing directories are
    /// never added, so an update installation cannot erase its own folder.
    /// </summary>
    public static void AddCustomInstallLocation(SnapshotDiff diff, string? location,
        DateTime notBeforeUtc)
    {
        if (string.IsNullOrWhiteSpace(location))
            return;

        try
        {
            var dir = Path.GetFullPath(location.Trim().Trim('"')).TrimEnd('\\');
            if (!Directory.Exists(dir) || PathSafety.IsProtectedPath(dir))
                return;

            if (FileRoots().Any(root => PathSafety.IsUnder(dir, root)))
                return;

            if (Directory.GetCreationTimeUtc(dir) < notBeforeUtc.AddMinutes(-1))
                return;

            if (diff.Folders.Any(folder =>
                    folder.Equals(dir, StringComparison.OrdinalIgnoreCase) ||
                    PathSafety.IsUnder(dir, folder)))
                return;

            diff.Folders.Add(dir);
        }
        catch
        {
            // A custom install path is a best-effort enhancement.
        }
    }

    public TraceInfo BuildTrace(SnapshotDiff diff, string id, string displayName)
    {
        var trace = new TraceInfo
        {
            Id = id,
            DisplayName = displayName,
            CreatedUtc = DateTime.UtcNow
        };

        // Only store the outermost new folders; the child files are redundant.
        var folderSet = new HashSet<string>(diff.Folders, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folderSet)
        {
            var parent = Path.GetDirectoryName(folder);
            var isNested = false;
            while (!string.IsNullOrEmpty(parent))
            {
                if (folderSet.Contains(parent))
                {
                    isNested = true;
                    break;
                }

                parent = Path.GetDirectoryName(parent);
            }

            if (!isNested)
                trace.Folders.Add(folder);
        }

        foreach (var file in diff.Files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(file);
            var insideFolder = false;
            while (!string.IsNullOrEmpty(parent))
            {
                if (folderSet.Contains(parent))
                {
                    insideFolder = true;
                    break;
                }

                parent = Path.GetDirectoryName(parent);
            }

            if (!insideFolder)
                trace.Files.Add(file);
        }

        var keySet = new HashSet<string>(diff.RegistryKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var key in keySet)
        {
            var isNested = false;
            var index = key.LastIndexOf('\\');
            while (index > 0)
            {
                var parent = key[..index];
                if (keySet.Contains(parent))
                {
                    isNested = true;
                    break;
                }

                index = parent.LastIndexOf('\\');
            }

            if (!isNested)
                trace.RegistryKeys.Add(key);
        }

        var seenValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, name) in diff.RegistryValues)
        {
            if (seenValues.Add(key + "\u0001" + name))
                trace.RegistryValues.Add((key, name));
        }

        return trace;
    }

    public static JobResult Revert(TraceInfo trace, CancellationToken token)
    {
        var items = new List<RemovalJobItem>();
        foreach (var folder in trace.Folders)
            items.Add(new RemovalJobItem { Kind = "folder", Path = folder });
        foreach (var file in trace.Files)
            items.Add(new RemovalJobItem { Kind = "file", Path = file });
        foreach (var key in trace.RegistryKeys)
            items.Add(new RemovalJobItem { Kind = "key", Path = key });
        foreach (var (key, name) in trace.RegistryValues)
            items.Add(new RemovalJobItem { Kind = "value", Path = key, ValueName = name });

        var engine = new RemovalEngine();
        return engine.RemoveItems(items, token);
    }

    /// <summary>
    /// Runs a setup program and waits until the process tree exits. Returns the
    /// exit code, or null when tracking was cancelled.
    /// </summary>
    private static int CountLiveChildren(int pid)
    {
        var count = 0;
        foreach (var process in ProcessUtil.GetProcessTree(pid))
        {
            try
            {
                if (!process.HasExited && process.Id != pid)
                    count++;
            }
            catch
            {
                // ignore
            }
            finally
            {
                process.Dispose();
            }
        }

        return count;
    }

    public int? RunSetup(string setupPath, CancellationToken token, Action<int>? started = null)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = setupPath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(setupPath) ?? Environment.CurrentDirectory
            };

            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
            {
                psi.Verb = "runas";
                process = Process.Start(psi);
            }

            if (process == null)
                return -1;

            try
            {
            started?.Invoke(process.Id);
            Log?.Invoke(LanguageManager.Instance.F("InstallTracker/logTrackingStarting", setupPath));

            var deadline = DateTime.UtcNow.AddHours(6);
            var rootGoneAt = DateTime.MinValue;
            while (!token.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                bool alive;
                try { alive = !process.HasExited; }
                catch { alive = false; }

                if (alive)
                {
                    Thread.Sleep(500);
                    continue;
                }

                if (rootGoneAt == DateTime.MinValue)
                    rootGoneAt = DateTime.UtcNow;

                var liveChildren = CountLiveChildren(process.Id);
                if (liveChildren == 0)
                {
                    if (DateTime.UtcNow - rootGoneAt > TimeSpan.FromSeconds(3))
                        break;
                }
                else if (DateTime.UtcNow - rootGoneAt > TimeSpan.FromMinutes(2))
                {
                    // Setup exited long ago but a browser/helper remains; stop waiting.
                    break;
                }

                Thread.Sleep(500);
            }

            if (token.IsCancellationRequested)
            {
                Log?.Invoke(LanguageManager.Instance.T("InstallTracker/logTrackingStoppedByUser",
                    "Tracing is stopped by user"));
                try { ProcessUtil.KillTree(process.Id); } catch { }
                return null;
            }

            Log?.Invoke(LanguageManager.Instance.T("InstallTracker/logTrackingAllProcessesFinished",
                "All installation processes are finished"));
            try { return process.HasExited ? process.ExitCode : 0; }
            catch { return 0; }
            }
            finally
            {
                process.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke(LanguageManager.Instance.F("InstallTracker/logTrackingStartingEror",
                ex.Message));
            return -1;
        }
    }
}
