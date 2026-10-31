using System.Text.Json;
using CleanRift.Core;
using Xunit;

namespace CleanRift.Tests;

public sealed class QuarantineSafetyTests
{
    [Fact]
    public void ManifestCannotEscapeItsBatchDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "cleanrift-traversal-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "data");

        var session = QuarantineSession.Create("traversal test", moveFiles: true,
            backupRegistry: false, out _);
        Assert.NotNull(session);
        Assert.Equal(CaptureResult.Captured,
            session.TryCapture(new RemovalJobItem { Kind = "file", Path = path }, out _));
        session.Complete();

        try
        {
            var manifest = Path.Combine(QuarantineStore.Root, session.Id, "manifest.json");
            var json = File.ReadAllText(manifest);
            var batch = JsonSerializer.Deserialize<QuarantineBatch>(json);
            Assert.NotNull(batch);
            Assert.Single(batch!.Entries);
            batch.Entries[0].StoredName = Path.Combine("..", "..", "victim.txt");

            File.WriteAllText(manifest, JsonSerializer.Serialize(batch,
                new JsonSerializerOptions { WriteIndented = true }));

            Assert.False(QuarantineStore.RestoreBatch(session.Id, out var error));
            Assert.Contains("Invalid quarantine entry", error);
            Assert.False(File.Exists(path));
        }
        finally
        {
            QuarantineStore.DeleteBatch(session.Id, out _);
            File.Delete(path);
        }
    }
}
