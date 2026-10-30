namespace CleanRift.Core;

/// <summary>
/// Central safety checks for destructive operations and for the elevated job
/// channel. Removal code must never trust a path, a registry key or a job file
/// just because it came from JSON or from a previous scan.
/// </summary>
public static class PathSafety
{
    private static readonly string[] HardProtectedRegistryRoots =
    {
        "SYSTEM", "SAM", "SECURITY", "HARDWARE", "BCD00000000"
    };

    private static readonly string[] ProtectedRegistryRoots =
    {
        "SOFTWARE",
        "SOFTWARE\\MICROSOFT",
        "SOFTWARE\\CLASSES",
        "SOFTWARE\\POLICIES",
        "SOFTWARE\\WOW6432NODE",
        "SOFTWARE\\MICROSOFT\\WINDOWS",
        "SOFTWARE\\MICROSOFT\\WINDOWS\\CURRENTVERSION",
        "SOFTWARE\\MICROSOFT\\WINDOWS\\CURRENTVERSION\\RUN",
        "SOFTWARE\\MICROSOFT\\WINDOWS\\CURRENTVERSION\\RUNONCE",
        "SOFTWARE\\MICROSOFT\\WINDOWS\\CURRENTVERSION\\UNINSTALL",
        "SOFTWARE\\MICROSOFT\\WINDOWS\\CURRENTVERSION\\EXPLORER",
        "SOFTWARE\\MICROSOFT\\WINDOWS NT",
        "ENVIRONMENT",
        "CONTROL PANEL"
    };

    public static bool IsProtectedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        try
        {
            var full = Normalize(path);
            if (full.Length <= 3)
                return true;

            var windows = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var system = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.System));
            var systemX86 = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86));
            var appDir = Normalize(AppContext.BaseDirectory);

            if (IsUnderOrSame(full, windows) ||
                IsUnderOrSame(full, system) ||
                IsUnderOrSame(full, systemX86) ||
                IsUnderOrSame(full, appDir))
                return true;

            // These roots are protected themselves; their normal application
            // subfolders remain valid removal targets.
            foreach (var root in LeftoverScanner.ProtectedPaths)
            {
                var normalized = Normalize(root);
                if (normalized.Length == 0)
                    continue;
                if (string.Equals(full, normalized, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            foreach (var root in ExactProtectedFolders())
            {
                var normalized = Normalize(root);
                if (normalized.Length > 0 &&
                    string.Equals(full, normalized, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            foreach (var root in CriticalFolders())
            {
                var normalized = Normalize(root);
                if (normalized.Length > 0 && IsUnderOrSame(full, normalized))
                    return true;
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    public static bool IsProtectedRegistryKey(string fullPath, out string reason) =>
        IsProtectedRegistryKey(fullPath, forValue: false, out reason);

    public static bool IsProtectedRegistryKey(string fullPath, bool forValue, out string reason)
    {
        reason = "";
        var parsed = LeftoverScanner.ParseKeyPath(fullPath);
        if (parsed == null)
        {
            reason = "Invalid registry path";
            return true;
        }

        var hive = parsed.Value.hive.ToUpperInvariant();
        var sub = parsed.Value.subPath.Trim('\\').ToUpperInvariant();
        if (sub.Length == 0)
        {
            reason = "Refusing to remove a registry hive root";
            return true;
        }

        // The 32-bit view exposes the same trees below WOW6432Node. Compare the
        // logical path so both views receive the same protection.
        var logical = sub.StartsWith(@"WOW6432NODE\", StringComparison.Ordinal)
            ? sub[@"WOW6432NODE\".Length..]
            : sub;

        foreach (var root in HardProtectedRegistryRoots)
        {
            if (logical.Equals(root, StringComparison.Ordinal) ||
                logical.StartsWith(root + "\\", StringComparison.Ordinal))
            {
                reason = "Refusing to remove a protected Windows registry tree";
                return true;
            }
        }

        if (logical.Equals("SOFTWARE", StringComparison.Ordinal))
        {
            reason = "Refusing to remove the SOFTWARE registry root";
            return true;
        }

        if (forValue)
            return false;

        foreach (var root in ProtectedRegistryRoots)
        {
            if (logical.Equals(root, StringComparison.Ordinal))
            {
                reason = "Refusing to remove a critical registry key";
                return true;
            }
        }

        // The uninstall keys themselves are containers. Their per-product
        // subkeys are legitimate removal targets.
        if (logical.EndsWith("\\UNINSTALL", StringComparison.Ordinal) ||
            logical.EndsWith("\\RUN", StringComparison.Ordinal) ||
            logical.EndsWith("\\RUNONCE", StringComparison.Ordinal) ||
            logical.EndsWith("\\APP PATHS", StringComparison.Ordinal) ||
            logical.EndsWith("\\STARTUPAPPROVED", StringComparison.Ordinal))
        {
            reason = "Refusing to remove a registry container";
            return true;
        }

        // Never delete an entire vendor umbrella (for example
        // SOFTWARE\Google) that happens to share a name with a product. Its
        // per-product subkeys remain valid targets.
        if (IsVendorUmbrella(logical))
        {
            reason = "Refusing to remove a shared vendor registry root";
            return true;
        }

        return false;
    }

    public static bool IsSafeRemovalItem(RemovalJobItem item, out string error)
    {
        error = "";
        if (item == null)
        {
            error = "Null removal item";
            return false;
        }

        var kind = (item.Kind ?? "").Trim().ToLowerInvariant();
        if (kind is not ("file" or "folder" or "key" or "value"))
        {
            error = "Unknown removal item kind";
            return false;
        }

        item.Kind = kind;

        if (kind is "file" or "folder")
        {
            if (string.IsNullOrWhiteSpace(item.Path) || !Path.IsPathFullyQualified(item.Path))
            {
                error = "Removal path must be absolute";
                return false;
            }

            if (item.Path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                error = "Network and device paths are not allowed for removal";
                return false;
            }

            var trimmed = item.Path.Trim();
            if (trimmed.IndexOfAny(new[] { '*', '?' }) >= 0 ||
                trimmed.IndexOf(':', 2) >= 0 ||
                trimmed.Contains('"'))
            {
                error = "Removal path contains invalid characters";
                return false;
            }

            if (IsProtectedPath(item.Path))
            {
                error = "Refusing to remove a protected path";
                return false;
            }

            return true;
        }

        if (string.IsNullOrWhiteSpace(item.Path))
        {
            error = "Registry path is empty";
            return false;
        }

        if (IsProtectedRegistryKey(item.Path, forValue: kind == "value", out var reason))
        {
            error = reason;
            return false;
        }

        if (kind == "value" && (item.ValueName ?? "").Length > 16_383)
        {
            error = "Registry value name is too long";
            return false;
        }

        return true;
    }

    public static bool IsSafeJobFile(string path, string jobDirectory, out string fullPath, out string error)
    {
        fullPath = "";
        error = "";
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                error = "Job path must be absolute";
                return false;
            }

            fullPath = Path.GetFullPath(path);
            var directory = Path.GetFullPath(jobDirectory).TrimEnd('\\') + "\\";
            if (!fullPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            {
                error = "Job file is outside the CleanRift job directory";
                return false;
            }

            if (!File.Exists(fullPath))
            {
                error = "Job file not found";
                return false;
            }

            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                error = "Job file cannot be a reparse point";
                return false;
            }

            if (HasReparsePointInPath(fullPath, Path.GetFullPath(jobDirectory)))
            {
                error = "Job file is inside a reparse point";
                return false;
            }

            if (new FileInfo(fullPath).Length > 4 * 1024 * 1024)
            {
                error = "Job file is too large";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        return true;
    }

    public static bool IsSafeStartupFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var full = Normalize(path);
            var current = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Startup));
            var common = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));
            if (!IsUnderOrSame(full, current) && !IsUnderOrSame(full, common))
                return false;
            if (File.Exists(full) &&
                (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                return false;
        }
        catch
        {
            return false;
        }

        return true;
    }

    public static bool IsSafeStoreTaskPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            return false;
        return !value.Contains("..", StringComparison.Ordinal) &&
               !value.Contains('\\') &&
               !value.Contains('/') &&
               !value.Contains('"') &&
               !value.Contains('\r') &&
               !value.Contains('\n');
    }

    /// <summary>
    /// Scheduled task folders are rooted paths such as "\" or
    /// "\Microsoft\Windows\Maintenance\".
    /// </summary>
    public static bool IsSafeTaskPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
            return false;
        if (value[0] != '\\' ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.Contains('"') ||
            value.Contains(':') ||
            value.Contains('\r') ||
            value.Contains('\n'))
            return false;

        foreach (var segment in value.Split('\\'))
        {
            if (segment.Length > 128)
                return false;
        }

        return true;
    }

    public static bool ValidateJob(RemovalJob? job, out string error)
    {
        error = "";
        if (job == null)
        {
            error = "Invalid job";
            return false;
        }

        if (string.Equals(job.Kind, "removal", StringComparison.OrdinalIgnoreCase))
        {
            if (job.Items == null)
            {
                error = "Job has no items";
                return false;
            }

            foreach (var item in job.Items)
            {
                if (!IsSafeRemovalItem(item, out error))
                    return false;
            }

            return true;
        }

        if (string.Equals(job.Kind, "startup", StringComparison.OrdinalIgnoreCase))
            return ValidateStartupJob(job.Startup, out error);

        error = "Unknown job kind";
        return false;
    }

    public static bool ValidateStartupJob(StartupJob? job, out string error)
    {
        error = "";
        if (job == null)
        {
            error = "Invalid startup job";
            return false;
        }

        var action = (job.Action ?? "").Trim().ToLowerInvariant();
        if (action is not ("add" or "enable" or "disable" or "delete"))
        {
            error = "Unknown startup action";
            return false;
        }

        var hive = job.Hive ?? "";
        var itemKind = (job.ItemKind ?? "").Trim();
        if (action is "enable" or "disable" or "delete")
        {
            var hiveBasedItem = itemKind.Length == 0 ||
                                itemKind.Equals("Run", StringComparison.OrdinalIgnoreCase) ||
                                itemKind.Equals("RunOnce", StringComparison.OrdinalIgnoreCase) ||
                                itemKind.Equals("StartupFolder", StringComparison.OrdinalIgnoreCase);
            if (hiveBasedItem &&
                !string.Equals(hive, "HKLM", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(hive, "HKCU", StringComparison.OrdinalIgnoreCase))
            {
                error = "Invalid startup hive";
                return false;
            }

            if (itemKind.Equals("Run", StringComparison.OrdinalIgnoreCase) ||
                itemKind.Equals("RunOnce", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(job.ValueName))
                {
                    error = "Startup value name is required";
                    return false;
                }
            }
            else if (itemKind.Equals("StartupFolder", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(job.FilePath))
                {
                    error = "Startup shortcut path is required";
                    return false;
                }
            }
            else if (itemKind.Equals("ScheduledTask", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(job.TaskName))
                {
                    error = "Scheduled task name is required";
                    return false;
                }
            }
            else if (itemKind.Equals("StoreTask", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(job.TaskPath) ||
                    string.IsNullOrWhiteSpace(job.TaskName))
                {
                    error = "Store startup task metadata is required";
                    return false;
                }
            }
        }

        var name = job.Name ?? "";
        var valueName = job.ValueName ?? "";
        var taskName = job.TaskName ?? "";
        var taskPath = job.TaskPath ?? "";
        var arguments = job.Arguments ?? "";
        var filePath = job.FilePath ?? "";
        var target = job.Target ?? "";

        if (name.Length > 200 || valueName.Length > 16_383 ||
            taskName.Length > 200 || taskPath.Length > 512)
        {
            error = "Startup metadata is too long";
            return false;
        }

        if (arguments.Length > 4096 || arguments.Contains('\r') || arguments.Contains('\n'))
        {
            error = "Invalid startup arguments";
            return false;
        }

        if (!string.IsNullOrEmpty(filePath) && !IsSafeStartupFile(filePath))
        {
            error = "Startup shortcut is outside the startup folders";
            return false;
        }

        var isScheduledTask = itemKind.Equals("ScheduledTask", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(taskPath) &&
            !(isScheduledTask ? IsSafeTaskPath(taskPath) : IsSafeStoreTaskPart(taskPath)))
        {
            error = "Invalid scheduled task path";
            return false;
        }

        if (!string.IsNullOrEmpty(taskName) && !IsSafeStoreTaskPart(taskName))
        {
            error = "Invalid scheduled task name";
            return false;
        }

        if (action == "add")
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(target))
            {
                error = "Startup name and target are required";
                return false;
            }

            if (!Path.IsPathFullyQualified(target))
            {
                error = "Startup target must be an absolute path";
                return false;
            }

            if (target.StartsWith(@"\\", StringComparison.Ordinal))
            {
                error = "Network startup targets are not allowed";
                return false;
            }
        }

        return true;
    }

    public static bool IsUnder(string path, string parent)
    {
        try
        {
            return IsUnderOrSame(Normalize(path), Normalize(parent));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Shared vendor roots that hold several unrelated products. They are
    /// never offered as a whole removal target; individual product subkeys are.
    /// </summary>
    private static bool IsVendorUmbrella(string logicalSubPath)
    {
        var parts = logicalSubPath.Split('\\');
        if (parts.Length != 2 ||
            !parts[0].Equals("SOFTWARE", StringComparison.Ordinal))
            return false;

        return VendorUmbrellaNames.Contains(parts[1]);
    }

    private static readonly HashSet<string> VendorUmbrellaNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "MICROSOFT", "GOOGLE", "MOZILLA", "APPLE", "ADOBE", "INTEL", "NVIDIA", "AMD",
        "REALTEK", "ORACLE", "IBM", "DELL", "HP", "LENOVO", "SAMSUNG", "AUTODESK",
        "JETBRAINS", "VMWARE", "CITRIX", "SAP", "SIEMENS", "CANON", "EPSON", "BROTHER",
        "KASPERSKY", "ESET", "AVAST", "AVG", "MALWAREBYTES", "NORTON", "MCAFEE",
        "BITDEFENDER", "TREND MICRO"
    };

    private static bool HasReparsePointInPath(string file, string stopAt)
    {
        try
        {
            var current = Path.GetDirectoryName(file);
            var stop = stopAt.TrimEnd('\\');
            while (!string.IsNullOrEmpty(current) &&
                   current.Length >= stop.Length)
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return true;
                if (current.Equals(stop, StringComparison.OrdinalIgnoreCase))
                    break;
                current = Path.GetDirectoryName(current);
            }
        }
        catch
        {
            // If the path cannot be inspected, treat it as unsafe.
            return true;
        }

        return false;
    }

    private static IEnumerable<string> ExactProtectedFolders()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(userProfile, "AppData");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        yield return Path.Combine(userProfile, "Downloads");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Favorites);
        yield return Path.Combine(userProfile, "Contacts");
        yield return Path.Combine(userProfile, "Links");
        yield return Path.Combine(userProfile, "Searches");
        yield return Path.Combine(userProfile, "Saved Games");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Templates);
    }

    private static IEnumerable<string> CriticalFolders()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

        yield return Path.Combine(programFiles, "Common Files");
        yield return Path.Combine(programFilesX86, "Common Files");
        yield return Path.Combine(programFiles, "Microsoft");
        yield return Path.Combine(programFilesX86, "Microsoft");
        yield return Path.Combine(programFiles, "WindowsApps");
        yield return Path.Combine(programFiles, "Windows Defender");
        yield return Path.Combine(programFiles, "Windows NT");
        yield return Path.Combine(commonAppData, "Microsoft");
        yield return Path.Combine(commonAppData, "Package Cache");
        yield return Path.Combine(commonAppData, "Windows Defender");
        yield return Path.Combine(userProfile, "Public");
        yield return Path.Combine(systemDrive, "Recovery");
        yield return Path.Combine(systemDrive, "Boot");
        yield return Path.Combine(systemDrive, "System Volume Information");
        yield return Path.Combine(systemDrive, "$Recycle.Bin");
        yield return Path.Combine(systemDrive, "PerfLogs");
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\');
            if (full.Length == 0)
                return "";

            // Expand 8.3 aliases (PROGRA~1) so protection checks cannot be
            // bypassed with a short path.
            if (Directory.Exists(full) || File.Exists(full))
            {
                var buffer = new System.Text.StringBuilder(1024);
                var length = NativeMethods.GetLongPathName(full, buffer, (uint)buffer.Capacity);
                if (length > 0 && length < buffer.Capacity)
                    full = buffer.ToString(0, (int)length).TrimEnd('\\');
            }

            return full;
        }
        catch
        {
            return "";
        }
    }

    private static bool IsUnderOrSame(string path, string parent)
    {
        if (path.Length == 0 || parent.Length == 0)
            return false;
        if (path.Equals(parent, StringComparison.OrdinalIgnoreCase))
            return true;
        return path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
