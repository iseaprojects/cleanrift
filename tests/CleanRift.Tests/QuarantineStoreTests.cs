using CleanRift.Core;
using Microsoft.Win32;
using Xunit;

namespace CleanRift.Tests;

public sealed class QuarantineStoreTests
{
    [Fact]
    public void FileCanBeQuarantinedAndRestored()
    {
        var path = Path.Combine(Path.GetTempPath(), "cleanrift-quarantine-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "test");

        var session = QuarantineSession.Create("unit test", moveFiles: true,
            backupRegistry: false, out var createError);
        Assert.NotNull(session);
        Assert.True(string.IsNullOrEmpty(createError));

        var capture = session.TryCapture(new RemovalJobItem { Kind = "file", Path = path }, out _);
        Assert.Equal(CaptureResult.Captured, capture);
        Assert.False(File.Exists(path));
        session.Complete();

        try
        {
            Assert.True(QuarantineStore.RestoreBatch(session.Id, out var restoreError),
                restoreError);
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
            QuarantineStore.DeleteBatch(session.Id, out _);
        }
    }

    [Fact]
    public void RegistryKeyCanBeBackedUpAndRestored()
    {
        var name = "CleanRiftQuarantineTest_" + Guid.NewGuid().ToString("N");
        var keyPath = @"HKEY_CURRENT_USER\Software\" + name;
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\" + name))
        {
            key.SetValue("Value", "hello");
        }

        var session = QuarantineSession.Create("unit test", moveFiles: false,
            backupRegistry: true, out _);
        Assert.NotNull(session);

        var capture = session.TryCapture(new RemovalJobItem { Kind = "key", Path = keyPath }, out _);
        Assert.Equal(CaptureResult.Captured, capture);
        session.Complete();

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\" + name, throwOnMissingSubKey: false);
            Assert.True(QuarantineStore.RestoreBatch(session.Id, out var restoreError),
                restoreError);
            using var restored = Registry.CurrentUser.OpenSubKey(@"Software\" + name);
            Assert.NotNull(restored);
            Assert.Equal("hello", restored!.GetValue("Value"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\" + name, throwOnMissingSubKey: false);
            QuarantineStore.DeleteBatch(session.Id, out _);
        }
    }
}
