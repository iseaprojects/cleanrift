using System.Drawing;
using System.Globalization;
using Microsoft.Win32;

namespace CleanRift.Core;

public enum ProgramSource
{
    Registry,
    Msi,
    Store,
    Custom
}

public enum ProgramCategory
{
    Apps,
    System,
    Hidden,
    Store,
    Recent,
    Updates
}

/// <summary>
/// One row of the Apps list. The fields map to the values found under
/// ...\CurrentVersion\Uninstall, Installer\UserData and the Appx package
/// repository.
/// </summary>
public sealed class InstalledProgram
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string DisplayVersion { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public string InstallDateRaw { get; set; } = "";
    public DateTime? InstallDate { get; set; }
    public long? EstimatedSizeKb { get; set; }
    public long SizeBytes { get; set; }
    public bool SizeComputed { get; set; }
    public string UninstallString { get; set; } = "";
    public string QuietUninstallString { get; set; } = "";
    public string ModifyPath { get; set; } = "";
    public string DisplayIcon { get; set; } = "";
    public string HelpLink { get; set; } = "";
    public string UrlInfoAbout { get; set; } = "";
    public string ReleaseType { get; set; } = "";
    public string ParentKeyName { get; set; } = "";
    public string ParentDisplayName { get; set; } = "";
    public bool SystemComponent { get; set; }
    public bool NoModify { get; set; }
    public bool NoRemove { get; set; }
    public bool WindowsInstaller { get; set; }
    public bool Is64Bit { get; set; }
    public string HiveName { get; set; } = "";
    public string RegistryPath { get; set; } = "";
    public ProgramSource Source { get; set; }
    public string ProductCode { get; set; } = "";

    // Microsoft Store
    public string PackageFullName { get; set; } = "";
    public string PackageFamilyName { get; set; } = "";
    public string StoreLogo { get; set; } = "";
    public string Architecture { get; set; } = "";
    public bool IsStoreApp => Source == ProgramSource.Store;

    // Traced data
    public bool IsTraced { get; set; }
    public string TraceFile { get; set; } = "";
    public string CustomTraceName { get; set; } = "";

    // Presentation / state
    public bool IsHidden { get; set; }
    public bool IsRecent { get; set; }
    public bool IsDependent { get; set; }
    public string GroupKey { get; set; } = "";
    public bool IsGroupParent { get; set; }
    public bool IsGroupExpanded { get; set; }
    public bool IsChild { get; set; }
    public int Indent { get; set; }
    public bool RemoveEntryOnly { get; set; }

    public Icon? Icon { get; set; }

    public ProgramCategory Category
    {
        get
        {
            if (IsStoreApp)
                return ProgramCategory.Store;
            if (IsHidden)
                return ProgramCategory.Hidden;
            if (SystemComponent || LooksLikeSystemComponent())
                return ProgramCategory.System;
            return ProgramCategory.Apps;
        }
    }

    public bool IsUpdate
    {
        get
        {
            if (IsStoreApp)
                return false;
            var rt = ReleaseType.Trim();
            return rt.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                   rt.Contains("Hotfix", StringComparison.OrdinalIgnoreCase) ||
                   !string.IsNullOrWhiteSpace(ParentKeyName) ||
                   !string.IsNullOrWhiteSpace(ParentDisplayName);
        }
    }

    public bool LooksLikeSystemComponent()
    {
        if (SystemComponent)
            return true;

        var name = DisplayName;
        var publisher = Publisher;
        string[] markers =
        {
            "Update for", "Security Update for", "Hotfix for", "Microsoft Visual C++",
            "Microsoft .NET", "Windows SDK", "Windows Driver", "Microsoft Edge",
            "Microsoft Visual Studio Installer", "Windows Software Development Kit"
        };
        if (markers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (publisher.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) &&
            (name.Contains("Windows", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    public string SortName => DisplayName.Trim();

    public string SizeText
    {
        get
        {
            if (SizeBytes <= 0 && !EstimatedSizeKb.HasValue)
                return "";
            var bytes = SizeBytes > 0 ? SizeBytes : EstimatedSizeKb.GetValueOrDefault() * 1024L;
            return FormatSize(bytes);
        }
    }

    public string InstallDateText =>
        InstallDate?.ToString(ShortDateFormat, UiCulture) ?? "";

    private static string ShortDateFormat =>
        UiCulture.TwoLetterISOLanguageName == "es"
            ? "d MMM yyyy"
            : "MMM d, yyyy";

    public string InstallDateLongText =>
        InstallDate?.ToString("F", UiCulture) ?? "";

    private static CultureInfo UiCulture
    {
        get
        {
            try
            {
                return CultureInfo.GetCultureInfo(LanguageManager.Instance.LangId);
            }
            catch
            {
                return CultureInfo.CurrentCulture;
            }
        }
    }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
            return "";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var decimals = value >= 100 ? 0 : value >= 10 ? 1 : 2;
        var text = value.ToString("F" + decimals, UiCulture);
        var separator = UiCulture.NumberFormat.NumberDecimalSeparator;
        if (text.Contains(separator, StringComparison.Ordinal))
            text = text.TrimEnd('0').TrimEnd(separator[0]);
        return $"{text} {units[unit]}";
    }

    public RegistryKey? OpenRegistryKey()
    {
        if (Source == ProgramSource.Store || string.IsNullOrEmpty(RegistryPath))
            return null;

        var hive = HiveName.Equals("HKLM", StringComparison.OrdinalIgnoreCase)
            ? RegistryHive.LocalMachine
            : RegistryHive.CurrentUser;
        var view = Is64Bit ? RegistryView.Registry64 : RegistryView.Registry32;
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        var sub = RegistryPath;
        foreach (var prefix in new[] { "HKEY_LOCAL_MACHINE\\", "HKEY_CURRENT_USER\\" })
        {
            if (sub.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                sub = sub[prefix.Length..];
                break;
            }
        }

        return baseKey.OpenSubKey(sub, writable: true) ?? baseKey.OpenSubKey(sub);
    }

    public string RegistryDisplay => RegistryPath;
}

public enum StartupKind
{
    Run,
    RunOnce,
    StartupFolder,
    ScheduledTask,
    StoreTask
}

public sealed class StartupItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public StartupKind Kind { get; set; }
    public string SourceText { get; set; } = "";
    public string Hive { get; set; } = "";
    public bool Is64Bit { get; set; }
    public string ValueName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string TaskPath { get; set; } = "";
    public string TaskName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Running { get; set; }
    public bool Invalid { get; set; }
    public string Publisher { get; set; } = "";
    public string StatusText { get; set; } = "";
    public Icon? Icon { get; set; }

    public string DisplayStatus
    {
        get
        {
            if (!Enabled)
                return "Disabled";
            if (Invalid)
                return "Invalid";
            return Running ? "Running" : "Not Running";
        }
    }
}

public enum LeftoverKind
{
    File,
    Folder,
    RegistryKey,
    RegistryValue
}

public sealed class LeftoverItem
{
    public LeftoverKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string ValueName { get; set; } = "";
    public string ValueData { get; set; } = "";
    public long Size { get; set; }
    public bool Checked { get; set; } = true;
    public bool Protected { get; set; }
    public List<LeftoverItem> Children { get; } = new();
    public LeftoverItem? Parent { get; set; }
    public bool Expanded { get; set; } = true;

    public long TotalSize
    {
        get
        {
            if (Kind == LeftoverKind.File)
                return Size;
            if (Kind is LeftoverKind.Folder or LeftoverKind.RegistryKey)
                return Children.Sum(c => c.TotalSize);
            return 0;
        }
    }

    public int TotalCount
    {
        get
        {
            var count = 1;
            foreach (var child in Children)
                count += child.TotalCount;
            return count;
        }
    }

    public string DetailText => Kind switch
    {
        LeftoverKind.RegistryValue => ValueName + " = " + ValueData,
        LeftoverKind.RegistryKey => FullPath,
        _ => FullPath
    };

    public IEnumerable<LeftoverItem> Flatten()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var descendant in child.Flatten())
                yield return descendant;
    }
}

public sealed class ScanResult
{
    public LeftoverItem FileSystemRoot { get; } = new() { Kind = LeftoverKind.Folder, Name = "File System" };
    public LeftoverItem RegistryRoot { get; } = new() { Kind = LeftoverKind.Folder, Name = "Registry" };
    public int FileCount { get; set; }
    public int RegistryCount { get; set; }
    public long TotalSize { get; set; }
    public List<string> Log { get; } = new();

    public bool HasItems => FileSystemRoot.Children.Count > 0 || RegistryRoot.Children.Count > 0;
}

public sealed class RemoveResult
{
    public int Deleted { get; set; }
    public int Failed { get; set; }
    public int ScheduledForReboot { get; set; }
    public List<string> Errors { get; } = new();
}
