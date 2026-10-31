using CleanRift.Core;
using Xunit;

namespace CleanRift.Tests;

public sealed class InstallTrackerTests
{
    [Fact]
    public void CustomInstallLocationIsAddedWhenNew()
    {
        var root = Path.Combine(CustomRootParent(), "cleanrift-custom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var diff = new SnapshotDiff();
            InstallTracker.AddCustomInstallLocation(diff, root, DateTime.UtcNow.AddMinutes(-2));
            Assert.Contains(root, diff.Folders);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExistingCustomInstallLocationIsNotAdded()
    {
        var root = Path.Combine(CustomRootParent(), "cleanrift-custom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var diff = new SnapshotDiff();
            InstallTracker.AddCustomInstallLocation(diff, root, DateTime.UtcNow.AddMinutes(10));
            Assert.DoesNotContain(root, diff.Folders);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildTraceCollapsesChildrenOfNewFolders()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cleanrift-trace-" + Guid.NewGuid().ToString("N"));
        var child = Path.Combine(folder, "child.txt");
        var diff = new SnapshotDiff();
        diff.Folders.Add(folder);
        diff.Files.Add(child);
        diff.Files.Add(child);
        diff.RegistryKeys.Add(@"HKEY_CURRENT_USER\SOFTWARE\Example");
        diff.RegistryKeys.Add(@"HKEY_CURRENT_USER\SOFTWARE\Example");

        var trace = new InstallTracker().BuildTrace(diff, "id", "Example");

        Assert.Single(trace.Folders);
        Assert.Empty(trace.Files);
        Assert.Single(trace.RegistryKeys);
    }

    [Fact]
    public void NoiseFilterKeepsNormalAppFilesAndDropsSystemOrTemporaryPaths()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.False(InstallTracker.IsNoisePath(
            Path.Combine(programFiles, "ExampleApp", "app.exe")));
        Assert.True(InstallTracker.IsNoisePath(Path.Combine(windows, "notepad.exe")));
        Assert.True(InstallTracker.IsNoisePath(
            Path.Combine(Path.GetTempPath(), "installer.tmp")));
    }

    private static string CustomRootParent()
    {
        var commonDocuments = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonDocuments);
        return string.IsNullOrEmpty(commonDocuments)
            ? Path.GetTempPath()
            : Path.GetDirectoryName(commonDocuments)!;
    }
}
