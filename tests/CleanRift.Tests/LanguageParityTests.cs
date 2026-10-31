using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace CleanRift.Tests;

public sealed class LanguageParityTests
{
    [Fact]
    public void EveryLanguageHasTheSameKeysAndPlaceholdersAsEnglish()
    {
        var root = FindRepositoryRoot();
        var directory = Path.Combine(root, "src", "CleanRift", "Languages");
        var files = Directory.GetFiles(directory, "*.xml");
        Assert.True(files.Length >= 6,
            $"Expected at least six languages, found {files.Length}.");

        var englishFile = files.First(file =>
            Path.GetFileName(file).Equals("English.xml", StringComparison.OrdinalIgnoreCase));
        var english = Read(englishFile);
        var langIds = new HashSet<int>();

        foreach (var file in files)
        {
            var values = Read(file);
            Assert.Equal(english.Count, values.Count);
            Assert.Equal(english.Keys.OrderBy(key => key),
                values.Keys.OrderBy(key => key));

            foreach (var pair in english)
            {
                var translated = values[pair.Key];
                Assert.False(string.IsNullOrWhiteSpace(translated),
                    $"{Path.GetFileName(file)} has an empty value for {pair.Key}.");
                Assert.Equal(Placeholders(pair.Value), Placeholders(translated));
            }

            var document = XDocument.Load(file);
            var id = int.TryParse(document.Root?.Attribute("LangID")?.Value, out var parsed)
                ? parsed
                : 0;
            Assert.NotEqual(0, id);
            Assert.True(langIds.Add(id),
                $"Duplicate LangID {id} in {Path.GetFileName(file)}.");
        }
    }

    private static Dictionary<string, string> Read(string file)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var document = XDocument.Load(file);
        if (document.Root != null)
            Walk(document.Root, "", result);
        return result;
    }

    private static void Walk(XElement element, string prefix,
        Dictionary<string, string> target)
    {
        foreach (var child in element.Elements())
        {
            var key = string.IsNullOrEmpty(prefix)
                ? child.Name.LocalName
                : prefix + "/" + child.Name.LocalName;
            if (child.HasElements)
                Walk(child, key, target);
            else
                target[key] = child.Value;
        }
    }

    private static string Placeholders(string value)
    {
        var tokens = Regex.Matches(value, "%[sudx]")
            .Select(match => match.Value);
        var escapes = Regex.Matches(value, @"\\[nt]")
            .Select(match => match.Value);
        return string.Join("|", tokens.Concat(escapes));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CleanRift.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("CleanRift.sln was not found.");
    }
}
