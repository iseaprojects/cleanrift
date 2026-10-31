using System.Diagnostics;
using System.Drawing;
using System.Text;
using CleanRift.Core;
using CleanRift.UI.Controls;

namespace CleanRift.UI.Forms;

/// <summary>
/// Install and Trace. CleanRift watches the usual installation roots in real
/// time while the setup runs and combines those events with a before/after
/// snapshot (registry and fallback). The result is trace data consumed by the
/// Removal Wizard later.
/// </summary>
public sealed class InstallTrackerForm : Form
{
    private enum Phase
    {
        Setup,
        Working,
        Analyzing,
        Done
    }

    private readonly AppSettings _settings;
    private readonly InstalledProgram? _appendTo;
    private readonly InstallTracker _tracker = new();
    private readonly ProgramScanner _scanner = new();
    private readonly List<string> _log = new();
    private readonly System.Windows.Forms.Timer _liveTimer = new() { Interval = 500 };

    private readonly Label _headerLabel;
    private readonly Label _subHeader;
    private readonly Panel _pathHost;
    private readonly TextBox _path;
    private readonly Button _browse;
    private readonly Label _statusLabel;
    private readonly ProgressBar _progress;
    private readonly ListView _events;
    private readonly CheckBox _minimize;
    private readonly Button _install;
    private readonly Button _stop;
    private readonly Button _saveLog;
    private readonly Button _revert;
    private readonly Button _cancel;

    private CancellationTokenSource? _cts;
    private LiveFileMonitor? _live;
    private bool _closeAfterCancel;
    private TraceInfo? _trace;
    private Phase _phase = Phase.Setup;
    private bool _working;
    private bool _wasMinimized;
    private int _eventCount;

    public bool Traced { get; private set; }

    public InstallTrackerForm(AppSettings settings, InstalledProgram? appendTo)
    {
        _settings = settings;
        _appendTo = appendTo;
        var lang = LanguageManager.Instance;
        var theme = Theme.Current;

        Text = appendTo != null
            ? lang.F("InstallTracker/TitleAppend", appendTo.DisplayName)
            : lang.T("InstallTracker/Title", "Install and Trace a Program");
        ClientSize = new Size(520, 420);
        MinimumSize = new Size(490, 380);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = theme.Window;
        ForeColor = theme.Text;
        Font = new Font("Segoe UI", 9f);
        Icon = AppIcons.AppIcon();
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;

        var header = new Panel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(16, 8, 16, 4) };
        var headerIcon = new PictureBox
        {
            Image = AppIcons.Get(AppIcons.InstallTrace, 36),
            Location = new Point(16, 12),
            Size = new Size(36, 36),
            SizeMode = PictureBoxSizeMode.Zoom
        };
        _headerLabel = new Label
        {
            Text = Text,
            Location = new Point(64, 10),
            AutoSize = false,
            Height = 24,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            UseMnemonic = false
        };
        _subHeader = new Label
        {
            Text = lang.T("InstallTracker/staticInstallProgress", "Installation Progress:"),
            Location = new Point(64, 34),
            AutoSize = false,
            Height = 18,
            ForeColor = theme.Muted,
            UseMnemonic = false
        };
        header.Controls.AddRange(new Control[] { headerIcon, _headerLabel, _subHeader });
        header.Resize += (_, _) =>
        {
            _headerLabel.Width = header.ClientSize.Width - 80;
            _subHeader.Width = header.ClientSize.Width - 80;
        };

        _pathHost = new Panel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(16, 4, 16, 6) };
        var pathLabel = new Label
        {
            Text = lang.T("InstallTracker/staticPathToProgram", "Path to a setup file (.exe, .msi):"),
            Location = new Point(16, 4),
            AutoSize = true
        };
        _path = new TextBox
        {
            Location = new Point(16, 22),
            Width = Math.Max(80, _pathHost.ClientSize.Width - 32),
            BorderStyle = BorderStyle.FixedSingle
        };
        _browse = new Button
        {
            Text = "...",
            Size = new Size(34, 24),
            FlatStyle = FlatStyle.System
        };
        _browse.Click += (_, _) => Browse();
        _pathHost.Controls.AddRange(new Control[] { pathLabel, _path, _browse });
        _pathHost.Resize += (_, _) =>
        {
            _browse.Location = new Point(_pathHost.ClientSize.Width - 16 - _browse.Width, 21);
            _path.Width = Math.Max(80, _browse.Left - 8 - 16);
        };

        var tips = new Panel { Dock = DockStyle.Top, Height = 120, Padding = new Padding(18, 2, 16, 4) };
        var tipsFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };
        var tipLabels = new List<Label>();
        void AddTip(string text)
        {
            var label = Tip(text);
            tipsFlow.Controls.Add(label);
            tipLabels.Add(label);
        }

        AddTip(lang.T("InstallTracker/installTip1",
            "Proceed with installation normally while CleanRift is watching for it. Tracing is stopped " +
            "automatically when all installation processes finish."));
        AddTip(lang.T("InstallTracker/installTip2",
            "Please make sure that no other installation is currently running."));
        AddTip(lang.T("InstallTracker/installTip3",
            "If a previous version of this program is already installed, remove it first before installing a newer version."));

        void LayoutTips()
        {
            // The labels must wrap to the panel width; AutoSize labels made the
            // long Spanish tip overflow to the right and clip its last words.
            var width = Math.Max(180, tipsFlow.ClientSize.Width -
                                      SystemInformation.VerticalScrollBarWidth - 4);
            foreach (var label in tipLabels)
            {
                label.Width = width;
                label.Height = MeasureTipHeight(label.Text, label.Font, width);
            }
        }

        tipsFlow.Resize += (_, _) => LayoutTips();
        tips.Controls.Add(tipsFlow);
        LayoutTips();

        var progressHost = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(16, 2, 16, 2) };
        _statusLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 18,
            ForeColor = theme.Muted,
            Text = "",
            UseMnemonic = false
        };
        _progress = new ProgressBar
        {
            Dock = DockStyle.Bottom,
            Height = 10,
            Style = ProgressBarStyle.Marquee,
            Visible = false
        };
        progressHost.Controls.Add(_statusLabel);
        progressHost.Controls.Add(_progress);

        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 2, 16, 2) };
        _events = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = theme.ListBack,
            ForeColor = theme.Text,
            Font = new Font("Segoe UI", 8.5f),
            Visible = false
        };
        _events.Columns.Add("Event", 130);
        _events.Columns.Add("Object", 500);
        content.Controls.Add(_events);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 66, Padding = new Padding(16, 4, 16, 8) };
        _minimize = new CheckBox
        {
            Text = lang.T("InstallTracker/checkMinimize",
                "Minimize CleanRift to system tray during installation"),
            Location = new Point(16, 6),
            AutoSize = true,
            Checked = true,
            UseMnemonic = false
        };
        _install = new Button
        {
            Text = lang.T("InstallTracker/btnInstall", "Install and Trace"),
            Size = new Size(120, 32),
            FlatStyle = FlatStyle.System,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };
        _stop = new Button
        {
            Text = lang.T("InstallTracker/btnStop", "Stop"),
            Size = new Size(100, 32),
            Visible = false,
            FlatStyle = FlatStyle.System,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };
        _revert = new Button
        {
            Text = lang.T("InstallTracker/btnNoAndRevert", "No + Revert"),
            Size = new Size(120, 32),
            Visible = false,
            FlatStyle = FlatStyle.System,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };
        _saveLog = new Button
        {
            Text = lang.T("InstallTracker/btnSaveLog", "Save Log..."),
            Size = new Size(110, 32),
            Visible = false,
            FlatStyle = FlatStyle.System,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };
        _cancel = new Button
        {
            Text = lang.T("Misc/btnCancel", "Cancel"),
            Size = new Size(100, 32),
            FlatStyle = FlatStyle.System,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };
        footer.Controls.AddRange(new Control[]
        {
            _minimize, _install, _stop, _revert, _saveLog, _cancel
        });
        footer.Resize += (_, _) => LayoutButtons();

        Controls.Add(content);
        Controls.Add(footer);
        Controls.Add(progressHost);
        Controls.Add(tips);
        Controls.Add(_pathHost);
        Controls.Add(header);

        _install.Click += async (_, _) => await RunAsync();
        _stop.Click += (_, _) => CancelTracking();
        _cancel.Click += (_, _) => CancelTracking();
        _saveLog.Click += (_, _) => SaveLog();
        _revert.Click += (_, _) => RevertChanges();

        _tracker.Log += text => AppendLog(text);
        _liveTimer.Tick += (_, _) =>
        {
            if (_live == null || _phase != Phase.Working)
                return;
            _statusLabel.Text = LanguageManager.Instance.F("InstallTracker/statusLiveTracking",
                "Watching for changes... %u events", _live.EventCount);
        };
        FormClosing += OnFormClosing;
        Theme.ApplyDialogTheme(this);
        Shown += (_, _) => _path.Focus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(this);
        Theme.ApplyDarkInputThemes(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _liveTimer.Stop();
            _liveTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void LayoutButtons()
    {
        var y = _cancel.Parent!.ClientSize.Height - 42;
        _cancel.Location = new Point(_cancel.Parent.ClientSize.Width - 16 - _cancel.Width, y);
        _saveLog.Location = new Point(_cancel.Left - 8 - _saveLog.Width, y);
        _revert.Location = new Point(_saveLog.Left - 8 - _revert.Width, y);
        _install.Location = new Point(_cancel.Left - 8 - _install.Width, y);
        _stop.Location = new Point(_cancel.Left - 8 - _stop.Width, y);
        _minimize.Width = _cancel.Parent.ClientSize.Width - 32;
    }

    private static Label Tip(string text)
    {
        var label = new Label
        {
            Text = "•  " + text,
            AutoSize = false,
            Width = 460,
            Height = 18,
            ForeColor = Theme.Current.Muted,
            Margin = new Padding(0, 0, 0, 4),
            UseMnemonic = false
        };
        label.Height = MeasureTipHeight(label.Text, label.Font, label.Width);
        return label;
    }

    private static int MeasureTipHeight(string text, Font font, int width)
    {
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix |
                    TextFormatFlags.TextBoxControl;
        return Math.Max(18,
            TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), flags).Height + 2);
    }

    private void Browse()
    {
        using var dialog = new OpenFileDialog
        {
            Title = LanguageManager.Instance.T("InstallTracker/selectInstallFile",
                "Select an installation file"),
            Filter = "Setup files (*.exe;*.msi)|*.exe;*.msi|" +
                     LanguageManager.Instance.T("DlgAddNewStartupProgram/filterAllFiles", "All Files") + " (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _path.Text = dialog.FileName;
    }

    private async Task RunAsync()
    {
        if (_working)
            return;

        var path = _path.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageDialog.Info(this, Text,
                LanguageManager.Instance.T("InstallTracker/selectInstallFile",
                    "Select an installation file"));
            return;
        }

        _working = true;
        _phase = Phase.Working;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _path.Enabled = false;
        _browse.Enabled = false;
        _install.Visible = false;
        _stop.Visible = true;
        _cancel.Visible = false;
        _minimize.Enabled = false;
        _progress.Visible = true;
        _events.Visible = true;
        _statusLabel.Text = LanguageManager.Instance.T("InstallTracker/logStartingService",
            "Starting Install Tracker...");
        AppendLog(LanguageManager.Instance.T("InstallTracker/logStartingService",
            "Starting Install Tracker..."));
        AppendLog(LanguageManager.Instance.T("InstallTracker/logTrackingStarting", "Starting '%s'...")
            .Replace("%s", path));

        if (_minimize.Checked)
        {
            _wasMinimized = WindowState == FormWindowState.Minimized;
            WindowState = FormWindowState.Minimized;
        }

        try
        {
            _statusLabel.Text = LanguageManager.Instance.T("InstallTracker/logIndexingBefore",
                "Indexing the system before installation...");
            var beforePrograms = await Task.Run(() => _scanner.Scan(includeStore: false, token), token);
            var before = await Task.Run(() => _tracker.TakeSnapshot(token), token);
            token.ThrowIfCancellationRequested();

            AppendLog($"Baseline snapshot: {before.Files.Count} files, {before.Folders.Count} folders, " +
                      $"{before.Keys.Count} keys, {before.Values.Count} values");
            _statusLabel.Text = LanguageManager.Instance.T("InstallTracker/logTrackingStarting",
                "Starting the setup program...");

            // Real-time filesystem tracking starts only after the baseline, so
            // indexing noise is not attributed to the installer.
            using var live = new LiveFileMonitor(text => AppendLog(text));
            var roots = InstallTracker.FileRoots().ToList();
            var liveRoots = live.Start(roots, before);
            if (liveRoots == 0)
            {
                AppendLog("Warning: no filesystem roots could be watched; using snapshot only.");
            }
            else
            {
                _live = live;
                _liveTimer.Start();
            }

            var exitCode = await Task.Run(() => _tracker.RunSetup(path, token), token);
            if (exitCode == null)
                throw new OperationCanceledException(token);

            // Give FileSystemWatcher a moment to drain the last events emitted
            // just before the setup process tree exited.
            try
            {
                await Task.Delay(350, token);
            }
            catch (OperationCanceledException)
            {
                // Stop below still flushes what has already arrived.
            }

            StopLiveStatus();
            live.Stop();
            var liveComplete = liveRoots == roots.Count && !live.Overflowed && !live.Truncated;
            AppendLog($"Live monitor: {live.EventCount} events, {live.FileCount} new files, " +
                      $"{live.FolderCount} new folders, {liveRoots} roots" +
                      (live.Overflowed ? " (buffer overflow; snapshot fallback merged)" : "") +
                      (live.Truncated ? " (path limit reached; snapshot fallback merged)" : ""));

            token.ThrowIfCancellationRequested();
            AppendLog($"Setup finished with exit code {exitCode}");
            _statusLabel.Text = LanguageManager.Instance.T("InstallTracker/logTracingAnalyzing",
                "Analyzing installation, please wait...");
            _phase = Phase.Analyzing;

            var after = await Task.Run(
                () => _tracker.TakeSnapshot(token, includeFiles: !liveComplete), token);
            token.ThrowIfCancellationRequested();
            if (!liveComplete)
                AppendLog("Final filesystem snapshot used because real-time coverage was incomplete.");

            var diff = _tracker.Diff(before, after);
            live.MergeInto(diff);
            AppendLog($"Detected {diff.Folders.Count} folders, {diff.Files.Count} files, " +
                      $"{diff.RegistryKeys.Count} keys and {diff.RegistryValues.Count} registry values");
            if (before.Truncated || after.Truncated)
            {
                AppendLog("Warning: the system snapshot was truncated (time or entry limit); " +
                          "some changes may be missing from the trace.");
            }

            if (_appendTo != null)
            {
                var trace = MergeTrace(diff, _appendTo);
                TraceStore.Save(trace);
                _trace = trace;
                ShowEvents(trace, diff);
                Traced = true;
                Finish();
                return;
            }

            _statusLabel.Text = LanguageManager.Instance.T("InstallTracker/logDetectingEntries",
                "Detecting installation entries...");
            var afterPrograms = await Task.Run(() => _scanner.Scan(includeStore: false, token), token);
            var beforeIds = beforePrograms.Select(p => p.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newPrograms = afterPrograms
                .Where(p => !beforeIds.Contains(p.Id) && !p.IsChild && !string.IsNullOrWhiteSpace(p.DisplayName))
                .ToList();

            AppendLog($"New installation entries detected: {newPrograms.Count}");
            InstalledProgram? target = null;
            var customName = "";

            if (newPrograms.Count == 0)
            {
                customName = AskCustomEntryName();
                if (string.IsNullOrWhiteSpace(customName))
                {
                    Finish(keepTrace: false);
                    return;
                }
            }
            else if (newPrograms.Count == 1)
            {
                target = newPrograms[0];
            }
            else
            {
                target = SelectMainEntry(newPrograms);
                if (target == null)
                {
                    Finish(keepTrace: false);
                    return;
                }
            }

            var id = target?.Id ?? "custom:" + Guid.NewGuid().ToString("N");
            var display = target?.DisplayName ?? customName;
            InstallTracker.AddCustomInstallLocation(diff, target?.InstallLocation, before.TakenUtc);
            var built = _tracker.BuildTrace(diff, id, display);
            TraceStore.Save(built);
            _trace = built;
            ShowEvents(built, diff);
            Traced = true;
            Finish();
        }
        catch (OperationCanceledException)
        {
            StopLiveStatus();
            AppendLog(LanguageManager.Instance.T("InstallTracker/logTrackingStoppedByUser",
                "Tracing is stopped by user"));
            _statusLabel.Text = LanguageManager.Instance.T("InstallTracker/logTrackingStoppedByUser",
                "Tracing is stopped by user");
            RestoreWindow();
            _working = false;
            _phase = Phase.Setup;
            _cts?.Dispose();
            _cts = null;
            ResetSetupState();
            if (_closeAfterCancel)
            {
                _closeAfterCancel = false;
                DialogResult = DialogResult.Cancel;
                Close();
            }
        }
        catch (Exception ex)
        {
            StopLiveStatus();
            AppendLog("Error: " + ex.Message);
            _statusLabel.Text = "Error: " + ex.Message;
            RestoreWindow();
            MessageDialog.Error(this, Text, ex.Message);
            _working = false;
            _phase = Phase.Setup;
            _cts?.Dispose();
            _cts = null;
            ResetSetupState();
            if (_closeAfterCancel)
            {
                _closeAfterCancel = false;
                DialogResult = DialogResult.Cancel;
                Close();
            }
        }
    }

    private void Finish(bool keepTrace = true)
    {
        StopLiveStatus();
        _working = false;
        _phase = Phase.Done;
        _cts?.Dispose();
        _cts = null;
        if (_closeAfterCancel)
        {
            _closeAfterCancel = false;
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }

        _progress.Visible = false;
        _stop.Visible = false;
        _revert.Visible = keepTrace && _trace != null;
        _saveLog.Visible = true;
        _cancel.Visible = false;
        _install.Visible = false;
        _path.Enabled = false;
        _browse.Enabled = false;
        _minimize.Enabled = false;
        RestoreWindow();

        if (!keepTrace)
        {
            _statusLabel.Text = LanguageManager.Instance.T("InstallTracker/logTrackingDone", "Done");
            _headerLabel.Text = LanguageManager.Instance.T("InstallTracker/InstallTrackerLocalizedName",
                "Install Tracker");
            _subHeader.Text = "";
            _revert.Visible = false;
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }

        _headerLabel.Text = LanguageManager.Instance.T("InstallTracker/logTrackingDone", "Done");
        _subHeader.Text = _trace?.DisplayName ?? "";
        _statusLabel.Text = LanguageManager.Instance.T("InstallTracker/logCompleted", "Completed");
        _statusLabel.ForeColor = Theme.Current.Muted;
    }

    private void ResetSetupState()
    {
        _path.Enabled = true;
        _browse.Enabled = true;
        _install.Visible = true;
        _stop.Visible = false;
        _cancel.Visible = true;
        _saveLog.Visible = false;
        _revert.Visible = false;
        _minimize.Enabled = true;
        _progress.Visible = false;
        _events.Visible = false;
        _headerLabel.Text = Text;
        _subHeader.Text = LanguageManager.Instance.T("InstallTracker/staticInstallProgress",
            "Installation Progress:");
    }

    private void RestoreWindow()
    {
        if (_wasMinimized)
        {
            WindowState = FormWindowState.Normal;
            _wasMinimized = false;
        }

        Show();
        Activate();
    }

    private void ShowEvents(TraceInfo trace, SnapshotDiff diff)
    {
        _events.BeginUpdate();
        _events.Items.Clear();
        _eventCount = 0;

        void Add(string kind, string path)
        {
            if (_eventCount++ >= 5000)
                return;
            _events.Items.Add(new ListViewItem(new[] { kind, path }));
        }

        foreach (var folder in diff.Folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            Add(LanguageManager.Instance.T("InstallTracker/logEventCreateFolder", "Create Folder"), folder);
        foreach (var file in diff.Files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            Add(LanguageManager.Instance.T("InstallTracker/logEventCreateFile", "Create File"), file);
        foreach (var key in diff.RegistryKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            Add(LanguageManager.Instance.T("InstallTracker/logEventCreateKey", "Create Key"), key);
        foreach (var (key, name) in diff.RegistryValues)
            Add(LanguageManager.Instance.T("InstallTracker/logEventSetValue", "Set Value"), key + "\\" + name);

        if (_eventCount > 5000)
        {
            _events.Items.Add(new ListViewItem(new[]
            {
                "...",
                $"Showing the first 5000 of {_eventCount} traced items"
            }));
        }

        _events.EndUpdate();
    }

    private string AskCustomEntryName()
    {
        using var dialog = new CustomEntryDialog();
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.EntryName : "";
    }

    private InstalledProgram? SelectMainEntry(List<InstalledProgram> programs)
    {
        using var dialog = new MultipleEntriesDialog(programs);
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.Selected : null;
    }

    private TraceInfo MergeTrace(SnapshotDiff diff, InstalledProgram appendTo)
    {
        var all = TraceStore.LoadAll();
        TraceInfo? existing = null;
        if (all.TryGetValue(appendTo.Id, out var direct))
            existing = direct;
        else if (!string.IsNullOrWhiteSpace(appendTo.TraceFile) && File.Exists(appendTo.TraceFile))
            existing = TraceStore.Load(appendTo.TraceFile);

        var trace = _tracker.BuildTrace(diff, appendTo.Id, appendTo.DisplayName);
        if (existing != null)
        {
            foreach (var folder in existing.Folders)
                if (!trace.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    trace.Folders.Add(folder);
            foreach (var file in existing.Files)
                if (!trace.Files.Contains(file, StringComparer.OrdinalIgnoreCase))
                    trace.Files.Add(file);
            foreach (var key in existing.RegistryKeys)
                if (!trace.RegistryKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                    trace.RegistryKeys.Add(key);
            foreach (var value in existing.RegistryValues)
                if (!trace.RegistryValues.Any(v => v.Key.Equals(value.Key,
                        StringComparison.OrdinalIgnoreCase) && v.Name.Equals(value.Name,
                        StringComparison.OrdinalIgnoreCase)))
                    trace.RegistryValues.Add(value);
            trace.CreatedUtc = existing.CreatedUtc;
            trace.File = existing.File;
        }

        return trace;
    }

    private void CancelTracking()
    {
        if (_working)
        {
            var confirm = MessageDialog.Confirm(this,
                LanguageManager.Instance.T("InstallTracker/InstallTrackerLocalizedName", "Install Tracker"),
                LanguageManager.Instance.T("InstallTracker/btnStop", "Stop"),
                "Do you want to stop tracing and cancel the installation?");
            if (confirm.Result != DialogResult.Yes)
                return;

            _stop.Enabled = false;
            _cts?.Cancel();
            return;
        }

        DialogResult = DialogResult.Cancel;
        Close();
    }

    private void SaveLog()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = "cleanrift-install-trace.log"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, string.Join(Environment.NewLine, _log), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            MessageDialog.Error(this, Text, ex.Message);
        }
    }

    private void RevertChanges()
    {
        if (_trace == null)
            return;
        var confirm = MessageDialog.Confirm(this,
            LanguageManager.Instance.T("InstallTracker/InstallTrackerLocalizedName", "Install Tracker"),
            LanguageManager.Instance.T("InstallTracker/staticRevertInfo",
                "Revert option attempts to delete all file system/registry items created during the installation tracing"),
            LanguageManager.Instance.T("InstallTracker/btnNoAndRevert", "No + Revert") + "?");
        if (confirm.Result != DialogResult.Yes)
            return;

        try
        {
            UseWaitCursor = true;
            var result = InstallTracker.Revert(_trace, CancellationToken.None);
            if (result.Failed > 0)
            {
                var details = string.Join(Environment.NewLine, result.Errors.Take(10));
                MessageDialog.Show(this, Text,
                    LanguageManager.Instance.T("DelayedFilesRemovalDialog/Title",
                        "Some files were not deleted"),
                    details, MessageBoxButtons.OK, AppIcons.Warning);
                return;
            }

            TraceStore.Delete(_trace.Id, _trace.File);
            Traced = false;
            MessageDialog.Info(this, Text, LanguageManager.Instance.T("InstallTracker/logCompleted", "Completed"));
            DialogResult = DialogResult.Cancel;
            Close();
        }
        catch (Exception ex)
        {
            MessageDialog.Error(this, Text, ex.Message);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void StopLiveStatus()
    {
        _liveTimer.Stop();
        _live = null;
    }

    private void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        void Add()
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
            if (_log.Count < 20_000)
                _log.Add(line);
            if (_phase is Phase.Working or Phase.Analyzing)
                _statusLabel.Text = text.Length <= 160 ? text : text[..157] + "...";
        }

        if (IsHandleCreated && InvokeRequired)
        {
            try
            {
                BeginInvoke((Action)Add);
            }
            catch
            {
                // The form closed while a background event was arriving.
            }
        }
        else
        {
            Add();
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_working)
            return;

        var confirm = MessageDialog.Confirm(this,
            LanguageManager.Instance.T("InstallTracker/InstallTrackerLocalizedName", "Install Tracker"),
            LanguageManager.Instance.T("InstallTracker/btnStop", "Stop"),
            LanguageManager.Instance.T("InstallTracker/staticCloseWhileTracing",
                "Tracing is still running. Stop tracing and close the window?"));
        if (confirm.Result != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        // Let the tracking task unwind before the window disappears.
        _closeAfterCancel = true;
        _cts?.Cancel();
        e.Cancel = true;
    }

    /// <summary>Dialog for creating a custom list entry.</summary>
    private sealed class CustomEntryDialog : Form
    {
        private readonly CheckBox _create;
        private readonly TextBox _name;

        public string EntryName => _create.Checked ? _name.Text.Trim() : "";

        public CustomEntryDialog()
        {
            var lang = LanguageManager.Instance;
            var theme = Theme.Current;
            Text = lang.T("InstallTracker/InstallTrackerLocalizedName", "Install Tracker");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(460, 280);
            BackColor = theme.Window;
            ForeColor = theme.Text;
            Font = new Font("Segoe UI", 9f);
            Icon = AppIcons.AppIcon();

            var text = new Label
            {
                Text = lang.T("InstallTrackerCustomEntryDialog/Title",
                    "No new installation entries were found during the tracing. If you are sure the installation " +
                    "went fine you may create a custom program in the Apps list. All currently found traces will " +
                    "refer to it."),
                Location = new Point(16, 14),
                AutoSize = false,
                Width = ClientSize.Width - 32,
                Height = 132,
                UseMnemonic = false
            };
            _create = new CheckBox
            {
                Text = lang.T("InstallTrackerCustomEntryDialog/checkCreateCustomEntry",
                    "I'd like to create a custom item in the list"),
                Location = new Point(16, 152),
                AutoSize = true,
                UseMnemonic = false
            };
            var nameLabel = new Label
            {
                Text = lang.T("InstallTrackerCustomEntryDialog/staticName", "Name:"),
                Location = new Point(32, 182),
                AutoSize = true,
                Enabled = false
            };
            _name = new TextBox
            {
                Location = new Point(32, 202),
                Width = ClientSize.Width - 64,
                BorderStyle = BorderStyle.FixedSingle,
                Enabled = false
            };
            _create.CheckedChanged += (_, _) =>
            {
                nameLabel.Enabled = _create.Checked;
                _name.Enabled = _create.Checked;
                if (_create.Checked)
                    _name.Focus();
            };

            var separator = new Panel
            {
                Location = new Point(0, 230),
                Size = new Size(ClientSize.Width, 1),
                BackColor = theme.Border
            };
            var save = new Button
            {
                Text = lang.T("InstallTracker/btnSave", "Save"),
                Size = new Size(86, 30),
                Location = new Point(ClientSize.Width - 16 - 86 - 8 - 86, 242),
                FlatStyle = FlatStyle.System
            };
            save.Click += (_, _) =>
            {
                if (_create.Checked && string.IsNullOrWhiteSpace(_name.Text))
                {
                    MessageDialog.Info(this, Text, "Please enter a name for the custom item.");
                    return;
                }

                DialogResult = DialogResult.OK;
                Close();
            };
            var cancel = new Button
            {
                Text = lang.T("Misc/btnCancel", "Cancel"),
                DialogResult = DialogResult.Cancel,
                Size = new Size(86, 30),
                Location = new Point(ClientSize.Width - 16 - 86, 242),
                FlatStyle = FlatStyle.System
            };

            Controls.AddRange(new Control[]
            {
                text, _create, nameLabel, _name, separator, save, cancel
            });
            AcceptButton = save;
            CancelButton = cancel;
            Shown += (_, _) => _create.Focus();
        }
    }

    /// <summary>Lets the user pick the main entry when several were installed.</summary>
    private sealed class MultipleEntriesDialog : Form
    {
        private readonly ComboBox _combo;

        public InstalledProgram? Selected => _combo.SelectedItem as InstalledProgram;

        public MultipleEntriesDialog(IReadOnlyList<InstalledProgram> programs)
        {
            var lang = LanguageManager.Instance;
            var theme = Theme.Current;
            Text = lang.T("InstallTrackerMultipleEntries/Caption", "Multiple installation entries detected");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(480, 260);
            BackColor = theme.Window;
            ForeColor = theme.Text;
            Font = new Font("Segoe UI", 9f);
            Icon = AppIcons.AppIcon();

            var text = new Label
            {
                Text = lang.T("InstallTrackerMultipleEntries/Title",
                    "Multiple installation entries were detected in the Windows registry after the installation. " +
                    "Please select the main item to display in the Apps list with a special icon in the 'Traced' column."),
                Location = new Point(16, 14),
                AutoSize = false,
                Width = ClientSize.Width - 32,
                Height = 96,
                UseMnemonic = false
            };
            _combo = new ComboBox
            {
                Location = new Point(16, 122),
                Width = ClientSize.Width - 32,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _combo.Items.AddRange(programs.Cast<object>().ToArray());
            _combo.DisplayMember = nameof(InstalledProgram.DisplayName);
            if (_combo.Items.Count > 0)
                _combo.SelectedIndex = 0;

            var separator = new Panel
            {
                Location = new Point(0, 176),
                Size = new Size(ClientSize.Width, 1),
                BackColor = theme.Border
            };
            var select = new Button
            {
                Text = lang.T("InstallTrackerMultipleEntries/btnSelect", "Select"),
                DialogResult = DialogResult.OK,
                Size = new Size(96, 30),
                Location = new Point(ClientSize.Width - 16 - 96, 194),
                FlatStyle = FlatStyle.System
            };
            var cancel = new Button
            {
                Text = lang.T("Misc/btnCancel", "Cancel"),
                DialogResult = DialogResult.Cancel,
                Size = new Size(96, 30),
                Location = new Point(ClientSize.Width - 16 - 96 - 8 - 96, 194),
                FlatStyle = FlatStyle.System
            };

            Controls.AddRange(new Control[] { text, _combo, separator, select, cancel });
            Theme.ApplyDialogTheme(this);
            AcceptButton = select;
            CancelButton = cancel;
        }
    }
}

