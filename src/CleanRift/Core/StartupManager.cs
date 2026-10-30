using Microsoft.Win32;

namespace CleanRift.Core;

public enum StartupAddType
{
    CurrentUserRun = 0,
    AllUsersRun = 1,
    CurrentUserFolder = 2,
    AllUsersFolder = 3,
    ScheduledTask = 4
}

/// <summary>
/// Enables, disables, deletes and creates startup entries. Run entries are
/// toggled through the Explorer\StartupApproved binary values instead of being
/// removed, scheduled tasks use schtasks, and Store startup tasks use the
/// AppModel SystemAppData state value.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ApprovedRun = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedRun32 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string ApprovedFolder = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    private const string StoreAppData = @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    public static bool SetEnabled(StartupItem item, bool enabled, out string error)
    {
        error = "";
        try
        {
            switch (item.Kind)
            {
                case StartupKind.Run:
                case StartupKind.RunOnce:
                    if (TryWriteApproved(item, enabled, out error))
                        return true;
                    if (string.IsNullOrEmpty(error) || !Elevation.IsElevated)
                    {
                        var result = Elevation.ExecuteStartup(new StartupJob
                        {
                            Action = enabled ? "enable" : "disable",
                            ItemKind = item.Kind.ToString(),
                            Hive = item.Hive,
                            Is64Bit = item.Is64Bit,
                            ValueName = item.ValueName
                        });
                        if (result is { Success: true })
                            return true;
                        error = result?.Error ?? error;
                        return false;
                    }

                    return false;

                case StartupKind.StartupFolder:
                    if (TryWriteFolderApproved(item, enabled, out error))
                        return true;
                    var folderResult = Elevation.ExecuteStartup(new StartupJob
                    {
                        Action = enabled ? "enable" : "disable",
                        ItemKind = item.Kind.ToString(),
                        Hive = item.Hive,
                        FilePath = item.FilePath,
                        ValueName = Path.GetFileName(item.FilePath)
                    });
                    if (folderResult is { Success: true })
                        return true;
                    error = folderResult?.Error ?? error;
                    return false;

                case StartupKind.ScheduledTask:
                {
                    var fullName = item.TaskPath.TrimEnd('\\') + "\\" + item.TaskName;
                    var (code, stdout, stderr) = ProcessUtil.Run(ProcessUtil.SchTasksPath,
                        $"/Change /TN \"{fullName}\" /{(enabled ? "ENABLE" : "DISABLE")}", 20000);
                    if (code == 0)
                        return true;
                    var elevated = Elevation.ExecuteStartup(new StartupJob
                    {
                        Action = enabled ? "enable" : "disable",
                        ItemKind = item.Kind.ToString(),
                        TaskPath = item.TaskPath,
                        TaskName = item.TaskName
                    });
                    if (elevated is { Success: true })
                        return true;
                    error = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                    if (string.IsNullOrWhiteSpace(error))
                        error = "schtasks failed with exit code " + code;
                    return false;
                }

                case StartupKind.StoreTask:
                    return SetStoreTaskState(item, enabled, out error);
            }

            error = "Unsupported startup item type";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool Delete(StartupItem item, out string error)
    {
        error = "";
        try
        {
            switch (item.Kind)
            {
                case StartupKind.Run:
                case StartupKind.RunOnce:
                {
                    var keyPath = item.Kind == StartupKind.RunOnce ? RunOnceKey : RunKey;
                    using var baseKey = RegistryUtil.OpenBase(item.Hive, item.Is64Bit, writable: true);
                    using var key = baseKey.OpenSubKey(keyPath, writable: true);
                    if (key == null)
                    {
                        error = "Startup key not found";
                        return false;
                    }

                    key.DeleteValue(item.ValueName, throwOnMissingValue: false);
                    DeleteApproved(item);
                    return true;
                }

                case StartupKind.StartupFolder:
                {
                    if (File.Exists(item.FilePath))
                        File.Delete(item.FilePath);
                    DeleteFolderApproved(item);
                    return true;
                }

                case StartupKind.ScheduledTask:
                {
                    var fullName = item.TaskPath.TrimEnd('\\') + "\\" + item.TaskName;
                    var (code, stdout, stderr) = ProcessUtil.Run(ProcessUtil.SchTasksPath,
                        $"/Delete /TN \"{fullName}\" /F", 20000);
                    if (code == 0)
                        return true;
                    var elevated = Elevation.ExecuteStartup(new StartupJob
                    {
                        Action = "delete",
                        ItemKind = item.Kind.ToString(),
                        TaskPath = item.TaskPath,
                        TaskName = item.TaskName
                    });
                    if (elevated is { Success: true })
                        return true;
                    error = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                    if (string.IsNullOrWhiteSpace(error))
                        error = "schtasks failed with exit code " + code;
                    return false;
                }

                case StartupKind.StoreTask:
                {
                    var path = StoreAppData + "\\" + item.TaskPath + "\\" + item.TaskName;
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
                    using var key = baseKey.OpenSubKey(path, writable: true);
                    if (key == null)
                    {
                        error = "Store startup task not found";
                        return false;
                    }

                    key.SetValue("State", 1, RegistryValueKind.DWord);
                    return true;
                }
            }

            error = "Unsupported startup item type";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            var result = Elevation.ExecuteStartup(new StartupJob
            {
                Action = "delete",
                ItemKind = item.Kind.ToString(),
                Hive = item.Hive,
                Is64Bit = item.Is64Bit,
                ValueName = item.ValueName,
                FilePath = item.FilePath,
                TaskPath = item.TaskPath,
                TaskName = item.TaskName
            });
            if (result is { Success: true })
                return true;
            error = result?.Error ?? "Access denied";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool ApplyStartupJob(StartupJob job, out string error, out bool deleted)
    {
        error = "";
        deleted = false;
        try
        {
            var item = new StartupItem
            {
                Kind = Enum.TryParse<StartupKind>(job.ItemKind, out var kind) ? kind : StartupKind.Run,
                Hive = job.Hive,
                Is64Bit = job.Is64Bit,
                ValueName = job.ValueName,
                FilePath = job.FilePath,
                TaskPath = job.TaskPath,
                TaskName = job.TaskName
            };

            switch ((job.Action ?? "").Trim().ToLowerInvariant())
            {
                case "enable":
                    return SetEnabledCore(item, true, out error);
                case "disable":
                    return SetEnabledCore(item, false, out error);
                case "delete":
                    return DeleteCore(item, out error, out deleted);
                case "add":
                {
                    var addType = Enum.TryParse<StartupAddType>(job.AddType, out var parsedType)
                        ? parsedType
                        : StartupAddType.CurrentUserRun;
                    return Add(addType, job.Name, job.Target, job.Arguments, job.HighestPrivileges,
                        out error, out deleted);
                }
                default:
                    error = "Unknown startup action";
                    return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool SetEnabledCore(StartupItem item, bool enabled, out string error)
    {
        error = "";
        return item.Kind switch
        {
            StartupKind.Run or StartupKind.RunOnce => TryWriteApproved(item, enabled, out error),
            StartupKind.StartupFolder => TryWriteFolderApproved(item, enabled, out error),
            StartupKind.ScheduledTask => RunSchtasks(item, enabled, out error),
            StartupKind.StoreTask => SetStoreTaskState(item, enabled, out error),
            _ => Fail("Unsupported startup item type", out error)
        };
    }

    private static bool DeleteCore(StartupItem item, out string error, out bool deleted)
    {
        deleted = false;
        error = "";
        switch (item.Kind)
        {
            case StartupKind.Run:
            case StartupKind.RunOnce:
            {
                var keyPath = item.Kind == StartupKind.RunOnce ? RunOnceKey : RunKey;
                using var baseKey = RegistryUtil.OpenBase(item.Hive, item.Is64Bit, writable: true);
                using var key = baseKey.OpenSubKey(keyPath, writable: true);
                if (key == null)
                {
                    error = "Startup key not found";
                    return false;
                }

                key.DeleteValue(item.ValueName, throwOnMissingValue: false);
                TryDeleteApproved(item);
                deleted = true;
                return true;
            }

            case StartupKind.StartupFolder:
                if (File.Exists(item.FilePath))
                    File.Delete(item.FilePath);
                TryDeleteFolderApproved(item);
                deleted = true;
                return true;

            case StartupKind.ScheduledTask:
            {
                if (!RunSchtasks(item, null, out error))
                    return false;
                deleted = true;
                return true;
            }

            case StartupKind.StoreTask:
            {
                var path = StoreAppData + "\\" + item.TaskPath + "\\" + item.TaskName;
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
                using var key = baseKey.OpenSubKey(path, writable: true);
                if (key == null)
                {
                    error = "Store startup task not found";
                    return false;
                }

                key.SetValue("State", 1, RegistryValueKind.DWord);
                deleted = true;
                return true;
            }
        }

        error = "Unsupported startup item type";
        return false;
    }

    private static bool RunSchtasks(StartupItem item, bool? enabled, out string error)
    {
        error = "";
        var fullName = item.TaskPath.TrimEnd('\\') + "\\" + item.TaskName;
        var arguments = enabled.HasValue
            ? $"/Change /TN \"{fullName}\" /{(enabled.Value ? "ENABLE" : "DISABLE")}"
            : $"/Delete /TN \"{fullName}\" /F";
        var (code, stdout, stderr) = ProcessUtil.Run(ProcessUtil.SchTasksPath, arguments, 20000);
        if (code == 0)
            return true;
        error = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
        if (string.IsNullOrWhiteSpace(error))
            error = "schtasks failed with exit code " + code;
        return false;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static bool SetStoreTaskState(StartupItem item, bool enabled, out string error)
    {
        error = "";
        try
        {
            var path = StoreAppData + "\\" + item.TaskPath + "\\" + item.TaskName;
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var key = baseKey.CreateSubKey(path, writable: true);
            key.SetValue("State", enabled ? 4 : 3, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryWriteApproved(StartupItem item, bool enabled, out string error)
    {
        error = "";
        try
        {
            var approvedPath = item.Is64Bit ? ApprovedRun : ApprovedRun32;
            using var baseKey = RegistryUtil.OpenBase(item.Hive, item.Is64Bit, writable: true);
            using var key = baseKey.CreateSubKey(approvedPath, writable: true);
            if (key == null)
            {
                error = "Cannot open StartupApproved key";
                return false;
            }

            var data = new byte[12];
            data[0] = enabled ? (byte)0x02 : (byte)0x03;
            var stamp = BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc());
            Array.Copy(stamp, 0, data, 4, 8);
            key.SetValue(item.ValueName, data, RegistryValueKind.Binary);
            error = "";
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "Access denied";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryWriteFolderApproved(StartupItem item, bool enabled, out string error)
    {
        error = "";
        try
        {
            using var baseKey = RegistryUtil.OpenBase(item.Hive, true, writable: true);
            using var key = baseKey.CreateSubKey(ApprovedFolder, writable: true);
            if (key == null)
            {
                error = "Cannot open StartupApproved key";
                return false;
            }

            var data = new byte[12];
            data[0] = enabled ? (byte)0x02 : (byte)0x03;
            var stamp = BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc());
            Array.Copy(stamp, 0, data, 4, 8);
            key.SetValue(Path.GetFileName(item.FilePath), data, RegistryValueKind.Binary);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "Access denied";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void DeleteApproved(StartupItem item)
    {
        try
        {
            var approvedPath = item.Is64Bit ? ApprovedRun : ApprovedRun32;
            using var baseKey = RegistryUtil.OpenBase(item.Hive, item.Is64Bit, writable: true);
            using var key = baseKey.OpenSubKey(approvedPath, writable: true);
            key?.DeleteValue(item.ValueName, false);
        }
        catch
        {
            // ignore
        }
    }

    private static void TryDeleteApproved(StartupItem item) => DeleteApproved(item);

    private static void DeleteFolderApproved(StartupItem item) => TryDeleteFolderApproved(item);

    private static void TryDeleteFolderApproved(StartupItem item)
    {
        try
        {
            using var baseKey = RegistryUtil.OpenBase(item.Hive, true, writable: true);
            using var key = baseKey.OpenSubKey(ApprovedFolder, writable: true);
            key?.DeleteValue(Path.GetFileName(item.FilePath), false);
        }
        catch
        {
            // ignore
        }
    }

    public static bool Rename(StartupItem item, string newName, out string error)
    {
        error = "";
        try
        {
            switch (item.Kind)
            {
                case StartupKind.Run:
                case StartupKind.RunOnce:
                {
                    var keyPath = item.Kind == StartupKind.RunOnce ? RunOnceKey : RunKey;
                    using var baseKey = RegistryUtil.OpenBase(item.Hive, item.Is64Bit, writable: true);
                    using var key = baseKey.OpenSubKey(keyPath, writable: true);
                    if (key == null)
                    {
                        error = "Startup key not found";
                        return false;
                    }

                    var value = key.GetString(item.ValueName);
                    key.DeleteValue(item.ValueName, false);
                    key.SetValue(newName, value, RegistryValueKind.String);
                    return true;
                }

                case StartupKind.StartupFolder:
                {
                    var dir = Path.GetDirectoryName(item.FilePath);
                    var ext = Path.GetExtension(item.FilePath);
                    if (dir == null)
                    {
                        error = "Invalid shortcut path";
                        return false;
                    }

                    var target = Path.Combine(dir, newName + ext);
                    File.Move(item.FilePath, target);
                    return true;
                }
            }

            error = "Renaming is not supported for this startup type";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool Add(StartupAddType type, string name, string target, string arguments,
        bool highestPrivileges, out string error, out bool needsElevation)
    {
        error = "";
        needsElevation = false;
        try
        {
            name = name.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "Please enter a name";
                return false;
            }

            if (name.Length > 200 || name.Contains('\r') || name.Contains('\n'))
            {
                error = "The startup name is too long or contains invalid characters";
                return false;
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                error = "Please choose an executable file";
                return false;
            }

            arguments = (arguments ?? "").Trim();
            if (arguments.Length > 4096 || arguments.Contains('\r') || arguments.Contains('\n'))
            {
                error = "The startup arguments are too long or contain invalid characters";
                return false;
            }

            var expanded = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(expanded) ||
                expanded.StartsWith(@"\\", StringComparison.Ordinal))
            {
                error = "The startup target must be a local absolute path";
                return false;
            }

            if (!File.Exists(expanded) && type != StartupAddType.ScheduledTask)
            {
                error = "The file does not exist:\n" + expanded;
                return false;
            }

            var command = "\"" + expanded + "\"";
            if (!string.IsNullOrWhiteSpace(arguments))
                command += " " + arguments.Trim();

            switch (type)
            {
                case StartupAddType.CurrentUserRun:
                    return AddRun("HKCU", true, name, command, out error);

                case StartupAddType.AllUsersRun:
                {
                    if (!Elevation.IsElevated)
                    {
                        needsElevation = true;
                        error = "Administrator rights are required";
                        return false;
                    }

                    return AddRun("HKLM", true, name, command, out error);
                }

                case StartupAddType.CurrentUserFolder:
                {
                    var dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                    return CreateStartupShortcut(dir, name, expanded, arguments, out error);
                }

                case StartupAddType.AllUsersFolder:
                {
                    var dir = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
                    try
                    {
                        return CreateStartupShortcut(dir, name, expanded, arguments, out error);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        needsElevation = true;
                        error = "Administrator rights are required";
                        return false;
                    }
                }

                case StartupAddType.ScheduledTask:
                {
                    if (arguments.Contains('"'))
                    {
                        error = "Scheduled task arguments cannot contain quote characters";
                        return false;
                    }

                    var taskName = "CleanRift_" + SanitizeName(name);
                    var (code, stdout, stderr) = ProcessUtil.Run(ProcessUtil.SchTasksPath,
                        $"/Create /TN \"{taskName}\" /TR \"{command}\" /SC ONLOGON " +
                        (highestPrivileges ? "/RL HIGHEST " : "") + "/F", 30000);
                    if (code == 0)
                        return true;
                    needsElevation = true;
                    error = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                    return false;
                }
            }

            error = "Unsupported startup type";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool AddRun(string hive, bool is64Bit, string name, string command, out string error)
    {
        error = "";
        try
        {
            using var baseKey = RegistryUtil.OpenBase(hive, is64Bit, writable: true);
            using var key = baseKey.CreateSubKey(RunKey, writable: true);
            if (key == null)
            {
                error = "Cannot open Run key";
                return false;
            }

            key.SetValue(name, command, RegistryValueKind.String);

            // A newly created value has no StartupApproved record; create an enabled one.
            using var approved = baseKey.CreateSubKey(is64Bit ? ApprovedRun : ApprovedRun32, writable: true);
            var data = new byte[12];
            data[0] = 0x02;
            var stamp = BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc());
            Array.Copy(stamp, 0, data, 4, 8);
            approved?.SetValue(name, data, RegistryValueKind.Binary);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "Access denied";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool CreateStartupShortcut(string directory, string name, string target, string arguments,
        out string error)
    {
        error = "";
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, SanitizeName(name) + ".lnk");
            if (File.Exists(path))
                File.Delete(path);
            if (!ShortcutUtil.CreateShortcut(path, target, arguments,
                    Path.GetDirectoryName(target) ?? "", name))
            {
                error = "Could not create shortcut";
                return false;
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "Access denied";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray())
            .Trim()
            .TrimEnd('.', ' ');

        if (cleaned is "." or "..")
            cleaned = "Startup item";

        var reserved = new[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        if (reserved.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
            cleaned = "_" + cleaned;

        if (cleaned.Length > 120)
            cleaned = cleaned[..120].TrimEnd('.', ' ');

        return string.IsNullOrWhiteSpace(cleaned) ? "Startup item" : cleaned;
    }
}
