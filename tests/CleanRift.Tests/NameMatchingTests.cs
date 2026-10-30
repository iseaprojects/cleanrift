using CleanRift.Core;
using Xunit;

namespace CleanRift.Tests;

public sealed class NameMatchingTests
{
    [Theory]
    [InlineData("7-Zip", "7-Zip", true)]
    [InlineData("Notepad++", "notepad", true)]
    [InlineData("Adobe Acrobat Reader", "Adobe Acrobat Reader", true)]
    [InlineData("Intel", "Intel", true)]
    [InlineData("App", "Application", false)]
    [InlineData("App", "App2", false)]
    [InlineData("Microsoft Visual C++ Redistributable", "Microsoft", true)]
    public void NamesMatchUsesExactOrLongContainment(string a, string b, bool expected)
    {
        Assert.Equal(expected, LeftoverScanner.NamesMatch(a, b));
    }

    [Fact]
    public void BuildDisplayNameVariantsDoesNotIncludePublisher()
    {
        var program = new InstalledProgram
        {
            DisplayName = "Example Product 2.5 (x64)",
            Publisher = "Example Corporation",
            InstallLocation = @"C:\Program Files\Example"
        };

        var names = LeftoverScanner.BuildDisplayNameVariants(program);
        Assert.Contains(names, n => n.Contains("Example Product", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Corporation", StringComparison.OrdinalIgnoreCase));
    }
}
