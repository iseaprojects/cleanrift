using System.Diagnostics;
using System.Drawing;
using Microsoft.Win32;
using CleanRift.Core;
using CleanRift.UI;
using CleanRift.UI.Controls;
using CleanRift.UI.Forms;

namespace CleanRift.UI;

/// <summary>
/// Entry point requested from the taskbar jump list. The switches are
/// /install_from_task_bar, /startup_from_task_bar and /store_apps_from_task_bar.
/// </summary>
public enum StartupTarget
{
    None,
    InstallTrace,
    Startup,
    StoreApps
}

public sealed class MainForm : Form
{
    private enum ViewMode
    {
        Programs,
        Startup
    }

    private readonly AppSettings _settings;
    private readonly ProgramScanner _scanner = new();
    private readonly StartupScanner _startupScanner;
    private readonly IconCache _icons = new();
    private readonly Icon? _appIcon;

    private readonly MenuStrip _menu = new();
    private readonly ToolStrip _toolbar = new();
    private readonly CategoryStrip _categories = new();
    private readonly BufferedListView _list = new();
    private readonly ExplorerPanel _explorer = new();
    private readonly Panel _body = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusIcon = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripStatusLabel _sizeLabel = new();
    private readonly ToolStripStatusLabel _recentLink = new();
    private readonly ToolStripButton _btnUninstaller = new();
    private readonly ToolStripButton _btnStartup = new();
    private readonly ToolStripButton _btnRefresh = new();
    private readonly ToolStripButton _btnPreferences = new();
    private readonly ToolStripButton _btnHelp = new();
    private readonly ContextMenuStrip _context = new();
    private readonly NotifyIcon _tray;
    private readonly ImageList _listIcons = new() { ImageSize = new Size(32, 32), ColorDepth = ColorDepth.Depth32Bit };
    private readonly System.Windows.Forms.Timer _filterTimer = new() { Interval = 180 };

    private ViewMode _mode = ViewMode.Programs;

    private int IconSize => _settings.SmallIcons ? 16 : 32;
    private List<InstalledProgram> _programs = new();
    private List<InstalledProgram>? _cachedPrograms;
    private List<StartupItem> _startup = new();
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private string _category = "apps";
    private string _filter = "";
    private int _sortColumn;
    private bool _sortAscending = true;
    private bool _scanning;
    private bool _refreshPending;
    private bool _startupScanning;
    private bool _startupRefreshPending;
    private bool _closing;
    private bool _externalClose;
    private readonly StartupTarget _initialTarget;
    private readonly string? _initialSetupPath;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _sizeCts;

    public MainForm(AppSettings settings, StartupTarget initialTarget = StartupTarget.None,
        string? initialSetupPath = null)
    {
        _settings = settings;
        _initialTarget = initialTarget;
        _initialSetupPath = initialSetupPath;
        _startupScanner = new StartupScanner(settings);
        _category = settings.LastCategory;
        // The secondary toolbar no longer exposes "recent"; normalize an old
        // saved state so a category is selected on startup.
        if (string.Equals(_category, "recent", StringComparison.OrdinalIgnoreCase))
            _category = "apps";

        var theme = Theme.Current;
        Text = AppInfo.DisplayName;
        _appIcon = AppIcons.AppIcon();
        Icon = _appIcon;
        MinimumSize = new Size(780, 480);

        var virtualScreen = SystemInformation.VirtualScreen;
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        var clientWidth = Math.Clamp(settings.WindowWidth, 764, Math.Max(764, virtualScreen.Width - 20));
        var clientHeight = Math.Clamp(settings.WindowHeight, 450, Math.Max(450, workArea.Height - 60));
        ClientSize = new Size(clientWidth, clientHeight);
        if (settings.WindowLeft != int.MinValue && settings.WindowTop != int.MinValue)
        {
            // Keep the window on a monitor that still exists (multi-monitor
            // layouts may change between sessions).
            var left = Math.Clamp(settings.WindowLeft, virtualScreen.Left,
                Math.Max(virtualScreen.Left, virtualScreen.Right - Width));
            var top = Math.Clamp(settings.WindowTop, virtualScreen.Top,
                Math.Max(virtualScreen.Top, virtualScreen.Bottom - Height));
            StartPosition = FormStartPosition.Manual;
            Location = new Point(left, top);
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
        }

        BackColor = theme.Window;
        ForeColor = theme.Text;
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        // App List > Compact list setting. The default is the large 32 px icon
        // view.
        _listIcons.ImageSize = new Size(IconSize, IconSize);

        BuildToolbar();
        BuildList();
        BuildStatus();
        BuildMenu();

        _explorer.SearchChanged += OnSearchChanged;
        _explorer.ActionInvoked += OnExplorerAction;
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            _filter = _explorer.SearchText;
            RebuildList();
            SelectFirstIfNone();
        };

        _body = new Panel { Dock = DockStyle.Fill };
        _body.Controls.Add(_list);
        _body.Controls.Add(_explorer);

        Controls.Add(_body);
        Controls.Add(_categories);
        Controls.Add(_toolbar);
        Controls.Add(_menu);
        Controls.Add(_status);
        MainMenuStrip = _menu;

        _categories.ItemClicked += item =>
        {
            if (item.IsAction)
            {
                StartInstallTrace(null);
                return;
            }

            _category = item.Key;
            _settings.LastCategory = _category;
            UpdateCategorySelection();
            RebuildList();
        };

        _tray = new NotifyIcon
        {
            Icon = _appIcon,
            Text = AppInfo.Name,
            Visible = false
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add(LanguageManager.Instance.T("Misc/trayOpen", "Open " + AppInfo.Name), null,
            (_, _) => RestoreFromTray());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(LanguageManager.Instance.T("Misc/btnExit", "Exit"), null,
            (_, _) => { _tray.Visible = false; Close(); });
        _tray.ContextMenuStrip = trayMenu;
        _tray.Visible = _settings.TrayIcon;

        ApplyThemeAndTexts();
        RestoreWindowState();
        UpdateCategorySelection();

        Shown += async (_, _) =>
        {
            // Handles exist only after the window is shown; apply the dark
            // scrollbar theme then, otherwise the list keeps the light theme.
            Theme.ApplyDarkScrollbars(_list);
            if (_settings.StartMinimized && _settings.TrayIcon)
                HideToTray();
            await RefreshAsync();
            if (_closing)
                return;

            if (!string.IsNullOrWhiteSpace(_initialSetupPath))
            {
                StartInstallTrace(null, _initialSetupPath);
                return;
            }

            switch (_initialTarget)
            {
                case StartupTarget.InstallTrace:
                    StartInstallTrace(null);
                    break;
                case StartupTarget.Startup:
                    SwitchMode(ViewMode.Startup);
                    break;
                case StartupTarget.StoreApps:
                    _category = "store";
                    SwitchMode(ViewMode.Programs);
                    UpdateCategorySelection();
                    RebuildList();
                    break;
            }
        };
        Resize += (_, _) =>
        {
            // Hiding without a tray icon would make the window unreachable.
            if (WindowState == FormWindowState.Minimized && _settings.MinimizeToTray &&
                _settings.TrayIcon)
                HideToTray();
        };
        FormClosing += OnFormClosing;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(this);
        Theme.ApplyDarkScrollbars(_list);
    }

    private void BuildToolbar()
    {
        _toolbar.Dock = DockStyle.Top;
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.Padding = new Padding(6, 2, 6, 2);
        _toolbar.AutoSize = false;
        _toolbar.Height = 64;
        _toolbar.ImageScalingSize = new Size(32, 32);
        _toolbar.Renderer = new ThemedToolStripRenderer(Theme.Current);

        void Configure(ToolStripButton button, int icon)
        {
            button.Image = AppIcons.Get(icon, 32);
            button.ImageAlign = ContentAlignment.TopCenter;
            button.TextAlign = ContentAlignment.BottomCenter;
            button.TextImageRelation = TextImageRelation.ImageAboveText;
            button.AutoSize = false;
            button.Size = new Size(80, 58);
            button.Margin = new Padding(2, 0, 2, 0);
            button.Font = new Font("Segoe UI", 8.5f);
            button.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            button.CheckOnClick = false;
            _toolbar.Items.Add(button);
        }

        Configure(_btnUninstaller, AppIcons.Uninstaller);
        Configure(_btnStartup, AppIcons.Startup);
        _toolbar.Items.Add(new ToolStripSeparator());
        Configure(_btnRefresh, AppIcons.Refresh);
        Configure(_btnPreferences, AppIcons.Tools);
        Configure(_btnHelp, AppIcons.Help);
        _toolbar.Items.Add(new ToolStripSeparator());

        _btnUninstaller.Click += (_, _) => SwitchMode(ViewMode.Programs);
        _btnStartup.Click += (_, _) => SwitchMode(ViewMode.Startup);
        _btnRefresh.Click += async (_, _) => await RefreshAsync();
        _btnPreferences.Click += (_, _) => OpenPreferences();
        _btnHelp.Click += (_, _) => OpenHelp();
    }

    private void BuildList()
    {
        var theme = Theme.Current;
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.MultiSelect = true;
        _list.BorderStyle = BorderStyle.None;
        _list.SmallImageList = _listIcons;
        _list.BackColor = theme.ListBack;
        _list.ForeColor = theme.Text;
        _list.Font = new Font("Segoe UI", 9f);
        _list.ShowItemToolTips = true;
        // Details view otherwise wraps long program names into two lines;
        // keep every row on one ellipsized line.
        _list.LabelWrap = false;
        _list.OwnerDraw = true;
        _list.DrawColumnHeader += OnDrawColumnHeader;
        _list.DrawItem += OnDrawListItem;
        _list.DrawSubItem += OnDrawListSubItem;
        _list.Columns.Add("", 340);
        _list.Columns.Add("", 100, HorizontalAlignment.Right);
        _list.Columns.Add("", 110);
        _list.Columns.Add("", 60, HorizontalAlignment.Center);
        _list.ColumnClick += OnColumnClick;
        _list.SelectedIndexChanged += (_, _) => OnSelectionChanged();
        _list.DoubleClick += (_, _) => OnListDoubleClick();
        _list.KeyDown += OnListKeyDown;
        _list.MouseUp += OnListMouseUp;
        _list.MouseLeave += (_, _) =>
        {
            // Clear any hover-only repaint artifact.
            if (!_list.IsDisposed)
                _list.Invalidate();
        };
        _list.Resize += (_, _) =>
        {
            UpdateExplorerWidth();
            AdjustProgramColumns();
            AdjustStartupColumns();
        };
    }

    private void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        e.DrawDefault = false;
        var theme = Theme.Current;
        using (var brush = new SolidBrush(theme.HeaderBack))
            e.Graphics.FillRectangle(brush, e.Bounds);

        using (var border = new Pen(theme.Border))
        {
            e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1,
                e.Bounds.Right, e.Bounds.Bottom - 1);
            e.Graphics.DrawLine(border, e.Bounds.Right - 1, e.Bounds.Top + 5,
                e.Bounds.Right - 1, e.Bounds.Bottom - 5);
        }

        if (e.Header is null || string.IsNullOrEmpty(e.Header.Text))
            return;

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        var rect = Rectangle.Inflate(e.Bounds, -6, 0);
        switch (e.Header.TextAlign)
        {
            case HorizontalAlignment.Right:
                flags |= TextFormatFlags.Right;
                break;
            case HorizontalAlignment.Center:
                flags |= TextFormatFlags.HorizontalCenter;
                break;
            default:
                flags |= TextFormatFlags.Left;
                break;
        }

        TextRenderer.DrawText(e.Graphics, e.Header.Text, _list.Font, rect, theme.Text, flags);
    }

    private void OnDrawListItem(object? sender, DrawListViewItemEventArgs e)
    {
        // Each sub-item paints its own background and text. Do not fill the
        // whole row here: ListView redraws a single sub-item on hover, and a
        // full-row fill would erase the text of the other columns until the
        // next full repaint.
        e.DrawDefault = false;
    }

    private void OnDrawListSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        e.DrawDefault = false;
        if (e.Item is null || e.SubItem is null)
            return;

        var theme = Theme.Current;
        var selected = e.Item.Selected;
        var back = selected ? theme.Selection : theme.ListBack;

        // On hover Windows only invalidates one sub-item. Use the row height
        // and a column-width fallback so a clipped/empty sub-item rectangle
        // cannot erase the text until the next full repaint.
        var cell = GetSubItemCellBounds(e);
        using (var brush = new SolidBrush(back))
            e.Graphics.FillRectangle(brush, cell);

        var color = selected
            ? (theme.IsDark ? Color.White : theme.Text)
            : (e.Item.ForeColor.IsEmpty ? theme.Text : e.Item.ForeColor);

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        var iconWidth = 0;

        if (e.ColumnIndex == 0 && e.Item.ImageIndex >= 0 &&
            e.Item.ImageIndex < _listIcons.Images.Count)
        {
            var image = _listIcons.Images[e.Item.ImageIndex];
            if (image != null)
            {
                iconWidth = image.Width;
                var iconRect = new Rectangle(cell.Left + 4,
                    cell.Top + (cell.Height - image.Height) / 2,
                    image.Width, image.Height);
                e.Graphics.DrawImage(image, iconRect);
                iconWidth += 6;
            }
        }

        var rect = new Rectangle(cell.Left + 4 + iconWidth, cell.Top,
            Math.Max(8, cell.Width - 8 - iconWidth), cell.Height);
        switch (e.Header?.TextAlign ?? HorizontalAlignment.Left)
        {
            case HorizontalAlignment.Right:
                flags |= TextFormatFlags.Right;
                break;
            case HorizontalAlignment.Center:
                flags |= TextFormatFlags.HorizontalCenter;
                break;
            default:
                flags |= TextFormatFlags.Left;
                break;
        }

        TextRenderer.DrawText(e.Graphics, e.SubItem.Text, _list.Font, rect, color, flags);
    }

    /// <summary>
    /// ListView sometimes passes clipped or empty bounds when it repaints only
    /// the hovered sub-item. Rebuild the cell from the row and column widths so
    /// painting one column can never erase another column's text.
    /// </summary>
    private Rectangle GetSubItemCellBounds(DrawListViewSubItemEventArgs e)
    {
        var itemBounds = e.Item?.Bounds ?? e.Bounds;
        var rowTop = itemBounds.Top;
        var rowHeight = Math.Max(1, itemBounds.Height);
        var left = e.Bounds.Left;
        var width = e.Bounds.Width;

        if (e.ColumnIndex > 0 && left <= 0)
        {
            left = 0;
            for (var i = 0; i < e.ColumnIndex && i < _list.Columns.Count; i++)
                left += _list.Columns[i].Width;
        }

        if (width <= 0 && e.ColumnIndex >= 0 && e.ColumnIndex < _list.Columns.Count)
            width = _list.Columns[e.ColumnIndex].Width;
        if (width <= 0)
            width = Math.Max(1, _list.ClientSize.Width - left);

        return new Rectangle(left, rowTop, width, rowHeight);
    }

    private void BuildStatus()
    {
        _status.Dock = DockStyle.Bottom;
        _status.SizingGrip = true;
        _status.Renderer = new ThemedToolStripRenderer(Theme.Current)
        {
            BackgroundOverride = Theme.Current.StatusBack
        };
        _statusIcon.Image = AppIcons.Get(AppIcons.Info, 16);
        _statusIcon.Margin = new Padding(4, 0, 2, 0);
        _statusLabel.Spring = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Padding = new Padding(2, 0, 0, 0);
        _statusLabel.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _recentLink.IsLink = true;
        _recentLink.LinkBehavior = LinkBehavior.HoverUnderline;
        _recentLink.Click += (_, _) =>
        {
            _category = "recent";
            SwitchMode(ViewMode.Programs);
            UpdateCategorySelection();
            RebuildList();
        };
        _sizeLabel.TextAlign = ContentAlignment.MiddleRight;
        // Status bar: info glyph, the bold summary and the size counter,
        // right above the grip.
        _status.Items.AddRange(new ToolStripItem[] { _statusIcon, _statusLabel, _sizeLabel });
        _status.Font = new Font("Segoe UI", 9f);
    }

    private void BuildMenu()
    {
        var lang = LanguageManager.Instance;
        _menu.Items.Clear();
        _menu.Dock = DockStyle.Top;
        _menu.Renderer = new ThemedToolStripRenderer(Theme.Current);
        _menu.Padding = new Padding(4, 2, 0, 2);

        // File
        var file = new ToolStripMenuItem(lang.T("MainMenu/mnuFile", "&File"));
        file.DropDownItems.Add(Item("MainMenu/mnuFileExport", (_, _) => ExportHtml(), Keys.Control | Keys.S));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("MainMenu/mnuFileExit", (_, _) => Close(), Keys.Control | Keys.Q));
        _menu.Items.Add(file);

        // Action
        var action = new ToolStripMenuItem(lang.T("MainMenu/mnuAction", "&Action"));
        if (_mode == ViewMode.Programs)
        {
            action.DropDownItems.Add(Tagged(Item("MainMenu/mnuActionUninstall", (_, _) => UninstallSelected()),
                "uninstall"));
            action.DropDownItems.Add(Tagged(Item("MainMenu/mnuActionQueue",
                (_, _) => OpenUninstallQueue()), "queue"));
            action.DropDownItems.Add(Tagged(Item("MainMenu/mnuActionModify", (_, _) => ModifySelected()), "modify"));
            action.DropDownItems.Add(Tagged(Item("MainMenu/mnuActionDelete", (_, _) => ForceRemoveSelected()), "any"));
            action.DropDownItems.Add(Tagged(Item("MainMenu/mnuActionDeleteBatch", (_, _) => ForceRemoveSelected()),
                "any"));
            action.DropDownItems.Add(new ToolStripSeparator());
            action.DropDownItems.Add(Tagged(Item("MainMenu/mnuActionRemoveEntry", (_, _) => RemoveEntrySelected()),
                "address"));
            action.DropDownItems.Add(Tagged(Item("MainMenu/mnuActionRemoveEntryBatch",
                (_, _) => RemoveEntrySelected()), "address"));
            action.DropDownItems.Add(new ToolStripSeparator());

            var traced = new ToolStripMenuItem(lang.T("MainMenu/mnuTracedData", "Traced Data"));
            traced.DropDownItems.Add(Tagged(Item("MainMenu/mnuTracedDataAsXML", (_, _) => ViewTracedXml()),
                "traced"));
            traced.DropDownItems.Add(Tagged(Item("MainMenu/mnuTracedDataUndoTracing",
                (_, _) => UndoTracing()), "traced"));
            traced.DropDownItems.Add(Tagged(Item("MainMenu/mnuTracedDataAppend",
                (_, _) => AppendTracing()), "single"));
            action.DropDownItems.Add(traced);

            action.DropDownItems.Add(new ToolStripSeparator());
            action.DropDownItems.Add(Tagged(Item("MainMenu/mnuActionJumpToRegistry",
                (_, _) => OpenRegistryEntry()), "registry"));
            action.DropDownItems.Add(Tagged(Item("MainMenu/mnuActionJumpToProgramFolder",
                (_, _) => BrowseLocation()), "folder"));
            action.DropDownItems.Add(Tagged(Item("MainMenu/mnuActionJumpToWebSite",
                (_, _) => OpenWebsite()), "website"));
            action.DropDownItems.Add(Tagged(Item("MainMenu/mnuActionOpenWindowsStore",
                (_, _) => OpenStorePage()), "store"));
            action.DropDownItems.Add(Tagged(Item("MainMenu/mnuActionBrowseLocation",
                (_, _) => BrowseLocation()), "folder"));
            action.DropDownItems.Add(new ToolStripSeparator());
            action.DropDownItems.Add(Item("SecondaryToolbar/btnInstallAndTrace",
                (_, _) => StartInstallTrace(null)));
        }
        else
        {
            action.DropDownItems.Add(Tagged(Item("ExplorerBar/itemDisableRunning",
                (_, _) => ToggleStartupSelected(false)), "startupItem"));
            action.DropDownItems.Add(Tagged(Item("ExplorerBar/itemEnableRunning",
                (_, _) => ToggleStartupSelected(true)), "startupItem"));
            action.DropDownItems.Add(new ToolStripSeparator());
            action.DropDownItems.Add(Tagged(Item("ExplorerBar/itemDeleteStartupItem",
                (_, _) => DeleteStartupSelected()), "startupItem"));
            action.DropDownItems.Add(Tagged(Item("ExplorerBar/itemAddNewStartupItem",
                (_, _) => AddStartup()), "startupAny"));
        }

        _menu.Items.Add(action);

        // View
        var view = new ToolStripMenuItem(lang.T("MainMenu/mnuView", "&View"));
        var mainToolbar = new ToolStripMenuItem(lang.T("MainMenu/mnuViewMainToolbarShow", "Show Main Toolbar"))
        {
            Checked = _toolbar.Visible,
            CheckOnClick = true
        };
        mainToolbar.CheckedChanged += (_, _) => _toolbar.Visible = mainToolbar.Checked;
        view.DropDownItems.Add(mainToolbar);
        var labels = new ToolStripMenuItem(lang.T("MainMenu/mnuViewMainToolbarButtonLabels",
            "Show button labels below icons"))
        {
            Checked = _settings.ToolbarCaptions,
            CheckOnClick = true
        };
        labels.CheckedChanged += (_, _) =>
        {
            _settings.ToolbarCaptions = labels.Checked;
            foreach (ToolStripItem item in _toolbar.Items)
            {
                if (item is ToolStripButton)
                    item.DisplayStyle = labels.Checked
                        ? ToolStripItemDisplayStyle.ImageAndText
                        : ToolStripItemDisplayStyle.Image;
            }
        };
        view.DropDownItems.Add(labels);
        var secondary = new ToolStripMenuItem(lang.T("MainMenu/mnuViewSecondaryToolbar", "Secondary Toolbar"))
        {
            Checked = _categories.Visible,
            CheckOnClick = true
        };
        secondary.CheckedChanged += (_, _) => _categories.Visible = secondary.Checked;
        view.DropDownItems.Add(secondary);
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(Item("MainMenu/mnuViewRefresh", async (_, _) => await RefreshAsync(),
            Keys.F5));
        var reset = Item("MainMenu/mnuViewListColumnsReset", (_, _) => ResetColumns());
        view.DropDownItems.Add(reset);
        view.DropDownItems.Add(new ToolStripSeparator());

        var language = new ToolStripMenuItem(lang.T("MainMenu/mnuViewLanguage", "Interface Language"));
        foreach (var info in LanguageManager.AvailableLanguages())
        {
            var entry = new ToolStripMenuItem(info.Name)
            {
                Checked = string.Equals(Path.GetFileName(info.File), Path.GetFileName(LanguageManager.Instance.LanguageFile),
                    StringComparison.OrdinalIgnoreCase)
            };
            entry.Click += (_, _) => ChangeLanguage(info.File);
            language.DropDownItems.Add(entry);
        }

        view.DropDownItems.Add(language);
        _menu.Items.Add(view);

        // Tools
        var tools = new ToolStripMenuItem(lang.T("MainMenu/mnuTools", "&Tools"));
        var systemTools = new ToolStripMenuItem(lang.T("MainMenu/mnuToolsSystemTools", "System Tools"));
        systemTools.DropDownItems.Add(SystemTool("MainMenu/mnuToolsAddRemove", "appwiz.cpl"));
        systemTools.DropDownItems.Add(SystemTool("MainMenu/mnuToolsComponents", "optionalfeatures.exe"));
        systemTools.DropDownItems.Add(SystemTool("MainMenu/mnuToolsSystemRestore", "rstrui.exe"));
        systemTools.DropDownItems.Add(SystemTool("MainMenu/mnuToolsSecurityCenter", "wscui.cpl"));
        systemTools.DropDownItems.Add(SystemTool("MainMenu/mnuToolsSystemProperties", "sysdm.cpl"));
        systemTools.DropDownItems.Add(SystemTool("MainMenu/mnuToolsSystemInfo", "msinfo32.exe"));
        systemTools.DropDownItems.Add(SystemTool("MainMenu/mnuToolsDefrag", "dfrgui.exe"));
        systemTools.DropDownItems.Add(SystemTool("MainMenu/mnuToolsServices", "services.msc"));
        systemTools.DropDownItems.Add(SystemTool("MainMenu/mnuToolsTaskScheduler", "taskschd.msc"));
        tools.DropDownItems.Add(systemTools);
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(Item("MainMenu/mnuToolsQuarantine",
            (_, _) => ShowQuarantine()));
        tools.DropDownItems.Add(Item("MainMenu/mnuToolsOptions", (_, _) => OpenPreferences()));
        _menu.Items.Add(tools);

        // Help
        var help = new ToolStripMenuItem(lang.T("MainMenu/mnuHelp", "&Help"));
        help.DropDownItems.Add(Item("MainMenu/mnuHelpOnline", (_, _) => OpenHelp(), Keys.F1));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(Item("MainMenu/mnuHelpAbout", (_, _) => ShowAbout()));
        _menu.Items.Add(help);

        _btnUninstaller.Checked = _mode == ViewMode.Programs;
        _btnStartup.Checked = _mode == ViewMode.Startup;
        BuildContextMenu();
    }

    private ToolStripMenuItem Item(string key, EventHandler click, Keys shortcut = Keys.None)
    {
        var text = LanguageManager.Instance.T(key);
        var item = new ToolStripMenuItem(text);
        if (shortcut != Keys.None)
        {
            try
            {
                item.ShortcutKeys = shortcut;
                item.ShowShortcutKeys = true;
            }
            catch (System.ComponentModel.InvalidEnumArgumentException)
            {
                // Combination not representable as a ToolStrip shortcut.
            }
        }

        item.Click += click;
        return item;
    }

    private static ToolStripMenuItem Tagged(ToolStripMenuItem item, string tag)
    {
        item.Tag = tag;
        return item;
    }

    private ToolStripMenuItem SystemTool(string key, string command)
    {
        return Item(key, (_, _) => RunSystemTool(command));
    }

    private void BuildContextMenu()
    {
        var lang = LanguageManager.Instance;
        _context.Items.Clear();
        if (_mode == ViewMode.Programs)
        {
            _context.Items.Add(Tagged(Item("MainMenu/mnuActionUninstall", (_, _) => UninstallSelected()), "uninstall"));
            _context.Items.Add(Tagged(Item("MainMenu/mnuActionQueue",
                (_, _) => OpenUninstallQueue()), "queue"));
            _context.Items.Add(Tagged(Item("MainMenu/mnuActionModify", (_, _) => ModifySelected()), "modify"));
            _context.Items.Add(new ToolStripSeparator());
            _context.Items.Add(Tagged(Item("MainMenu/mnuActionDelete", (_, _) => ForceRemoveSelected()), "any"));
            _context.Items.Add(Tagged(Item("MainMenu/mnuActionDeleteBatch", (_, _) => ForceRemoveSelected()), "any"));
            _context.Items.Add(Tagged(Item("MainMenu/mnuActionRemoveEntry", (_, _) => RemoveEntrySelected()), "address"));
            _context.Items.Add(new ToolStripSeparator());
            _context.Items.Add(Tagged(Item("MainMenu/mnuActionJumpToRegistry", (_, _) => OpenRegistryEntry()), "registry"));
            _context.Items.Add(Tagged(Item("MainMenu/mnuActionJumpToProgramFolder", (_, _) => BrowseLocation()), "folder"));
            _context.Items.Add(Tagged(Item("MainMenu/mnuActionJumpToWebSite", (_, _) => OpenWebsite()), "website"));
            _context.Items.Add(Tagged(Item("MainMenu/mnuActionBrowseLocation", (_, _) => BrowseLocation()), "folder"));
            _context.Items.Add(new ToolStripSeparator());
            _context.Items.Add(Tagged(Item("MainMenu/mnuTracedDataAsXML", (_, _) => ViewTracedXml()), "traced"));
            _context.Items.Add(Tagged(Item("MainMenu/mnuTracedDataUndoTracing", (_, _) => UndoTracing()), "traced"));
        }
        else
        {
            _context.Items.Add(Tagged(Item("ExplorerBar/itemDisableRunning", (_, _) => ToggleStartupSelected(false)), "startupItem"));
            _context.Items.Add(Tagged(Item("ExplorerBar/itemEnableRunning", (_, _) => ToggleStartupSelected(true)), "startupItem"));
            _context.Items.Add(new ToolStripSeparator());
            _context.Items.Add(Tagged(Item("ExplorerBar/itemDeleteStartupItem", (_, _) => DeleteStartupSelected()), "startupItem"));
            _context.Items.Add(Tagged(Item("ExplorerBar/itemAddNewStartupItem", (_, _) => AddStartup()), "startupAny"));
        }

        _context.Opening += (_, _) => UpdateActionStates();
        _list.ContextMenuStrip = _context;
    }

    private void ApplyThemeAndTexts()
    {
        var theme = Theme.Current;
        BackColor = theme.Window;
        ForeColor = theme.Text;
        _menu.Renderer = new ThemedToolStripRenderer(theme);
        _toolbar.Renderer = new ThemedToolStripRenderer(theme);
        _status.Renderer = new ThemedToolStripRenderer(theme);
        _list.BackColor = theme.ListBack;
        _list.ForeColor = theme.Text;
        _menu.BackColor = theme.ToolbarBack;
        _menu.ForeColor = theme.Text;
        _toolbar.BackColor = theme.ToolbarBack;
        _toolbar.ForeColor = theme.Text;
        _status.BackColor = theme.ToolbarBack;
        _status.ForeColor = theme.Text;
        _categories.BackColor = theme.ToolbarBack;
        _explorer.BackColor = theme.Window;

        var lang = LanguageManager.Instance;
        Text = AppInfo.DisplayName;
        _btnUninstaller.Text = lang.T("ToolBar/btnUninstaller", "Uninstaller");
        _btnUninstaller.ToolTipText = lang.T("ToolBar/btnUninstallerToolTip", "Uninstall existing programs or install and trace new software");
        _btnStartup.Text = lang.T("ToolBar/btnStartup", "Startup");
        _btnStartup.ToolTipText = lang.T("ToolBar/btnStartupToolTip", "Applications that automatically run at startup");
        _btnRefresh.Text = lang.T("ToolBar/btnRefresh", "Refresh");
        _btnRefresh.ToolTipText = lang.T("ToolBar/btnRefreshToolTip", "Refresh programs list");
        _btnPreferences.Text = lang.T("ToolBar/btnPrefs", "Preferences");
        _btnPreferences.ToolTipText = lang.T("ToolBar/btnPrefsToolTip", "CleanRift preferences");
        _btnHelp.Text = lang.T("ToolBar/btnHelp", "Help") + " ▾";
        _btnHelp.ToolTipText = lang.T("ToolBar/btnHelpToolTip", "Open the quick start guide");

        if (!_settings.ToolbarCaptions)
        {
            foreach (ToolStripItem item in _toolbar.Items)
                if (item is ToolStripButton)
                    item.DisplayStyle = ToolStripItemDisplayStyle.Image;
        }

        _categories.SetItems(new[]
        {
            new CategoryStrip.Item
            {
                Key = "apps",
                Text = lang.T("SecondaryToolbar/btnApps", "Apps"),
                IconId = AppIcons.Apps,
                ToolTip = lang.T("SecondaryToolbar/btnAppsToolTip", "Applications")
            },
            new CategoryStrip.Item
            {
                Key = "system",
                Text = lang.T("SecondaryToolbar/btnSystem", "System"),
                IconId = AppIcons.System,
                ToolTip = lang.T("SecondaryToolbar/btnSystemToolTip", "System Components and Updates")
            },
            new CategoryStrip.Item
            {
                Key = "hidden",
                Text = lang.T("SecondaryToolbar/btnHidden", "Hidden"),
                IconId = AppIcons.Hidden,
                ToolTip = lang.T("SecondaryToolbar/btnHiddenToolTip", "Hidden Components")
            },
            new CategoryStrip.Item
            {
                Key = "store",
                Text = lang.T("SecondaryToolbar/btnStoreApps", "Microsoft Store Apps"),
                IconId = AppIcons.Store,
                ToolTip = lang.T("SecondaryToolbar/btnStoreAppsToolTip", "Apps installed from Microsoft Store")
            },
            new CategoryStrip.Item
            {
                Key = "trace",
                Text = lang.T("SecondaryToolbar/btnInstallAndTrace", "Install and trace"),
                IconId = AppIcons.InstallTrace,
                ToolTip = lang.T("SecondaryToolbar/btnInstallAndTraceTooltip",
                    "Install a new program and watch all changes it makes to file system and registry"),
                IsAction = true
            }
        });

        _categories.Select(_category);
        _explorer.RefreshCueBanner();
        _tray.Visible = _settings.TrayIcon;
        UpdateColumnHeaders();
        UpdateStatus();
    }

    private void UpdateColumnHeaders()
    {
        var lang = LanguageManager.Instance;
        if (_mode == ViewMode.Programs)
        {
            if (_list.Columns.Count != 4)
            {
                _list.Columns.Clear();
                _list.Columns.Add("", 340);
                _list.Columns.Add("", 100, HorizontalAlignment.Right);
                _list.Columns.Add("", 110);
                _list.Columns.Add("", 70, HorizontalAlignment.Center);
            }

            _list.Columns[0].Text = lang.T("ListCtrl/columnProgramName", "Program Name");
            _list.Columns[1].Text = lang.T("ListCtrl/columnSize", "Size");
            _list.Columns[2].Text = lang.T("ListCtrl/columnInstallationDate", "Installed On");
            _list.Columns[3].Text = lang.T("ListCtrl/columnTraced", "Traced");
            ApplyColumnWidths();
        }
        else
        {
            _list.Columns.Clear();
            _list.Columns.Add("", Math.Max(200, _settings.StartupColumnWidth));
            _list.Columns.Add("", 110);
            _list.Columns.Add("", 180);
            _list.Columns.Add("", 140);
            _list.Columns.Add("", 80);
            _list.Columns[0].Text = lang.T("ListCtrl/columnProgramName", "Program Name");
            _list.Columns[1].Text = lang.T("ListCtrl/columnStatus", "Status");
            _list.Columns[2].Text = lang.T("ListCtrl/columnSource", "Source");
            _list.Columns[3].Text = lang.T("ListCtrl/columnCompany", "Company");
            _list.Columns[4].Text = lang.T("ListCtrl/columnImpact", "Startup impact");
            AdjustStartupColumns();
        }
    }

    private void AdjustStartupColumns()
    {
        if (_mode != ViewMode.Startup || _list.Columns.Count != 5)
            return;

        // Status, source, publisher and impact are proportional to the list
        // width so the name column keeps room at the 780 px minimum.
        var width = _list.ClientSize.Width;
        var status = Math.Clamp(width / 7, 80, 110);
        var source = Math.Clamp(width / 5, 100, 190);
        var company = Math.Clamp(width / 8, 70, 140);
        var impact = Math.Clamp(width / 9, 70, 100);
        var name = Math.Max(120, width - status - source - company - impact - 4);

        _list.Columns[0].Width = name;
        _list.Columns[1].Width = status;
        _list.Columns[2].Width = source;
        _list.Columns[3].Width = company;
        _list.Columns[4].Width = impact;
    }

    private void ApplyColumnWidths()
    {
        if (_mode != ViewMode.Programs || _list.Columns.Count != 4)
            return;
        _list.Columns[0].Width = Math.Max(180, _settings.ColumnNameWidth);
        _list.Columns[1].Width = Math.Max(60, _settings.ColumnSizeWidth);
        _list.Columns[2].Width = Math.Max(70, _settings.ColumnDateWidth);
        _list.Columns[3].Width = Math.Max(50, _settings.ColumnTracedWidth);
        AdjustProgramColumns();
    }

    /// <summary>Keeps the program name column filling the remaining list width.</summary>
    private void AdjustProgramColumns()
    {
        if (_mode != ViewMode.Programs || _list.Columns.Count != 4)
            return;

        var fixedWidth = _list.Columns[1].Width + _list.Columns[2].Width + _list.Columns[3].Width;
        var available = _list.ClientSize.Width - fixedWidth - 4;
        if (available > 180 && _list.Columns[0].Width != available)
            _list.Columns[0].Width = available;
    }

    private void UpdateExplorerWidth()
    {
        // The explorer occupies about 28% of the client width.
        var width = Math.Clamp(ClientSize.Width * 28 / 100, 240, 330);
        if (_explorer.Width != width)
            _explorer.Width = width;
    }

    private void UpdateCategorySelection()
    {
        _categories.Select(_mode == ViewMode.Programs ? _category : "");
        _categories.Visible = _mode == ViewMode.Programs;

        _toolbar.Height = 64;
        foreach (ToolStripItem item in _toolbar.Items)
        {
            if (item is not ToolStripButton button)
                continue;
            button.DisplayStyle = _settings.ToolbarCaptions
                ? ToolStripItemDisplayStyle.ImageAndText
                : ToolStripItemDisplayStyle.Image;
            button.ImageAlign = _settings.ToolbarCaptions
                ? ContentAlignment.TopCenter
                : ContentAlignment.MiddleCenter;
        }
    }

    private async Task RefreshAsync()
    {
        if (_mode == ViewMode.Programs)
            await RefreshProgramsAsync();
        else
            await RefreshStartupAsync();
    }

    private async Task RefreshProgramsAsync()
    {
        if (_scanning)
        {
            // A refresh was requested while a scan was running: run it once
            // the current scan finishes instead of silently ignoring it.
            _refreshPending = true;
            return;
        }

        _refreshPending = false;
        _scanning = true;
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;
        var selected = SelectedPrograms().Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var lang = LanguageManager.Instance;
        _statusLabel.Text = lang.T("ListCtrl/staticLoading", "Loading, please wait...");
        _sizeLabel.Text = "";
        _explorer.ShowNothing(lang.T("ListCtrl/staticLoading", "Loading, please wait..."));
        Cursor = Cursors.WaitCursor;

        var progress = new Progress<string>(text =>
        {
            if (!_closing)
                _statusLabel.Text = text;
        });
        void OnProgress(string text) => ((IProgress<string>)progress).Report(text);
        _scanner.Progress += OnProgress;

        try
        {
            var programs = await Task.Run(() => _scanner.Scan(includeStore: true, token), token);
            token.ThrowIfCancellationRequested();
            if (_closing)
                return;

            _programs = programs;
            ProgramScanner.ApplyState(_programs, _settings);
            ProgramScanner.UpdateKnownDates(_programs, _settings);
            ApplyCachedData();

            _statusLabel.Text = lang.T("ListCtrl/staticLoading", "Loading, please wait...");
            await Task.Run(() => _icons.Preload(_programs), token);
            token.ThrowIfCancellationRequested();
            if (_closing)
                return;

            foreach (var program in _programs)
                program.Icon ??= _icons.Resolve(program);

            RebuildList();
            RestoreSelection(selected);
            SelectFirstIfNone();
            UpdateCounts();
            UpdateStatus();
            _ = ComputeSizesAsync();
        }
        catch (OperationCanceledException)
        {
            // Closing or a newer scan superseded this one.
        }
        catch (Exception ex)
        {
            if (!_closing && !IsDisposed)
                MessageDialog.Error(this, "CleanRift", ex.Message);
        }
        finally
        {
            _scanner.Progress -= OnProgress;
            _scanning = false;
            if (!_closing && !IsDisposed)
            {
                Cursor = Cursors.Default;
                if (_refreshPending)
                {
                    _refreshPending = false;
                    _ = RefreshProgramsAsync();
                }
            }
        }
    }

    private async Task RefreshStartupAsync()
    {
        if (_startupScanning)
        {
            _startupRefreshPending = true;
            return;
        }

        _startupRefreshPending = false;
        _startupScanning = true;
        var lang = LanguageManager.Instance;
        _statusLabel.Text = lang.T("ListCtrl/staticLoading", "Loading, please wait...");
        Cursor = Cursors.WaitCursor;
        var progress = new Progress<string>(text =>
        {
            if (!_closing)
                _statusLabel.Text = text;
        });
        void OnProgress(string text) => ((IProgress<string>)progress).Report(text);
        _startupScanner.Progress += OnProgress;
        try
        {
            _startup = await Task.Run(() => _startupScanner.Scan());
            if (_closing)
                return;
            RebuildList();
            SelectFirstIfNone();
            UpdateCounts();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            if (!_closing && !IsDisposed)
                MessageDialog.Error(this, "CleanRift", ex.Message);
        }
        finally
        {
            _startupScanner.Progress -= OnProgress;
            _startupScanning = false;
            if (!_closing && !IsDisposed)
            {
                Cursor = Cursors.Default;
                if (_startupRefreshPending)
                {
                    _startupRefreshPending = false;
                    _ = RefreshStartupAsync();
                }
            }
        }
    }

    private void ApplyCachedData()
    {
        _cachedPrograms ??= UninstallCache.TryLoad()?.ToList();
        if (_cachedPrograms == null || _cachedPrograms.Count == 0)
            return;

        var byId = new Dictionary<string, InstalledProgram>(StringComparer.OrdinalIgnoreCase);
        foreach (var cached in _cachedPrograms)
        {
            if (!string.IsNullOrEmpty(cached.Id))
                byId[cached.Id] = cached;
        }

        foreach (var program in _programs)
        {
            if (program.IsChild || string.IsNullOrEmpty(program.Id))
                continue;
            if (!byId.TryGetValue(program.Id, out var cached))
                continue;
            // Reuse a size only when the cached entry still describes the same
            // product; the registry display name is a cheap identity check.
            if (!string.Equals(program.DisplayName, cached.DisplayName, StringComparison.Ordinal))
                continue;
            if (!string.Equals(program.InstallLocation, cached.InstallLocation,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (program.SizeBytes == 0 && cached.SizeBytes > 0)
            {
                program.SizeBytes = cached.SizeBytes;
                program.SizeComputed = cached.SizeComputed;
                program.EstimatedSizeKb ??= cached.EstimatedSizeKb;
            }

            program.InstallDate ??= cached.InstallDate;
        }
    }

    private async Task ComputeSizesAsync()
    {
        _sizeCts?.Cancel();
        _sizeCts?.Dispose();
        _sizeCts = new CancellationTokenSource();
        var token = _sizeCts.Token;
        var programs = _programs;
        var pending = programs.Count(p => p.SizeBytes == 0 && !string.IsNullOrWhiteSpace(p.InstallLocation));
        if (pending == 0)
        {
            UninstallCache.Save(programs);
            return;
        }

        var lang = LanguageManager.Instance;
        var progress = new Progress<string>(name =>
        {
            if (!_closing)
                _sizeLabel.Text = lang.F("Misc/statusCalculatingOccupiedSize", name);
        });
        try
        {
            await _scanner.ComputeSizesAsync(programs, progress, token);
            if (_closing)
                return;
            if (!token.IsCancellationRequested)
            {
                _sizeLabel.Text = "";
                UpdateStatus();
                UpdateVisibleSizeTexts();
                UninstallCache.Save(programs);
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch
        {
            // Size calculation is best-effort; never surface it as fatal.
        }
    }

    private void UpdateVisibleSizeTexts()
    {
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is InstalledProgram program && _mode == ViewMode.Programs)
            {
                if (item.SubItems.Count > 1)
                    item.SubItems[1].Text = program.SizeText;
            }
        }
    }

    private void RebuildList()
    {
        var selected = SelectedPrograms().Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _list.BeginUpdate();
        _list.Items.Clear();
        _listIcons.Images.Clear();

        if (_mode == ViewMode.Programs)
            PopulatePrograms();
        else
            PopulateStartup();

        _list.EndUpdate();
        if (selected.Count > 0)
            RestoreSelection(selected);
        OnSelectionChanged();
    }

    private bool MatchesCategory(InstalledProgram program)
    {
        return _category switch
        {
            "system" => program.Category == ProgramCategory.System || program.IsUpdate,
            "hidden" => program.IsHidden,
            "store" => program.IsStoreApp,
            "recent" => program.IsRecent,
            _ => program.Category == ProgramCategory.Apps
        };
    }

    private bool MatchesFilter(InstalledProgram program)
    {
        if (string.IsNullOrWhiteSpace(_filter))
            return true;
        var f = _filter.Trim();
        return program.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
               program.Publisher.Contains(f, StringComparison.OrdinalIgnoreCase) ||
               program.DisplayVersion.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private void PopulatePrograms()
    {
        var lang = LanguageManager.Instance;
        var visible = _programs.Where(p => MatchesCategory(p) && MatchesFilterOrChild(p)).ToList();
        var visibleIds = visible.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parents = visible.Where(p => !p.IsChild || !visibleIds.Contains(p.GroupKey)).ToList();
        var filterActive = !string.IsNullOrWhiteSpace(_filter);
        parents = SortPrograms(parents);

        foreach (var parent in parents)
        {
            var children = _programs.Where(c => c.IsChild && c.GroupKey == parent.Id)
                .Where(c => !filterActive || MatchesFilter(c))
                .ToList();
            var hasChildren = children.Count > 0;
            var expanded = !_collapsed.Contains(parent.Id) && parent.IsGroupExpanded;
            if (filterActive)
                expanded = true;

            var name = parent.DisplayName;
            if (hasChildren && _category != "system")
                name = (expanded ? "▾ " : "▸ ") + name;

            AddProgramItem(parent, name, indent: 0);
            if (hasChildren && expanded)
            {
                foreach (var child in SortPrograms(children))
                    AddProgramItem(child, child.DisplayName, indent: 1);
            }
        }

        if (_list.Items.Count == 0)
        {
            var text = filterActive
                ? lang.F("ExplorerBar/itemNoItemsMatchingFilter", _filter, _category)
                : lang.F("ListCtrl/staticNoProgramsInCategory", CategoryDisplayName());
            var empty = new ListViewItem(text) { ForeColor = Theme.Current.Muted };
            _list.Items.Add(empty);
        }
    }

    private bool MatchesFilterOrChild(InstalledProgram program)
    {
        if (string.IsNullOrWhiteSpace(_filter))
            return true;
        if (MatchesFilter(program))
            return true;
        if (program.IsChild)
            return false;
        return _programs.Any(c => c.IsChild && c.GroupKey == program.Id && MatchesFilter(c));
    }

    private string CategoryDisplayName()
    {
        var lang = LanguageManager.Instance;
        return _category switch
        {
            "system" => lang.T("SecondaryToolbar/btnSystem", "System"),
            "hidden" => lang.T("SecondaryToolbar/btnHidden", "Hidden"),
            "store" => lang.T("SecondaryToolbar/btnStoreApps", "Microsoft Store Apps"),
            "recent" => lang.T("SecondaryToolbar/btnRecentlyInstalled", "Recently Installed"),
            _ => lang.T("SecondaryToolbar/btnApps", "Apps")
        };
    }

    private List<InstalledProgram> SortPrograms(List<InstalledProgram> list)
    {
        Func<InstalledProgram, object> key = _sortColumn switch
        {
            1 => p => p.SizeBytes,
            2 => p => p.InstallDate ?? DateTime.MinValue,
            3 => p => p.IsTraced ? 1 : 0,
            _ => p => p.SortName
        };

        var comparer = Comparer<object>.Create((a, b) =>
        {
            var result = a is string sa && b is string sb
                ? string.Compare(sa, sb, StringComparison.CurrentCultureIgnoreCase)
                : Comparer<object>.Default.Compare(a, b);
            return _sortAscending ? result : -result;
        });

        return list.OrderBy(key, comparer).ToList();
    }

    private List<StartupItem> SortStartup(List<StartupItem> list)
    {
        Func<StartupItem, object> key = _sortColumn switch
        {
            1 => p => p.DisplayStatus,
            2 => p => p.SourceText,
            3 => p => p.Publisher,
            4 => p => p.Impact,
            _ => p => p.Name
        };

        var comparer = Comparer<object>.Create((a, b) =>
        {
            var result = a is string sa && b is string sb
                ? string.Compare(sa, sb, StringComparison.CurrentCultureIgnoreCase)
                : Comparer<object>.Default.Compare(a, b);
            return _sortAscending ? result : -result;
        });

        return list.OrderBy(key, comparer).ToList();
    }

    private static string ImpactText(StartupImpact impact)
    {
        var lang = LanguageManager.Instance;
        return impact switch
        {
            StartupImpact.High => lang.T("ListCtrl/impactHigh", "High"),
            StartupImpact.Medium => lang.T("ListCtrl/impactMedium", "Medium"),
            _ => lang.T("ListCtrl/impactLow", "Low")
        };
    }

    private void AddProgramItem(InstalledProgram program, string text, int indent)
    {
        // Append the "(32-bit)" marker to 32-bit entries when "Mark 32-bit
        // apps" is enabled; 64-bit rows stay unmarked.
        if (_settings.Mark32Bit && !program.IsChild && !program.IsStoreApp && !program.Is64Bit)
            text += " " + LanguageManager.Instance.T("ListCtrl/mark32bitItem", "(32-bit)");

        var item = new ListViewItem(text)
        {
            Tag = program,
            IndentCount = indent,
            ToolTipText = program.DisplayName
        };
        item.SubItems.Add(program.SizeText);
        item.SubItems.Add(program.InstallDateText);
        item.SubItems.Add(program.IsTraced ? "✓" : "");
        if (program.Mark32Bit() && _settings.Mark32Bit)
            item.ToolTipText += "  " + LanguageManager.Instance.T("ListCtrl/mark32bitItem", "(32-bit)");

        var icon = program.Icon ?? _appIcon;
        if (icon != null)
        {
            try
            {
                using var bitmap = icon.ToBitmap();
                using var scaled = new Bitmap(bitmap, new Size(IconSize, IconSize));
                _listIcons.Images.Add(scaled);
                item.ImageIndex = _listIcons.Images.Count - 1;
            }
            catch
            {
                item.ImageIndex = -1;
            }
        }

        if (program.IsTraced)
            item.ForeColor = Theme.Current.Text;
        _list.Items.Add(item);
    }

    private void PopulateStartup()
    {
        var filter = _filter.Trim();
        var items = SortStartup(_startup
            .Where(i => string.IsNullOrWhiteSpace(filter) ||
                        i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        i.Command.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        i.SourceText.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList());

        foreach (var startup in items)
        {
            var item = new ListViewItem(startup.Name)
            {
                Tag = startup,
                ToolTipText = startup.Command
            };
            item.SubItems.Add(startup.DisplayStatus);
            item.SubItems.Add(startup.SourceText);
            item.SubItems.Add(startup.Publisher);
            item.SubItems.Add(ImpactText(startup.Impact));
            if (!startup.Enabled)
                item.ForeColor = Theme.Current.Muted;

            var icon = startup.Icon ?? _appIcon;
            if (icon != null)
            {
                try
                {
                    using var bitmap = icon.ToBitmap();
                    using var scaled = new Bitmap(bitmap, new Size(IconSize, IconSize));
                    _listIcons.Images.Add(scaled);
                    item.ImageIndex = _listIcons.Images.Count - 1;
                }
                catch
                {
                    item.ImageIndex = -1;
                }
            }

            _list.Items.Add(item);
        }

        if (_list.Items.Count == 0)
        {
            _list.Items.Add(new ListViewItem(string.IsNullOrWhiteSpace(filter)
                ? LanguageManager.Instance.T("ListCtrl/staticNoStartupItems",
                    "There're no programs that automatically run on Windows start")
                : LanguageManager.Instance.F("ExplorerBar/itemNoItemsMatchingFilter", filter,
                    LanguageManager.Instance.T("ToolBar/btnStartup", "Startup")))
            {
                ForeColor = Theme.Current.Muted
            });
        }
    }

    private void RestoreSelection(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _list.BeginUpdate();
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is InstalledProgram program && set.Contains(program.Id))
                item.Selected = true;
            else if (item.Tag is StartupItem startup && set.Contains(startup.Id))
                item.Selected = true;
        }

        _list.EndUpdate();
    }

    private void SelectFirstIfNone()
    {
        if (_list.Items.Count == 0 || _list.SelectedItems.Count > 0)
            return;

        var first = _list.Items[0];
        first.Selected = true;
        first.Focused = true;
        _list.EnsureVisible(0);
        OnSelectionChanged();
    }

    private List<InstalledProgram> SelectedPrograms()
    {
        if (_mode != ViewMode.Programs)
            return new List<InstalledProgram>();
        return _list.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag as InstalledProgram)
            .Where(p => p != null)
            .Select(p => p!)
            .ToList();
    }

    private List<StartupItem> SelectedStartupItems()
    {
        if (_mode != ViewMode.Startup)
            return new List<StartupItem>();
        return _list.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag as StartupItem)
            .Where(i => i != null)
            .Select(i => i!)
            .ToList();
    }

    private void OnSelectionChanged()
    {
        if (_mode == ViewMode.Programs)
        {
            var selected = SelectedPrograms();
            if (selected.Count == 0)
            {
                _explorer.ShowNothing(LanguageManager.Instance.T("ExplorerBar/itemNothingFound",
                    "Nothing found!"));
            }
            else
            {
                _explorer.ShowPrograms(selected);
            }
        }
        else
        {
            var selected = SelectedStartupItems();
            _explorer.ShowStartup(selected.Count > 0 ? selected[0] : null, selected);
        }

        UpdateActionStates();
    }

    private void UpdateActionStates()
    {
        var programs = SelectedPrograms();
        var startup = SelectedStartupItems();
        var single = programs.Count == 1 ? programs[0] : null;
        var startupSingle = startup.Count == 1 ? startup[0] : null;
        var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["any"] = programs.Count > 0 || startup.Count > 0,
            ["single"] = single != null || startupSingle != null,
            ["uninstall"] = single != null && !single.NoRemove && !string.IsNullOrWhiteSpace(single.UninstallString),
            ["queue"] = programs.Count(p => !p.NoRemove &&
                (!string.IsNullOrWhiteSpace(p.UninstallString) || p.IsStoreApp)) > 1,
            ["modify"] = single != null && !string.IsNullOrWhiteSpace(single.ModifyPath),
            ["address"] = programs.Count > 0 && programs.All(p => p.Source != ProgramSource.Store),
            ["traced"] = single?.IsTraced == true,
            ["registry"] = single != null && !string.IsNullOrWhiteSpace(single.RegistryPath),
            ["folder"] = single != null && FolderFor(single) != null,
            ["website"] = WebsiteFor(single) != null,
            ["store"] = single?.IsStoreApp == true,
            ["startupItem"] = startup.Count > 0,
            ["startupAny"] = _mode == ViewMode.Startup
        };

        ApplyStates(_menu.Items, states);
        ApplyStates(_context.Items, states);
    }

    private static void ApplyStates(ToolStripItemCollection items, Dictionary<string, bool> states)
    {
        foreach (ToolStripItem item in items)
        {
            if (item.Tag is string tag && states.TryGetValue(tag, out var enabled))
                item.Enabled = enabled;
            if (item is ToolStripMenuItem menu && menu.DropDownItems.Count > 0)
                ApplyStates(menu.DropDownItems, states);
        }
    }

    private void OnSearchChanged(string text)
    {
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        if (e.Column == _sortColumn)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = e.Column;
            _sortAscending = true;
        }

        RebuildList();
    }

    private void OnListDoubleClick()
    {
        if (_mode != ViewMode.Programs)
        {
            ToggleStartupSelected(!SelectedStartupItems().FirstOrDefault()?.Enabled ?? true);
            return;
        }

        var program = SelectedPrograms().FirstOrDefault();
        if (program == null)
            return;
        if (program.IsGroupParent)
        {
            ToggleGroup(program);
            return;
        }

        UninstallSelected();
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Left or Keys.Right && _mode == ViewMode.Programs)
        {
            var program = SelectedPrograms().FirstOrDefault();
            if (program is { IsGroupParent: true })
            {
                ToggleGroup(program, expand: e.KeyCode == Keys.Right);
                e.Handled = true;
            }
        }
        else if (e.KeyCode == Keys.Enter)
        {
            if (_mode == ViewMode.Programs)
                UninstallSelected();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Delete)
        {
            if (_mode == ViewMode.Programs)
                ForceRemoveSelected();
            else
                DeleteStartupSelected();
            e.Handled = true;
        }
    }

    private void OnListMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
            return;
        var info = _list.HitTest(e.Location);
        if (info.Item != null && !info.Item.Selected)
        {
            _list.SelectedItems.Clear();
            info.Item.Selected = true;
        }
    }

    private void ToggleGroup(InstalledProgram program, bool? expand = null)
    {
        var currentlyCollapsed = _collapsed.Contains(program.Id) || !program.IsGroupExpanded;
        var shouldExpand = expand ?? currentlyCollapsed;
        if (shouldExpand)
        {
            _collapsed.Remove(program.Id);
            program.IsGroupExpanded = true;
        }
        else
        {
            _collapsed.Add(program.Id);
            program.IsGroupExpanded = false;
        }

        RebuildList();
        RestoreSelection(new[] { program.Id });
    }

    private void SwitchMode(ViewMode mode)
    {
        if (_mode == mode)
            return;
        _mode = mode;
        _filter = "";
        _explorer.SearchText = "";
        _btnUninstaller.Checked = mode == ViewMode.Programs;
        _btnStartup.Checked = mode == ViewMode.Startup;
        UpdateColumnHeaders();
        BuildMenu();
        BuildContextMenu();
        UpdateCategorySelection();
        RebuildList();
        UpdateStatus();
        _ = RefreshAsync();
    }

    private void UpdateCounts()
    {
        if (_mode == ViewMode.Programs)
        {
            _categories.UpdateCount("apps", _programs.Count(p => p.Category == ProgramCategory.Apps));
            _categories.UpdateCount("system", _programs.Count(p => p.Category == ProgramCategory.System || p.IsUpdate));
            _categories.UpdateCount("hidden", _programs.Count(p => p.IsHidden));
            _categories.UpdateCount("store", _programs.Count(p => p.IsStoreApp));
            _categories.UpdateCount("recent", _programs.Count(p => p.IsRecent));
        }
        else
        {
            _categories.UpdateCount("apps", 0);
            _categories.UpdateCount("system", 0);
            _categories.UpdateCount("hidden", 0);
            _categories.UpdateCount("store", 0);
            _categories.UpdateCount("recent", 0);
        }
    }

    private void UpdateStatus()
    {
        var lang = LanguageManager.Instance;
        if (_mode == ViewMode.Programs)
        {
            var visible = _programs.Where(MatchesCategory).ToList();
            var size = visible.Where(p => !p.IsChild).Sum(p => p.SizeBytes);
            var sizeText = size > 0 ? InstalledProgram.FormatSize(size)
                : lang.T("Misc/statusUnknown", "unknown");
            _statusLabel.Text = lang.F("Misc/statusTotalInfo", visible.Count, sizeText);
            _recentLink.Text = lang.T("Misc/statusRecentItems", "recently installed");
            _recentLink.ToolTipText = lang.T("Misc/statusRecentItemsTip",
                "View recently installed/modified programs");
        }
        else
        {
            _statusLabel.Text = lang.F("Misc/statusStartupTotalInfo", _startup.Count,
                _startup.Count(i => i.Running));
            _recentLink.Text = "";
            _recentLink.ToolTipText = "";
        }
    }

    private void OnExplorerAction(string action)
    {
        switch (action)
        {
            case "uninstall":
                UninstallSelected();
                break;
            case "modify":
                ModifySelected();
                break;
            case "force":
                ForceRemoveSelected();
                break;
            case "removeentry":
                RemoveEntrySelected();
                break;
            case "registry":
                OpenRegistryEntry();
                break;
            case "browse":
                BrowseLocation();
                break;
            case "website":
                OpenWebsite();
                break;
            case "store":
                OpenStorePage();
                break;
            case "addstartup":
                AddStartup();
                break;
            case "enable":
            case "batchenable":
                ToggleStartupSelected(true);
                break;
            case "disable":
            case "batchdisable":
                ToggleStartupSelected(false);
                break;
            case "delete":
            case "batchdelete":
                DeleteStartupSelected();
                break;
        }
    }

    // -------------------- Program actions --------------------

    private void UninstallSelected()
    {
        var selected = SelectedPrograms();
        if (selected.Count == 0)
            return;

        var uninstallable = selected.Where(p => !p.NoRemove &&
                                                (!string.IsNullOrWhiteSpace(p.UninstallString) ||
                                                 p.IsStoreApp)).ToList();
        if (uninstallable.Count == 0)
        {
            var lang = LanguageManager.Instance;
            var result = MessageDialog.Show(this,
                lang.T("UninstallWizard/UninstallWizardLocalizedName", "Removal Wizard"),
                lang.T("ExplorerBar/itemNoUninstall",
                    "The program cannot be uninstalled in a normal way, you can try Force Removal instead."),
                lang.T("Messages/msgForceRemovalDescription",
                    "Force removal will not run the program's own uninstaller; it will only erase the file system and registry items that belong to it."),
                MessageBoxButtons.YesNo, AppIcons.Warning);
            if (result.Result == DialogResult.Yes)
                ForceRemove(selected);
            return;
        }

        using var wizard = new UninstallWizardForm(uninstallable, _settings);
        wizard.ShowDialog(this);
        if (wizard.ProgramsRemoved)
            _ = RefreshProgramsAsync();
    }

    private void OpenUninstallQueue()
    {
        var selected = SelectedPrograms()
            .Where(p => !p.NoRemove &&
                        (!string.IsNullOrWhiteSpace(p.UninstallString) || p.IsStoreApp))
            .ToList();
        if (selected.Count == 0)
            return;

        using var queue = new UninstallQueueForm(selected, _settings);
        queue.ShowDialog(this);
        if (queue.Removed)
            _ = RefreshProgramsAsync();
    }

    private void ModifySelected()
    {
        var program = SelectedPrograms().FirstOrDefault();
        if (program == null || string.IsNullOrWhiteSpace(program.ModifyPath))
            return;

        var (fileName, arguments) = UninstallRunner.SplitCommand(program.ModifyPath);
        if (string.IsNullOrWhiteSpace(fileName))
            return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                WorkingDirectory = Directory.Exists(program.InstallLocation)
                    ? program.InstallLocation
                    : Environment.CurrentDirectory
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageDialog.Error(this, AppInfo.Name, ex.Message);
        }
    }

    private void ForceRemoveSelected()
    {
        var selected = SelectedPrograms();
        if (selected.Count == 0)
            return;
        ForceRemove(selected);
    }

    private void ForceRemove(IReadOnlyList<InstalledProgram> selected)
    {
        using var form = new ForceRemovalForm(selected, _settings);
        form.ShowDialog(this);
        if (form.Removed)
            _ = RefreshProgramsAsync();
    }

    private void RemoveEntrySelected()
    {
        var selected = SelectedPrograms();
        if (selected.Count == 0)
            return;

        var lang = LanguageManager.Instance;
        var result = MessageDialog.Show(this,
            lang.T("MainMenu/mnuActionRemoveEntry", "Remove Entry").Replace("&", ""),
            lang.T("Messages/msgRemoveEntryQuestion", "Are you sure you want to remove this entry?"),
            lang.T("Messages/msgRemoveEntryDescription",
                "This function removes the program's uninstall registry entry so it no longer appears in the list. " +
                "With quarantine enabled, the entry can be restored from Tools > Quarantine and undo."),
            MessageBoxButtons.YesNo, AppIcons.Warning);
        if (result.Result != DialogResult.Yes)
            return;

        var items = selected
            .Where(p => !string.IsNullOrWhiteSpace(p.RegistryPath))
            .Select(p => new RemovalJobItem { Kind = "key", Path = p.RegistryPath })
            .ToList();
        var engine = new RemovalEngine(_settings.QuarantineRemovals,
            _settings.BackupRegistry, _settings.CreateRestorePoint,
            "Remove entry: " + string.Join(", ",
                selected.Take(3).Select(p => p.DisplayName)));
        var removed = engine.RemoveItems(items, CancellationToken.None);
        if (removed.Failed > 0)
        {
            MessageDialog.Error(this, AppInfo.Name,
                LanguageManager.Instance.T("Messages/msgRemoveEntryFailed",
                    "Failed to delete the registry entry of this item.") + Environment.NewLine +
                string.Join(Environment.NewLine, removed.Errors.Take(8)));
        }

        foreach (var program in selected)
            _settings.HiddenProgramIds.Remove(program.Id);

        _settings.Save();
        _ = RefreshProgramsAsync();
    }

    private void ViewTracedXml()
    {
        var program = SelectedPrograms().FirstOrDefault();
        if (program == null)
            return;
        var traces = TraceStore.LoadAll();
        var trace = TraceStore.FindFor(traces, program.Id, program.DisplayName);
        if (trace == null)
        {
            MessageDialog.Info(this, LanguageManager.Instance.T("MainMenu/mnuTracedData", "Traced Data"),
                LanguageManager.Instance.T("Messages/msgNoTracedData", "This program has no traced data."));
            return;
        }

        using var form = new TracedXmlForm(trace);
        form.ShowDialog(this);
    }

    private void UndoTracing()
    {
        var program = SelectedPrograms().FirstOrDefault();
        if (program == null)
            return;
        var result = MessageDialog.Confirm(this, "Undo Tracing",
            LanguageManager.Instance.T("Messages/msgUndoTracing",
                "Are you sure you want CleanRift to forget all traced data of this program?"),
            "");
        if (result.Result != DialogResult.Yes)
            return;
        TraceStore.Delete(program.Id, program.TraceFile);
        _ = RefreshProgramsAsync();
    }

    private void AppendTracing()
    {
        var program = SelectedPrograms().FirstOrDefault();
        if (program == null)
            return;
        using var form = new InstallTrackerForm(_settings, program);
        form.ShowDialog(this);
        if (form.Traced)
            _ = RefreshProgramsAsync();
    }

    private void StartInstallTrace(InstalledProgram? appendTo, string? initialPath = null)
    {
        using var form = new InstallTrackerForm(_settings, appendTo, initialPath);
        if (form.ShowDialog(this) == DialogResult.OK || form.Traced)
            _ = RefreshProgramsAsync();
    }

    private void OpenRegistryEntry()
    {
        var program = SelectedPrograms().FirstOrDefault();
        if (program == null || string.IsNullOrWhiteSpace(program.RegistryPath))
            return;
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
            {
                key?.SetValue("LastKey", "Computer\\" + program.RegistryPath);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = SystemToolPath("regedit.exe"),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageDialog.Error(this, AppInfo.Name, ex.Message);
        }
    }

    private void BrowseLocation()
    {
        var program = SelectedPrograms().FirstOrDefault();
        if (program == null)
            return;
        var folder = FolderFor(program);
        if (folder == null)
        {
            MessageDialog.Info(this, AppInfo.Name,
                LanguageManager.Instance.T("Messages/msgCannotOpenFolder",
                    "Cannot open the program folder."));
            return;
        }

        try
        {
            if (_settings.TotalCommander)
            {
                var tc = FindTotalCommander();
                if (tc != null)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = tc,
                        Arguments = $"/O /T \"{folder}\"",
                        UseShellExecute = true
                    });
                    return;
                }
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = SystemToolPath("explorer.exe"),
                Arguments = "\"" + folder + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageDialog.Error(this, AppInfo.Name, ex.Message);
        }
    }

    private static string? FindTotalCommander()
    {
        string[] candidates =
        {
            @"C:\Program Files\totalcmd\TOTALCMD64.EXE",
            @"C:\Program Files (x86)\totalcmd\TOTALCMD.EXE",
            @"C:\totalcmd\TOTALCMD64.EXE"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FolderFor(InstalledProgram program)
    {
        if (!string.IsNullOrWhiteSpace(program.InstallLocation) &&
            Directory.Exists(program.InstallLocation))
            return program.InstallLocation;

        if (!string.IsNullOrWhiteSpace(program.DisplayIcon))
        {
            var icon = program.DisplayIcon.Trim().Trim('"');
            var comma = icon.LastIndexOf(',');
            if (comma > 2 && int.TryParse(icon[(comma + 1)..].Trim(), out _))
                icon = icon[..comma];
            var dir = Path.GetDirectoryName(icon);
            if (dir != null && Directory.Exists(dir))
                return dir;
        }

        return null;
    }

    private void OpenWebsite()
    {
        var url = WebsiteFor(SelectedPrograms().FirstOrDefault());
        if (url != null)
            OpenUrl(url);
    }

    private static string? WebsiteFor(InstalledProgram? program)
    {
        if (program == null)
            return null;
        if (IsWebUrl(program.UrlInfoAbout))
            return program.UrlInfoAbout;
        return IsWebUrl(program.HelpLink) ? program.HelpLink : null;
    }

    private static bool IsWebUrl(string? url)
    {
        return Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private void OpenStorePage()
    {
        var program = SelectedPrograms().FirstOrDefault();
        if (program == null)
            return;
        var query = Uri.EscapeDataString(program.DisplayName);
        OpenShellTarget("ms-windows-store://search/?query=" + query);
    }

    private void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        // Registry-provided links are untrusted data. Only open web links;
        // never hand an arbitrary protocol or local file to the shell.
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;

        OpenShellTarget(uri.AbsoluteUri);
    }

    private void OpenShellTarget(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageDialog.Error(this, "CleanRift", ex.Message);
        }
    }

    private void RunSystemTool(string command)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = SystemToolPath(command),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageDialog.Error(this, AppInfo.Name, ex.Message);
        }
    }

    private static string SystemToolPath(string name)
    {
        if (Path.IsPathRooted(name))
            return name;

        var system = Path.Combine(Environment.SystemDirectory, name);
        if (File.Exists(system))
            return system;

        var windows = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), name);
        return File.Exists(windows) ? windows : name;
    }

    // -------------------- Startup actions --------------------

    private void ToggleStartupSelected(bool enable)
    {
        var items = SelectedStartupItems();
        if (items.Count == 0)
            return;
        var errors = new List<string>();
        foreach (var item in items)
        {
            if (item.Enabled == enable)
                continue;
            if (!StartupManager.SetEnabled(item, enable, out var error))
                errors.Add(item.Name + ": " + error);
        }

        if (errors.Count > 0)
            MessageDialog.Error(this, LanguageManager.Instance.T("ToolBar/btnStartup", "Startup"),
                LanguageManager.Instance.T("Messages/msgCantModifyAutorunEntry",
                    "Cannot modify this entry properties.") + Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, errors));

        _ = RefreshStartupAsync();
    }

    private void DeleteStartupSelected()
    {
        var items = SelectedStartupItems();
        if (items.Count == 0)
            return;

        var lang = LanguageManager.Instance;
        var confirm = items.Count == 1
            ? MessageDialog.Show(this, LanguageManager.Instance.T("ToolBar/btnStartup", "Startup"),
                lang.T("ExplorerBar/itemDeleteStartupItem", "Delete from the list"),
                lang.T("Messages/msgStartupDeleteConfirm", "This will completely erase this entry from your system registry (start menu)."),
                MessageBoxButtons.YesNo, AppIcons.Warning)
            : MessageDialog.Show(this, LanguageManager.Instance.T("ToolBar/btnStartup", "Startup"),
                lang.T("ExplorerBar/itemDeleteSelected", "Delete selected items from the list"),
                lang.T("Messages/msgRemoveMultipleItemsDescription",
                    "This will remove the items above from your system registry (start menu) and they will never run when your Windows starts."),
                MessageBoxButtons.YesNo, AppIcons.Warning);

        if (confirm.Result != DialogResult.Yes)
            return;

        var errors = new List<string>();
        foreach (var item in items)
        {
            if (!StartupManager.Delete(item, out var error))
                errors.Add(item.Name + ": " + error);
        }

        if (errors.Count > 0)
            MessageDialog.Error(this, LanguageManager.Instance.T("ToolBar/btnStartup", "Startup"),
                string.Join(Environment.NewLine, errors));

        _ = RefreshStartupAsync();
    }

    private void AddStartup()
    {
        using var form = new AddStartupForm();
        if (form.ShowDialog(this) == DialogResult.OK)
            _ = RefreshStartupAsync();
    }

    // -------------------- Menus / misc --------------------

    private void ExportHtml()
    {
        var lang = LanguageManager.Instance;
        using var dialog = new SaveFileDialog
        {
            Filter = lang.F("HTMLReport/htmlFiles", "HTML Files") + " (*.html)|*.html|" +
                     lang.F("HTMLReport/allFiles", "All Files") + " (*.*)|*.*||",
            FileName = "cleanrift-report.html"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            // Export the list that is currently displayed.
            if (_mode == ViewMode.Startup)
            {
                var startup = _startup.Count > 0 ? _startup : new StartupScanner(_settings).Scan();
                HtmlExporter.ExportStartup(dialog.FileName, startup);
            }
            else
            {
                HtmlExporter.ExportPrograms(dialog.FileName, _programs);
            }

            Process.Start(new ProcessStartInfo { FileName = dialog.FileName, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageDialog.Error(this, AppInfo.Name, ex.Message);
        }
    }

    private void OpenPreferences()
    {
        var smallIcons = _settings.SmallIcons;
        var previousScheme = _settings.ColorScheme;
        using var form = new PreferencesForm(_settings);
        var result = form.ShowDialog(this);
        if (result != DialogResult.OK)
            return;

        var languageChanged = !string.Equals(Path.GetFileName(form.SelectedLanguageFile),
            Path.GetFileName(LanguageManager.Instance.LanguageFile), StringComparison.OrdinalIgnoreCase);
        var schemeChanged = previousScheme != _settings.ColorScheme;

        if (languageChanged || schemeChanged)
        {
            if (languageChanged)
            {
                _settings.LanguageFile = form.SelectedLanguageFile;
                LanguageManager.Instance.Load(form.SelectedLanguageFile);
            }

            _settings.LastCategory = _category;
            SaveSettings();
            Theme.Current = Theme.FromScheme(_settings.ColorScheme);

            // Recreate the window so the toolbar, explorer, list and dialogs
            // all pick up the new language/color scheme at once.
            var target = _mode == ViewMode.Startup ? StartupTarget.Startup : StartupTarget.None;
            var next = new MainForm(_settings, target);
            next.Show();
            _externalClose = true;
            Close();
            return;
        }

        _settings.Save();
        Theme.Current = Theme.FromScheme(_settings.ColorScheme);
        ApplyThemeAndTexts();
        _tray.Visible = _settings.TrayIcon;
        BuildMenu();
        if (smallIcons != _settings.SmallIcons)
            ApplyIconSize();
        else
            RebuildList();
    }

    private void ApplyIconSize()
    {
        var size = new Size(IconSize, IconSize);
        if (_listIcons.ImageSize == size)
            return;

        _list.SmallImageList = null;
        _listIcons.Images.Clear();
        _listIcons.ImageSize = size;
        _list.SmallImageList = _listIcons;
        RebuildList();
    }

    private void ChangeLanguage(string file)
    {
        _settings.LanguageFile = file;
        _settings.LastCategory = _category;
        SaveSettings();
        LanguageManager.Instance.Load(file);
        Theme.Current = Theme.FromScheme(_settings.ColorScheme);
        var next = new MainForm(_settings);
        next.Show();
        _externalClose = true;
        Close();
    }

    private void ShowQuarantine()
    {
        using var form = new QuarantineForm();
        form.ShowDialog(this);
    }

    private void ShowAbout()
    {
        using var form = new AboutForm();
        form.ShowDialog(this);
    }

    private void OpenHelp()
    {
        var readme = Path.Combine(AppContext.BaseDirectory, "README.txt");
        if (File.Exists(readme))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = readme, UseShellExecute = true });
                return;
            }
            catch
            {
                // Fall through to the in-app summary.
            }
        }

        MessageDialog.Info(this, AppInfo.Name,
            LanguageManager.Instance.T("Messages/msgQuickHelp",
                "CleanRift removes programs and their leftovers.\n\n" +
                "F1  Open this guide\n" +
                "F5  Refresh the list\n" +
                "Ctrl+F  Search\n" +
                "Del  Remove the selected program\n\n" +
                "Preferences let you change the theme, language and uninstall wizard mode."));
    }

    private void ResetColumns()
    {
        _settings.ColumnNameWidth = 340;
        _settings.ColumnSizeWidth = 100;
        _settings.ColumnDateWidth = 110;
        _settings.ColumnTracedWidth = 85;
        _settings.StartupColumnWidth = 340;
        UpdateColumnHeaders();
    }

    private void RestoreWindowState()
    {
        if (_settings.WindowMaximized)
            WindowState = FormWindowState.Maximized;
        Application.Idle += OnFirstIdle;
    }

    private void OnFirstIdle(object? sender, EventArgs e)
    {
        Application.Idle -= OnFirstIdle;
        UpdateExplorerWidth();
    }

    private void HideToTray()
    {
        _tray.Visible = true;
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Called when a second process signals Local\CleanRift_ACTIVATE. The previous
    /// instance closes so the new one can take over the mutex.
    /// </summary>
    public void RequestExternalClose()
    {
        _externalClose = true;
        Close();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_externalClose && e.CloseReason == CloseReason.UserClosing &&
            _settings.MinimizeOnClose && _settings.TrayIcon && _tray.Visible)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
        _sizeCts?.Cancel();
        _sizeCts?.Dispose();
        _sizeCts = null;
        _closing = true;
        _filterTimer.Stop();
        _filterTimer.Dispose();
        _context.Dispose();
        UninstallCache.Save(_programs);
        SaveSettings();
        _icons.Dispose();
        _list.SmallImageList = null;
        _listIcons.Dispose();
        var trayMenu = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = null;
        trayMenu?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _appIcon?.Dispose();
    }

    private void SaveSettings()
    {
        _settings.LastCategory = _category;
        if (WindowState == FormWindowState.Normal)
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.WindowWidth = ClientSize.Width;
            _settings.WindowHeight = ClientSize.Height;
        }

        _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
        if (_mode == ViewMode.Programs && _list.Columns.Count == 4)
        {
            _settings.ColumnNameWidth = _list.Columns[0].Width;
            _settings.ColumnSizeWidth = _list.Columns[1].Width;
            _settings.ColumnDateWidth = _list.Columns[2].Width;
            _settings.ColumnTracedWidth = _list.Columns[3].Width;
        }
        else if (_mode == ViewMode.Startup && _list.Columns.Count >= 1)
        {
            _settings.StartupColumnWidth = _list.Columns[0].Width;
        }

        _settings.Save();
    }
}

internal static class ProgramExtensions
{
    public static bool Mark32Bit(this InstalledProgram program) =>
        !program.Is64Bit && !program.IsStoreApp;
}
