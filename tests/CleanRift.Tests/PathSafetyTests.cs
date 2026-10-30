using CleanRift.Core;
using Xunit;

namespace CleanRift.Tests;

public sealed class PathSafetyTests
{
    [Fact]
    public void WindowsFolderIsProtected()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.True(PathSafety.IsProtectedPath(Path.Combine(windows, "notepad.exe")));
    }

    [Fact]
    public void ProgramFilesRootIsProtectedButAppSubfolderIsNot()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.True(PathSafety.IsProtectedPath(programFiles));
        Assert.False(PathSafety.IsProtectedPath(Path.Combine(programFiles, "ExampleApp")));
    }

    [Fact]
    public void TempPathIsNotProtected()
    {
        var path = Path.Combine(Path.GetTempPath(), "cleanrift-tests", "file.txt");
        Assert.False(PathSafety.IsProtectedPath(path));
    }

    [Fact]
    public void RelativeAndNetworkPathsAreRejected()
    {
        Assert.False(PathSafety.IsSafeRemovalItem(new RemovalJobItem
        {
            Kind = "file",
            Path = @"relative\file.txt"
        }, out _));

        Assert.False(PathSafety.IsSafeRemovalItem(new RemovalJobItem
        {
            Kind = "file",
            Path = @"\\server\share\file.txt"
        }, out _));

        Assert.False(PathSafety.IsSafeRemovalItem(new RemovalJobItem
        {
            Kind = "file",
            Path = @"C:\Windows\System32\*"
        }, out _));
    }

    [Fact]
    public void SystemRegistryTreesAreProtected()
    {
        Assert.True(PathSafety.IsProtectedRegistryKey(@"HKLM\SYSTEM\CurrentControlSet", out _));
        Assert.True(PathSafety.IsProtectedRegistryKey(@"HKLM\SOFTWARE\Microsoft", out _));
        Assert.True(PathSafety.IsProtectedRegistryKey(
            @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", out _));
        Assert.True(PathSafety.IsProtectedRegistryKey(@"HKCU\SOFTWARE\Google", out _));
    }

    [Fact]
    public void ProductUninstallKeyAndRunValueAreAllowed()
    {
        Assert.False(PathSafety.IsProtectedRegistryKey(
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{11111111-1111-1111-1111-111111111111}",
            out _));

        Assert.False(PathSafety.IsProtectedRegistryKey(
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            forValue: true, out _));
    }

    [Fact]
    public void JobFileMustLiveInTheJobDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "cleanrift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var inside = Path.Combine(root, "job.json");
            File.WriteAllText(inside, "{}");
            var outside = Path.Combine(Path.GetTempPath(), "outside.json");
            File.WriteAllText(outside, "{}");
            try
            {
                Assert.True(PathSafety.IsSafeJobFile(inside, root, out _, out _));
                Assert.False(PathSafety.IsSafeJobFile(outside, root, out _, out _));
            }
            finally
            {
                File.Delete(outside);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
