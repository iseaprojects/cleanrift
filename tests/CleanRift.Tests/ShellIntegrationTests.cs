using CleanRift.Core;
using Microsoft.Win32;
using Xunit;

namespace CleanRift.Tests;

public sealed class ShellIntegrationTests
{
    [Fact]
    public void ContextMenuCanBeRegisteredAndRemoved()
    {
        const string exe = @"C:\Program Files\CleanRift\CleanRift.exe";
        try
        {
            Assert.True(ShellIntegration.SetEnabled(true, exe, "Test install", out var error),
                error);
            using (var key = Registry.CurrentUser.OpenSubKey(
                       @"Software\Classes\exefile\shell\CleanRift.InstallTrace\command"))
            {
                Assert.NotNull(key);
                Assert.Contains("/install_and_trace", key!.GetValue("") as string);
            }

            Assert.True(ShellIntegration.SetEnabled(true, exe, "Test install", out _),
                "Registering twice must be idempotent.");
        }
        finally
        {
            Assert.True(ShellIntegration.SetEnabled(false, exe, "Test install", out _));
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\exefile\shell\CleanRift.InstallTrace");
            Assert.Null(key);
        }
    }
}
