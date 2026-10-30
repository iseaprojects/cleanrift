using System.Runtime.InteropServices;
using System.Text;

namespace CleanRift.Core;

/// <summary>
/// Resolves and creates shell shortcuts (.lnk) and internet shortcuts (.url)
/// through the IShellLink COM interfaces.
/// </summary>
public static class ShortcutUtil
{
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    private const uint STGM_READ = 0;

    public static string? ResolveTarget(string shortcutPath)
    {
        try
        {
            if (!File.Exists(shortcutPath))
                return null;

            var ext = Path.GetExtension(shortcutPath);
            if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                return ReadUrlTarget(shortcutPath) ?? shortcutPath;
            if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                return shortcutPath;

            var link = (IShellLinkW)new ShellLinkObject();
            ((IPersistFile)link).Load(shortcutPath, STGM_READ);
            var buffer = new StringBuilder(1024);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            var target = buffer.ToString();
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    public static string? ResolveWorkingDirectory(string shortcutPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLinkObject();
            ((IPersistFile)link).Load(shortcutPath, STGM_READ);
            var buffer = new StringBuilder(1024);
            link.GetWorkingDirectory(buffer, buffer.Capacity);
            return buffer.ToString();
        }
        catch
        {
            return null;
        }
    }

    public static string? ResolveArguments(string shortcutPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLinkObject();
            ((IPersistFile)link).Load(shortcutPath, STGM_READ);
            var buffer = new StringBuilder(1024);
            link.GetArguments(buffer, buffer.Capacity);
            return buffer.ToString();
        }
        catch
        {
            return null;
        }
    }

    public static string? ReadUrlTarget(string urlFile)
    {
        try
        {
            foreach (var line in File.ReadAllLines(urlFile))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                    return trimmed[4..].Trim();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    public static bool CreateShortcut(string shortcutPath, string target, string arguments = "",
        string workingDirectory = "", string description = "", string? iconPath = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (Path.GetExtension(shortcutPath).Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(shortcutPath,
                    "[InternetShortcut]\r\nURL=" + target + "\r\n",
                    Encoding.ASCII);
                return true;
            }

            var link = (IShellLinkW)new ShellLinkObject();
            link.SetPath(target);
            if (!string.IsNullOrWhiteSpace(arguments))
                link.SetArguments(arguments);
            if (!string.IsNullOrWhiteSpace(workingDirectory))
                link.SetWorkingDirectory(workingDirectory);
            if (!string.IsNullOrWhiteSpace(description))
                link.SetDescription(description);
            if (!string.IsNullOrWhiteSpace(iconPath))
                link.SetIconLocation(iconPath, 0);

            ((IPersistFile)link).Save(shortcutPath, true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
