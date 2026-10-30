using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace CleanRift.Core;

public sealed class TraceInfo
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string File { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public List<string> Files { get; set; } = new();
    public List<string> Folders { get; set; } = new();
    public List<string> RegistryKeys { get; set; } = new();
    public List<(string Key, string Name)> RegistryValues { get; set; } = new();
}

/// <summary>
/// Persists "Install and Trace" data as one XML document per program in the
/// CleanRift data folder, named "&lt;program&gt;_Traces.xml" and shaped as
/// &lt;CleanRiftTraces version="1"&gt; with Folders/Folder, Files/File,
/// RegistryKeys/Key and RegistryValues/Value(Key) nodes. Items that no longer
/// exist are dropped when a trace is loaded.
/// </summary>
public static class TraceStore
{
    private const string TraceSuffix = "_Traces.xml";

    public static string DataDirectory => AppSettings.DataDirectory;

    /// <summary>Trace files live next to preferences.xml.</summary>
    public static string TraceDirectory => DataDirectory;

    public static Dictionary<string, TraceInfo> LoadAll()
    {
        var result = new Dictionary<string, TraceInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var file in Directory.EnumerateFiles(TraceDirectory, "*" + TraceSuffix))
            {
                var trace = Load(file);
                if (trace != null)
                    result[trace.Id] = trace;
            }
        }
        catch
        {
            // Ignore an unreadable data folder; tracing simply appears empty.
        }

        return result;
    }

    /// <summary>
    /// Returns the trace for a product id, or a display-name match only when
    /// it is unambiguous. Two products with the same name must never share one
    /// trace, because removal would erase the other product's leftovers.
    /// </summary>
    public static TraceInfo? FindFor(Dictionary<string, TraceInfo> traces, string? id,
        string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(id) && traces.TryGetValue(id, out var direct))
            return direct;
        if (string.IsNullOrWhiteSpace(displayName))
            return null;

        TraceInfo? match = null;
        foreach (var trace in traces.Values)
        {
            if (!string.Equals(trace.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (match != null)
                return null;
            match = trace;
        }

        return match;
    }

    public static TraceInfo? Load(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            return null;

        try
        {
            if (new FileInfo(file).Length > 64 * 1024 * 1024)
                return null;

            var doc = XDocument.Load(file);
            var root = doc.Root;
            if (root == null)
                return null;

            var name = Path.GetFileName(file);
            if (name.EndsWith(TraceSuffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^TraceSuffix.Length];
            else
                name = Path.GetFileNameWithoutExtension(file);

            var storedId = root.Attribute("id")?.Value?.Trim();
            var displayName = root.Attribute("name")?.Value?.Trim();
            var trace = new TraceInfo
            {
                Id = string.IsNullOrEmpty(storedId) ? name : storedId,
                DisplayName = string.IsNullOrEmpty(displayName) ? name : displayName,
                File = file,
                CreatedUtc = File.GetCreationTimeUtc(file)
            };

            var folders = root.Element("Folders");
            if (folders != null)
            {
                foreach (var element in folders.Elements("Folder"))
                {
                    var path = element.Value.Trim();
                    if (path.Length > 0 && Directory.Exists(path))
                        trace.Folders.Add(path);
                }
            }

            var files = root.Element("Files");
            if (files != null)
            {
                foreach (var element in files.Elements("File"))
                {
                    var path = element.Value.Trim();
                    if (path.Length > 0 && System.IO.File.Exists(path))
                        trace.Files.Add(path);
                }
            }

            var keys = root.Element("RegistryKeys");
            if (keys != null)
            {
                foreach (var element in keys.Elements("Key"))
                {
                    var path = element.Value.Trim();
                    if (path.Length > 0 && RegistryPathExists(path))
                        trace.RegistryKeys.Add(path);
                }
            }

            var values = root.Element("RegistryValues");
            if (values != null)
            {
                foreach (var element in values.Elements("Value"))
                {
                    var valueName = element.Value.Trim();
                    var key = element.Element("Key")?.Value.Trim() ?? "";
                    if (valueName.Length == 0 || key.Length == 0)
                        continue;
                    if (RegistryPathExists(key))
                        trace.RegistryValues.Add((key, valueName));
                }
            }

            return trace;
        }
        catch
        {
            return null;
        }
    }

    public static string Save(TraceInfo trace)
    {
        if (string.IsNullOrWhiteSpace(trace.Id))
            trace.Id = SafeFileName(trace.DisplayName);

        var file = trace.File;
        if (string.IsNullOrWhiteSpace(file) ||
            !string.Equals(Path.GetDirectoryName(file), TraceDirectory, StringComparison.OrdinalIgnoreCase))
        {
            // Include a short hash of the stable program id so two products
            // that share a display name never overwrite each other's trace.
            var stem = SafeFileName(trace.DisplayName);
            if (stem.Length > 80)
                stem = stem[..80].TrimEnd(' ', '.');
            file = Path.Combine(TraceDirectory, stem + "_" + ShortHash(trace.Id) + TraceSuffix);
        }

        // CleanRift trace document, UTF-8 without BOM, version="1".
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            IndentChars = "    ",
            NewLineChars = "\r\n",
            NewLineHandling = NewLineHandling.Replace
        };

        AtomicFile.Write(file, stream =>
        {
            using var writer = XmlWriter.Create(stream, settings);
            writer.WriteStartDocument();
            writer.WriteStartElement("CleanRiftTraces");
            writer.WriteAttributeString("version", "1");
            writer.WriteAttributeString("id", trace.Id);
            writer.WriteAttributeString("name", trace.DisplayName);

            writer.WriteStartElement("Folders");
            foreach (var folder in trace.Folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStartElement("Folder");
                writer.WriteString(folder);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();

            writer.WriteStartElement("Files");
            foreach (var path in trace.Files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStartElement("File");
                writer.WriteString(path);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();

            writer.WriteStartElement("RegistryKeys");
            foreach (var key in trace.RegistryKeys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStartElement("Key");
                writer.WriteString(key);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();

            writer.WriteStartElement("RegistryValues");
            foreach (var (key, name) in trace.RegistryValues
                         .Distinct()
                         .OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStartElement("Value");
                writer.WriteString(name);
                writer.WriteStartElement("Key");
                writer.WriteString(key);
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        });

        trace.File = file;
        trace.CreatedUtc = System.IO.File.GetCreationTimeUtc(file);
        return file;
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    public static void Delete(string id, string? knownFile = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        try
        {
            if (!string.IsNullOrWhiteSpace(knownFile) && File.Exists(knownFile) &&
                string.Equals(Path.GetDirectoryName(knownFile), TraceDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(knownFile);
            }

            foreach (var file in Directory.EnumerateFiles(TraceDirectory, "*" + TraceSuffix))
            {
                var name = Path.GetFileName(file)[..^TraceSuffix.Length];
                if (string.Equals(name, id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(SafeFileName(name), SafeFileName(id), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ReadStoredId(file), id, StringComparison.OrdinalIgnoreCase))
                {
                    System.IO.File.Delete(file);
                }
            }
        }
        catch
        {
            // Ignore; the caller only needs a best-effort delete.
        }
    }

    private static string ReadStoredId(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            reader.MoveToContent();
            return reader.GetAttribute("id") ?? "";
        }
        catch
        {
            return "";
        }
    }

    public static string SafeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "trace";

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
            builder.Append(invalid.Contains(c) ? '_' : c);

        var safe = builder.ToString().Trim(' ', '.');
        return safe.Length == 0 ? "trace" : safe;
    }

    public static bool RegistryPathExists(string path)
    {
        try
        {
            var (hive, subKey) = SplitRegistryPath(path);
            if (hive == null || string.IsNullOrEmpty(subKey))
                return false;

            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive.Value, view);
                using var key = baseKey.OpenSubKey(subKey);
                if (key != null)
                    return true;
            }
        }
        catch
        {
            // Treat inaccessible keys as missing, exactly like the loader.
        }

        return false;
    }

    public static (RegistryHive? Hive, string SubKey) SplitRegistryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (null, "");

        var normalized = path.Trim();
        if (normalized.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
            return (RegistryHive.LocalMachine, normalized["HKEY_LOCAL_MACHINE\\".Length..]);
        if (normalized.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
            return (RegistryHive.CurrentUser, normalized["HKEY_CURRENT_USER\\".Length..]);
        if (normalized.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase))
            return (RegistryHive.LocalMachine, normalized["HKLM\\".Length..]);
        if (normalized.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase))
            return (RegistryHive.CurrentUser, normalized["HKCU\\".Length..]);

        return (null, "");
    }
}
