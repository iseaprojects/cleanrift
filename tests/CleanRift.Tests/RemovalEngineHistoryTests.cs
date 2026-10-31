using CleanRift.Core;
using Xunit;

namespace CleanRift.Tests;

public sealed class RemovalEngineHistoryTests
{
    [Fact]
    public void RemovalIsRecordedInTheInjectedHistory()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "cleanrift-history-engine-" + Guid.NewGuid().ToString("N") + ".txt");
        var historyFile = Path.Combine(Path.GetTempPath(),
            "cleanrift-history-engine-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "data");

        var store = new ActionHistoryStore(historyFile);
        var batchId = "";
        try
        {
            var engine = new RemovalEngine(quarantine: true, backupRegistry: false,
                createRestorePoint: false, description: "History test",
                history: store);
            var result = engine.RemoveItems(new[]
            {
                new RemovalJobItem { Kind = "file", Path = path }
            }, CancellationToken.None);

            Assert.True(result.Success);
            var entry = Assert.Single(store.Load());
            Assert.Equal(ActionHistoryKind.Removal, entry.Kind);
            Assert.Equal("History test", entry.Title);
            Assert.Equal(1, entry.Deleted);
            Assert.Equal(result.QuarantineBatchId, entry.QuarantineBatchId);
            batchId = result.QuarantineBatchId;
        }
        finally
        {
            if (!string.IsNullOrEmpty(batchId))
                QuarantineStore.DeleteBatch(batchId, out _);
            if (File.Exists(path))
                File.Delete(path);
            if (File.Exists(historyFile))
                File.Delete(historyFile);
        }
    }
}
