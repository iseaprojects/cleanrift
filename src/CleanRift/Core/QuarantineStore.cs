using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace CleanRift.Core;

public enum QuarantineKind
{
    File,
    Folder,
    RegistryKey,
    RegistryValue
}

public sealed class QuarantineEntry
{
    public QuarantineKind Kind { get; set; }
    public string OriginalPath { get; set; } = "";
    public string StoredName { get; set; } = "";
    public string ValueName { get; set; } = "";
    public RegistryValueKind RegistryKind { get; set; }
    public int RegistryView { get; set; }
    public string? StringData { get; set; }
    public string[]? MultiStringData { get; set; }
    public string? BinaryData { get; set; }
    public long? NumberData { get; set; }
}

public sealed class QuarantineBatch
{
    public string Id { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string Description { get; set; } = "";
    public bool MoveFiles { get; set; }
    public bool BackupRegistry { get; set; }
    public List<QuarantineEntry> Entries { get; set; } = new();
    [JsonIgnore]
    public string Directory { get; set; } = "";
}

public sealed class QuarantineBatchInfo
{
    public string Id { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string Description { get; set; } = "";
    public int EntryCount { get; set; }
    public int FileCount { get; set; }
    public int RegistryCount { get; set; }
}

public sealed record QuarantineCleanupResult(
    int BatchesDeleted,
    long BytesFreed,
    int BatchesKept);

/// <summary>
/// Moves removed files into a per-batch folder and exports removed registry
/// keys/values to a manifest. A batch can be restored later from the UI.
/// </summary>
public static class QuarantineStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly object MaintenanceGate = new();

    public static string Root => Path.Combine(AppSettings.DataDirectory, "Quarantine");

    public static IReadOnlyList<QuarantineBatchInfo> ListBatches()
    {
        var result = new List<QuarantineBatchInfo>();
        try
        {
            if (!Directory.Exists(Root))
                return result;

            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                var manifest = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifest))
                    continue;
                try
                {
                    var batch = JsonSerializer.Deserialize<QuarantineBatch>(
                        File.ReadAllText(manifest), JsonOptions);
                    if (batch == null)
                        continue;
                    batch.Directory = dir;
                    result.Add(new QuarantineBatchInfo
                    {
                        Id = batch.Id,
                        CreatedUtc = batch.CreatedUtc,
                        Description = batch.Description,
                        EntryCount = batch.Entries.Count,
                        FileCount = batch.Entries.Count(e =>
                            e.Kind is QuarantineKind.File or QuarantineKind.Folder),
                        RegistryCount = batch.Entries.Count(e =>
                            e.Kind is QuarantineKind.RegistryKey or QuarantineKind.RegistryValue)
                    });
                }
                catch
                {
                    // Ignore a damaged batch.
                }
            }
        }
        catch
        {
            // Best effort.
        }

        return result.OrderByDescending(b => b.CreatedUtc).ToList();
    }

    public static QuarantineBatch? LoadBatch(string id)
    {
        try
        {
            var dir = FindBatchDirectory(id);
            if (dir == null)
                return null;
            var manifest = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifest))
                return null;
            var batch = JsonSerializer.Deserialize<QuarantineBatch>(
                File.ReadAllText(manifest), JsonOptions);
            if (batch != null)
                batch.Directory = dir;
            return batch;
        }
        catch
        {
            return null;
        }
    }

    public static bool RestoreBatch(string id, out string error)
    {
        lock (MaintenanceGate)
            return RestoreBatchLocked(id, out error);
    }

    private static bool RestoreBatchLocked(string id, out string error)
    {
        error = "";
        var batch = LoadBatch(id);
        if (batch == null)
        {
            error = "Quarantine batch not found";
            return false;
        }

        var remaining = new List<QuarantineEntry>();
        foreach (var entry in batch.Entries)
        {
            if (!TryRestoreEntry(batch, entry, out var entryError))
            {
                error = entryError;
                remaining.Add(entry);
            }
        }

        batch.Entries = remaining;
        if (remaining.Count == 0)
        {
            DeleteBatch(batch.Id, out _);
            return true;
        }

        SaveManifest(batch);
        return false;
    }

    public static bool DeleteBatch(string id, out string error)
    {
        lock (MaintenanceGate)
            return DeleteBatchLocked(id, out error);
    }

    private static bool DeleteBatchLocked(string id, out string error)
    {
        error = "";
        try
        {
            var dir = FindBatchDirectory(id);
            if (dir != null)
                Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? FindBatchDirectory(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !Directory.Exists(Root))
            return null;
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            if (Path.GetFileName(dir).Equals(id, StringComparison.OrdinalIgnoreCase))
                return dir;
        }

        return null;
    }

    private static bool TryRestoreEntry(QuarantineBatch batch, QuarantineEntry entry, out string error)
    {
        error = "";
        try
        {
            switch (entry.Kind)
            {
                case QuarantineKind.File:
                case QuarantineKind.Folder:
                {
                    if (!Path.IsPathFullyQualified(entry.OriginalPath))
                    {
                        error = "Invalid original path";
                        return false;
                    }

                    if (PathSafety.IsProtectedPath(entry.OriginalPath))
                    {
                        error = "Refusing to restore into a protected path";
                        return false;
                    }

                    var source = SafeStoredPath(batch, entry.StoredName);
                    if (source == null)
                    {
                        error = "Invalid quarantine entry";
                        return false;
                    }
                    if (entry.Kind == QuarantineKind.File && !File.Exists(source))
                        return true;
                    if (entry.Kind == QuarantineKind.Folder && !Directory.Exists(source))
                        return true;
                    if (File.Exists(entry.OriginalPath) || Directory.Exists(entry.OriginalPath))
                    {
                        error = "The original path already exists: " + entry.OriginalPath;
                        return false;
                    }

                    var parent = Path.GetDirectoryName(entry.OriginalPath);
                    if (!string.IsNullOrEmpty(parent))
                        Directory.CreateDirectory(parent);

                    if (entry.Kind == QuarantineKind.File)
                        QuarantineSession.MoveFile(source, entry.OriginalPath);
                    else
                        QuarantineSession.MoveDirectory(source, entry.OriginalPath);
                    return true;
                }

                case QuarantineKind.RegistryKey:
                {
                    if (PathSafety.IsProtectedRegistryKey(entry.OriginalPath, out _))
                    {
                        error = "Refusing to restore a protected registry key";
                        return false;
                    }

                    var source = SafeStoredPath(batch, entry.StoredName);
                    if (source == null)
                    {
                        error = "Invalid quarantine entry";
                        return false;
                    }
                    if (!File.Exists(source))
                        return true;
                    var viewArg = entry.RegistryView == 32 ? " /reg:32"
                        : entry.RegistryView == 64 ? " /reg:64" : "";
                    var (code, _, stderr) = ProcessUtil.Run(
                        ProcessUtil.SystemTool("reg.exe"),
                        "import \"" + source + "\"" + viewArg, 30000);
                    if (code == 0)
                        return true;
                    error = string.IsNullOrWhiteSpace(stderr)
                        ? "Could not import registry backup"
                        : stderr.Trim();
                    return false;
                }

                case QuarantineKind.RegistryValue:
                {
                    if (PathSafety.IsProtectedRegistryKey(entry.OriginalPath, forValue: true, out _))
                    {
                        error = "Refusing to restore a protected registry value";
                        return false;
                    }

                    if (!TryOpenRegistryKey(entry.OriginalPath, writable: true,
                            entry.RegistryView, out var key, out var keyError))
                    {
                        error = keyError;
                        return false;
                    }

                    using (key)
                    {
                        if (key == null)
                        {
                            error = "Registry key not found";
                            return false;
                        }

                        var value = ValueFromEntry(entry);
                        if (value != null)
                            key.SetValue(entry.ValueName, value, entry.RegistryKind);
                    }

                    return true;
                }

                default:
                    return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? SafeStoredPath(QuarantineBatch batch, string storedName)
    {
        try
        {
            var root = Path.GetFullPath(batch.Directory).TrimEnd('\\') + "\\";
            var full = Path.GetFullPath(Path.Combine(batch.Directory, storedName));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    private static object? ValueFromEntry(QuarantineEntry entry)
    {
        return entry.RegistryKind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => entry.StringData ?? "",
            RegistryValueKind.MultiString => entry.MultiStringData ?? Array.Empty<string>(),
            RegistryValueKind.Binary => entry.BinaryData == null
                ? Array.Empty<byte>()
                : Convert.FromBase64String(entry.BinaryData),
            RegistryValueKind.DWord => (int)(entry.NumberData ?? 0),
            RegistryValueKind.QWord => entry.NumberData ?? 0,
            _ => entry.StringData ?? ""
        };
    }

    private static bool TryOpenRegistryKey(string path, bool writable, int registryView,
        out RegistryKey? key, out string error)
    {
        key = null;
        error = "";
        var (hive, subKey) = TraceStore.SplitRegistryPath(path);
        if (hive == null || string.IsNullOrEmpty(subKey))
        {
            error = "Invalid registry path";
            return false;
        }

        try
        {
            var view = registryView switch
            {
                32 => RegistryView.Registry32,
                64 => RegistryView.Registry64,
                _ => RegistryView.Default
            };
            using var baseKey = RegistryKey.OpenBaseKey(hive.Value, view);
            key = writable ? baseKey.CreateSubKey(subKey, writable: true) : baseKey.OpenSubKey(subKey);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static void SaveManifest(QuarantineBatch batch)
    {
        try
        {
            Directory.CreateDirectory(batch.Directory);
            AtomicFile.WriteAllText(Path.Combine(batch.Directory, "manifest.json"),
                JsonSerializer.Serialize(batch, JsonOptions));
        }
        catch
        {
            // The caller reports failures from the capture operation.
        }
    }

    internal static string NewBatchDirectory(string description)
    {
        Directory.CreateDirectory(Root);
        var baseName = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                       "-" + Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(Root, baseName);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "items"));
        return dir;
    }

    /// <summary>
    /// Deletes batches older than <paramref name="retentionDays"/> and, if the
    /// total size exceeds <paramref name="maxSizeMb"/>, the oldest batches that
    /// are at least one day old. Recent batches are preserved so undo keeps
    /// working.
    /// </summary>
    public static QuarantineCleanupResult Cleanup(int retentionDays, int maxSizeMb)
    {
        lock (MaintenanceGate)
            return CleanupAt(Root, retentionDays, maxSizeMb, DateTime.UtcNow);
    }

    internal static QuarantineCleanupResult CleanupAt(string root, int retentionDays,
        int maxSizeMb, DateTime nowUtc)
    {
        retentionDays = Math.Clamp(retentionDays, 1, 3650);
        if (maxSizeMb < 0)
            maxSizeMb = 0;

        var deleted = 0;
        long freed = 0;
        var remaining = new List<(string Directory, DateTime CreatedUtc, long Size)>();
        try
        {
            if (!Directory.Exists(root))
                return new QuarantineCleanupResult(0, 0, 0);

            var cutoff = nowUtc.AddDays(-retentionDays);
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var created = SafeCreationTime(directory);
                var manifest = Path.Combine(directory, "manifest.json");
                if (File.Exists(manifest))
                {
                    try
                    {
                        var batch = JsonSerializer.Deserialize<QuarantineBatch>(
                            File.ReadAllText(manifest), JsonOptions);
                        if (batch?.CreatedUtc is { } manifestTime &&
                            manifestTime != default)
                            created = manifestTime;
                    }
                    catch
                    {
                        // A damaged manifest falls back to the directory time.
                    }
                }

                var size = DirectorySize(directory);
                if (created < cutoff)
                {
                    if (TryDeleteDirectory(directory))
                    {
                        deleted++;
                        freed += size;
                        continue;
                    }
                }

                remaining.Add((directory, created, size));
            }

            var kept = remaining.Count;
            var total = remaining.Sum(entry => entry.Size);
            var limit = (long)maxSizeMb * 1024L * 1024L;
            if (limit > 0 && total > limit)
            {
                var oneDayAgo = nowUtc.AddDays(-1);
                foreach (var entry in remaining.OrderBy(item => item.CreatedUtc))
                {
                    if (total <= limit)
                        break;
                    if (entry.CreatedUtc > oneDayAgo)
                        continue;
                    if (!TryDeleteDirectory(entry.Directory))
                        continue;
                    deleted++;
                    kept--;
                    freed += entry.Size;
                    total -= entry.Size;
                }
            }

            return new QuarantineCleanupResult(deleted, freed, Math.Max(0, kept));
        }
        catch
        {
            return new QuarantineCleanupResult(deleted, freed, remaining.Count);
        }
    }

    private static DateTime SafeCreationTime(string directory)
    {
        try
        {
            return Directory.GetCreationTimeUtc(directory);
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    private static long DirectorySize(string directory)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*",
                         SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // A file disappearing during cleanup is not an error.
                }
            }
        }
        catch
        {
            // Best effort: an unreadable folder does not block cleanup.
        }

        return total;
    }

    private static bool TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Live capture session for one removal operation.</summary>
public sealed class QuarantineSession
{
    private readonly object _lock = new();
    private readonly QuarantineBatch _batch;
    private int _nextItem;

    public QuarantineSession(QuarantineBatch batch)
    {
        _batch = batch;
    }

    public bool HasEntries
    {
        get { lock (_lock) return _batch.Entries.Count > 0; }
    }

    public int EntryCount
    {
        get { lock (_lock) return _batch.Entries.Count; }
    }

    public string Description => _batch.Description;
    public string Id => _batch.Id;
    public bool MovesFiles => _batch.MoveFiles;
    public bool BacksUpRegistry => _batch.BackupRegistry;

    public static QuarantineSession? Create(string description, bool moveFiles,
        bool backupRegistry, out string error)
    {
        error = "";
        try
        {
            var dir = QuarantineStore.NewBatchDirectory(description);
            var batch = new QuarantineBatch
            {
                Id = Path.GetFileName(dir),
                CreatedUtc = DateTime.UtcNow,
                Description = description,
                MoveFiles = moveFiles,
                BackupRegistry = backupRegistry,
                Directory = dir
            };
            QuarantineStore.SaveManifest(batch);
            return new QuarantineSession(batch);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public CaptureResult TryCapture(RemovalJobItem item, out string error)
    {
        error = "";
        try
        {
            switch (item.Kind)
            {
                case "file" when _batch.MoveFiles:
                case "folder" when _batch.MoveFiles:
                    return CaptureFileSystem(item, out error);

                case "key" when _batch.BackupRegistry:
                    return CaptureRegistryKey(item, out error);

                case "value" when _batch.BackupRegistry:
                    return CaptureRegistryValue(item, out error);

                default:
                    return CaptureResult.Skipped;
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            error = ex.Message;
            return CaptureResult.AccessDenied;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return CaptureResult.Failed;
        }
    }

    private CaptureResult CaptureFileSystem(RemovalJobItem item, out string error)
    {
        error = "";
        var isFolder = item.Kind == "folder";
        if (isFolder ? !Directory.Exists(item.Path) : !File.Exists(item.Path))
            return CaptureResult.Skipped;

        var storedName = Path.Combine("items",
            (++_nextItem).ToString(CultureInfo.InvariantCulture) + (isFolder ? ".dir" : ".file"));
        var destination = Path.Combine(_batch.Directory, storedName);

        if (isFolder)
            MoveDirectory(item.Path, destination);
        else
            MoveFile(item.Path, destination);

        lock (_lock)
        {
            _batch.Entries.Add(new QuarantineEntry
            {
                Kind = isFolder ? QuarantineKind.Folder : QuarantineKind.File,
                OriginalPath = item.Path,
                StoredName = storedName
            });
        }

        QuarantineStore.SaveManifest(_batch);
        return CaptureResult.Captured;
    }

    private CaptureResult CaptureRegistryKey(RemovalJobItem item, out string error)
    {
        error = "";
        var exists = false;
        var added = new List<QuarantineEntry>();
        var exported = new List<string>();
        foreach (var view in new[] { 64, 32 })
        {
            if (!RegistryKeyExists(item.Path, view))
                continue;

            exists = true;
            var storedName = Path.Combine("items",
                (++_nextItem).ToString(CultureInfo.InvariantCulture) + "." + view + ".reg");
            var destination = Path.Combine(_batch.Directory, storedName);
            var (code, _, stderr) = ProcessUtil.Run(
                ProcessUtil.SystemTool("reg.exe"),
                "export \"" + item.Path + "\" \"" + destination + "\" /y /reg:" + view,
                30000);
            if (code != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr)
                    ? "Could not export registry key"
                    : stderr.Trim();

                // The key must not be removed unless both views are backed up.
                foreach (var file in exported)
                {
                    try { File.Delete(file); } catch { }
                }

                return CaptureResult.Failed;
            }

            exported.Add(destination);
            added.Add(new QuarantineEntry
            {
                Kind = QuarantineKind.RegistryKey,
                OriginalPath = item.Path,
                StoredName = storedName,
                RegistryView = view
            });
        }

        if (!exists)
            return CaptureResult.Skipped;

        lock (_lock)
            _batch.Entries.AddRange(added);
        QuarantineStore.SaveManifest(_batch);
        return CaptureResult.Captured;
    }

    private CaptureResult CaptureRegistryValue(RemovalJobItem item, out string error)
    {
        error = "";
        var (hive, subKey) = TraceStore.SplitRegistryPath(item.Path);
        if (hive == null || string.IsNullOrEmpty(subKey))
        {
            error = "Invalid registry path";
            return CaptureResult.Failed;
        }

        var exists = false;
        var allCaptured = true;
        foreach (var view in new[] { 64, 32 })
        {
            try
            {
                var registryView = view == 32 ? RegistryView.Registry32 : RegistryView.Registry64;
                using var baseKey = RegistryKey.OpenBaseKey(hive.Value, registryView);
                using var key = baseKey.OpenSubKey(subKey);
                if (key == null)
                    continue;
                var names = key.GetValueNames();
                if (!names.Any(n => string.Equals(n, item.ValueName, StringComparison.OrdinalIgnoreCase)))
                    continue;

                exists = true;
                var kind = key.GetValueKind(item.ValueName);
                var value = key.GetValue(item.ValueName, null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                var entry = new QuarantineEntry
                {
                    Kind = QuarantineKind.RegistryValue,
                    OriginalPath = item.Path,
                    ValueName = item.ValueName,
                    RegistryKind = kind,
                    RegistryView = view
                };

                switch (value)
                {
                    case string text:
                        entry.StringData = text;
                        break;
                    case string[] array:
                        entry.MultiStringData = array;
                        break;
                    case byte[] bytes:
                        entry.BinaryData = Convert.ToBase64String(bytes);
                        break;
                    case int number:
                        entry.NumberData = number;
                        break;
                    case long number:
                        entry.NumberData = number;
                        break;
                    default:
                        entry.StringData = value?.ToString() ?? "";
                        break;
                }

                lock (_lock)
                    _batch.Entries.Add(entry);
            }
            catch (UnauthorizedAccessException ex)
            {
                error = ex.Message;
                allCaptured = false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                allCaptured = false;
            }
        }

        if (!exists)
            return CaptureResult.Skipped;
        if (!allCaptured)
            return CaptureResult.AccessDenied;

        QuarantineStore.SaveManifest(_batch);
        return CaptureResult.Captured;
    }

    private static bool RegistryKeyExists(string path, int view)
    {
        var (hive, subKey) = TraceStore.SplitRegistryPath(path);
        if (hive == null || string.IsNullOrEmpty(subKey))
            return false;
        try
        {
            var registryView = view == 32 ? RegistryView.Registry32 : RegistryView.Registry64;
            using var baseKey = RegistryKey.OpenBaseKey(hive.Value, registryView);
            using var key = baseKey.OpenSubKey(subKey);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    internal static void MoveFile(string source, string destination)
    {
        try
        {
            File.Move(source, destination);
        }
        catch (IOException)
        {
            // Cross-volume move: copy first, then remove the original.
            File.Copy(source, destination, overwrite: false);
            try
            {
                File.Delete(source);
            }
            catch
            {
                try { File.Delete(destination); } catch { }
                throw;
            }
        }
    }

    internal static void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException)
        {
            CopyDirectory(source, destination);
            try
            {
                Directory.Delete(source, recursive: true);
            }
            catch
            {
                try { Directory.Delete(destination, recursive: true); } catch { }
                throw;
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, target, overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cannot quarantine a directory containing a reparse point: " +
                                      directory);
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    public void Complete()
    {
        if (!HasEntries)
        {
            try
            {
                Directory.Delete(_batch.Directory, recursive: true);
            }
            catch
            {
                // Ignore cleanup failure.
            }
            return;
        }

        QuarantineStore.SaveManifest(_batch);
    }
}

public enum CaptureResult
{
    Skipped,
    Captured,
    AccessDenied,
    Failed
}
