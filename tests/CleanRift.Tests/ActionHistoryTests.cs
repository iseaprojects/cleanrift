using CleanRift.Core;
using Xunit;

namespace CleanRift.Tests;

public sealed class ActionHistoryTests
{
    [Fact]
    public void EntriesAreLoadedNewestFirstAndCanBeMarkedUndone()
    {
        var file = NewFile();
        try
        {
            var store = new ActionHistoryStore(file);
            store.Add(new ActionHistoryEntry
            {
                Kind = ActionHistoryKind.Removal,
                Title = "First",
                TimestampUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                QuarantineBatchId = "batch-a",
                Deleted = 2
            });
            store.Add(new ActionHistoryEntry
            {
                Kind = ActionHistoryKind.Revert,
                Title = "Second",
                TimestampUtc = new DateTime(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc),
                QuarantineBatchId = "batch-b"
            });

            var entries = store.Load();
            Assert.Equal(2, entries.Count);
            Assert.Equal("Second", entries[0].Title);
            Assert.Equal("First", entries[1].Title);

            Assert.Equal(1, store.MarkUndoneByBatch("batch-a"));
            var updated = store.Load();
            Assert.True(updated.Single(entry => entry.QuarantineBatchId == "batch-a").Undone);
            Assert.False(updated.Single(entry => entry.QuarantineBatchId == "batch-b").Undone);
        }
        finally
        {
            Delete(file);
        }
    }

    [Fact]
    public void ClearRemovesTheHistoryFile()
    {
        var file = NewFile();
        try
        {
            var store = new ActionHistoryStore(file);
            store.Add(new ActionHistoryEntry { Kind = ActionHistoryKind.Removal, Title = "x" });
            Assert.Single(store.Load());

            store.Clear();

            Assert.Empty(store.Load());
            Assert.False(File.Exists(file));
        }
        finally
        {
            Delete(file);
        }
    }

    [Fact]
    public void DamagedFileIsTreatedAsEmpty()
    {
        var file = NewFile();
        try
        {
            File.WriteAllText(file, "{ not valid json");
            var store = new ActionHistoryStore(file);
            Assert.Empty(store.Load());

            store.Add(new ActionHistoryEntry
            {
                Kind = ActionHistoryKind.Queue,
                Title = "Queue"
            });
            Assert.Single(store.Load());
        }
        finally
        {
            Delete(file);
        }
    }

    private static string NewFile() =>
        Path.Combine(Path.GetTempPath(),
            "cleanrift-history-" + Guid.NewGuid().ToString("N") + ".json");

    private static void Delete(string file)
    {
        try
        {
            if (File.Exists(file))
                File.Delete(file);
        }
        catch
        {
            // Best effort.
        }
    }
}
