using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;

namespace CleanRift.Core;

/// <summary>
/// Enumerates installed programs the same way CleanRift does:
/// HKCU/HKLM uninstall keys in both registry views, MSI Installer UserData
/// products and Microsoft Store packages.
/// </summary>
public sealed class ProgramScanner
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallKey32 = @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public event Action<string>? Progress;

    public List<InstalledProgram> Scan(bool includeStore = true)
    {
        var programs = new List<InstalledProgram>();
        ScanRegistryView(programs, "HKLM", is64Bit: true);
        ScanRegistryView(programs, "HKLM", is64Bit: false);
        ScanRegistryView(programs, "HKCU", is64Bit: true);
        ScanRegistryView(programs, "HKCU", is64Bit: false);
        ScanMsiProducts(programs);

        if (includeStore)
        {
            try
            {
                ScanStoreApps(programs);
            }
            catch
            {
                // Store enumeration is optional.
            }
        }

        Deduplicate(programs);
        return programs;
    }

    private void ScanRegistryView(List<InstalledProgram> programs, string hive, bool is64Bit)
    {
        Progress?.Invoke(is64Bit ? "Reading 64-bit registry..." : "Reading 32-bit registry...");
        try
        {
            using var baseKey = RegistryUtil.OpenBase(hive, is64Bit);
            using var uninstall = baseKey.OpenSubKey(UninstallKey);
            if (uninstall == null)
                return;

            foreach (var subName in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(subName);
                    if (key == null)
                        continue;

                    var name = key.GetString("DisplayName").Trim();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var program = new InstalledProgram
                    {
                        DisplayName = name,
                        DisplayVersion = key.GetString("DisplayVersion"),
                        Publisher = key.GetString("Publisher"),
                        InstallLocation = key.GetString("InstallLocation"),
                        InstallDateRaw = key.GetString("InstallDate"),
                        EstimatedSizeKb = key.GetLong("EstimatedSize") is var kb and > 0 ? kb : null,
                        UninstallString = key.GetString("UninstallString"),
                        QuietUninstallString = key.GetString("QuietUninstallString"),
                        ModifyPath = key.GetString("ModifyPath"),
                        DisplayIcon = key.GetString("DisplayIcon"),
                        HelpLink = key.GetString("HelpLink"),
                        UrlInfoAbout = key.GetString("URLInfoAbout"),
                        ReleaseType = key.GetString("ReleaseType"),
                        ParentKeyName = key.GetString("ParentKeyName"),
                        ParentDisplayName = key.GetString("ParentDisplayName"),
                        SystemComponent = key.GetBool("SystemComponent"),
                        NoModify = key.GetBool("NoModify"),
                        NoRemove = key.GetBool("NoRemove"),
                        WindowsInstaller = key.GetBool("WindowsInstaller"),
                        HiveName = hive,
                        Is64Bit = is64Bit,
                        RegistryPath = (hive == "HKLM" ? "HKEY_LOCAL_MACHINE\\" : "HKEY_CURRENT_USER\\") +
                                       UninstallKey + "\\" + subName,
                        Source = ProgramSource.Registry,
                        ProductCode = key.GetString("ProductCode")
                    };

                    program.InstallDate = RegistryUtil.GetLastWriteTime(key)
                                          ?? RegistryUtil.ParseInstallDate(program.InstallDateRaw);
                    program.Id = MakeId(program.Source, hive, is64Bit, subName);
                    if (program.EstimatedSizeKb is > 0)
                        program.SizeBytes = program.EstimatedSizeKb.Value * 1024L;

                    if (program.WindowsInstaller && string.IsNullOrWhiteSpace(program.ProductCode) &&
                        subName.StartsWith('{'))
                        program.ProductCode = subName;

                    programs.Add(program);
                }
                catch
                {
                    // Skip unreadable keys.
                }
            }
        }
        catch
        {
            // Registry view unavailable.
        }
    }

    private void ScanMsiProducts(List<InstalledProgram> programs)
    {
        Progress?.Invoke("Reading MSI products...");
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var userData = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData");
            if (userData == null)
                return;

            var knownProducts = new HashSet<string>(programs
                .Where(p => !string.IsNullOrEmpty(p.ProductCode))
                .Select(p => p.ProductCode.Trim('{', '}').ToUpperInvariant()));

            foreach (var sid in userData.GetSubKeyNames())
            {
                using var products = userData.OpenSubKey(sid + @"\Products");
                if (products == null)
                    continue;

                foreach (var product in products.GetSubKeyNames())
                {
                    try
                    {
                        using var props = products.OpenSubKey(product + @"\InstallProperties");
                        if (props == null)
                            continue;

                        var name = props.GetString("DisplayName").Trim();
                        if (string.IsNullOrWhiteSpace(name))
                            continue;

                        var productCode = RegistryUtil.PackedGuidToGuid(product);
                        if (knownProducts.Contains(productCode.Trim('{', '}').ToUpperInvariant()))
                            continue;

                        var program = new InstalledProgram
                        {
                            DisplayName = name,
                            DisplayVersion = props.GetString("DisplayVersion"),
                            Publisher = props.GetString("Publisher"),
                            InstallLocation = props.GetString("InstallLocation"),
                            InstallDateRaw = props.GetString("InstallDate"),
                            EstimatedSizeKb = props.GetLong("EstimatedSize") is var kb and > 0 ? kb : null,
                            UninstallString = props.GetString("UninstallString"),
                            ModifyPath = props.GetString("ModifyPath"),
                            DisplayIcon = props.GetString("DisplayIcon"),
                            HelpLink = props.GetString("HelpLink"),
                            UrlInfoAbout = props.GetString("URLInfoAbout"),
                            WindowsInstaller = true,
                            SystemComponent = props.GetBool("SystemComponent"),
                            HiveName = "HKLM",
                            Is64Bit = true,
                            RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\" +
                                           sid + @"\Products\" + product,
                            Source = ProgramSource.Msi,
                            ProductCode = productCode
                        };
                        program.InstallDate = RegistryUtil.GetLastWriteTime(props)
                                              ?? RegistryUtil.ParseInstallDate(program.InstallDateRaw);
                        program.Id = MakeId(program.Source, "HKLM", true, product);
                        if (program.EstimatedSizeKb is > 0)
                            program.SizeBytes = program.EstimatedSizeKb.Value * 1024L;

                        if (string.IsNullOrWhiteSpace(program.UninstallString))
                            program.UninstallString = $"MsiExec.exe /X{productCode}";

                        knownProducts.Add(productCode.Trim('{', '}').ToUpperInvariant());
                        programs.Add(program);
                    }
                    catch
                    {
                        // Skip malformed product.
                    }
                }
            }
        }
        catch
        {
            // Installer data unavailable.
        }
    }

    private void ScanStoreApps(List<InstalledProgram> programs)
    {
        Progress?.Invoke("Reading Microsoft Store apps...");
        const string script = """
            $ErrorActionPreference='SilentlyContinue'
            Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage -and -not $_.IsBundle } |
              Select-Object Name,PackageFullName,PackageFamilyName,PublisherDisplayName,Version,Architecture,InstallLocation |
              ConvertTo-Json -Compress -Depth 3
            """;
        var json = ProcessUtil.RunPowerShell(script, 45000).Trim();
        if (string.IsNullOrWhiteSpace(json))
            return;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch
        {
            return;
        }

        using (doc)
        {
            var elements = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };

            foreach (var element in elements)
            {
                try
                {
                    var packageName = GetString(element, "Name");
                    var fullName = GetString(element, "PackageFullName");
                    var family = GetString(element, "PackageFamilyName");
                    var installLocation = GetString(element, "InstallLocation");
                    if (string.IsNullOrWhiteSpace(fullName))
                        continue;

                    var (displayName, logo) = ReadManifest(installLocation, packageName);
                    if (string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                        displayName = PrettifyPackageName(packageName);

                    var program = new InstalledProgram
                    {
                        DisplayName = displayName.Trim(),
                        DisplayVersion = GetString(element, "Version"),
                        Publisher = GetString(element, "PublisherDisplayName"),
                        InstallLocation = installLocation,
                        Architecture = GetString(element, "Architecture"),
                        PackageFullName = fullName,
                        PackageFamilyName = family,
                        StoreLogo = logo,
                        Source = ProgramSource.Store,
                        HiveName = "",
                        Is64Bit = Environment.Is64BitOperatingSystem,
                        UninstallString = $"powershell -WindowStyle hidden \"Get-AppxPackage -name {packageName} | Remove-AppxPackage\"",
                        Id = MakeId(ProgramSource.Store, "", false, fullName)
                    };

                    if (DateTime.TryParse(program.DisplayVersion, out _))
                    {
                        // Version strings are numeric; not a date.
                    }

                    try
                    {
                        var info = new DirectoryInfo(installLocation);
                        if (info.Exists)
                            program.InstallDate = info.CreationTime;
                    }
                    catch
                    {
                        // ignore
                    }

                    programs.Add(program);
                }
                catch
                {
                    // Skip malformed package.
                }
            }
        }
    }

    private static (string displayName, string logo) ReadManifest(string installLocation, string packageName)
    {
        try
        {
            var manifest = Path.Combine(installLocation, "AppxManifest.xml");
            if (!File.Exists(manifest))
                return ("", "");
            var doc = XDocument.Load(manifest);
            var displayName = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName is "DisplayName" &&
                                     e.Parent?.Name.LocalName == "Properties")?.Value ?? "";
            var logo = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Logo" &&
                                     e.Parent?.Name.LocalName == "Properties")?.Value ?? "";
            if (!string.IsNullOrWhiteSpace(logo))
            {
                logo = Path.Combine(installLocation, logo.Replace('/', '\\'));
                if (!File.Exists(logo))
                    logo = "";
            }

            return (displayName, logo);
        }
        catch
        {
            return ("", "");
        }
    }

    private static string PrettifyPackageName(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return "Store app";
        var name = packageName.Split('.')[0];
        name = System.Text.RegularExpressions.Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
        name = name.Replace('_', ' ').Trim();
        return string.IsNullOrWhiteSpace(name) ? packageName : name;
    }

    private static string GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            _ => ""
        };
    }

    private static string MakeId(ProgramSource source, string hive, bool is64Bit, string key)
    {
        return $"{source}|{hive}|{(is64Bit ? 64 : 32)}|{key}".ToUpperInvariant();
    }

    private static void Deduplicate(List<InstalledProgram> programs)
    {
        // The 32/64 views can expose the same product. Keep the 64-bit entry,
        // preferring one with an uninstall command.
        var groups = programs.GroupBy(p => (p.DisplayName.Trim().ToUpperInvariant(), p.DisplayVersion.Trim(),
            p.Publisher.Trim().ToUpperInvariant()));
        var result = new List<InstalledProgram>();
        foreach (var group in groups)
        {
            var ordered = group
                .OrderByDescending(p => p.Source == ProgramSource.Store)
                .ThenByDescending(p => p.Is64Bit)
                .ThenByDescending(p => !string.IsNullOrWhiteSpace(p.UninstallString))
                .ToList();
            var best = ordered[0];
            var alt = ordered.FirstOrDefault(p => p.Is64Bit != best.Is64Bit && !string.IsNullOrWhiteSpace(p.UninstallString));
            if (best.Source != ProgramSource.Store && alt is { Is64Bit: true } && !best.Is64Bit)
                best = alt;
            result.Add(best);
        }

        programs.Clear();
        programs.AddRange(result);
    }

    public static void ApplyState(List<InstalledProgram> programs, AppSettings settings)
    {
        var traced = TraceStore.LoadAll();
        foreach (var program in programs)
        {
            program.IsHidden = settings.HiddenProgramIds.Contains(program.Id);
            program.IsRecent = program.InstallDate.HasValue &&
                               program.InstallDate.Value >= DateTime.Now.AddDays(-7);
            traced.TryGetValue(program.Id, out var trace);
            trace ??= traced.Values.FirstOrDefault(t => string.Equals(t.DisplayName, program.DisplayName,
                StringComparison.OrdinalIgnoreCase));
            if (trace != null)
            {
                program.IsTraced = true;
                program.TraceFile = trace.File;
            }
        }

        // Software updates are shown as children of their parent product.
        var byName = programs
            .Where(p => !p.IsChild)
            .GroupBy(p => p.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var update in programs.Where(p => p.IsUpdate))
        {
            var parentName = !string.IsNullOrWhiteSpace(update.ParentDisplayName)
                ? update.ParentDisplayName
                : update.ParentKeyName;
            if (string.IsNullOrWhiteSpace(parentName))
                continue;

            if (byName.TryGetValue(parentName.Trim(), out var parent) && !ReferenceEquals(parent, update))
            {
                update.IsChild = true;
                update.GroupKey = parent.Id;
                update.Indent = 1;
                parent.IsGroupParent = true;
                parent.IsGroupExpanded = settings.UpdatesMode != 0;
            }
        }
    }

    public static void UpdateKnownDates(List<InstalledProgram> programs, AppSettings settings)
    {
        foreach (var program in programs)
        {
            if (!program.InstallDate.HasValue)
                continue;
            var key = program.Id;
            if (settings.KnownInstallDates.TryGetValue(key, out var old) &&
                DateTime.TryParse(old, out var oldDate) &&
                Math.Abs((program.InstallDate.Value - oldDate).TotalMinutes) > 1)
            {
                program.IsRecent = true;
            }

            settings.KnownInstallDates[key] = program.InstallDate.Value.ToString("O");
        }
    }

    public async Task ComputeSizesAsync(IReadOnlyList<InstalledProgram> programs, IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        var pending = programs.Where(p => p.SizeBytes == 0 && !string.IsNullOrWhiteSpace(p.InstallLocation)).ToList();
        await Task.Run(() =>
        {
            Parallel.ForEach(pending, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2),
                CancellationToken = token
            }, program =>
            {
                try
                {
                    progress?.Report(program.DisplayName);
                    program.SizeBytes = GetDirectorySize(program.InstallLocation, token);
                    program.SizeComputed = program.SizeBytes > 0;
                }
                catch
                {
                    // ignore
                }
            });
        }, token);
    }

    public static long GetDirectorySize(string path, CancellationToken token = default)
    {
        var dir = path.Trim().Trim('"');
        if (!Directory.Exists(dir))
            return 0;

        long total = 0;
        var count = 0;
        var start = Environment.TickCount64;
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            if (token.IsCancellationRequested || Environment.TickCount64 - start > 5000 || count > 20000)
                break;
            var current = stack.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                        count++;
                    }
                    catch
                    {
                        // ignore
                    }
                }

                foreach (var sub in Directory.EnumerateDirectories(current))
                {
                    try
                    {
                        var attributes = File.GetAttributes(sub);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            continue;
                    }
                    catch
                    {
                        continue;
                    }

                    stack.Push(sub);
                }
            }
            catch
            {
                // ignore inaccessible folder
            }
        }

        return total;
    }

    public static string? FindRelatedFolder(InstalledProgram program)
    {
        if (!string.IsNullOrWhiteSpace(program.InstallLocation) && Directory.Exists(program.InstallLocation))
            return program.InstallLocation;
        return null;
    }
}
