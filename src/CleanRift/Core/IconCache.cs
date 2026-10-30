using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CleanRift.Core;

/// <summary>
/// Extracts shell icons for installed programs using DisplayIcon,
/// InstallLocation and shortcut fallbacks.
/// </summary>
public sealed class IconCache : IDisposable
{
    private readonly ConcurrentDictionary<string, Icon?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Icon? Get(string key) => _cache.TryGetValue(key, out var icon) ? icon : null;

    public void Preload(IEnumerable<InstalledProgram> programs)
    {
        Parallel.ForEach(programs, p =>
        {
            try
            {
                p.Icon = Resolve(p);
            }
            catch
            {
                p.Icon = null;
            }
        });
    }

    public Icon? Resolve(InstalledProgram program)
    {
        var candidates = new List<(string path, int index)>();

        if (program.IsStoreApp && !string.IsNullOrWhiteSpace(program.StoreLogo))
            candidates.Add((program.StoreLogo!, -1));

        var displayIcon = program.DisplayIcon;
        if (!string.IsNullOrWhiteSpace(displayIcon))
        {
            var cleaned = Environment.ExpandEnvironmentVariables(displayIcon.Trim().Trim('"'));
            var comma = cleaned.LastIndexOf(',');
            var index = 0;
            if (comma > 2 && int.TryParse(cleaned[(comma + 1)..].Trim(), out var idx))
            {
                index = idx;
                cleaned = cleaned[..comma].Trim().Trim('"');
            }

            if (File.Exists(cleaned))
                candidates.Add((cleaned, index));
        }

        foreach (var exe in GuessExecutables(program))
            candidates.Add((exe, 0));

        foreach (var candidate in candidates)
        {
            var cacheKey = candidate.path + "#" + candidate.index;
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                if (cached != null)
                    return cached;
                continue;
            }

            var icon = candidate.index < 0 ? LoadImageIcon(candidate.path) : ExtractIcon(candidate.path, candidate.index);
            _cache[cacheKey] = icon;
            if (icon != null)
                return icon;
        }

        return null;
    }

    private static IEnumerable<string> GuessExecutables(InstalledProgram program)
    {
        if (string.IsNullOrWhiteSpace(program.InstallLocation))
            yield break;

        var dir = program.InstallLocation.Trim().Trim('"');
        if (!Directory.Exists(dir))
            yield break;

        string? best = null;
        long bestSize = -1;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Contains("unins", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("update", StringComparison.OrdinalIgnoreCase))
                    continue;

                var info = new FileInfo(file);
                if (info.Length > bestSize)
                {
                    bestSize = info.Length;
                    best = file;
                }
            }
        }
        catch
        {
            // Ignore access errors.
        }

        if (best != null)
            yield return best;
    }

    private static Icon? LoadImageIcon(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var image = Image.FromStream(stream);
            using var bitmap = new Bitmap(image, 32, 32);
            var hIcon = bitmap.GetHicon();
            try
            {
                using var temp = Icon.FromHandle(hIcon);
                return (Icon)temp.Clone();
            }
            finally
            {
                NativeMethods.DestroyIcon(hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    public static Icon? ExtractIcon(string path, int index)
    {
        var large = new IntPtr[1];
        var small = new IntPtr[1];
        try
        {
            var count = NativeMethods.ExtractIconEx(path, index, large, small, 1);
            if (count > 0)
            {
                var handle = large[0] != IntPtr.Zero ? large[0] : small[0];
                if (handle != IntPtr.Zero)
                {
                    using var icon = Icon.FromHandle(handle);
                    var clone = (Icon)icon.Clone();
                    // ExtractIconEx may fill both arrays; both handles belong
                    // to the caller and must be released.
                    if (large[0] != IntPtr.Zero)
                        NativeMethods.DestroyIcon(large[0]);
                    if (small[0] != IntPtr.Zero)
                        NativeMethods.DestroyIcon(small[0]);
                    return clone;
                }

                if (large[0] != IntPtr.Zero)
                    NativeMethods.DestroyIcon(large[0]);
                if (small[0] != IntPtr.Zero)
                    NativeMethods.DestroyIcon(small[0]);
            }
        }
        catch
        {
            // Fall through to shell icon.
        }

        return ShellIcon(path);
    }

    public static Icon? ShellIcon(string path, bool small = false)
    {
        try
        {
            var info = new NativeMethods.SHFILEINFO();
            var flags = NativeMethods.SHGFI_ICON | (small ? NativeMethods.SHGFI_SMALLICON : NativeMethods.SHGFI_LARGEICON);
            if (!File.Exists(path) && !Directory.Exists(path))
                flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;
            var result = NativeMethods.SHGetFileInfo(path, 0, ref info,
                (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(), flags);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;
            using var icon = Icon.FromHandle(info.hIcon);
            var clone = (Icon)icon.Clone();
            NativeMethods.DestroyIcon(info.hIcon);
            return clone;
        }
        catch
        {
            return null;
        }
    }

    public static Icon? LoadAppIcon()
    {
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(ico))
                return new Icon(ico, 64, 64);
        }
        catch
        {
            // ignore
        }

        return null;
    }

    public void Dispose()
    {
        foreach (var icon in _cache.Values)
            icon?.Dispose();
        _cache.Clear();
    }
}
