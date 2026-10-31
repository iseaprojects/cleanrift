using System.Text.Json;
using CleanRift.Core;
using Xunit;

namespace CleanRift.Tests;

public sealed class QuarantineCleanupTests
{
    [Fact]
    public void OldBatchesAreDeletedAndRecentBatchesKept()
    {
        var root = NewRoot();
        var now = DateTime.UtcNow;
        var oldBatch = CreateBatch(root, "old", now.AddDays(-40), 2048);
        var recentBatch = CreateBatch(root, "recent", now.AddDays(-2), 2048);

        try
        {
            var result = QuarantineStore.CleanupAt(root, 30, 2048, now);

            Assert.Equal(1, result.BatchesDeleted);
            Assert.Equal(1, result.BatchesKept);
            Assert.False(Directory.Exists(oldBatch));
            Assert.True(Directory.Exists(recentBatch));
            Assert.True(result.BytesFreed >= 2048);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void SizeLimitDeletesTheOldestBatchFirst()
    {
        var root = NewRoot();
        var now = DateTime.UtcNow;
        var oldest = CreateBatch(root, "oldest", now.AddDays(-20), 700 * 1024);
        var newer = CreateBatch(root, "newer", now.AddDays(-10), 700 * 1024);

        try
        {
            var result = QuarantineStore.CleanupAt(root, 365, 1, now);

            Assert.Equal(1, result.BatchesDeleted);
            Assert.False(Directory.Exists(oldest));
            Assert.True(Directory.Exists(newer));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void RecentBatchIsPreservedEvenAboveTheSizeLimit()
    {
        var root = NewRoot();
        var now = DateTime.UtcNow;
        var recent = CreateBatch(root, "recent", now.AddHours(-2), 2 * 1024 * 1024);

        try
        {
            var result = QuarantineStore.CleanupAt(root, 30, 1, now);

            Assert.Equal(0, result.BatchesDeleted);
            Assert.True(Directory.Exists(recent));
            Assert.Equal(1, result.BatchesKept);
        }
        finally
        {
            Delete(root);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "cleanrift-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateBatch(string root, string id, DateTime createdUtc,
        int size)
    {
        var directory = Path.Combine(root, id);
        var items = Path.Combine(directory, "items");
        Directory.CreateDirectory(items);
        File.WriteAllBytes(Path.Combine(items, "data.bin"), new byte[size]);

        var batch = new QuarantineBatch
        {
            Id = id,
            CreatedUtc = createdUtc,
            Description = id,
            MoveFiles = true,
            BackupRegistry = true
        };
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(batch, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        return directory;
    }

    private static void Delete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }
}
