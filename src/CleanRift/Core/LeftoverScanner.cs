using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CleanRift.Core;

/// <summary>
/// Deep leftover scan used by the Removal Wizard and Force Removal.
/// It checks the install folder, Start Menu and Desktop shortcuts, matching
/// folders under Program Files, ProgramData and the user profile, plus registry
/// keys and values that refer to the program.
/// </summary>
public sealed class LeftoverScanner
{
    private const int MaxEntriesPerRoot = 4000;
    private const int MaxDepth = 10;

    public event Action<string>? Progress;

    public static readonly string[] ProtectedPaths =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\"
    };

    public ScanResult Scan(InstalledProgram program, TraceInfo? trace, CancellationToken token)
    {
        var result = new ScanResult();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = BuildNameVariants(program);
        var strongNames = BuildDisplayNameVariants(program);

        Progress?.Invoke(LanguageManager.Instance.T("UninstallWizard/statusScanning",
            "Scanning for leftover files, folders and registry items..."));

        // 1. Traced data is authoritative for traced programs.
        if (trace != null)
        {
            Progress?.Invoke(LanguageManager.Instance.T("UninstallWizard/statusScanningTraced",
                "Generating the list of leftover traced items, please wait..."));

            foreach (var folder in trace.Folders)
            {
                token.ThrowIfCancellationRequested();
                AddFileRoot(result, seenPaths, folder, traced: true);
            }

            foreach (var file in trace.Files)
            {
                token.ThrowIfCancellationRequested();
                AddFileRoot(result, seenPaths, file, traced: true);
            }

            foreach (var key in trace.RegistryKeys)
            {
                token.ThrowIfCancellationRequested();
                AddRegistryRoot(result, seenKeys, key, traced: true);
            }

            foreach (var (key, valueName) in trace.RegistryValues)
            {
                token.ThrowIfCancellationRequested();
                AddRegistryValue(result, seenKeys, key, valueName, traced: true);
            }
        }

        // 2. Installation folder.
        Progress?.Invoke(LanguageManager.Instance.T("UninstallWizard/statusScanning",
            "Scanning for leftover files, folders and registry items..."));

        if (!string.IsNullOrWhiteSpace(program.InstallLocation))
        {
            var location = program.InstallLocation.Trim().Trim('"');
            if (Directory.Exists(location))
                AddFileRoot(result, seenPaths, location, traced: false);
            else if (File.Exists(location))
                AddFileRoot(result, seenPaths, location, traced: false);
        }

        // 3. Folders matching the product/publisher name.
        try
        {
            foreach (var candidate in FindMatchingFolders(program, names, strongNames, token))
            {
                token.ThrowIfCancellationRequested();
                AddFileRoot(result, seenPaths, candidate, traced: false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A single inaccessible location must not abort the whole scan.
        }

        // 4. Start menu and desktop shortcuts.
        try
        {
            foreach (var shortcut in FindShortcuts(program, names, token))
            {
                token.ThrowIfCancellationRequested();
                AddFileRoot(result, seenPaths, shortcut, traced: false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // ignore
        }

        // 5. Registry.
        try
        {
            ScanRegistry(program, names, strongNames, result, seenKeys, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // ignore
        }

        result.FileCount = result.FileSystemRoot.Children.Sum(c => c.TotalCount);
        result.RegistryCount = result.RegistryRoot.Children.Sum(c => c.TotalCount);
        result.TotalSize = result.FileSystemRoot.TotalSize;

        return result;
    }

    public static List<string> BuildNameVariants(InstalledProgram program)
    {
        var variants = BuildDisplayNameVariants(program);
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            var cleaned = CleanName(value);
            if (cleaned.Length >= 2)
                variants.Add(cleaned);
        }

        var publisher = program.Publisher ?? "";
        Add(publisher);
        Add(publisher.Replace(" Corporation", "").Replace(" Inc.", "").Replace(" Ltd.", ""));

        return variants.Where(v => v.Length >= 2).ToList();
    }

    /// <summary>
    /// Variants derived from the product itself (not from the publisher).
    /// Only these may match a whole top-level folder or registry key.
    /// </summary>
    public static List<string> BuildDisplayNameVariants(InstalledProgram program)
    {
        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            var cleaned = CleanName(value);
            if (cleaned.Length >= 2)
                variants.Add(cleaned);
        }

        var display = program.DisplayName ?? "";
        Add(display);
        Add(Regex.Replace(display, @"\s*\(.*?\)\s*$", ""));
        Add(Regex.Replace(display, @"\s+v?\d+(\.\d+)*.*$", ""));
        Add(Regex.Replace(display, @"\s+(x64|x86|64-bit|32-bit|64 bit|32 bit)$", "",
            RegexOptions.IgnoreCase));

        if (!string.IsNullOrWhiteSpace(program.InstallLocation))
        {
            try
            {
                var dir = program.InstallLocation.Trim().Trim('"').TrimEnd('\\');
                Add(Path.GetFileName(dir));
            }
            catch
            {
                // ignore
            }
        }

        return variants.Where(v => v.Length >= 2).ToList();
    }

    private static string CleanName(string value)
    {
        var text = value.Trim();
        text = text.Replace("™", "").Replace("®", "").Replace("©", "");
        text = Regex.Replace(text, @"\s+", " ");
        return text.Trim();
    }

    private IEnumerable<string> FindMatchingFolders(InstalledProgram program, List<string> names,
        List<string> strongNames, CancellationToken token)
    {
        var found = new List<string>();
        var roots = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        };

        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (root == null || !Directory.Exists(root))
                continue;

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(root);
            }
            catch
            {
                continue;
            }

            foreach (var dir in children)
            {
                token.ThrowIfCancellationRequested();
                if (IsProtected(dir))
                    continue;

                var name = Path.GetFileName(dir);
                if (ShouldSkipFolder(name))
                    continue;

                var exactFolder = strongNames.Any(n => Normalize(n) == Normalize(name));
                if (exactFolder)
                {
                    found.Add(dir);
                    continue;
                }

                // Publisher folder containing a product folder. A fuzzy match on
                // the umbrella folder itself is not enough: only descend and
                // target the product subfolder so a shared vendor directory is
                // never offered for wholesale deletion.
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        var subName = Path.GetFileName(sub);
                        if (names.Any(n => NamesMatch(n, subName)))
                        {
                            found.Add(sub);
                            break;
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }

        return found
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !IsInsideAny(path, found))
            .ToList();
    }

    private static bool ShouldSkipFolder(string name)
    {
        string[] skip =
        {
            "Windows", "Microsoft", "Google", "Mozilla", "Temp", "Packages", "Installer",
            "Common Files", "WindowsApps", "System32", "SysWOW64", "Reference Assemblies",
            "Package Cache", "NuGet", "dotnet", "node_modules", "CleanRift"
        };
        return skip.Any(s => name.Equals(s, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<string> FindShortcuts(InstalledProgram program, List<string> names,
        CancellationToken token)
    {
        var result = new List<string>();
        var folders = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };

        var installDir = program.InstallLocation.Trim().Trim('"');

        foreach (var folder in folders.Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (folder == null || !Directory.Exists(folder))
                continue;

            foreach (var file in SafeEnumerateFiles(folder, token))
            {
                token.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(file);
                if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = Path.GetFileNameWithoutExtension(file);
                if (names.Any(n => NamesMatch(n, name)))
                {
                    result.Add(file);
                    continue;
                }

                var target = ShortcutUtil.ResolveTarget(file);
                if (!string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(installDir) &&
                    IsSameOrUnder(target, installDir))
                {
                    result.Add(file);
                }
            }
        }

        return result;
    }

    private void ScanRegistry(InstalledProgram program, List<string> names, List<string> strongNames,
        ScanResult result, HashSet<string> seenKeys, CancellationToken token)
    {
        // The registered uninstall key itself.
        if (!string.IsNullOrWhiteSpace(program.RegistryPath))
            AddRegistryRoot(result, seenKeys, program.RegistryPath, traced: false);

        foreach (var hive in new[] { "HKEY_LOCAL_MACHINE", "HKEY_CURRENT_USER" })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                token.ThrowIfCancellationRequested();

                // Uninstall entries with the same display name in another view.
                ScanUninstallEntries(hive, view, program, result, seenKeys, token);

                // Software\<product> and Software\<publisher>\<product>.
                ScanSoftware(hive, view, names, strongNames, result, seenKeys);

                // App Paths.
                ScanAppPaths(hive, view, program, result, seenKeys);

                // Run / RunOnce values referring to the program.
                ScanRunValues(hive, view, program, result, seenKeys);
            }
        }

        // MSI Installer UserData product key.
        if (program.WindowsInstaller && !string.IsNullOrWhiteSpace(program.ProductCode))
        {
            try
            {
                var packed = program.ProductCode.GuidToPacked();
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var userData = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData");
                if (userData != null)
                {
                    foreach (var sid in userData.GetSubKeyNames())
                    {
                        var path = $@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\{sid}\Products\{packed}";
                        using var product = userData.OpenSubKey(sid + @"\Products\" + packed);
                        if (product != null)
                            AddRegistryRoot(result, seenKeys, path, traced: false);
                    }
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch
            {
                files = Array.Empty<string>();
            }

            foreach (var file in files)
                yield return file;

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(dir);
            }
            catch
            {
                subdirs = Array.Empty<string>();
            }

            foreach (var sub in subdirs)
            {
                try
                {
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
                        continue;
                }
                catch
                {
                    continue;
                }

                pending.Push(sub);
            }
        }
    }

    private static void ScanUninstallEntries(string hive, RegistryView view, InstalledProgram program,
        ScanResult result, HashSet<string> seenKeys, CancellationToken token)
    {
        try
        {
            var registryHive = hive == "HKEY_LOCAL_MACHINE" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            using var baseKey = RegistryKey.OpenBaseKey(registryHive, view);
            using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall == null)
                return;

            foreach (var sub in uninstall.GetSubKeyNames())
            {
                token.ThrowIfCancellationRequested();
                using var key = uninstall.OpenSubKey(sub);
                if (key == null)
                    continue;
                var displayName = key.GetString("DisplayName").Trim();
                if (string.IsNullOrWhiteSpace(displayName))
                    continue;
                if (!displayName.Equals(program.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;

                var full = $@"{hive}\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{sub}";
                AddRegistryRoot(result, seenKeys, full, traced: false);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void ScanSoftware(string hive, RegistryView view, List<string> names,
        List<string> strongNames, ScanResult result, HashSet<string> seenKeys)
    {
        try
        {
            var registryHive = hive == "HKEY_LOCAL_MACHINE" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            using var baseKey = RegistryKey.OpenBaseKey(registryHive, view);
            using var software = baseKey.OpenSubKey("SOFTWARE");
            if (software == null)
                return;

            foreach (var sub in software.GetSubKeyNames())
            {
                if (sub.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                    sub.Equals("Classes", StringComparison.OrdinalIgnoreCase) ||
                    sub.Equals("Policies", StringComparison.OrdinalIgnoreCase) ||
                    sub.Equals("Clients", StringComparison.OrdinalIgnoreCase) ||
                    sub.Equals("RegisteredApplications", StringComparison.OrdinalIgnoreCase))
                    continue;

                var exactKey = strongNames.Any(n => Normalize(n) == Normalize(sub));
                if (exactKey)
                {
                    AddRegistryRoot(result, seenKeys,
                        $@"{hive}\SOFTWARE\{sub}", traced: false);
                    continue;
                }

                // One level down: publisher\product. Do not remove the whole
                // top-level key on a fuzzy match; it is often a vendor umbrella
                // that is shared by several products.
                try
                {
                    using var publisher = software.OpenSubKey(sub);
                    if (publisher == null)
                        continue;
                    foreach (var product in publisher.GetSubKeyNames())
                    {
                        if (!names.Any(n => NamesMatch(n, product)))
                            continue;
                        AddRegistryRoot(result, seenKeys,
                            $@"{hive}\SOFTWARE\{sub}\{product}", traced: false);
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void ScanAppPaths(string hive, RegistryView view, InstalledProgram program,
        ScanResult result, HashSet<string> seenKeys)
    {
        try
        {
            var installDir = program.InstallLocation.Trim().Trim('"');
            if (string.IsNullOrEmpty(installDir))
                return;
            var registryHive = hive == "HKEY_LOCAL_MACHINE" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            using var baseKey = RegistryKey.OpenBaseKey(registryHive, view);
            using var appPaths = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            if (appPaths == null)
                return;

            foreach (var sub in appPaths.GetSubKeyNames())
            {
                try
                {
                    using var key = appPaths.OpenSubKey(sub);
                    if (key == null)
                        continue;
                    var value = key.GetString("").Trim().Trim('"');
                    if (IsSameOrUnder(value, installDir))
                    {
                        AddRegistryRoot(result, seenKeys,
                            $@"{hive}\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{sub}", traced: false);
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void ScanRunValues(string hive, RegistryView view, InstalledProgram program,
        ScanResult result, HashSet<string> seenKeys)
    {
        var paths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"
        };
        var markers = new List<string>();
        if (!string.IsNullOrWhiteSpace(program.InstallLocation))
            markers.Add(program.InstallLocation.Trim().Trim('"'));

        try
        {
            var registryHive = hive == "HKEY_LOCAL_MACHINE" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            using var baseKey = RegistryKey.OpenBaseKey(registryHive, view);
            foreach (var path in paths)
            {
                using var key = baseKey.OpenSubKey(path);
                if (key == null)
                    continue;
                foreach (var name in key.GetValueNames())
                {
                    var data = key.GetString(name);
                    if (string.IsNullOrWhiteSpace(data))
                        continue;
                    if (markers.Count == 0 ||
                        !markers.Any(m => ReferencesPath(data, m)))
                        continue;
                    AddRegistryValue(result, seenKeys, $@"{hive}\{path}", name, traced: false);
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void AddFileRoot(ScanResult result, HashSet<string> seen, string path, bool traced)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(full) || IsProtected(full) || !seen.Add(full))
                return;

            if (File.Exists(full))
            {
                var info = new FileInfo(full);
                result.FileSystemRoot.Children.Add(new LeftoverItem
                {
                    Kind = LeftoverKind.File,
                    Name = info.Name,
                    FullPath = info.FullName,
                    Size = SafeLength(info),
                    Checked = true
                });
                return;
            }

            if (!Directory.Exists(full))
                return;

            var rootName = Path.GetFileName(full);
            if (string.IsNullOrEmpty(rootName))
                rootName = full;

            var item = new LeftoverItem
            {
                Kind = LeftoverKind.Folder,
                Name = rootName,
                FullPath = full,
                Checked = true
            };
            FillDirectory(item, full, 0, traced);
            result.FileSystemRoot.Children.Add(item);
        }
        catch
        {
            // ignore
        }
    }

    private static void FillDirectory(LeftoverItem parent, string directory, int depth, bool traced)
    {
        if (depth >= MaxDepth || parent.Children.Count >= MaxEntriesPerRoot)
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (parent.Children.Count >= MaxEntriesPerRoot)
                    return;
                try
                {
                    var info = new FileInfo(file);
                    parent.Children.Add(new LeftoverItem
                    {
                        Kind = LeftoverKind.File,
                        Name = info.Name,
                        FullPath = info.FullName,
                        Size = SafeLength(info),
                        Parent = parent,
                        Checked = true
                    });
                }
                catch
                {
                    // ignore
                }
            }

            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                if (parent.Children.Count >= MaxEntriesPerRoot)
                    return;
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

                var child = new LeftoverItem
                {
                    Kind = LeftoverKind.Folder,
                    Name = Path.GetFileName(sub),
                    FullPath = sub,
                    Parent = parent,
                    Checked = true
                };
                FillDirectory(child, sub, depth + 1, traced);
                parent.Children.Add(child);
            }
        }
        catch
        {
            // ignore inaccessible folder
        }
    }

    private static long SafeLength(FileInfo info)
    {
        try { return info.Length; }
        catch { return 0; }
    }

    private static void AddRegistryRoot(ScanResult result, HashSet<string> seen, string keyPath, bool traced)
    {
        try
        {
            var normalized = keyPath.TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(normalized) ||
                !seen.Add("K|" + normalized) ||
                PathSafety.IsProtectedRegistryKey(normalized, out _))
                return;

            var item = new LeftoverItem
            {
                Kind = LeftoverKind.RegistryKey,
                Name = normalized.Split('\\').Last(),
                FullPath = normalized,
                Checked = true
            };

            FillRegistryKey(item, normalized, 0);
            result.RegistryRoot.Children.Add(item);
        }
        catch
        {
            // ignore
        }
    }

    private static void FillRegistryKey(LeftoverItem parent, string keyPath, int depth)
    {
        if (depth >= 6 || parent.Children.Count >= MaxEntriesPerRoot)
            return;

        try
        {
            var parsed = ParseKeyPath(keyPath);
            if (parsed == null)
                return;
            var (hive, subPath) = parsed.Value;
            var registryHive = hive == "HKEY_LOCAL_MACHINE" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            using var baseKey = RegistryKey.OpenBaseKey(registryHive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(subPath);
            if (key == null)
                return;

            foreach (var valueName in key.GetValueNames())
            {
                try
                {
                    var value = key.GetValue(valueName);
                    var text = value switch
                    {
                        null => "",
                        byte[] bytes => Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, 32)) + (bytes.Length > 32 ? "..." : ""),
                        string[] array => string.Join("; ", array),
                        _ => value.ToString() ?? ""
                    };
                    parent.Children.Add(new LeftoverItem
                    {
                        Kind = LeftoverKind.RegistryValue,
                        Name = string.IsNullOrEmpty(valueName) ? "(Default)" : valueName,
                        ValueName = valueName,
                        ValueData = text,
                        FullPath = keyPath,
                        Parent = parent,
                        Checked = true
                    });
                }
                catch
                {
                    // ignore
                }
            }

            foreach (var sub in key.GetSubKeyNames())
            {
                var child = new LeftoverItem
                {
                    Kind = LeftoverKind.RegistryKey,
                    Name = sub,
                    FullPath = keyPath + "\\" + sub,
                    Parent = parent,
                    Checked = true
                };
                FillRegistryKey(child, child.FullPath, depth + 1);
                parent.Children.Add(child);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void AddRegistryValue(ScanResult result, HashSet<string> seen, string keyPath,
        string valueName, bool traced)
    {
        var id = "V|" + keyPath + "|" + valueName;
        if (!seen.Add(id))
            return;

        var parent = result.RegistryRoot.Children.FirstOrDefault(c =>
            c.FullPath.Equals(keyPath, StringComparison.OrdinalIgnoreCase));
        if (parent == null)
        {
            parent = new LeftoverItem
            {
                Kind = LeftoverKind.RegistryKey,
                Name = keyPath.Split('\\').Last(),
                FullPath = keyPath,
                Checked = false
            };
            result.RegistryRoot.Children.Add(parent);
        }

        parent.Checked = false;
        parent.Children.Add(new LeftoverItem
        {
            Kind = LeftoverKind.RegistryValue,
            Name = valueName,
            ValueName = valueName,
            FullPath = keyPath,
            Parent = parent,
            Checked = true
        });
    }

    public static (string hive, string subPath)? ParseKeyPath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return null;

        foreach (var hive in new[] { "HKEY_LOCAL_MACHINE", "HKEY_CURRENT_USER", "HKLM", "HKCU" })
        {
            if (!fullPath.StartsWith(hive + "\\", StringComparison.OrdinalIgnoreCase))
                continue;
            var normalized = hive switch
            {
                "HKLM" => "HKEY_LOCAL_MACHINE",
                "HKCU" => "HKEY_CURRENT_USER",
                _ => hive
            };
            return (normalized, fullPath[(hive.Length + 1)..]);
        }

        return null;
    }

    private static bool IsProtected(string path) => PathSafety.IsProtectedPath(path);

    private static bool IsSameOrUnder(string path, string parent)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(parent))
            return false;
        var full = path.Trim().Trim('"').TrimEnd('\\');
        var root = parent.Trim().Trim('"').TrimEnd('\\');
        if (root.Length == 0)
            return false;
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when a command line references the given path as a whole
    /// path, not merely as a prefix of a different product's path.
    /// </summary>
    private static bool ReferencesPath(string command, string path)
    {
        var needle = path.Trim().Trim('"').TrimEnd('\\');
        if (needle.Length < 3)
            return false;

        var index = 0;
        while ((index = command.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var beforeOk = index == 0 || IsCommandBoundary(command[index - 1]);
            var end = index + needle.Length;
            var afterOk = end >= command.Length || IsCommandBoundary(command[end]);
            if (beforeOk && afterOk)
                return true;
            index = end;
        }

        return false;
    }

    private static bool IsCommandBoundary(char c) =>
        c is '"' or '\'' or ' ' or '\t' or ',' or ';' or '=' or '\\' or '\0';

    private static bool IsInsideAny(string path, IEnumerable<string> parents)
    {
        foreach (var parent in parents)
        {
            if (path.Equals(parent, StringComparison.OrdinalIgnoreCase))
                continue;
            if (path.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool NamesMatch(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        var left = Normalize(a);
        var right = Normalize(b);
        if (left.Length == 0 || right.Length == 0)
            return false;
        if (left.Equals(right, StringComparison.OrdinalIgnoreCase))
            return true;
        if (left.Contains(right, StringComparison.OrdinalIgnoreCase) ||
            right.Contains(left, StringComparison.OrdinalIgnoreCase))
            return Math.Min(left.Length, right.Length) >= 6;
        var similarity = Similarity(left, right);
        return similarity >= 0.85 && Math.Max(left.Length, right.Length) >= 6;
    }

    private static string Normalize(string value)
    {
        var text = value.ToLowerInvariant();
        text = Regex.Replace(text, @"[^\p{L}\p{N}]+", "");
        return text;
    }

    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
            return 1;
        var distance = Levenshtein(a, b);
        return 1.0 - (double)distance / Math.Max(a.Length, b.Length);
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    public static List<RunningProcessInfo> FindRunningProcesses(InstalledProgram program)
    {
        var result = new List<RunningProcessInfo>();
        try
        {
            var dir = program.InstallLocation.Trim().Trim('"');
            var exeName = "";
            if (!string.IsNullOrWhiteSpace(program.DisplayIcon))
            {
                var icon = program.DisplayIcon.Trim().Trim('"');
                var comma = icon.LastIndexOf(',');
                if (comma > 2 && int.TryParse(icon[(comma + 1)..].Trim(), out _))
                    icon = icon[..comma];
                exeName = Path.GetFileNameWithoutExtension(icon);
            }

            foreach (var process in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    var path = process.MainModule?.FileName ?? "";
                    if (string.IsNullOrEmpty(path))
                        continue;
                    if (path.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var matches = (!string.IsNullOrEmpty(dir) &&
                                   path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) ||
                                  (!string.IsNullOrEmpty(exeName) &&
                                   Path.GetFileNameWithoutExtension(path)
                                       .Equals(exeName, StringComparison.OrdinalIgnoreCase));
                    if (!matches)
                        continue;

                    result.Add(new RunningProcessInfo
                    {
                        Pid = process.Id,
                        Name = process.ProcessName,
                        Path = path,
                        Title = process.MainWindowTitle
                    });
                }
                catch
                {
                    // access denied, skip
                }
            }
        }
        catch
        {
            // ignore
        }

        return result;
    }
}

public sealed class RunningProcessInfo
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
}

public static class RegistryUtilExtensions
{
    public static string GuidToPacked(this string guid)
    {
        var clean = guid.Trim('{', '}').Replace("-", "").ToUpperInvariant();
        if (clean.Length != 32)
            return clean;
        string ReversePairs(string s) => string.Concat(Enumerable.Range(0, s.Length / 2)
            .Select(i => s.Substring(i * 2, 2)).Reverse());
        return ReversePairs(clean[..8]) + ReversePairs(clean[8..12]) + ReversePairs(clean[12..16]) +
               clean[16..];
    }
}
