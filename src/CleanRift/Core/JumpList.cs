using System.Runtime.InteropServices;
using System.Text;

namespace CleanRift.Core;

/// <summary>
/// Taskbar jump list with the three CleanRift shortcuts: install and trace,
/// Microsoft Store apps and startup items. Each entry uses the application
/// icon and the language strings of the current UI.
/// </summary>
public static class JumpList
{
    private const string AppId = "CleanRift.Desktop.1";
    private const ushort VtLpwstr = 31;

    public static void Register(string executable)
    {
        try
        {
            var result = SetCurrentProcessExplicitAppUserModelID(AppId);
            if (result != 0)
                Log($"SetCurrentProcessExplicitAppUserModelID failed: 0x{result:X8}");

            var list = (ICustomDestinationList)new CDestinationList();
            list.SetAppID(AppId);

            var iid = typeof(IObjectArray).GUID;
            list.BeginList(out _, ref iid, out _);

            var tasks = (IObjectCollection)new CEnumerableObjectCollection();
            var lang = LanguageManager.Instance;

            AddTask(tasks, executable,
                "/install_from_task_bar",
                lang.T("SecondaryToolbar/btnInstallAndTrace", "Install and trace"),
                lang.T("SecondaryToolbar/btnInstallAndTraceTooltip",
                    "Install a new program and watch all changes it makes to file system and registry"));

            if (Environment.OSVersion.Version >= new Version(6, 2))
            {
                AddTask(tasks, executable,
                    "/store_apps_from_task_bar",
                    lang.T("SecondaryToolbar/btnStoreApps", "Microsoft Store Apps"),
                    lang.T("SecondaryToolbar/btnStoreAppsToolTip",
                        "Apps installed from Microsoft Store"));
            }

            AddTask(tasks, executable,
                "/startup_from_task_bar",
                lang.T("ToolBar/btnStartup", "Startup"),
                lang.T("ToolBar/btnStartupToolTip",
                    "Applications that automatically run at startup"));

            list.AddUserTasks((IObjectArray)tasks);
            list.CommitList();
        }
        catch (Exception ex)
        {
            Log("Jump list registration failed: " + ex.Message);
        }
    }

    private static void AddTask(IObjectCollection tasks, string executable,
        string arguments, string title, string description)
    {
        var link = (IShellLinkW)new CShellLink();
        link.SetPath(executable);
        link.SetArguments(arguments);
        link.SetDescription(description);
        link.SetIconLocation(executable, 0);

        SetTitle(link, title);
        tasks.AddObject(link);
    }

    private static void SetTitle(IShellLinkW link, string title)
    {
        var store = (IPropertyStore)link;
        var key = new PROPERTYKEY
        {
            formatId = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"),
            propertyId = 2 // PKEY_Title
        };
        var value = new PROPVARIANT
        {
            variantType = VtLpwstr,
            pointerValue = Marshal.StringToCoTaskMemUni(title)
        };

        try
        {
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally
        {
            Marshal.FreeCoTaskMem(value.pointerValue);
        }
    }

    private static void Log(string message)
    {
        try
        {
            var dir = AppSettings.DataDirectory;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "jumplist.log"),
                $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics only.
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid formatId;
        public uint propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort variantType;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr pointerValue;
    }

    [ComImport]
    [Guid("77F10CF0-3DB5-4966-B520-B7C54FD35ED6")]
    private class CDestinationList
    {
    }

    [ComImport]
    [Guid("2D3468C1-36A7-43B6-AC24-D3F02FD9607A")]
    private class CEnumerableObjectCollection
    {
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink
    {
    }

    [ComImport]
    [Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        void BeginList(out uint minSlots, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object removedDestinations);
        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string category,
            [MarshalAs(UnmanagedType.Interface)] IObjectArray destinations);
        void AppendKnownCategory(int category);
        void AddUserTasks([MarshalAs(UnmanagedType.Interface)] IObjectArray tasks);
        void CommitList();
        void GetRemovedDestinations(ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object removedDestinations);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string appId);
        void AbortList();
    }

    [ComImport]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint count);
        void GetAt(uint index, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object item);
    }

    [ComImport]
    [Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection
    {
        void GetCount(out uint count);
        void GetAt(uint index, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object item);
        void AddObject([MarshalAs(UnmanagedType.Interface)] object item);
        void AddFromArray([MarshalAs(UnmanagedType.Interface)] IObjectArray source);
        void RemoveObjectAt(uint index);
        void Clear();
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath,
            IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
            int maxPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PROPERTYKEY key);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        void Commit();
    }
}
