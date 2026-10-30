using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using CleanRift.Core;
using CleanRift.UI;
using CleanRift.UI.Controls;
using CleanRift.UI.Forms;

namespace RenderForms;

/// <summary>
/// Diagnostic helper: lays out CleanRift's dialogs and renders them to PNG
/// files without showing a window. Used to check geometry and theming without
/// taking over the desktop.
/// </summary>
internal static class Program
{
    private const int SwShowNoActivate = 4;
    private const int SwHide = 0;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var outDir = args.Length > 0 ? args[0] : "rendered";
        Directory.CreateDirectory(outDir);

        if (args.Length >= 2 && args[0] == "--skipuac-xml")
        {
            var appExe = Path.Combine(AppContext.BaseDirectory, "CleanRift.exe");
            if (!File.Exists(appExe))
                appExe = Environment.ProcessPath!;
            var method = typeof(SkipUacTask).GetMethod("BuildTaskXml",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var xml = (string?)method?.Invoke(null, new object[] { appExe });
            if (xml == null)
                throw new InvalidOperationException("BuildTaskXml not found");
            File.WriteAllText(args[1], xml, System.Text.Encoding.Unicode);
            Console.WriteLine($"skipuac xml={args[1]} name={SkipUacTask.Name}");
            return;
        }

        var settings = AppSettings.Load();
        var language = settings.LanguageFile;
        if (string.IsNullOrWhiteSpace(language) || !File.Exists(language))
            language = LanguageManager.PickDefault();
        if (!string.IsNullOrWhiteSpace(language))
            LanguageManager.Instance.Load(language);
        var langArg = args.FirstOrDefault(a => a.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase));
        if (langArg != null)
        {
            var candidate = Path.Combine(LanguageManager.LanguagesDirectory, langArg[7..] + ".xml");
            if (File.Exists(candidate))
                LanguageManager.Instance.Load(candidate);
        }
        Theme.Current = Theme.FromScheme(settings.ColorScheme);
        if (args.Contains("--light"))
            Theme.Current = Theme.Light;
        if (args.Contains("--dark"))
            Theme.Current = Theme.Dark;

        if (args.Length >= 1 && args[0] == "--jumplist")
        {
            var appExe = Path.Combine(AppContext.BaseDirectory, "CleanRift.exe");
            if (!File.Exists(appExe))
                appExe = Environment.ProcessPath!;
            JumpList.Register(appExe);
            Console.WriteLine($"jumplist app={appExe}");
            return;
        }

        if (args.Contains("--main"))
        {
            var main = new MainForm(settings);
            main.ShowInTaskbar = false;
            main.StartPosition = FormStartPosition.Manual;
            main.Location = new Point(-32000, -32000);
            var path = Path.Combine(outDir, "main.png");
            main.Shown += (_, _) =>
            {
                var timer = new System.Windows.Forms.Timer { Interval = Math.Max(2000, _waitMs) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    Capture(main, path);
                    Application.Exit();
                };
                timer.Start();
            };
            Application.Run(main);
            return;
        }

        if (args.Contains("--icons"))
        {
            RenderIconSheet(Path.Combine(outDir, "icons32.png"), 32, 6);
            RenderIconSheet(Path.Combine(outDir, "icons16.png"), 16, 10);
            Console.WriteLine("icon sheets rendered");
            return;
        }

        Console.WriteLine($"language={Path.GetFileName(language ?? "")} dark={Theme.Current.IsDark}");

        var tabIndex = -1;
        var tabArg = args.FirstOrDefault(a => a.StartsWith("--tab=", StringComparison.OrdinalIgnoreCase));
        if (tabArg != null && int.TryParse(tabArg[6..], out var parsedTab))
            tabIndex = parsedTab;
        if (tabIndex >= 0)
            _tabIndex = tabIndex;

        var waitArg = args.FirstOrDefault(a => a.StartsWith("--wait=", StringComparison.OrdinalIgnoreCase));
        if (waitArg != null && int.TryParse(waitArg[7..], out var waitMs))
            _waitMs = Math.Clamp(waitMs, 0, 30000);

        Render(new AboutForm(), Path.Combine(outDir, "dlg_about.png"));
        Render(new PreferencesForm(settings), Path.Combine(outDir, "dlg_preferences.png"));
        Render(new AddStartupForm(), Path.Combine(outDir, "dlg_addstartup.png"));
        Render(new InstallTrackerForm(settings, null), Path.Combine(outDir, "dlg_tracker.png"));

        var program = SampleProgram();
        Render(new ForceRemovalForm(new[] { program }, settings), Path.Combine(outDir, "dlg_force.png"));
        Render(new TracedXmlForm(SampleTrace()), Path.Combine(outDir, "dlg_tracedxml.png"));

        var explorer = new ExplorerPanel { Dock = DockStyle.None, Width = 253, Height = 650 };
        explorer.ShowPrograms(new[] { program });
        RenderControl(explorer, Path.Combine(outDir, "panel_explorer.png"));

        var explorerMin = new ExplorerPanel { Dock = DockStyle.None, Width = 250, Height = 295 };
        explorerMin.ShowPrograms(new[] { program });
        RenderControl(explorerMin, Path.Combine(outDir, "panel_explorer_min.png"));
    }

    private static InstalledProgram SampleProgram()
    {
        return new InstalledProgram
        {
            Id = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\7-Zip",
            DisplayName = "7-Zip 25.01 (x64)",
            DisplayVersion = "25.01",
            Publisher = "Igor Pavlov",
            InstallLocation = @"C:\Program Files\7-Zip",
            InstallDate = new DateTime(2025, 11, 21),
            InstallDateRaw = "20251121",
            EstimatedSizeKb = 5_800_000,
            SizeBytes = 5_800_000,
            UninstallString = @"""C:\Program Files\7-Zip\Uninstall.exe""",
            ModifyPath = @"C:\Program Files\7-Zip\7zFM.exe",
            UrlInfoAbout = "https://www.7-zip.org/",
            Source = ProgramSource.Registry,
            Is64Bit = true,
            HiveName = "HKEY_LOCAL_MACHINE",
            RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\7-Zip",
            ReleaseType = "",
            WindowsInstaller = false
        };
    }

    private static TraceInfo SampleTrace()
    {
        var trace = new TraceInfo
        {
            Id = "sample",
            DisplayName = "Sample Program 1.0",
            File = "Sample Program_Traces.xml",
            CreatedUtc = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc)
        };
        trace.Files.Add(@"C:\Program Files\Sample\sample.exe");
        trace.Files.Add(@"C:\Program Files\Sample\readme.txt");
        trace.Folders.Add(@"C:\Program Files\Sample");
        trace.RegistryKeys.Add(@"HKEY_LOCAL_MACHINE\SOFTWARE\Sample");
        trace.RegistryValues.Add((@"HKEY_LOCAL_MACHINE\SOFTWARE\Sample", "InstallPath"));
        return trace;
    }

    private static void CreateHandles(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            _ = child.Handle;
            CreateHandles(child);
        }
    }

    private static int _tabIndex = -1;
    private static int _waitMs;

    private static void SelectTab(Control root, int index)
    {
        foreach (Control control in root.Controls)
        {
            if (control is TabControl tabs)
            {
                if (index >= 0 && index < tabs.TabCount)
                    tabs.SelectedIndex = index;
                return;
            }

            if (control.HasChildren)
                SelectTab(control, index);
        }
    }

    private static void Capture(Form form, string path)
    {
        form.Refresh();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine(
            $"{Path.GetFileName(path)} outer={form.Width}x{form.Height} client={form.ClientSize.Width}x{form.ClientSize.Height}");
    }

    private static void RenderIconSheet(string path, int size, int columns)
    {
        const int count = 36;
        var cell = size + 28;
        var rows = (count + columns - 1) / columns;
        using var bitmap = new Bitmap(columns * cell, rows * cell);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Theme.Current.Window);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        for (var i = 1; i <= count; i++)
        {
            var image = AppIcons.Get(i, size);
            if (image == null)
                continue;
            var x = ((i - 1) % columns) * cell + 14;
            var y = ((i - 1) / columns) * cell + 14;
            graphics.DrawImage(image, x, y, size, size);
        }

        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine($"{Path.GetFileName(path)} size={bitmap.Width}x{bitmap.Height}");
    }

    private static void RenderControl(Control control, string path)
    {
        control.CreateControl();
        CreateHandles(control);
        control.PerformLayout();
        control.Refresh();
        using var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine($"{Path.GetFileName(path)} size={control.Width}x{control.Height}");
        control.Dispose();
    }

    private static void Render(Form form, string path)
    {
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.CreateControl();
        CreateHandles(form);
        form.PerformLayout();

        if (_tabIndex >= 0)
            SelectTab(form, _tabIndex);

        // Make the window visible far off-screen without activation so child
        // controls paint into WM_PRINT. Nothing appears on the desktop and the
        // foreground window keeps focus.
        ShowWindow(form.Handle, SwShowNoActivate);
        foreach (Control child in form.Controls)
            child.PerformLayout();
        Application.DoEvents();

        if (_waitMs > 0)
        {
            var deadline = Environment.TickCount64 + _waitMs;
            while (Environment.TickCount64 < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(30);
            }
        }

        form.Refresh();

        var size = form.Size;
        using var bitmap = new Bitmap(size.Width, size.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));
        ShowWindow(form.Handle, SwHide);
        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine(
            $"{Path.GetFileName(path)} outer={size.Width}x{size.Height} client={form.ClientSize.Width}x{form.ClientSize.Height}");
        form.Dispose();
    }
}
