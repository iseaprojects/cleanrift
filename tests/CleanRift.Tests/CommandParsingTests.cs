using CleanRift.Core;
using Xunit;

namespace CleanRift.Tests;

public sealed class CommandParsingTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\unins000.exe\" /SILENT",
        "C:\\Program Files\\App\\unins000.exe", "/SILENT")]
    [InlineData("MsiExec.exe /X{11111111-1111-1111-1111-111111111111}",
        "MsiExec.exe", "/X{11111111-1111-1111-1111-111111111111}")]
    [InlineData("\"C:\\App\\remove.exe\"",
        "C:\\App\\remove.exe", "")]
    [InlineData("C:\\Program Files\\App\\remove.exe /quiet",
        "C:\\Program Files\\App\\remove.exe", "/quiet")]
    public void SplitCommandHandlesQuotedAndUnquotedCommands(string command,
        string expectedFile, string expectedArguments)
    {
        var (fileName, arguments) = UninstallRunner.SplitCommand(command);
        Assert.Equal(expectedFile, fileName);
        Assert.Equal(expectedArguments, arguments);
    }

    [Fact]
    public void FormatPrintfReplacesPositionalArguments()
    {
        Assert.Equal("Hello World, 42 (2a) 100%",
            LanguageManager.FormatPrintf("Hello %s, %d (%x) %u%%", "World", 42, 42, 100));
    }

    [Fact]
    public void FormatPrintfKeepsUnknownSpecifiersAndMissingArguments()
    {
        Assert.Equal("%q %s", LanguageManager.FormatPrintf("%q %s"));
        Assert.Equal("a b ", LanguageManager.FormatPrintf("a %s %s", "b"));
    }

    [Fact]
    public void PackedGuidIsConvertedBackToGuid()
    {
        var guid = RegistryUtil.PackedGuidToGuid("78563412341234121234123456789012");
        Assert.Equal("{12345678-1234-1234-1234-123456789012}", guid);
    }
}
