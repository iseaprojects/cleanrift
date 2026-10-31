using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;

namespace CleanRift.Core;

/// <summary>
/// Enumerates the four autorun locations Windows uses plus logon scheduled
/// tasks and Store startup tasks, and reads the StartupApproved enabled state.
/// </summary>
public sealed class StartupScanner
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ApprovedRun = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedRun32 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string ApprovedFolder = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    private readonly AppSettings _settings;

    public StartupScanner(AppSettings settings)
    {
        _settings = settings;
    }

    public event Action<string>? Progress;

    public List<StartupItem> Scan()
    {
        var items = new List<StartupItem>();
        AddRunItems(items, "HKLM", true, RunKey, StartupKind.Run);
        AddRunItems(items, "HKLM", false, RunKey, StartupKind.Run);
        AddRunItems(items, "HKCU", true, RunKey, StartupKind.Run);
        AddRunItems(items, "HKCU", false, RunKey, StartupKind.Run);
        AddRunItems(items, "HKLM", true, RunOnceKey, StartupKind.RunOnce);
        AddRunItems(items, "HKLM", false, RunOnceKey, StartupKind.RunOnce);
        AddRunItems(items, "HKCU", true, RunOnceKey, StartupKind.RunOnce);
        AddRunItems(items, "HKCU", false, RunOnceKey, StartupKind.RunOnce);
        AddStartupFolder(items, Environment.SpecialFolder.CommonStartup, true);
        AddStartupFolder(items, Environment.SpecialFolder.Startup, false);
        AddScheduledTasks(items);
        AddStoreStartupTasks(items);

        var disabled = DisabledStartupStore.Load();
        foreach (var item in items)
        {
            var stored = disabled.FirstOrDefault(d => d.Id == item.Id);
            if (stored != null)
                item.Enabled = false;
            item.StatusText = item.DisplayStatus;
            item.Icon = IconCache.ShellIcon(item.ExecutablePath.Length > 0 ? item.ExecutablePath : item.FilePath, true);
            item.Running = IsItemRunning(item);
            item.Impact = ComputeImpact(item);
        }

        return items
            .GroupBy(i => i.Hive + "|" + i.Kind + "|" + i.Name + "|" + i.Command,
                StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void AddRunItems(List<StartupItem> items, string hive, bool is64Bit, string keyPath, StartupKind kind)
    {
        try
        {
            using var baseKey = RegistryUtil.OpenBase(hive, is64Bit);
            using var key = baseKey.OpenSubKey(keyPath);
            if (key == null)
                return;

            foreach (var valueName in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(valueName))
                    continue;
                var command = key.GetString(valueName);
                if (string.IsNullOrWhiteSpace(command))
                    continue;

                var item = new StartupItem
                {
                    Id = $"{kind}|{hive}|{(is64Bit ? 64 : 32)}|{keyPath}|{valueName}".ToUpperInvariant(),
                    Name = valueName,
                    Command = command,
                    Kind = kind,
                    Hive = hive,
                    Is64Bit = is64Bit,
                    ValueName = valueName,
                    ExecutablePath = ProcessUtil.GetExecutableFromCommandLine(command) ?? "",
                    Enabled = IsApproved(hive, is64Bit, kind, valueName),
                    SourceText = kind == StartupKind.RunOnce
                        ? GetSourceText(hive, runOnce: true)
                        : GetSourceText(hive, runOnce: false)
                };
                item.Arguments = ExtractArguments(command, item.ExecutablePath);
                item.Publisher = GetCompany(item.ExecutablePath);
                items.Add(item);
            }
        }
        catch
        {
            // ignore
        }
    }

    private void AddStartupFolder(List<StartupItem> items, Environment.SpecialFolder folder, bool allUsers)
    {
        try
        {
            var path = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return;

            foreach (var file in Directory.EnumerateFiles(path))
            {
                var ext = Path.GetExtension(file);
                if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                    continue;

                var target = ShortcutUtil.ResolveTarget(file) ?? "";
                var item = new StartupItem
                {
                    Id = $"FOLDER|{(allUsers ? "ALL" : "CU")}|{file}".ToUpperInvariant(),
                    Name = Path.GetFileNameWithoutExtension(file),
                    Command = target.Length > 0 ? target : file,
                    FilePath = file,
                    ExecutablePath = target,
                    Kind = StartupKind.StartupFolder,
                    Hive = allUsers ? "HKLM" : "HKCU",
                    Enabled = IsApprovedFolder(allUsers, Path.GetFileName(file)),
                    SourceText = allUsers
                        ? LanguageManager.Instance.T("ListCtrl/sourceStartMenuAll", "StartMenu All")
                        : LanguageManager.Instance.T("ListCtrl/sourceStartMenuCU", "StartMenu CU")
                };
                item.Publisher = GetCompany(target);
                items.Add(item);
            }
        }
        catch
        {
            // ignore
        }
    }

    private void AddScheduledTasks(List<StartupItem> items)
    {
        Progress?.Invoke("Reading scheduled tasks...");
        const string script = """
            $ErrorActionPreference='SilentlyContinue'
            Get-ScheduledTask | ForEach-Object {
              $t=$_
              $trigger = $t.Triggers | Where-Object { $_.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger' } | Select-Object -First 1
              if ($trigger) {
                $action = $t.Actions | Select-Object -First 1
                [pscustomobject]@{
                  Name=$t.TaskName; Path=$t.TaskPath; State=$t.State.ToString();
                  Execute=$action.Execute; Arguments=$action.Arguments; WorkDir=$action.WorkingDirectory;
                  UserId=$t.Principal.UserId
                }
              }
            } | ConvertTo-Json -Compress -Depth 3
            """;
        var json = ProcessUtil.RunPowerShell(script, 45000).Trim();
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var elements = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };
            foreach (var element in elements)
            {
                var name = GetString(element, "Name");
                var path = GetString(element, "Path");
                var execute = GetString(element, "Execute");
                var arguments = GetString(element, "Arguments");
                var state = GetString(element, "State");
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var item = new StartupItem
                {
                    Id = $"TASK|{path}|{name}".ToUpperInvariant(),
                    Name = name,
                    TaskName = name,
                    TaskPath = path,
                    Command = string.Join(' ', new[] { execute, arguments }.Where(s => !string.IsNullOrWhiteSpace(s))),
                    ExecutablePath = execute,
                    Arguments = arguments,
                    Kind = StartupKind.ScheduledTask,
                    Enabled = !string.Equals(state, "Disabled", StringComparison.OrdinalIgnoreCase),
                    SourceText = LanguageManager.Instance.T("ListCtrl/sourceWindowsTask", "Windows Task")
                };
                item.Publisher = GetCompany(execute);
                items.Add(item);
            }
        }
        catch
        {
            // ignore malformed task output
        }
    }

    private void AddStoreStartupTasks(List<StartupItem> items)
    {
        Progress?.Invoke("Reading Microsoft Store startup tasks...");
        const string script = """
            $ErrorActionPreference='SilentlyContinue'
            Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage } |
              Select-Object Name,PackageFamilyName,InstallLocation | ConvertTo-Json -Compress -Depth 3
            """;
        var json = ProcessUtil.RunPowerShell(script, 45000).Trim();
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var elements = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };
            foreach (var element in elements)
            {
                var name = GetString(element, "Name");
                var family = GetString(element, "PackageFamilyName");
                var location = GetString(element, "InstallLocation");
                var manifest = Path.Combine(location, "AppxManifest.xml");
                if (!File.Exists(manifest))
                    continue;

                XDocument manifestDoc;
                try
                {
                    manifestDoc = XDocument.Load(manifest);
                }
                catch
                {
                    continue;
                }

                foreach (var task in manifestDoc.Descendants().Where(e => e.Name.LocalName == "StartupTask"))
                {
                    var taskId = task.Attribute("TaskId")?.Value;
                    if (string.IsNullOrWhiteSpace(taskId))
                        continue;
                    var enabled = !string.Equals(task.Attribute("Enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase);
                    var appId = manifestDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application")?
                        .Attribute("Id")?.Value ?? "";
                    var item = new StartupItem
                    {
                        Id = $"STORETASK|{family}|{taskId}".ToUpperInvariant(),
                        Name = $"{Prettify(name)} ({taskId})",
                        Command = $"shell:AppsFolder\\{family}!{appId}",
                        Kind = StartupKind.StoreTask,
                        Enabled = enabled,
                        SourceText = LanguageManager.Instance.T("ListCtrl/sourceWindowsStoreTask", "Microsoft Store App"),
                        TaskName = taskId,
                        TaskPath = family
                    };
                    items.Add(item);
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string Prettify(string packageName)
    {
        var name = packageName.Split('.')[0];
        return System.Text.RegularExpressions.Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
    }

    private static string GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    private static string GetSourceText(string hive, bool runOnce)
    {
        var key = (hive, runOnce) switch
        {
            ("HKLM", false) => "ListCtrl/sourceHKLMRun",
            ("HKLM", true) => "ListCtrl/sourceHKLMRunOnce",
            ("HKCU", false) => "ListCtrl/sourceHKCURun",
            _ => "ListCtrl/sourceHKCURunOnce"
        };
        return LanguageManager.Instance.T(key, "Run");
    }

    private static bool IsApproved(string hive, bool is64Bit, StartupKind kind, string valueName)
    {
        if (kind == StartupKind.RunOnce)
            return true;
        try
        {
            var path = is64Bit ? ApprovedRun : ApprovedRun32;
            using var baseKey = RegistryUtil.OpenBase(hive, is64Bit);
            using var key = baseKey.OpenSubKey(path);
            if (key?.GetValue(valueName) is byte[] data && data.Length > 0)
                return data[0] is not (3 or 7);
        }
        catch
        {
            // ignore
        }

        return true;
    }

    private static bool IsApprovedFolder(bool allUsers, string fileName)
    {
        try
        {
            using var baseKey = RegistryUtil.OpenBase(allUsers ? "HKLM" : "HKCU", true);
            using var key = baseKey.OpenSubKey(ApprovedFolder);
            if (key?.GetValue(fileName) is byte[] data && data.Length > 0)
                return data[0] is not (3 or 7);
        }
        catch
        {
            // ignore
        }

        return true;
    }

    private static bool IsItemRunning(StartupItem item)
    {
        try
        {
            var exe = item.ExecutablePath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                return false;
            var name = Path.GetFileNameWithoutExtension(exe);
            return ProcessUtil.IsRunning(name);
        }
        catch
        {
            return false;
        }
    }

    private static StartupImpact ComputeImpact(StartupItem item)
    {
        var path = item.ExecutablePath;
        var userWritable = IsUserWritable(path);
        var command = item.Command;

        if (item.Running && (userWritable || string.IsNullOrWhiteSpace(path)))
            return StartupImpact.High;

        if (item.Running || userWritable ||
            command.Contains("update", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("helper", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("tray", StringComparison.OrdinalIgnoreCase))
            return StartupImpact.Medium;

        return StartupImpact.Low;
    }

    private static bool IsUserWritable(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;
        try
        {
            var full = Path.GetFullPath(path);
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Path.GetTempPath()
            };
            return roots.Where(r => !string.IsNullOrWhiteSpace(r))
                .Any(r => full.StartsWith(r.TrimEnd('\\') + "\\",
                    StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return true;
        }
    }

    private static string ExtractArguments(string command, string exe)
    {
        if (string.IsNullOrEmpty(exe))
            return "";
        var index = command.IndexOf(exe, StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? command[(index + exe.Length)..].Trim() : "";
    }

    public static string GetCompany(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return "";
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.CompanyName ?? "";
        }
        catch
        {
            return "";
        }
    }
}

public sealed class DisabledStartupItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Hive { get; set; } = "";
    public string ValueName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string TaskPath { get; set; } = "";
    public string TaskName { get; set; } = "";
}

public static class DisabledStartupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string FilePath => Path.Combine(AppSettings.DataDirectory,
        "DisabledStartupItems.json");

    public static List<DisabledStartupItem> Load()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 8 * 1024 * 1024)
                return new List<DisabledStartupItem>();
            return JsonSerializer.Deserialize<List<DisabledStartupItem>>(File.ReadAllText(FilePath)) ??
                   new List<DisabledStartupItem>();
        }
        catch
        {
            return new List<DisabledStartupItem>();
        }
    }

    public static void Save(IEnumerable<DisabledStartupItem> items)
    {
        try
        {
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(items.ToList(), JsonOptions));
        }
        catch
        {
            // ignore
        }
    }

    public static void Add(DisabledStartupItem item)
    {
        var list = Load();
        list.RemoveAll(i => i.Id == item.Id);
        list.Add(item);
        Save(list);
    }

    public static void Remove(string id)
    {
        var list = Load();
        list.RemoveAll(i => i.Id == id);
        Save(list);
    }
}
