using System.Diagnostics;
using CleanRift.Core;
using Xunit;

namespace CleanRift.Tests;

public sealed class LiveFileMonitorTests
{
    [Fact]
    public void ReportsCreatedFilesAndRemovesDeletedOnes()
    {
        var root = Path.Combine(Path.GetTempPath(), "cleanrift-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var monitor = new LiveFileMonitor(null, _ => false);
            Assert.Equal(1, monitor.Start(new[] { root }));

            var file = Path.Combine(root, "new-file.txt");
            File.WriteAllText(file, "test");
            Assert.True(WaitUntil(() => monitor.FileCount == 1, 4000),
                "The created file event was not observed.");

            var diff = new SnapshotDiff();
            monitor.MergeInto(diff);
            Assert.Contains(file, diff.Files);

            File.Delete(file);
            Assert.True(WaitUntil(() => monitor.FileCount == 0, 4000),
                "The deleted file event was not observed.");

            var afterDelete = new SnapshotDiff();
            monitor.MergeInto(afterDelete);
            Assert.DoesNotContain(file, afterDelete.Files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DoesNotReportPathsThatExistedInTheBaseline()
    {
        var root = Path.Combine(Path.GetTempPath(), "cleanrift-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var existing = Path.Combine(root, "existing.txt");
            File.WriteAllText(existing, "before");

            var baseline = new InstallSnapshot();
            baseline.Files.Add(existing);

            using var monitor = new LiveFileMonitor(null, _ => false);
            monitor.Start(new[] { root }, baseline);

            File.Delete(existing);
            File.WriteAllText(existing, "after");
            Thread.Sleep(800);

            var diff = new SnapshotDiff();
            monitor.MergeInto(diff);
            Assert.DoesNotContain(existing, diff.Files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReportsNewFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "cleanrift-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var monitor = new LiveFileMonitor(null, _ => false);
            monitor.Start(new[] { root });

            var folder = Path.Combine(root, "new-folder");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "file.txt"), "test");

            Assert.True(WaitUntil(() => monitor.FolderCount >= 1, 4000),
                "The created folder event was not observed.");

            var diff = new SnapshotDiff();
            monitor.MergeInto(diff);
            Assert.Contains(folder, diff.Folders);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CapturesAFileCreatedByASetupProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), "cleanrift-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "created-by-setup.txt");
            var setup = Path.Combine(root, "setup.cmd");
            File.WriteAllText(setup,
                "@echo off\r\necho hello> \"" + target + "\"\r\nexit /b 0\r\n");

            using var monitor = new LiveFileMonitor(null, _ => false);
            monitor.Start(new[] { root });

            var exitCode = new InstallTracker().RunSetup(setup, CancellationToken.None);
            Thread.Sleep(800);
            monitor.Stop();

            var diff = new SnapshotDiff();
            monitor.MergeInto(diff);
            Assert.Equal(0, exitCode);
            Assert.Contains(target, diff.Files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool WaitUntil(Func<bool> predicate, int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate())
                return true;
            Thread.Sleep(25);
        }

        return predicate();
    }
}
