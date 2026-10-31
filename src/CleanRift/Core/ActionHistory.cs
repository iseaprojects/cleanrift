using System.Text.Json;
using System.Text.Json.Serialization;

namespace CleanRift.Core;

public enum ActionHistoryKind
{
    Removal,
    Revert,
    Queue,
    Restore,
    Delete,
    RemoveEntry
}

public sealed class ActionHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public ActionHistoryKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Details { get; set; } = "";
    public int Deleted { get; set; }
    public int Failed { get; set; }
    public int ScheduledForReboot { get; set; }
    public string QuarantineBatchId { get; set; } = "";
    public bool Undone { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Small, bounded local log of destructive actions. It never leaves the
/// machine and is separate from traces so clearing it cannot lose trace data.
/// </summary>
public sealed class ActionHistoryStore
{
    private const int MaxEntries = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _lock = new();
    private readonly string _file;

    public ActionHistoryStore(string? file = null)
    {
        _file = file ?? DefaultPath;
    }

    public static string DefaultPath =>
        Path.Combine(AppSettings.DataDirectory, "history.json");

    public IReadOnlyList<ActionHistoryEntry> Load()
    {
        lock (_lock)
            return LoadUnlocked();
    }

    public void Add(ActionHistoryEntry entry)
    {
        try
        {
            lock (_lock)
            {
                var entries = LoadUnlocked().ToList();
                entries.Insert(0, entry);
                if (entries.Count > MaxEntries)
                    entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
                SaveUnlocked(entries);
            }
        }
        catch
        {
            // History is best effort and must never break a removal.
        }
    }

    public bool MarkUndone(string id)
    {
        try
        {
            lock (_lock)
            {
                var entries = LoadUnlocked().ToList();
                var entry = entries.FirstOrDefault(item =>
                    string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                if (entry == null || entry.Undone)
                    return false;
                entry.Undone = true;
                SaveUnlocked(entries);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    public int MarkUndoneByBatch(string batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
            return 0;
        try
        {
            lock (_lock)
            {
                var entries = LoadUnlocked().ToList();
                var changed = 0;
                foreach (var entry in entries)
                {
                    if (entry.Undone ||
                        !string.Equals(entry.QuarantineBatchId, batchId,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    entry.Undone = true;
                    changed++;
                }

                if (changed > 0)
                    SaveUnlocked(entries);
                return changed;
            }
        }
        catch
        {
            return 0;
        }
    }

    public void Clear()
    {
        try
        {
            lock (_lock)
            {
                if (File.Exists(_file))
                    File.Delete(_file);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private List<ActionHistoryEntry> LoadUnlocked()
    {
        try
        {
            if (!File.Exists(_file))
                return new List<ActionHistoryEntry>();
            var entries = JsonSerializer.Deserialize<List<ActionHistoryEntry>>(
                File.ReadAllText(_file), JsonOptions);
            if (entries == null)
                return new List<ActionHistoryEntry>();

            return entries
                .Where(entry => entry != null)
                .OrderByDescending(entry => entry.TimestampUtc)
                .Take(MaxEntries)
                .ToList();
        }
        catch
        {
            return new List<ActionHistoryEntry>();
        }
    }

    private void SaveUnlocked(List<ActionHistoryEntry> entries)
    {
        var directory = Path.GetDirectoryName(_file);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        AtomicFile.WriteAllText(_file, JsonSerializer.Serialize(entries, JsonOptions));
    }
}

public static class ActionHistory
{
    public static ActionHistoryStore Default { get; } = new();

    public static IReadOnlyList<ActionHistoryEntry> Load() => Default.Load();

    public static void Add(ActionHistoryEntry entry) => Default.Add(entry);

    public static int MarkUndoneByBatch(string batchId) =>
        Default.MarkUndoneByBatch(batchId);

    public static void Clear() => Default.Clear();
}
