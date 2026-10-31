using CleanRift.Core;
using Xunit;

namespace CleanRift.Tests;

public sealed class LiveRegistryMonitorTests
{
    [Fact]
    public void NormalizesMachineRegistryPath()
    {
        Assert.Equal(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Example\Product",
            LiveRegistryMonitor.NormalizeKeyName(
                @"\REGISTRY\MACHINE\SOFTWARE\Example\Product"));
    }

    [Fact]
    public void NormalizesCurrentUserRegistryPath()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        Assert.Equal(
            @"HKEY_CURRENT_USER\Software\Example",
            LiveRegistryMonitor.NormalizeKeyName(
                @"\REGISTRY\USER\" + sid + @"\Software\Example", sid));
    }

    [Fact]
    public void IgnoresOtherUsersAndNonSoftwareTrees()
    {
        Assert.Null(LiveRegistryMonitor.NormalizeKeyName(
            @"\REGISTRY\USER\S-1-5-21-9-9-9-1001\Software\Example",
            "S-1-5-21-1-2-3-1001"));
        Assert.Null(LiveRegistryMonitor.NormalizeKeyName(
            @"\REGISTRY\MACHINE\SYSTEM\CurrentControlSet\Services"));
        Assert.Null(LiveRegistryMonitor.NormalizeKeyName(@"not-a-registry-path"));
    }
}
