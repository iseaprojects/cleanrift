using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using CleanRift.Core;
using CleanRift.UI;

namespace CleanRift.UI.Controls;

/// <summary>
/// The explorer bar shown on the left of the list: the selected program
/// details and contextual actions on top, and the search box at the bottom.
/// </summary>
public sealed class ExplorerPanel : UserControl
{
    private static readonly Font CaptionFont = new("Segoe UI", 8.5f, FontStyle.Bold);
    private static readonly Font ValueFont = new("Segoe UI", 7.5f);
    private static readonly Font ActionFont = new("Segoe UI", 9f);
    private static readonly Font LinkFont = new("Segoe UI", 8.5f);

    private readonly TextBox _search;
    private readonly Panel _header;
    private readonly PictureBox _icon;
    private readonly Label _title;
    private readonly Panel _details;
    private readonly Panel _actions;
    private readonly List<(string Caption, string Value, bool Wrap)> _fields = new();
    private readonly List<Control> _actionControls = new();
    private readonly List<(string Text, string Action, int Icon)> _linkActions = new();
    private bool _suppressSearch;
    private bool _layingOutFields;
    private Image? _ownedIcon;

    public event Action<string>? ActionInvoked;
    public event Action<string>? SearchChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SearchText
    {
        get => _search.Text;
        set
        {
            _suppressSearch = true;
            _search.Text = value;
            _suppressSearch = false;
        }
    }

    public ExplorerPanel()
    {
        var chrome = Theme.Current.IsDark
            ? Theme.Current.Window
            : Color.FromArgb(230, 243, 245);
        var searchChrome = Theme.Current.IsDark
            ? Color.FromArgb(25, 25, 25)
            : Color.FromArgb(230, 243, 245);
        Width = 260;
        Dock = DockStyle.Left;
        Padding = new Padding(0);
        DoubleBuffered = true;
        BackColor = chrome;
        ForeColor = Theme.Current.Text;
        Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Current.Border);
            e.Graphics.DrawLine(pen, ClientSize.Width - 1, 0, ClientSize.Width - 1, ClientSize.Height);
        };

        var searchHost = new Panel
        {
            Dock = DockStyle.Top,
            Height = 68,
            BackColor = searchChrome
        };
        var searchTitle = new Label
        {
            Location = new Point(30, 5),
            Height = 18,
            Text = LanguageManager.Instance.T("ExplorerBar/itemSearch", "Search"),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = false
        };
        var searchIcon = new PictureBox
        {
            Image = AppIcons.Get(AppIcons.Search, 16),
            Location = new Point(10, 5),
            Size = new Size(16, 16),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = searchChrome
        };
        var fillColor = Theme.Current.IsDark ? Theme.Current.Window : Color.White;
        var borderColor = Theme.Current.IsDark
            ? Theme.Current.Border
            : Color.FromArgb(178, 178, 178);
        var searchBox = new Panel
        {
            Location = new Point(10, 26),
            Size = new Size(220, 28),
            BackColor = searchChrome
        };
        _search = new TextBox
        {
            Location = new Point(8, 5),
            Width = 200,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 9.5f),
            BackColor = fillColor,
            ForeColor = Theme.Current.Text
        };
        searchBox.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, searchBox.Width - 1, searchBox.Height - 1);
            using var path = RoundedRectangle(rect, 5);
            using var fill = new SolidBrush(fillColor);
            using var border = new Pen(borderColor);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
        };
        _search.TextChanged += (_, _) =>
        {
            if (!_suppressSearch)
                SearchChanged?.Invoke(_search.Text);
        };
        searchBox.Controls.Add(_search);
        searchHost.Controls.Add(searchBox);
        searchHost.Controls.Add(searchTitle);
        searchHost.Controls.Add(searchIcon);

        var searchChevron = Chevron();
        searchHost.Controls.Add(searchChevron);
        searchChevron.Location = new Point(246, 5);
        searchHost.Resize += (_, _) =>
        {
            searchTitle.Width = Math.Max(40, searchHost.ClientSize.Width - 50);
            searchBox.Width = Math.Max(80, searchHost.ClientSize.Width - 20);
            _search.Width = Math.Max(40, searchBox.Width - 14);
            searchChevron.Location = new Point(searchHost.ClientSize.Width - 26, 5);
        };

        _header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(14, 4, 12, 6),
            BackColor = chrome
        };
        _icon = new PictureBox
        {
            Location = new Point(18, 12),
            Size = new Size(32, 32),
            SizeMode = PictureBoxSizeMode.Zoom
        };
        _title = new Label
        {
            Location = new Point(58, 16),
            AutoSize = false,
            Height = 28,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            AutoEllipsis = true,
            UseMnemonic = false
        };
        _header.Controls.Add(_icon);
        _header.Controls.Add(_title);

        var headerChevron = Chevron();
        _header.Controls.Add(headerChevron);
        headerChevron.Location = new Point(274, 8);
        _header.Resize += (_, _) =>
        {
            headerChevron.Location = new Point(_header.ClientSize.Width - 26, 8);
            LayoutHeader();
        };

        _actions = new Panel
        {
            Dock = DockStyle.Top,
            AutoScroll = true,
            Padding = new Padding(10, 6, 10, 4),
            Height = 190,
            BackColor = Theme.Current.Window,
            ForeColor = Theme.Current.Text
        };

        _details = new Panel
        {
            Dock = DockStyle.Top,
            Height = 200,
            AutoScroll = true,
            Padding = new Padding(14, 2, 14, 8),
            BackColor = Theme.Current.Window
        };
        _details.Resize += (_, _) => LayoutFields();

        _actions.Resize += (_, _) => LayoutActionControls();
        Resize += (_, _) =>
        {
            LayoutActionControls();
        };

        // Docking order: fill first, then the top sections in reverse order.
        // Visual stack: header, actions, details, search, empty filler.
        var filler = new Panel { Dock = DockStyle.Fill, BackColor = searchChrome };
        Controls.Add(filler);
        Controls.Add(searchHost);
        Controls.Add(_details);
        Controls.Add(_actions);
        Controls.Add(_header);
    }

    private void LayoutActionControls()
    {
        var padding = _actions.Padding;
        var width = Math.Max(120, _actions.ClientSize.Width - padding.Horizontal -
                                  SystemInformation.VerticalScrollBarWidth - 4);
        var y = padding.Top;
        foreach (var control in _actionControls)
        {
            control.Left = padding.Left + (control is LinkLabel ? 2 : 0);
            control.Top = y;
            if (control is Button button)
                button.Width = width;
            y += control.Height + 2;
        }

        var needed = y + padding.Bottom;
        // Keep room for the 64 px header, the 66 px search box and at least
        // 70 px of details. On a very short window the action block scrolls
        // instead of pushing the details area off-panel.
        var parentHeight = ClientSize.Height;
        var available = Math.Max(70, parentHeight - 58 - 68 - 70);
        var height = Math.Clamp(needed, 70, Math.Min(360, available));
        var autoScroll = needed > height;
        if (_actions.Height != height)
            _actions.Height = height;
        if (_actions.AutoScroll != autoScroll)
            _actions.AutoScroll = autoScroll;

        // The details height is bounded by the space left after the actions,
        // so recompute it when the action block grows or shrinks.
        if (_fields.Count > 0)
            LayoutFields();
    }

    private void ResizeActionButtons()
    {
        LayoutActionControls();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.SendMessage(_search.Handle, NativeMethods.EM_SETCUEBANNER, (IntPtr)1,
            LanguageManager.Instance.T("ExplorerBar/itemShortSearchTip",
                "Type to find a program"));
    }

    private void LayoutHeader()
    {
        _title.Width = Math.Max(40, _header.ClientSize.Width - 88);
    }

    private void SetIcon(Image? image, bool owned)
    {
        if (_ownedIcon != null && !ReferenceEquals(_ownedIcon, image))
            _ownedIcon.Dispose();
        _icon.Image = image;
        _ownedIcon = owned ? image : null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _ownedIcon?.Dispose();
        base.Dispose(disposing);
    }

    public void RefreshCueBanner()
    {
        if (_search.IsHandleCreated)
            NativeMethods.SendMessage(_search.Handle, NativeMethods.EM_SETCUEBANNER, (IntPtr)1,
                LanguageManager.Instance.T("ExplorerBar/itemShortSearchTip",
                "Type to find a program"));
    }

    public void ShowNothing(string message)
    {
        SetIcon(null, false);
        _title.Text = "";
        _fields.Clear();
        _details.Controls.Clear();
        AddField("", message, true);
        LayoutFields();
        RebuildActions(Array.Empty<(string Key, string Text, int Icon, bool Primary, bool Enabled)>());
    }

    public void ShowProgram(InstalledProgram? program, int selectionCount)
    {
        var lang = LanguageManager.Instance;
        _fields.Clear();
        _actions.SuspendLayout();
        ClearActions();
        if (program == null)
        {
            ShowNothing(lang.T("ExplorerBar/itemNothingFound", "Nothing found!"));
            _actions.ResumeLayout();
            return;
        }

        var icon = program.Icon?.ToBitmap();
        SetIcon(icon ?? AppIcons.Get(AppIcons.Apps, 48), icon != null);
        _title.Text = program.DisplayName;
        LayoutHeader();

        AddField(lang.T("ExplorerBar/itemVersion", "Version:"), program.DisplayVersion, false);
        AddField(lang.T("ExplorerBar/itemPublisher", "Publisher:"), program.Publisher, true);
        AddField(lang.T("ExplorerBar/itemInstalledOn", "Installed/Updated On:"),
            program.InstallDateLongText, false);
        if (program.IsTraced)
            AddField(lang.T("ListCtrl/columnTraced", "Traced"), "Yes", false);
        if (program.IsGroupParent)
            AddField("", lang.T("ExplorerBar/itemGroup", "This is a group of programs"), true);
        if (program.IsStoreApp)
            AddField("", "Microsoft Store", false);
        if (!CanUninstall(program))
            AddField("", lang.T("ExplorerBar/itemNoUninstall",
                "The program cannot be uninstalled in a normal way, you can try Force Removal instead."), true);

        LayoutFields();
        _actions.ResumeLayout();
    }

    public void ShowPrograms(IReadOnlyList<InstalledProgram> selected)
    {
        var lang = LanguageManager.Instance;
        _fields.Clear();
        if (selected.Count == 0)
        {
            ShowNothing(lang.T("ExplorerBar/itemNothingFound", "Nothing found!"));
            return;
        }

        if (selected.Count == 1)
        {
            ShowProgram(selected[0], 1);
            RebuildProgramActions(selected[0]);
            return;
        }

        SetIcon(AppIcons.Get(AppIcons.Apps, 48), false);
        _title.Text = lang.F("ExplorerBar/itemMultiplSelected", selected.Count);
        AddField("", string.Join(Environment.NewLine, selected.Take(8).Select(p => "• " + p.DisplayName)), true);
        if (selected.Count > 8)
            AddField("", "...", false);
        LayoutFields();
        RebuildProgramActions(selected[0], batch: selected);
    }

    public void ShowStartup(StartupItem? item, IReadOnlyList<StartupItem> selected)
    {
        var lang = LanguageManager.Instance;
        _fields.Clear();
        ClearActions();
        if (item == null)
        {
            ShowNothing(lang.T("ListCtrl/staticNoStartupItems",
                "There're no programs that automatically run on Windows start"));
            return;
        }

        var startupIcon = item.Icon?.ToBitmap();
        SetIcon(startupIcon ?? AppIcons.Get(AppIcons.Startup, 48), startupIcon != null);
        _title.Text = item.Name;
        LayoutHeader();

        AddField(lang.T("ExplorerBar/itemStatusCaption", "Status:"), item.DisplayStatus, false);
        AddField(lang.T("ExplorerBar/itemSourceCaption", "Source:"), item.SourceText, true);
        AddField(lang.T("ExplorerBar/itemCommandLineCaption", "Command Line:"), item.Command, true);

        LayoutFields();
        RebuildStartupActions(item, selected);
    }

    public void RebuildProgramActions(InstalledProgram program, IReadOnlyList<InstalledProgram>? batch = null)
    {
        var lang = LanguageManager.Instance;
        var isBatch = batch is { Count: > 1 };
        var actions = new List<(string Key, string Text, int Icon, bool Primary, bool Enabled)>();
        if (isBatch)
        {
            actions.Add(("uninstall", lang.F("MainMenu/mnuActionUninstall", "&Uninstall...")
                .Replace("&", ""), AppIcons.Uninstaller, true, !string.IsNullOrWhiteSpace(program.UninstallString)));
            actions.Add(("force", lang.T("MainMenu/mnuActionDeleteBatch", "Batch &Force Removal").Replace("&", ""),
                AppIcons.Delete, false, true));
            actions.Add(("removeentry", lang.T("MainMenu/mnuActionRemoveEntryBatch", "Batch Remove &Entries").Replace("&", ""),
                AppIcons.RemoveEntry, false, true));
        }
        else
        {
            actions.Add(("uninstall", lang.T("ExplorerBar/itemUninstallIt", "Uninstall"),
                AppIcons.Delete, true, CanUninstall(program)));
            if (!string.IsNullOrWhiteSpace(program.ModifyPath))
                actions.Add(("modify", lang.T("ExplorerBar/itemModifyIt", "Modify"),
                    AppIcons.Tools, false, true));
            actions.Add(("force", lang.T("ExplorerBar/itemDelete", "Force removal of all traces"),
                AppIcons.Tools, false, true));
        }

        RebuildActions(actions);

        if (!isBatch)
        {
            if (!string.IsNullOrWhiteSpace(program.InstallLocation))
            {
                var folder = program.InstallLocation.TrimEnd('\\') + "\\";
                AddActionLink(folder, "browse", AppIcons.FolderSearch);
            }
            else
            {
                AddActionLink(lang.T("MainMenu/mnuActionBrowseLocation", "Browse Location...")
                    .Replace("&", ""), "browse", AppIcons.FolderSearch);
            }

            if (!string.IsNullOrWhiteSpace(program.UrlInfoAbout))
                AddLink(program.UrlInfoAbout, "website", AppIcons.Globe);
        }
    }

    private void RebuildStartupActions(StartupItem item, IReadOnlyList<StartupItem> selected)
    {
        var lang = LanguageManager.Instance;
        var isBatch = selected.Count > 1;
        var enabled = item.Enabled;
        var actions = new List<(string Key, string Text, int Icon, bool Primary, bool Enabled)>
        {
            (enabled ? "disable" : "enable",
                lang.T(enabled ? "ExplorerBar/itemDisableRunning" : "ExplorerBar/itemEnableRunning",
                    enabled ? "Disable running on startup" : "Enable running on startup"),
                enabled ? AppIcons.Disable : AppIcons.Startup, true, true),
            ("delete", lang.T("ExplorerBar/itemDeleteStartupItem", "Delete from the list"),
                AppIcons.Delete, false, true)
        };

        if (isBatch)
        {
            actions[0] = (enabled ? "batchenable" : "batchdisable",
                lang.T("ExplorerBar/itemDisableSelected", "Disable selected items"), AppIcons.Disable, true, true);
            actions[1] = ("batchdelete", lang.T("ExplorerBar/itemDeleteSelected",
                "Delete selected items from the list"), AppIcons.Delete, false, true);
        }

        RebuildActions(actions);
        AddLink(lang.T("ExplorerBar/itemAddNewStartupItem", "Add a new program..."), "addstartup");
    }

    private void RebuildActions(IEnumerable<(string Key, string Text, int Icon, bool Primary, bool Enabled)> actions)
    {
        ClearActions();
        foreach (var action in actions)
        {
            var button = new Button
            {
                Text = "  " + action.Text,
                Image = AppIcons.Get(action.Icon, 16),
                ImageAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                Height = 26,
                Width = Math.Max(120, _actions.ClientSize.Width - 26),
                Margin = new Padding(0, 0, 0, 2),
                Enabled = action.Enabled,
                Tag = action.Key,
                Cursor = Cursors.Hand,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                UseMnemonic = false,
                TabStop = false,
                BackColor = Theme.Current.Window,
                ForeColor = action.Primary
                    ? (Theme.Current.IsDark ? Theme.Current.Accent : Color.FromArgb(190, 30, 30))
                    : Theme.Current.Text,
                Font = ActionFont
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Theme.Current.Hover;
            button.FlatAppearance.MouseDownBackColor = Theme.Current.Hover;

            button.Click += (_, _) => ActionInvoked?.Invoke(action.Key);
            _actions.Controls.Add(button);
            _actionControls.Add(button);
        }

        LayoutActionControls();
    }

    private void AddActionLink(string text, string action, int iconId)
    {
        var button = new Button
        {
            Text = "  " + text,
            Image = AppIcons.Get(iconId, 16),
            ImageAlign = ContentAlignment.MiddleLeft,
            TextAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            Height = 24,
            Width = Math.Max(120, _actions.ClientSize.Width - 26),
            Margin = new Padding(0, 0, 0, 2),
            Tag = action,
            Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            UseMnemonic = false,
            TabStop = false,
            BackColor = Theme.Current.Window,
            ForeColor = Theme.Current.Accent,
            Font = ActionFont
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Theme.Current.Hover;
        button.FlatAppearance.MouseDownBackColor = Theme.Current.Hover;
        button.Click += (_, _) => ActionInvoked?.Invoke(action);
        _actions.Controls.Add(button);
        _actionControls.Add(button);
        LayoutActionControls();
    }

    private void AddLink(string text, string action, int iconId = 0)
    {
        _linkActions.Add((text, action, iconId));
        LayoutFields();
    }

    private void ClearActions()
    {
        foreach (var control in _actionControls)
        {
            _actions.Controls.Remove(control);
            control.Dispose();
        }

        _actionControls.Clear();
        _linkActions.Clear();
        if (_fields.Count > 0)
            LayoutFields();
    }

    private void AddField(string caption, string value, bool wrap)
    {
        if (string.IsNullOrWhiteSpace(caption) && string.IsNullOrWhiteSpace(value))
            return;
        _fields.Add((caption, value, wrap));
    }

    private void LayoutFields()
    {
        if (_layingOutFields)
            return;
        _layingOutFields = true;

        _details.SuspendLayout();
        var old = _details.Controls.Cast<Control>().ToList();
        _details.Controls.Clear();
        foreach (var control in old)
            control.Dispose();

        var width = Math.Max(80, _details.ClientSize.Width - _details.Padding.Horizontal - 4);
        var y = 4;
        foreach (var field in _fields)
        {
            if (!string.IsNullOrEmpty(field.Caption))
            {
                var caption = new Label
                {
                    Text = field.Caption,
                    ForeColor = Theme.Current.Text,
                    Location = new Point(_details.Padding.Left, y),
                    AutoSize = false,
                    Width = width,
                    Height = 17,
                    Font = CaptionFont
                };
                _details.Controls.Add(caption);
                y += 17;
            }

            var value = string.IsNullOrEmpty(field.Value) ? "-" : field.Value;
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
            var measured = TextRenderer.MeasureText(value, Font, new Size(width, int.MaxValue), flags);
            var height = field.Wrap ? Math.Min(160, Math.Max(18, measured.Height)) : 18;
            var label = new Label
            {
                Text = value,
                Location = new Point(_details.Padding.Left, y),
                AutoSize = false,
                Width = width,
                Height = height,
                Font = ValueFont,
                ForeColor = Theme.Current.Text,
                UseMnemonic = false
            };
            _details.Controls.Add(label);
            y += height + 8;
        }

        foreach (var link in _linkActions)
        {
            var button = new Button
            {
                Text = (link.Icon != 0 ? "  " : "") + link.Text,
                Image = link.Icon != 0 ? AppIcons.Get(link.Icon, 16) : null,
                ImageAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                Location = new Point(_details.Padding.Left, y),
                Width = width,
                Height = 20,
                Font = LinkFont,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                UseMnemonic = false,
                TabStop = false,
                BackColor = Theme.Current.Window,
                ForeColor = Theme.Current.Accent,
                Cursor = Cursors.Hand,
                Tag = link.Action
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Theme.Current.Window;
            button.FlatAppearance.MouseDownBackColor = Theme.Current.Window;
            button.Click += (_, _) => ActionInvoked?.Invoke(link.Action);
            _details.Controls.Add(button);
            y += button.Height + 2;
        }

        _details.ResumeLayout();

        // The search section sits directly below the details, not pinned to the
        // bottom of the window.
        var contentHeight = y + _details.Padding.Bottom;
        var parentHeight = ClientSize.Height;
        var available = Math.Max(60,
            parentHeight - _header.Height - _actions.Height - 68);
        var target = Math.Min(contentHeight, available);
        if (_details.Height != target)
            _details.Height = target;
        _details.AutoScroll = contentHeight > target;
        _layingOutFields = false;
    }

    private static bool CanUninstall(InstalledProgram program)
    {
        return !program.NoRemove && !string.IsNullOrWhiteSpace(program.UninstallString);
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Panel Chevron()
    {
        var panel = new Panel
        {
            Size = new Size(18, 18),
            BackColor = Color.Transparent
        };
        panel.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Theme.Current.Muted, 1.4f);
            e.Graphics.DrawLines(pen, new[]
            {
                new Point(4, 11), new Point(9, 6), new Point(14, 11)
            });
        };
        return panel;
    }
}
