using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CleanRift.Core;

public static class RegistryUtil
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryInfoKey(IntPtr hKey, IntPtr lpClass, IntPtr lpcchClass, IntPtr lpReserved,
        IntPtr lpcSubKeys, IntPtr lpcbMaxSubKeyLen, IntPtr lpcbMaxClassLen, IntPtr lpcValues,
        IntPtr lpcbMaxValueNameLen, IntPtr lpcbMaxValueLen, IntPtr lpcbSecurityDescriptor, out long lpftLastWriteTime);

    public static DateTime? GetLastWriteTime(RegistryKey key)
    {
        try
        {
            var result = RegQueryInfoKey(key.Handle.DangerousGetHandle(), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var fileTime);
            if (result != 0 || fileTime == 0)
                return null;
            return DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
        }
        catch
        {
            return null;
        }
    }

    public static RegistryKey OpenBase(string hive, bool is64Bit, bool writable = false)
    {
        var registryHive = hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase)
            ? RegistryHive.LocalMachine
            : RegistryHive.CurrentUser;
        return RegistryKey.OpenBaseKey(registryHive, is64Bit ? RegistryView.Registry64 : RegistryView.Registry32);
    }

    public static RegistryKey? OpenKey(string fullPath, bool writable = false)
    {
        try
        {
            var parts = fullPath.Split('\\');
            var hive = parts[0];
            var sub = string.Join('\\', parts.Skip(1));
            RegistryView view = RegistryView.Default;
            // Prefer the 64-bit view; callers that need 32-bit pass the explicit path with a view marker.
            using var baseKey = hive.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase)
                ? RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view)
                : RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            return writable ? baseKey.CreateSubKey(sub, writable: true) : baseKey.OpenSubKey(sub);
        }
        catch
        {
            return null;
        }
    }

    public static RegistryKey? OpenKeyExact(string hive, bool is64Bit, string subPath, bool writable = false)
    {
        try
        {
            using var baseKey = OpenBase(hive, is64Bit, writable);
            return writable ? baseKey.CreateSubKey(subPath, writable: true) : baseKey.OpenSubKey(subPath);
        }
        catch
        {
            return null;
        }
    }

    public static string GetString(this RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name) switch
            {
                null => "",
                string s => s,
                string[] array => string.Join(" ", array),
                _ => key.GetValue(name)?.ToString() ?? ""
            };
        }
        catch
        {
            return "";
        }
    }

    public static long GetLong(this RegistryKey key, string name)
    {
        try
        {
            var value = key.GetValue(name);
            return value switch
            {
                int i => i,
                long l => l,
                uint ui => ui,
                ulong ul => unchecked((long)ul),
                string s when long.TryParse(s, out var parsed) => parsed,
                _ => 0
            };
        }
        catch
        {
            return 0;
        }
    }

    public static bool GetBool(this RegistryKey key, string name)
    {
        try
        {
            var value = key.GetValue(name);
            return value switch
            {
                int i => i != 0,
                string s => s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    public static DateTime? ParseInstallDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        raw = raw.Trim();
        string[] formats = { "yyyyMMdd", "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "MM/dd/yyyy", "dd.MM.yyyy" };
        if (DateTime.TryParseExact(raw, formats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var exact))
            return exact;
        if (DateTime.TryParse(raw, out var parsed))
            return parsed;
        return null;
    }

    public static string PackedGuidToGuid(string packed)
    {
        if (packed.Length != 32)
            return packed;
        try
        {
            var p = packed.ToUpperInvariant();
            string ReversePairs(string s) => string.Concat(Enumerable.Range(0, s.Length / 2)
                .Select(i => s.Substring(i * 2, 2)).Reverse());
            var part1 = ReversePairs(p[..8]);
            var part2 = ReversePairs(p[8..12]);
            var part3 = ReversePairs(p[12..16]);
            var part4 = p[16..20];
            var part5 = p[20..32];
            return $"{{{part1}-{part2}-{part3}-{part4}-{part5}}}";
        }
        catch
        {
            return packed;
        }
    }
}
