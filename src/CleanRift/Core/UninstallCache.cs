using System.Text;
using System.Text.Json;

namespace CleanRift.Core;

/// <summary>
/// Size/list cache stored in %LOCALAPPDATA%\CleanRift\cache.dat. The file is a
/// CleanRift-specific magic header followed by JSON, so damaged or foreign data
/// is ignored instead of breaking startup.
/// </summary>
public static class UninstallCache
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CLEANRIFT1\n");
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public static string FilePath => Path.Combine(TraceStore.DataDirectory, "cache.dat");

    public static IReadOnlyList<InstalledProgram>? TryLoad()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;
            if (new FileInfo(FilePath).Length > 32 * 1024 * 1024)
                return null;

            var bytes = File.ReadAllBytes(FilePath);
            if (bytes.Length <= Magic.Length)
                return null;
            for (var i = 0; i < Magic.Length; i++)
            {
                if (bytes[i] != Magic[i])
                    return null;
            }

            var json = Encoding.UTF8.GetString(bytes, Magic.Length,
                bytes.Length - Magic.Length);
            var cache = JsonSerializer.Deserialize<CacheDocument>(json, JsonOptions);
            if (cache == null || cache.Entries.Count == 0)
                return null;
            if (cache.Machine != Environment.MachineName)
                return null;
            var age = DateTime.UtcNow - cache.SavedUtc;
            if (age < TimeSpan.Zero || age > MaxAge)
                return null;

            return cache.Entries.Select(ToProgram).ToList();
        }
        catch
        {
            // A damaged cache must never prevent startup.
            return null;
        }
    }

    public static void Save(IReadOnlyList<InstalledProgram> programs)
    {
        try
        {
            var document = new CacheDocument
            {
                SavedUtc = DateTime.UtcNow,
                Machine = Environment.MachineName,
                Entries = programs.Where(p => !p.IsChild).Select(ToEntry).ToList()
            };
            if (document.Entries.Count == 0)
                return;

            var json = JsonSerializer.Serialize(document, JsonOptions);
            var payload = new byte[Magic.Length + Encoding.UTF8.GetByteCount(json)];
            Buffer.BlockCopy(Magic, 0, payload, 0, Magic.Length);
            Encoding.UTF8.GetBytes(json, 0, json.Length, payload, Magic.Length);

            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            AtomicFile.WriteAllBytes(FilePath, payload);
        }
        catch
        {
            // Cache writes are best-effort.
        }
    }

    private static CacheEntry ToEntry(InstalledProgram program) => new()
    {
        Id = program.Id,
        DisplayName = program.DisplayName,
        DisplayVersion = program.DisplayVersion,
        Publisher = program.Publisher,
        InstallLocation = program.InstallLocation,
        InstallDateRaw = program.InstallDateRaw,
        InstallDate = program.InstallDate,
        EstimatedSizeKb = program.EstimatedSizeKb,
        SizeBytes = program.SizeBytes,
        SizeComputed = program.SizeComputed,
        UninstallString = program.UninstallString,
        QuietUninstallString = program.QuietUninstallString,
        ModifyPath = program.ModifyPath,
        DisplayIcon = program.DisplayIcon,
        HelpLink = program.HelpLink,
        UrlInfoAbout = program.UrlInfoAbout,
        ReleaseType = program.ReleaseType,
        ParentKeyName = program.ParentKeyName,
        ParentDisplayName = program.ParentDisplayName,
        SystemComponent = program.SystemComponent,
        NoModify = program.NoModify,
        NoRemove = program.NoRemove,
        WindowsInstaller = program.WindowsInstaller,
        Is64Bit = program.Is64Bit,
        HiveName = program.HiveName,
        RegistryPath = program.RegistryPath,
        Source = (int)program.Source,
        ProductCode = program.ProductCode,
        PackageFullName = program.PackageFullName,
        PackageFamilyName = program.PackageFamilyName,
        StoreLogo = program.StoreLogo,
        Architecture = program.Architecture,
        IsTraced = program.IsTraced,
        TraceFile = program.TraceFile,
        CustomTraceName = program.CustomTraceName,
        IsHidden = program.IsHidden,
        IsRecent = program.IsRecent,
        IsDependent = program.IsDependent,
        GroupKey = program.GroupKey,
        IsGroupParent = program.IsGroupParent,
        IsGroupExpanded = program.IsGroupExpanded,
        IsChild = program.IsChild,
        Indent = program.Indent,
        RemoveEntryOnly = program.RemoveEntryOnly
    };

    private static InstalledProgram ToProgram(CacheEntry entry) => new()
    {
        Id = entry.Id,
        DisplayName = entry.DisplayName,
        DisplayVersion = entry.DisplayVersion,
        Publisher = entry.Publisher,
        InstallLocation = entry.InstallLocation,
        InstallDateRaw = entry.InstallDateRaw,
        InstallDate = entry.InstallDate,
        EstimatedSizeKb = entry.EstimatedSizeKb,
        SizeBytes = entry.SizeBytes,
        SizeComputed = entry.SizeComputed,
        UninstallString = entry.UninstallString,
        QuietUninstallString = entry.QuietUninstallString,
        ModifyPath = entry.ModifyPath,
        DisplayIcon = entry.DisplayIcon,
        HelpLink = entry.HelpLink,
        UrlInfoAbout = entry.UrlInfoAbout,
        ReleaseType = entry.ReleaseType,
        ParentKeyName = entry.ParentKeyName,
        ParentDisplayName = entry.ParentDisplayName,
        SystemComponent = entry.SystemComponent,
        NoModify = entry.NoModify,
        NoRemove = entry.NoRemove,
        WindowsInstaller = entry.WindowsInstaller,
        Is64Bit = entry.Is64Bit,
        HiveName = entry.HiveName,
        RegistryPath = entry.RegistryPath,
        Source = (ProgramSource)entry.Source,
        ProductCode = entry.ProductCode,
        PackageFullName = entry.PackageFullName,
        PackageFamilyName = entry.PackageFamilyName,
        StoreLogo = entry.StoreLogo,
        Architecture = entry.Architecture,
        IsTraced = entry.IsTraced,
        TraceFile = entry.TraceFile,
        CustomTraceName = entry.CustomTraceName,
        IsHidden = entry.IsHidden,
        IsRecent = entry.IsRecent,
        IsDependent = entry.IsDependent,
        GroupKey = entry.GroupKey,
        IsGroupParent = entry.IsGroupParent,
        IsGroupExpanded = entry.IsGroupExpanded,
        IsChild = entry.IsChild,
        Indent = entry.Indent,
        RemoveEntryOnly = entry.RemoveEntryOnly
    };

    private sealed class CacheDocument
    {
        public DateTime SavedUtc { get; set; }
        public string Machine { get; set; } = "";
        public List<CacheEntry> Entries { get; set; } = new();
    }

    private sealed class CacheEntry
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
        public int Source { get; set; }
        public string ProductCode { get; set; } = "";
        public string PackageFullName { get; set; } = "";
        public string PackageFamilyName { get; set; } = "";
        public string StoreLogo { get; set; } = "";
        public string Architecture { get; set; } = "";
        public bool IsTraced { get; set; }
        public string TraceFile { get; set; } = "";
        public string CustomTraceName { get; set; } = "";
        public bool IsHidden { get; set; }
        public bool IsRecent { get; set; }
        public bool IsDependent { get; set; }
        public string GroupKey { get; set; } = "";
        public bool IsGroupParent { get; set; }
        public bool IsGroupExpanded { get; set; }
        public bool IsChild { get; set; }
        public int Indent { get; set; }
        public bool RemoveEntryOnly { get; set; }
    }
}
