using CleanRift.Core;
using Xunit;

namespace CleanRift.Tests;

public sealed class RemovalEngineQuarantineTests
{
    [Fact]
    public void RemovedFileCanBeRestoredFromQuarantine()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "cleanrift-engine-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "hello");

        var engine = new RemovalEngine(quarantine: true, backupRegistry: false,
            createRestorePoint: false, description: "unit test");
        var result = engine.RemoveItems(new[]
        {
            new RemovalJobItem { Kind = "file", Path = path }
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(File.Exists(path));
        Assert.False(string.IsNullOrWhiteSpace(result.QuarantineBatchId));

        try
        {
            Assert.True(QuarantineStore.RestoreBatch(result.QuarantineBatchId, out var error),
                error);
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
            QuarantineStore.DeleteBatch(result.QuarantineBatchId, out _);
        }
    }
}
