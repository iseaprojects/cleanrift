using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CleanRift.Core;

public enum ColorScheme
{
    Light = 0,
    Auto = 1,
    Dark = 2
}

public enum WizardMode
{
    Ask = 0,
    Automatic = 1,
    Disable = 2
}

/// <summary>
/// CleanRift preferences. Settings live under %LOCALAPPDATA%\CleanRift so the
/// application never touches another product's profile or registry keys.
/// The XML file stores window/list preferences; a small JSON sidecar stores
/// state that is not part of the settings document.
/// </summary>
public sealed class AppSettings
{
    private const string ProductFolder = "CleanRift";

    public string LanguageFile { get; set; } = "";
    public bool ToolbarCaptions { get; set; } = true;
    public bool ToolbarVisible { get; set; } = true;
    public bool SecondToolbarVisible { get; set; } = true;
    public bool SmallIcons { get; set; }
    public bool Mark32Bit { get; set; } = true;
    public ColorScheme ColorScheme { get; set; } = ColorScheme.Light;
    public bool DisplayUpdates { get; set; } = true;
    public int UpdatesMode { get; set; } // 0 collapsed, 1 expanded, 2 always expanded
    public bool DisplayRecentPopup { get; set; } = true;
    public bool HighlightRecent { get; set; } = true;
    public bool RecentOnToolbar { get; set; } = true;
    public WizardMode WizardMode { get; set; } = WizardMode.Ask;
    public bool CheckForUpdates { get; set; } = true;
    public bool CheckForBeta { get; set; }
    public bool ControlPanelIcon { get; set; } = true;
    public bool ShellExtension { get; set; }
    public bool TotalCommander { get; set; }
    public bool TrayIcon { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool MinimizeOnClose { get; set; }
    public bool SkipUac { get; set; }
    public string LastCategory { get; set; } = "apps";
    public int WindowLeft { get; set; } = int.MinValue;
    public int WindowTop { get; set; } = int.MinValue;
    public int WindowWidth { get; set; } = 1000;
    public int WindowHeight { get; set; } = 640;
    public bool WindowMaximized { get; set; }
    public int ColumnNameWidth { get; set; } = 340;
    public int ColumnSizeWidth { get; set; } = 90;
    public int ColumnDateWidth { get; set; } = 110;
    public int ColumnTracedWidth { get; set; } = 85;
    public int StartupColumnWidth { get; set; } = 340;
    public int SortColumn { get; set; }
    public bool SortAscending { get; set; } = true;
    public List<string> HiddenProgramIds { get; set; } = new();
    public Dictionary<string, string> KnownInstallDates { get; set; } = new();
    public DateTime FirstRunUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string SettingsPath { get; private set; } = "";

    [JsonIgnore]
    public string StatePath { get; private set; } = "";

    public static string DataDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductFolder);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string PreferencesPath => Path.Combine(DataDirectory, "preferences.xml");

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        var path = PreferencesPath;

        try
        {
            if (File.Exists(path))
                settings.ReadPreferences(path);
        }
        catch
        {
            settings = new AppSettings();
        }

        settings.SettingsPath = path;
        settings.StatePath = Path.Combine(DataDirectory, "state.json");
        settings.ReadState();
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            WritePreferences(SettingsPath);
            WriteState();
        }
        catch
        {
            // Preferences are best-effort; never crash the app because of them.
        }
    }

    private void ReadPreferences(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root;
        if (root == null)
            return;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in root.Elements())
            values[element.Name.LocalName] = element.Value.Trim();

        string Get(string name, string fallback = "") =>
            values.TryGetValue(name, out var value) ? value : fallback;

        bool GetBool(string name, bool fallback) =>
            values.TryGetValue(name, out var value)
                ? value is "1" or "true" or "True" || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                : fallback;

        int GetInt(string name, int fallback) =>
            int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;

        LanguageFile = ResolveLanguage(Get("Interface_Language"));
        ColorScheme = (ColorScheme)Math.Clamp(GetInt("Interface_ColorScheme", (int)ColorScheme.Light), 0, 2);
        Mark32Bit = GetBool("List_Mark32Bit", Mark32Bit);
        HighlightRecent = GetBool("List_HighlightRecent", HighlightRecent);
        SmallIcons = GetBool("List_SmallIcons", SmallIcons);
        DisplayRecentPopup = GetBool("List_ShowRecentPopup", DisplayRecentPopup);
        CheckForBeta = GetBool("Updates_CheckBeta", CheckForBeta);
        TotalCommander = GetBool("General_TotalCommander", TotalCommander);
        SortColumn = GetInt("List_SortColumn", SortColumn);
        SortAscending = GetBool("List_SortAscending", SortAscending);
        RecentOnToolbar = GetBool("List_RecentOnToolbar", RecentOnToolbar);
        SecondToolbarVisible = GetBool("Toolbar_SecondVisible", SecondToolbarVisible);
        ShellExtension = GetBool("Integration_ShellExtension", ShellExtension);
        SkipUac = GetBool("Security_SkipUac", SkipUac);
        CheckForUpdates = GetBool("Updates_CheckOnStartup", CheckForUpdates);
        ToolbarCaptions = GetBool("Toolbar_Captions", ToolbarCaptions);
        ToolbarVisible = GetBool("Toolbar_Visible", ToolbarVisible);
        TrayIcon = GetBool("Tray_Show", TrayIcon);
        MinimizeOnClose = GetBool("Tray_HideOnClose", MinimizeOnClose);
        MinimizeToTray = GetBool("Tray_HideOnMinimize", MinimizeToTray);
        WindowMaximized = GetBool("Window_Maximized", WindowMaximized);
        WizardMode = (WizardMode)Math.Clamp(GetInt("Uninstall_WizardMode", (int)WizardMode), 0, 2);

        ColumnNameWidth = DecodeColumn(GetInt("List_Column_0", 0), ColumnNameWidth);
        ColumnSizeWidth = DecodeColumn(GetInt("List_Column_1", 0), ColumnSizeWidth);
        ColumnDateWidth = DecodeColumn(GetInt("List_Column_2", 0), ColumnDateWidth);
        ColumnTracedWidth = DecodeColumn(GetInt("List_Column_3", 0), ColumnTracedWidth);
        StartupColumnWidth = DecodeColumn(GetInt("StartupList_Column_0", 0), StartupColumnWidth);

        var rect = Get("Window_Bounds");
        var match = Regex.Match(rect,
            @"^CleanRiftRect\(\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*\)$");
        if (match.Success)
        {
            WindowLeft = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            WindowTop = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var right = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            var bottom = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
            if (right - WindowLeft >= 320)
                WindowWidth = right - WindowLeft;
            if (bottom - WindowTop >= 240)
                WindowHeight = bottom - WindowTop;
        }
    }

    private void WritePreferences(string path)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\r\n");
        builder.Append("<CleanRiftPreferences version=\"1\">\r\n");

        void Bool(string name, bool value) =>
            builder.Append("    <").Append(name).Append(" type=\"1\">")
                .Append(value ? '1' : '0').Append("</").Append(name).Append(">\r\n");

        void Int(string name, int value) =>
            builder.Append("    <").Append(name).Append(" type=\"1\">")
                .Append(value.ToString(CultureInfo.InvariantCulture))
                .Append("</").Append(name).Append(">\r\n");

        void Text(string name, string value) =>
            builder.Append("    <").Append(name).Append(" type=\"2\">")
                .Append(System.Security.SecurityElement.Escape(value) ?? "")
                .Append("</").Append(name).Append(">\r\n");

        Bool("List_Mark32Bit", Mark32Bit);
        Bool("List_HighlightRecent", HighlightRecent);
        Bool("List_SmallIcons", SmallIcons);
        Bool("List_ShowRecentPopup", DisplayRecentPopup);
        Bool("List_SortAscending", SortAscending);
        Int("List_SortColumn", SortColumn);
        Bool("List_RecentOnToolbar", RecentOnToolbar);
        Int("List_Column_0", EncodeColumn(ColumnNameWidth));
        Int("List_Column_1", EncodeColumn(ColumnSizeWidth));
        Int("List_Column_2", EncodeColumn(ColumnDateWidth));
        Int("List_Column_3", EncodeColumn(ColumnTracedWidth));
        Int("StartupList_Column_0", EncodeColumn(StartupColumnWidth));
        Int("Interface_ColorScheme", (int)ColorScheme);
        Text("Interface_Language", Path.GetFileNameWithoutExtension(LanguageFile));
        Bool("Updates_CheckOnStartup", CheckForUpdates);
        Bool("Updates_CheckBeta", CheckForBeta);
        Bool("General_TotalCommander", TotalCommander);
        Bool("Integration_ShellExtension", ShellExtension);
        Bool("Security_SkipUac", SkipUac);
        Bool("Toolbar_Captions", ToolbarCaptions);
        Bool("Toolbar_Visible", ToolbarVisible);
        Bool("Toolbar_SecondVisible", SecondToolbarVisible);
        Bool("Tray_Show", TrayIcon);
        Bool("Tray_HideOnClose", MinimizeOnClose);
        Bool("Tray_HideOnMinimize", MinimizeToTray);
        Bool("Window_Maximized", WindowMaximized);
        Int("Uninstall_WizardMode", (int)WizardMode);

        var left = WindowLeft == int.MinValue ? 0 : WindowLeft;
        var top = WindowTop == int.MinValue ? 0 : WindowTop;
        builder.Append("    <Window_Bounds type=\"3\">CleanRiftRect(")
            .Append(left).Append(", ").Append(top).Append(", ")
            .Append(left + Math.Max(320, WindowWidth)).Append(", ")
            .Append(top + Math.Max(240, WindowHeight))
            .Append(")</Window_Bounds>\r\n");

        builder.Append("</CleanRiftPreferences>\r\n");
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static int EncodeColumn(int width) => 0x10000 | Math.Clamp(width, 0, 0xFFFF);

    private static int DecodeColumn(int raw, int fallback)
    {
        if (raw <= 0)
            return fallback;
        var width = raw & 0xFFFF;
        return width > 0 ? width : fallback;
    }

    private static string ResolveLanguage(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        if (File.Exists(value))
            return value;

        var candidate = Path.Combine(LanguageManager.LanguagesDirectory, value + ".xml");
        if (File.Exists(candidate))
            return candidate;

        var inBase = Path.Combine(AppContext.BaseDirectory, "Languages", value + ".xml");
        return File.Exists(inBase) ? inBase : value;
    }

    private void ReadState()
    {
        try
        {
            if (!File.Exists(StatePath))
                return;

            using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
            var root = doc.RootElement;
            if (root.TryGetProperty("DisplayUpdates", out var displayUpdates))
                DisplayUpdates = displayUpdates.GetBoolean();
            if (root.TryGetProperty("UpdatesMode", out var updatesMode))
                UpdatesMode = updatesMode.GetInt32();
            if (root.TryGetProperty("ControlPanelIcon", out var controlPanel))
                ControlPanelIcon = controlPanel.GetBoolean();
            if (root.TryGetProperty("StartMinimized", out var startMinimized))
                StartMinimized = startMinimized.GetBoolean();
            if (root.TryGetProperty("LastCategory", out var lastCategory))
                LastCategory = lastCategory.GetString() ?? LastCategory;
            if (root.TryGetProperty("FirstRunUtc", out var firstRun) &&
                firstRun.TryGetDateTime(out var firstRunDate))
                FirstRunUtc = firstRunDate;
            if (root.TryGetProperty("HiddenProgramIds", out var hidden))
                HiddenProgramIds = hidden.EnumerateArray().Select(e => e.GetString() ?? "")
                    .Where(s => s.Length > 0).ToList();
            if (root.TryGetProperty("KnownInstallDates", out var dates))
                KnownInstallDates = dates.EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "",
                        StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // State is optional.
        }
    }

    private void WriteState()
    {
        try
        {
            var state = new
            {
                DisplayUpdates,
                UpdatesMode,
                ControlPanelIcon,
                StartMinimized,
                LastCategory,
                FirstRunUtc,
                HiddenProgramIds,
                KnownInstallDates
            };
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // State is optional.
        }
    }
}
