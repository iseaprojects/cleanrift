using System.Drawing;
using CleanRift.Core;
using CleanRift.UI.Controls;

namespace CleanRift.UI.Forms;

/// <summary>
/// Preferences dialog. The property sheet uses a tab control with four option
/// groups: General, Interface, Application list and Removal Wizard.
/// </summary>
public sealed class PreferencesForm : Form
{
    private readonly AppSettings _settings;
    private readonly TabControl _tabs;

    private readonly CheckBox _skipUac;
    private readonly CheckBox _checkUpdates;
    private readonly CheckBox _checkBeta;
    private readonly CheckBox _controlPanel;
    private readonly CheckBox _shellExtension;
    private readonly CheckBox _totalCommander;
    private readonly CheckBox _quarantine;
    private readonly CheckBox _backupRegistry;
    private readonly CheckBox _restorePoint;
    private readonly CheckBox _quarantineCleanup;
    private readonly NumericUpDown _retentionDays;
    private readonly NumericUpDown _maxSizeMb;
    private readonly CheckBox _silentUninstall;

    private readonly CheckBox _toolbarCaptions;
    private readonly ComboBox _language;
    private readonly RadioButton _schemeAuto;
    private readonly RadioButton _schemeLight;
    private readonly RadioButton _schemeDark;
    private readonly CheckBox _trayIcon;
    private readonly CheckBox _startMinimized;
    private readonly CheckBox _minimizeToTray;
    private readonly CheckBox _minimizeOnClose;

    private readonly CheckBox _smallIcons;
    private readonly CheckBox _mark32Bit;
    private readonly CheckBox _displayUpdates;
    private readonly ComboBox _updatesMode;
    private readonly CheckBox _recentPopup;
    private readonly CheckBox _highlightRecent;
    private readonly CheckBox _recentToolbar;

    private readonly ComboBox _wizardMode;

    public string SelectedLanguageFile { get; private set; } = "";

    public PreferencesForm(AppSettings settings)
    {
        _settings = settings;
        var lang = LanguageManager.Instance;
        var theme = Theme.Current;

        SelectedLanguageFile = string.IsNullOrEmpty(settings.LanguageFile)
            ? LanguageManager.Instance.LanguageFile
            : settings.LanguageFile;

        Text = lang.T("DlgPreferences/DlgPreferencesCaption", "Preferences");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(590, 470);
        BackColor = theme.Window;
        ForeColor = theme.Text;
        Font = new Font("Segoe UI", 9f);
        Icon = AppIcons.AppIcon();
        AutoScaleMode = AutoScaleMode.Dpi;

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(14, 6)
        };
        _tabs = tabs;

        Panel? tabHeader = null;
        Panel? tabHost = null;
        Panel? tabAccent = null;
        List<Button>? tabButtons = null;
        if (Theme.Current.IsDark)
        {
            // The native tab header ignores the dark theme, so the header is
            // drawn with flat buttons and the native strip is reduced to 1 px.
            tabs.ItemSize = new Size(0, 1);
            tabs.SizeMode = TabSizeMode.Fixed;
            tabs.Appearance = TabAppearance.FlatButtons;
            tabHeader = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = theme.Window };
            tabAccent = new Panel { Height = 2, BackColor = theme.Accent };
            tabButtons = new List<Button>();
            tabHeader.Controls.Add(tabAccent);
            tabHost = new Panel { Dock = DockStyle.Fill };
            tabHost.Controls.Add(tabs);
            tabHost.Controls.Add(tabHeader);
        }

        // ---------------------------- General ----------------------------
        var general = NewPage(tabs, lang.T("DlgPreferences/categoryGeneral", "General"));
        // Derive the checkbox from the scheduled task itself, not from a stored
        // boolean.
        var skipUacEnabled = ElevationBootstrap.IsElevated
            ? SkipUacTask.Exists()
            : settings.SkipUac;
        _skipUac = Check("DlgPreferences/checkSkipUAC",
            "Use Windows Task Scheduler to bypass User Account Control confirmation dialog (when necessary)",
            skipUacEnabled);
        _checkUpdates = Check("DlgPreferences/checkCheckForUpdates",
            "Check for updates on program startup", settings.CheckForUpdates);
        _checkBeta = Check("DlgPreferences/checkCheckForBetaVersions",
            "Check for beta versions as well", settings.CheckForBeta);
        _checkBeta.Margin = new Padding(28, 0, 12, 6);
        _checkBeta.Width -= 16;
        _controlPanel = Check("DlgPreferences/checkControlPanelIcon",
            "Show CleanRift icon in Control Panel", settings.ControlPanelIcon);
        _shellExtension = Check("DlgPreferences/checkShellExtension",
            "Add 'Install and Trace' command for setup files (exe/msi) to the Windows Explorer context menu",
            settings.ShellExtension);
        _totalCommander = Check("DlgPreferences/checkTotalCommander",
            "Navigate to program directory using Total Commander (if installed)", settings.TotalCommander);
        _quarantine = Check("DlgPreferences/checkQuarantine",
            "Move removed files to quarantine (undo is available from Tools)", settings.QuarantineRemovals);
        _backupRegistry = Check("DlgPreferences/checkBackupRegistry",
            "Back up removed registry keys and values", settings.BackupRegistry);
        _restorePoint = Check("DlgPreferences/checkRestorePoint",
            "Create a System Restore point before removal", settings.CreateRestorePoint);
        _quarantineCleanup = Check("DlgPreferences/checkQuarantineCleanup",
            "Delete old quarantine batches automatically", settings.QuarantineAutoClean);
        _retentionDays = Numeric(1, 365, settings.QuarantineRetentionDays, 70);
        _maxSizeMb = Numeric(100, 102400, settings.QuarantineMaxSizeMb, 90);
        var retentionRow = Row(
            FieldLabel("DlgPreferences/quarantineRetentionLabel",
                "Delete batches older than:"),
            _retentionDays,
            FieldLabel("DlgPreferences/quarantineRetentionSuffix", "days"));
        var sizeRow = Row(
            FieldLabel("DlgPreferences/quarantineMaxSizeLabel",
                "Keep quarantine under:"),
            _maxSizeMb,
            FieldLabel("DlgPreferences/quarantineMaxSizeSuffix", "MB"));

        general.Controls.Add(_skipUac);
        general.Controls.Add(_checkUpdates);
        general.Controls.Add(_checkBeta);
        general.Controls.Add(_controlPanel);
        general.Controls.Add(_shellExtension);
        general.Controls.Add(_totalCommander);
        general.Controls.Add(_quarantine);
        general.Controls.Add(_backupRegistry);
        general.Controls.Add(_restorePoint);
        general.Controls.Add(_quarantineCleanup);
        general.Controls.Add(retentionRow);
        general.Controls.Add(sizeRow);
        general.Controls.Add(Note("DlgPreferences/quarantineCleanupNote",
            "Cleanup runs at startup. Recent batches are always kept so undo remains available."));
        _retentionDays.Enabled = _quarantineCleanup.Checked;
        _maxSizeMb.Enabled = _quarantineCleanup.Checked;
        _quarantineCleanup.CheckedChanged += (_, _) =>
        {
            _retentionDays.Enabled = _quarantineCleanup.Checked;
            _maxSizeMb.Enabled = _quarantineCleanup.Checked;
        };
        general.Controls.Add(Note("DlgPreferences/checkSkipUACHelpText",
            "Activating this option allows to bypass UAC confirmation when launching CleanRift. When turned on, " +
            "CleanRift creates a Windows Task Scheduler elevated task which is executed every time you'd like to run the app."));
        general.Controls.Add(Note("DlgPreferences/safetyNote",
            "Quarantine and registry backups can be restored from Tools > Quarantine and undo."));

        foreach (var control in new CheckBox[] { _checkUpdates, _checkBeta, _controlPanel, _totalCommander })
            DisableCheck(control);
        general.Controls.Add(Note("DlgPreferences/betaGeneralNote",
            "Update checks, the Control Panel icon and Total Commander navigation are not available in the 1.0 beta. " +
            "The Explorer 'Install and Trace' command is available and optional."));

        // --------------------------- Interface ---------------------------
        var interfacePage = NewPage(tabs, lang.T("DlgPreferences/categoryInterface", "Interface"));
        _toolbarCaptions = Check("DlgPreferences/checkToolBarCaptions",
            "Display toolbar buttons captions", settings.ToolbarCaptions);
        interfacePage.Controls.Add(_toolbarCaptions);

        var languageLabel = new Label
        {
            Text = lang.T("DlgPreferences/staticInterfaceLanguage", "Interface language:"),
            AutoSize = true,
            Margin = new Padding(12, 10, 12, 2)
        };
        _language = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 320,
            Margin = new Padding(14, 0, 12, 8),
            DisplayMember = nameof(LanguageInfo.Name)
        };
        foreach (var info in LanguageManager.AvailableLanguages())
            _language.Items.Add(info);
        var current = _language.Items.Cast<LanguageInfo>().FirstOrDefault(i =>
            string.Equals(Path.GetFileName(i.File), Path.GetFileName(SelectedLanguageFile),
                StringComparison.OrdinalIgnoreCase));
        if (current != null)
            _language.SelectedItem = current;
        else if (_language.Items.Count > 0)
            _language.SelectedIndex = 0;

        var selectedLanguage = current ?? _language.Items.Cast<LanguageInfo>().FirstOrDefault();
        var languageChoices = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(14, 0, 12, 8)
        };
        foreach (var info in _language.Items.Cast<LanguageInfo>())
        {
            var choice = new RadioButton
            {
                Text = info.Name,
                Tag = info,
                AutoSize = true,
                Checked = ReferenceEquals(info, selectedLanguage),
                Margin = new Padding(2, 0, 2, 2)
            };
            choice.CheckedChanged += (_, _) =>
            {
                if (choice.Checked)
                    _language.SelectedItem = info;
            };
            languageChoices.Controls.Add(choice);
        }

        interfacePage.Controls.Add(languageLabel);
        interfacePage.Controls.Add(languageChoices);
        _language.Visible = false;
        _language.TabStop = false;
        interfacePage.Controls.Add(_language);

        var schemeGroup = new Panel
        {
            Width = 280,
            Height = 116,
            BackColor = theme.Window,
            Margin = new Padding(12, 4, 12, 8)
        };
        schemeGroup.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Current.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, schemeGroup.Width - 1, schemeGroup.Height - 1);
        };
        var schemeCaption = new Label
        {
            Text = lang.T("DlgPreferences/frameColorScheme", "Color scheme"),
            Location = new Point(12, 6),
            AutoSize = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        schemeGroup.Controls.Add(schemeCaption);
        var schemeFlow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Location = new Point(14, 28)
        };
        _schemeAuto = Radio("DlgPreferences/colorSchemeAuto", "Match Windows light/dark mode",
            settings.ColorScheme == ColorScheme.Auto);
        _schemeLight = Radio("DlgPreferences/colorSchemeLight", "Light mode (default)",
            settings.ColorScheme == ColorScheme.Light);
        _schemeDark = Radio("DlgPreferences/colorSchemeDark", "Dark mode",
            settings.ColorScheme == ColorScheme.Dark);
        schemeFlow.Controls.AddRange(new Control[] { _schemeLight, _schemeDark, _schemeAuto });
        schemeGroup.Controls.Add(schemeFlow);
        interfacePage.Controls.Add(schemeGroup);

        var trayLabel = new Label
        {
            Text = lang.T("DlgPreferences/staticTrayGroup",
                "System notification area (system tray) icon:"),
            AutoSize = true,
            Margin = new Padding(12, 8, 12, 2),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        _trayIcon = Check("DlgPreferences/checkTrayIcon",
            "Display CleanRift system tray icon", settings.TrayIcon);
        _startMinimized = Check("DlgPreferences/checkStartMinimized",
            "Automatically start CleanRift minimized in system tray on Windows startup",
            settings.StartMinimized);
        _minimizeToTray = Check("DlgPreferences/checkMinimizeToTray",
            "Minimize to system tray on window minimize", settings.MinimizeToTray);
        _minimizeOnClose = Check("DlgPreferences/checkMinimizeOnClose",
            "Minimize to system tray on window close", settings.MinimizeOnClose);
        _startMinimized.Margin = new Padding(28, 0, 12, 6);
        _minimizeToTray.Margin = new Padding(28, 0, 12, 6);
        _minimizeOnClose.Margin = new Padding(28, 0, 12, 6);
        _startMinimized.Width -= 16;
        _minimizeToTray.Width -= 16;
        _minimizeOnClose.Width -= 16;
        interfacePage.Controls.Add(trayLabel);
        interfacePage.Controls.Add(_trayIcon);
        interfacePage.Controls.Add(_startMinimized);
        interfacePage.Controls.Add(_minimizeToTray);
        interfacePage.Controls.Add(_minimizeOnClose);

        // ------------------------ Application list -----------------------
        var appList = NewPage(tabs, lang.T("DlgPreferences/categoryAppList", "Application list"));
        _smallIcons = Check("DlgPreferences/checkSmallIcons", "Compact list with apps", settings.SmallIcons);
        _mark32Bit = Check("DlgPreferences/checkMark32bitApps", "Mark 32-bit apps", settings.Mark32Bit);
        _displayUpdates = Check("DlgPreferences/checkDisplayUpdates", "Display software updates",
            settings.DisplayUpdates);
        var updatesLabel = new Label
        {
            Text = lang.T("DlgPreferences/staticTreeLikeUpdates", "Tree-like software updates (sub-items):"),
            AutoSize = true,
            Margin = new Padding(12, 10, 12, 2)
        };
        _updatesMode = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 260,
            Margin = new Padding(14, 0, 12, 8)
        };
        _updatesMode.Items.Add(lang.T("DlgPreferences/comboItemTreeUpdatesCollapsed", "Initially collapsed"));
        _updatesMode.Items.Add(lang.T("DlgPreferences/comboItemTreeUpdatesExpanded", "Initially expanded"));
        _updatesMode.Items.Add(lang.T("DlgPreferences/comboItemTreeUpdatesAlwaysExpanded", "Always expanded"));
        _updatesMode.SelectedIndex = Math.Clamp(settings.UpdatesMode, 0, 2);
        _displayUpdates.CheckedChanged += (_, _) => _updatesMode.Enabled = _displayUpdates.Checked;

        var recentLabel = new Label
        {
            Text = lang.T("DlgPreferences/staticRecentlyInstalled", "Recently Installed Programs:"),
            AutoSize = true,
            Margin = new Padding(12, 10, 12, 2),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        _recentPopup = Check("DlgPreferences/checkDisplayPopup",
            "Display pop-up window with recently installed applications", settings.DisplayRecentPopup);
        _highlightRecent = Check("DlgPreferences/checkHighlighRecent", "Highlight recently installed programs",
            settings.HighlightRecent);
        _recentToolbar = Check("DlgPreferences/checkRecentOnSecondaryToolbar",
            "Display recent programs in the secondary toolbar", settings.RecentOnToolbar);

        appList.Controls.Add(_smallIcons);
        appList.Controls.Add(_mark32Bit);
        appList.Controls.Add(_displayUpdates);
        appList.Controls.Add(updatesLabel);
        appList.Controls.Add(_updatesMode);
        appList.Controls.Add(Note("DlgPreferences/subitemsHelpText",
            "Parent items can be expanded/collapsed with mouse or using Left/Right keyboard keys. " +
            "'Initially collapsed' is recommended; 'Always expanded' disables expand/collapse functionality."));
        appList.Controls.Add(recentLabel);
        appList.Controls.Add(_recentPopup);
        appList.Controls.Add(_highlightRecent);
        appList.Controls.Add(_recentToolbar);

        foreach (var control in new CheckBox[] { _displayUpdates, _recentPopup, _highlightRecent, _recentToolbar })
            DisableCheck(control);
        _updatesMode.Enabled = false;
        _updatesMode.Visible = false;
        updatesLabel.Visible = false;
        appList.Controls.Add(Note("DlgPreferences/betaAppListNote",
            "Software update groups, the recent-items popup and the recent-items toolbar button are not available in the 1.0 beta."));

        // ------------------------- Removal Wizard ----------------------
        var wizard = NewPage(tabs, lang.T("DlgPreferences/categoryUninstallWizard", "Removal Wizard"));
        wizard.Controls.Add(Note("DlgPreferences/staticWizardDesctiption",
            "When uninstalling an application, after running its built-in uninstall program, Removal Wizard may " +
            "perform a deep scan of the system registry and file system for unused registry entries and files. " +
            "You can choose to run it automatically, disable Removal Wizard, or be asked each time. " +
            "Removal Wizard always runs for traced programs.", 360));
        var wizardLabel = new Label
        {
            Text = lang.T("DlgPreferences/staticWizardUsage",
                "Launch Removal Wizard after running built-in uninstall program:"),
            AutoSize = true,
            Margin = new Padding(12, 8, 12, 2)
        };
        _wizardMode = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 260,
            Margin = new Padding(14, 0, 12, 8)
        };
        _wizardMode.Items.Add(lang.T("DlgPreferences/comboItemWizardOption1", "Ask before launching"));
        _wizardMode.Items.Add(lang.T("DlgPreferences/comboItemWizardOption2", "Automatically"));
        _wizardMode.Items.Add(lang.T("DlgPreferences/comboItemWizardOption3", "Disable"));
        _wizardMode.SelectedIndex = (int)settings.WizardMode;

        var wizardChoices = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(14, 0, 12, 8)
        };
        for (var index = 0; index < _wizardMode.Items.Count; index++)
        {
            var option = index;
            var choice = new RadioButton
            {
                Text = _wizardMode.GetItemText(_wizardMode.Items[index]),
                AutoSize = true,
                Checked = index == _wizardMode.SelectedIndex,
                Margin = new Padding(2, 0, 2, 2)
            };
            choice.CheckedChanged += (_, _) =>
            {
                if (choice.Checked)
                    _wizardMode.SelectedIndex = option;
            };
            wizardChoices.Controls.Add(choice);
        }

        wizard.Controls.Add(wizardLabel);
        wizard.Controls.Add(wizardChoices);
        _wizardMode.Visible = false;
        _wizardMode.TabStop = false;
        wizard.Controls.Add(_wizardMode);
        wizard.Controls.Add(Note("DlgPreferences/staticWizardTracedNote",
            "Removal Wizard always runs for traced programs and cannot be disabled for them."));
        _silentUninstall = Check("DlgPreferences/checkSilentUninstall",
            "Try silent uninstall when the program provides a quiet uninstall command",
            settings.SilentUninstall);
        wizard.Controls.Add(_silentUninstall);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(12, 8, 12, 10) };
        var ok = new Button
        {
            Text = lang.T("Misc/btnOK", "OK"),
            DialogResult = DialogResult.OK,
            Size = new Size(88, 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.System
        };
        var cancel = new Button
        {
            Text = lang.T("Misc/btnCancel", "Cancel"),
            DialogResult = DialogResult.Cancel,
            Size = new Size(88, 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.System
        };
        footer.Controls.Add(ok);
        footer.Controls.Add(cancel);
        footer.Resize += (_, _) =>
        {
            cancel.Location = new Point(footer.ClientSize.Width - 12 - cancel.Width, 10);
            ok.Location = new Point(cancel.Left - 8 - ok.Width, 10);
        };

        if (tabHost != null)
            Controls.Add(tabHost);
        else
            Controls.Add(tabs);
        Controls.Add(footer);
        Theme.ApplyDialogTheme(this);

        if (tabHeader != null && tabButtons != null && tabAccent != null)
        {
            var buttons = tabButtons;
            void UpdateTabHeader()
            {
                for (var i = 0; i < buttons.Count && i < tabs.TabPages.Count; i++)
                {
                    var selected = i == tabs.SelectedIndex;
                    var button = buttons[i];
                    button.BackColor = selected ? theme.WindowAlt : theme.Window;
                    button.ForeColor = selected ? theme.Text : theme.Muted;
                    if (selected)
                    {
                        tabAccent.Left = button.Left;
                        tabAccent.Width = button.Width;
                        tabAccent.Top = 26;
                    }
                }
            }

            var x = 8;
            tabs.SelectedIndexChanged += (_, _) => UpdateTabHeader();
            foreach (TabPage page in tabs.TabPages)
            {
                var index = buttons.Count;
                var button = new Button
                {
                    Text = page.Text,
                    AutoSize = false,
                    Height = 24,
                    Width = TextRenderer.MeasureText(page.Text, tabs.Font).Width + 28,
                    Left = x,
                    Top = 2,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = theme.Window,
                    ForeColor = theme.Muted,
                    Font = tabs.Font
                };
                button.FlatAppearance.BorderSize = 0;
                button.Click += (_, _) =>
                {
                    tabs.SelectedIndex = index;
                    UpdateTabHeader();
                };
                tabHeader.Controls.Add(button);
                buttons.Add(button);
                x += button.Width;
            }

            UpdateTabHeader();
        }

        AcceptButton = ok;
        CancelButton = cancel;

        ok.Click += (_, _) => Apply();
        _trayIcon.CheckedChanged += (_, _) =>
        {
            var enabled = _trayIcon.Checked;
            _startMinimized.Enabled = enabled;
            _minimizeToTray.Enabled = enabled;
            _minimizeOnClose.Enabled = enabled;
        };
        _trayIcon.Checked = settings.TrayIcon;
        _startMinimized.Enabled = _minimizeToTray.Enabled = _minimizeOnClose.Enabled = settings.TrayIcon;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(this);
        Theme.ApplyDarkInputThemes(this);
        Theme.ApplyDarkTabs(_tabs);
    }

    private void Apply()
    {
        _settings.SkipUac = _skipUac.Checked;
        _settings.CheckForUpdates = _checkUpdates.Checked;
        _settings.CheckForBeta = _checkBeta.Checked;
        _settings.ControlPanelIcon = _controlPanel.Checked;
        _settings.ShellExtension = _shellExtension.Checked;
        _settings.TotalCommander = _totalCommander.Checked;
        _settings.QuarantineRemovals = _quarantine.Checked;
        _settings.BackupRegistry = _backupRegistry.Checked;
        _settings.CreateRestorePoint = _restorePoint.Checked;
        _settings.QuarantineAutoClean = _quarantineCleanup.Checked;
        _settings.QuarantineRetentionDays = (int)_retentionDays.Value;
        _settings.QuarantineMaxSizeMb = (int)_maxSizeMb.Value;
        _settings.SilentUninstall = _silentUninstall.Checked;

        _settings.ToolbarCaptions = _toolbarCaptions.Checked;
        if (_language.SelectedItem is LanguageInfo info)
            SelectedLanguageFile = info.File;
        _settings.ColorScheme = _schemeDark.Checked ? ColorScheme.Dark
            : _schemeLight.Checked ? ColorScheme.Light
            : ColorScheme.Auto;
        _settings.TrayIcon = _trayIcon.Checked;
        // These options would hide the window without a way to restore it.
        _settings.StartMinimized = _trayIcon.Checked && _startMinimized.Checked;
        _settings.MinimizeToTray = _trayIcon.Checked && _minimizeToTray.Checked;
        _settings.MinimizeOnClose = _trayIcon.Checked && _minimizeOnClose.Checked;

        _settings.SmallIcons = _smallIcons.Checked;
        _settings.Mark32Bit = _mark32Bit.Checked;
        _settings.DisplayUpdates = _displayUpdates.Checked;
        _settings.UpdatesMode = Math.Max(0, _updatesMode.SelectedIndex);
        _settings.DisplayRecentPopup = _recentPopup.Checked;
        _settings.HighlightRecent = _highlightRecent.Checked;
        _settings.RecentOnToolbar = _recentToolbar.Checked;

        _settings.WizardMode = (WizardMode)Math.Max(0, _wizardMode.SelectedIndex);

        _settings.Save();

        var executable = Environment.ProcessPath;
        if (ElevationBootstrap.IsElevated && !string.IsNullOrEmpty(executable) &&
            !SkipUacTask.SetEnabled(_settings.SkipUac, executable, out var taskError) &&
            !string.IsNullOrWhiteSpace(taskError))
        {
            MessageDialog.Error(this, Text, taskError);
        }

        if (!string.IsNullOrEmpty(executable) &&
            !ShellIntegration.SetEnabled(_settings.ShellExtension, executable,
                LanguageManager.Instance.T("ShellIntegration/installAndTrace",
                    "Install and trace with CleanRift"), out var shellError) &&
            !string.IsNullOrWhiteSpace(shellError))
        {
            MessageDialog.Error(this, Text, shellError);
        }

        if (_settings.QuarantineAutoClean)
        {
            var retentionDays = _settings.QuarantineRetentionDays;
            var maxSizeMb = _settings.QuarantineMaxSizeMb;
            _ = Task.Run(() => QuarantineStore.Cleanup(retentionDays, maxSizeMb));
        }
    }

    private static FlowLayoutPanel NewPage(TabControl tabs, string title)
    {
        var page = new TabPage(title) { BackColor = Theme.Current.Window, ForeColor = Theme.Current.Text };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(6, 8, 6, 8)
        };
        page.Controls.Add(flow);
        tabs.TabPages.Add(page);
        return flow;
    }

    /// <summary>
    /// Marks a beta-unavailable preference as read-only without using the
    /// system's disabled text color, which is unreadable on the dark theme.
    /// </summary>
    private static void DisableCheck(CheckBox check)
    {
        check.AutoCheck = false;
        check.TabStop = false;
        check.Cursor = Cursors.Default;
        check.Checked = false;
        check.ForeColor = Theme.Current.IsDark
            ? Color.FromArgb(125, 132, 134)
            : Color.FromArgb(140, 140, 140);
    }

    private static NumericUpDown Numeric(int minimum, int maximum, int value, int width) =>
        new()
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = Math.Clamp(value, minimum, maximum),
            Width = width,
            Margin = new Padding(0, 2, 4, 0),
            TextAlign = HorizontalAlignment.Right,
            BackColor = Theme.Current.Window,
            ForeColor = Theme.Current.Text
        };

    private static Label FieldLabel(string key, string fallback) => new()
    {
        Text = LanguageManager.Instance.T(key, fallback),
        AutoSize = true,
        Margin = new Padding(0, 5, 4, 0),
        UseMnemonic = false
    };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(32, 0, 12, 6),
            BackColor = Theme.Current.Window
        };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private static CheckBox Check(string key, string fallback, bool value)
    {
        var text = LanguageManager.Instance.T(key, fallback);
        const int width = 516;
        using var measureFont = new Font("Segoe UI", 9f);
        var height = TextRenderer.MeasureText(text, measureFont,
            new Size(width - 24, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + 6;
        return new CheckBox
        {
            Text = text,
            Checked = value,
            AutoSize = false,
            Width = width,
            Height = Math.Max(20, height),
            Margin = new Padding(12, 0, 12, 6),
            UseMnemonic = false
        };
    }

    private static RadioButton Radio(string key, string fallback, bool value)
    {
        return new RadioButton
        {
            Text = LanguageManager.Instance.T(key, fallback),
            Checked = value,
            AutoSize = true,
            Margin = new Padding(4, 0, 4, 4),
            UseMnemonic = false
        };
    }

    private static Label Note(string key, string fallback, int width = 500)
    {
        var text = string.IsNullOrEmpty(key) ? fallback : LanguageManager.Instance.T(key, fallback);
        using var measureFont = new Font("Segoe UI", 9f);
        var height = TextRenderer.MeasureText(text, measureFont,
            new Size(width - 20, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + 8;
        return new Label
        {
            Text = text,
            AutoSize = false,
            Width = width,
            Height = height,
            ForeColor = Theme.Current.Muted,
            Margin = new Padding(14, 4, 12, 8),
            UseMnemonic = false
        };
    }
}
