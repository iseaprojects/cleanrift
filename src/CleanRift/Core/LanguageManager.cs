using System.Globalization;
using System.Xml.Linq;

namespace CleanRift.Core;

/// <summary>
/// Loads CleanRift's own language XML files. Keys use a stable section/name
/// scheme ("MainMenu/mnuFile", "ListCtrl/columnProgramName", ...) and each
/// file declares a display name and LCID on its root element.
/// </summary>
public sealed class LanguageManager
{
    private readonly Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _fallback = new(StringComparer.OrdinalIgnoreCase);

    public static LanguageManager Instance { get; } = new();

    public string LanguageFile { get; private set; } = "";
    public string LanguageName { get; private set; } = "English";
    public int LangId { get; private set; } = 1033;

    public static string LanguagesDirectory => Path.Combine(AppContext.BaseDirectory, "Languages");

    public static IReadOnlyList<LanguageInfo> AvailableLanguages()
    {
        var result = new List<LanguageInfo>();
        var dir = LanguagesDirectory;
        if (!Directory.Exists(dir))
            return result;

        foreach (var file in Directory.EnumerateFiles(dir, "*.xml").OrderBy(Path.GetFileName))
        {
            var info = new LanguageInfo { File = file, Name = Path.GetFileNameWithoutExtension(file) };
            try
            {
                var doc = XDocument.Load(file);
                var root = doc.Root;
                if (root != null)
                {
                    info.LangId = int.TryParse(root.Attribute("LangID")?.Value, out var id) ? id : 0;
                    var displayName = root.Attribute("Name")?.Value;
                    if (!string.IsNullOrWhiteSpace(displayName))
                    {
                        info.Name = displayName;
                    }
                    else if (info.LangId != 0)
                    {
                        try
                        {
                            info.Name = CultureInfo.GetCultureInfo(info.LangId).NativeName;
                            info.Name = char.ToUpper(info.Name[0], CultureInfo.InvariantCulture) + info.Name[1..];
                        }
                        catch
                        {
                            // keep file name
                        }
                    }
                }
            }
            catch
            {
                // Ignore malformed language files.
            }

            result.Add(info);
        }

        return result;
    }

    public void Load(string file)
    {
        _strings.Clear();
        LoadInto(file, _strings);

        var english = Path.Combine(LanguagesDirectory, "English.xml");
        _fallback.Clear();
        if (!string.Equals(file, english, StringComparison.OrdinalIgnoreCase))
            LoadInto(english, _fallback);

        LanguageFile = file;
        LanguageName = Path.GetFileNameWithoutExtension(file);
        try
        {
            var root = XDocument.Load(file).Root;
            LangId = int.TryParse(root?.Attribute("LangID")?.Value, out var id) ? id : 1033;
        }
        catch
        {
            LangId = 1033;
        }
    }

    private static void LoadInto(string file, Dictionary<string, string> target)
    {
        if (!File.Exists(file))
            return;

        try
        {
            var doc = XDocument.Load(file);
            if (doc.Root == null)
                return;
            Walk(doc.Root, "", target);
        }
        catch
        {
            // Ignore malformed file.
        }
    }

    private static void Walk(XElement element, string prefix, Dictionary<string, string> target)
    {
        foreach (var child in element.Elements())
        {
            var key = string.IsNullOrEmpty(prefix) ? child.Name.LocalName : prefix + "/" + child.Name.LocalName;
            if (!child.HasElements)
                target[key] = Unescape(child.Value);
            else
                Walk(child, key, target);
        }
    }

    private static string Unescape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        return value.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\t", "\t");
    }

    public string T(string key, string fallback = "")
    {
        if (_strings.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
            return value;
        if (_fallback.TryGetValue(key, out var english) && !string.IsNullOrEmpty(english))
            return english;
        return string.IsNullOrEmpty(fallback) ? key : fallback;
    }

    /// <summary>
    /// Formats a language string. CleanRift language files use printf
    /// placeholders (%s, %d, %u, %x) rather than .NET composite formatting, so
    /// they are substituted positionally here.
    /// </summary>
    public string F(string key, params object[] args)
    {
        return FormatPrintf(T(key), args);
    }

    public static string FormatPrintf(string template, params object[] args)
    {
        if (string.IsNullOrEmpty(template) || args == null || args.Length == 0)
            return template;

        var builder = new System.Text.StringBuilder(template.Length + 16);
        var index = 0;
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c != '%' || i + 1 >= template.Length)
            {
                builder.Append(c);
                continue;
            }

            var spec = template[i + 1];
            if (spec == '%')
            {
                builder.Append('%');
                i++;
                continue;
            }

            if (spec is not ('s' or 'S' or 'd' or 'D' or 'i' or 'u' or 'U' or 'x' or 'X' or 'f' or 'F' or 'c'))
            {
                builder.Append(c);
                continue;
            }

            object? value = index < args.Length ? args[index] : null;
            index++;
            builder.Append(spec switch
            {
                's' or 'S' => value?.ToString() ?? "",
                'c' => value?.ToString() ?? "",
                'd' or 'D' or 'i' => Convert.ToInt64(value ?? 0).ToString(CultureInfo.InvariantCulture),
                'u' or 'U' => ((ulong)Math.Max(0, Convert.ToInt64(value ?? 0))).ToString(CultureInfo.InvariantCulture),
                'x' => Convert.ToInt64(value ?? 0).ToString("x", CultureInfo.InvariantCulture),
                'X' => Convert.ToInt64(value ?? 0).ToString("X", CultureInfo.InvariantCulture),
                'f' or 'F' => Convert.ToDouble(value ?? 0).ToString("0.##", CultureInfo.InvariantCulture),
                _ => value?.ToString() ?? ""
            });
            i++;
        }

        return builder.ToString();
    }

    /// <summary>Loads a language file whose LangID best matches the OS UI language.</summary>
    public static string PickDefault()
    {
        var languages = AvailableLanguages();
        if (languages.Count == 0)
            return "";

        var os = CultureInfo.CurrentUICulture;
        var exact = languages.FirstOrDefault(l => l.LangId == os.LCID);
        if (exact != null)
            return exact.File;

        var neutral = languages.FirstOrDefault(l => l.LangId == os.Parent.LCID);
        if (neutral != null)
            return neutral.File;

        // Language files can use a different LCID for the same language
        // (for example Spanish is 1034 in some tools while es-ES reports
        // 3082), so compare the primary language bits before giving up.
        static int Primary(int langId) => langId & 0x3FF;
        var primary = languages.FirstOrDefault(l => Primary(l.LangId) == Primary(os.LCID));
        if (primary != null)
            return primary.File;

        var english = languages.FirstOrDefault(l => l.LangId == 1033);
        return (english ?? languages[0]).File;
    }
}

public sealed class LanguageInfo
{
    public string File { get; set; } = "";
    public string Name { get; set; } = "";
    public int LangId { get; set; }

    public override string ToString() => Name;
}
