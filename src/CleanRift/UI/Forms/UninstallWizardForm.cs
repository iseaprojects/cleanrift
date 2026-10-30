using System.Drawing;
using System.Text;
using CleanRift.Core;
using CleanRift.UI;
using CleanRift.UI.Controls;

namespace CleanRift.UI.Forms;

/// <summary>
/// Removal Wizard: confirmation, standard uninstall, deep leftover scan and
/// final cleanup, for one or many programs.
/// </summary>
public sealed class UninstallWizardForm : Form
{
    private enum Phase
    {
        Confirm,
        Running,
        ScanPrompt,
        Scanning,
        Results,
        Removing,
        Done
    }

    private readonly IReadOnlyList<InstalledProgram> _programs;
    private readonly AppSettings _settings;
    private readonly UninstallRunner _runner = new();
    private readonly LeftoverScanner _scanner = new();
    private readonly List<string> _log = new();

    private readonly Panel _headerPanel;
    private readonly PictureBox _iconBox;
    private readonly Label _headerLabel;
    private readonly Label _headerSub;
    private readonly Label _bodyLabel;
    private readonly Panel _progressPanel;
    private readonly ProgressBar _progress;
    private readonly Panel _content;
    private readonly LeftoverTreeControl _tree;
    private readonly RichTextBox _logBox;
    private readonly Panel _footer;
    private readonly Label _infoLabel;
    private readonly LinkLabel _saveLogLink;
    private readonly LinkLabel _exportLink;
    private readonly Button _primary;
    private readonly Button _secondary;
    private readonly Button _cancel;

    private CancellationTokenSource? _cts;
    private Phase _phase = Phase.Confirm;
    private ScanResult _scan = new();
    private bool _working;
    private bool _removed;
    private bool _closeRequested;

    public bool ProgramsRemoved => _removed || _uninstalled;

    private bool _uninstalled;

    public UninstallWizardForm(IReadOnlyList<InstalledProgram> programs, AppSettings settings)
    {
        _programs = programs;
        _settings = settings;

        var lang = LanguageManager.Instance;
        var theme = Theme.Current;
        Text = programs.Count == 1
            ? lang.F("UninstallWizard/UninstallWizardCaption", programs[0].DisplayName)
            : lang.T("UninstallWizard/UninstallWizardLocalizedName", "Removal Wizard");

        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(620, 480);
        ClientSize = new Size(700, 540);
        BackColor = theme.Window;
        ForeColor = theme.Text;
        Font = new Font("Segoe UI", 9f);
        Icon = AppIcons.AppIcon();
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;

        _headerPanel = new Panel { Dock = DockStyle.Top, Height = 62, Padding = new Padding(16, 10, 16, 8) };
        _iconBox = new PictureBox
        {
            Location = new Point(16, 12),
            Size = new Size(36, 36),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = AppIcons.Get(AppIcons.Uninstaller, 36)
        };
        _headerLabel = new Label
        {
            Location = new Point(64, 10),
            AutoSize = false,
            Height = 24,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            UseMnemonic = false
        };
        _headerSub = new Label
        {
            Location = new Point(64, 34),
            AutoSize = false,
            Height = 18,
            ForeColor = theme.Muted,
            UseMnemonic = false
        };
        _headerPanel.Controls.AddRange(new Control[] { _iconBox, _headerLabel, _headerSub });
        _headerPanel.Resize += (_, _) =>
        {
            _headerLabel.Width = _headerPanel.ClientSize.Width - 80;
            _headerSub.Width = _headerPanel.ClientSize.Width - 80;
        };

        _bodyLabel = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 42,
            Padding = new Padding(16, 0, 16, 0),
            ForeColor = theme.Text,
            UseMnemonic = false
        };

        _progressPanel = new Panel { Dock = DockStyle.Top, Height = 16, Padding = new Padding(16, 2, 16, 4) };
        _progress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, Visible = false };
        _progressPanel.Controls.Add(_progress);

        _content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 4, 16, 4) };
        _tree = new LeftoverTreeControl { Dock = DockStyle.Fill, Visible = false };
        _tree.SummaryChanged += UpdateFooterInfo;
        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = theme.ListBack,
            ForeColor = theme.Text,
            Font = new Font("Consolas", 8.5f),
            Visible = false,
            DetectUrls = false
        };
        _content.Controls.Add(_tree);
        _content.Controls.Add(_logBox);

        _footer = new Panel { Dock = DockStyle.Bottom, Height = 96, Padding = new Padding(16, 6, 16, 12) };
        _infoLabel = new Label
        {
            Location = new Point(16, 6),
            AutoSize = false,
            Height = 20,
            ForeColor = theme.Muted,
            UseMnemonic = false
        };
        _saveLogLink = new LinkLabel
        {
            Text = lang.T("UninstallWizard/staticHyperlinkRemovalLog", "Save Uninstall Log"),
            AutoSize = true,
            Location = new Point(16, 28),
            Visible = false
        };
        _saveLogLink.LinkClicked += (_, _) => SaveLog();
        _exportLink = new LinkLabel
        {
            Text = lang.T("UninstallWizard/staticHyperlinkExport", "Export to XML"),
            AutoSize = true,
            Location = new Point(150, 28),
            Visible = false
        };
        _exportLink.LinkClicked += (_, _) => ExportXml();

        _primary = new Button
        {
            Size = new Size(104, 32),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            FlatStyle = FlatStyle.System,
            Visible = false
        };
        _primary.Click += (_, _) => OnPrimary();
        _secondary = new Button
        {
            Size = new Size(104, 32),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            FlatStyle = FlatStyle.System,
            Visible = false
        };
        _cancel = new Button
        {
            Text = lang.T("UninstallWizard/btnCancel", "Cancel"),
            Size = new Size(104, 32),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            FlatStyle = FlatStyle.System
        };
        _cancel.Click += (_, _) => OnCancel();
        _footer.Controls.AddRange(new Control[] { _infoLabel, _saveLogLink, _exportLink, _primary, _secondary, _cancel });
        _footer.Resize += (_, _) => LayoutButtons();

        Controls.Add(_content);
        Controls.Add(_footer);
        Controls.Add(_progressPanel);
        Controls.Add(_bodyLabel);
        Controls.Add(_headerPanel);
        Theme.ApplyDialogTheme(this);

        _runner.Log += AppendLog;
        _scanner.Progress += AppendLog;

        Shown += async (_, _) => await BeginAsync();
        FormClosing += OnFormClosing;
    }

    private void LayoutButtons()
    {
        var y = _footer.ClientSize.Height - 44;
        _cancel.Location = new Point(_footer.ClientSize.Width - 16 - _cancel.Width, y);
        _primary.Location = new Point(_cancel.Left - 8 - _primary.Width, y);
        _secondary.Location = new Point(_primary.Left - 8 - _secondary.Width, y);
        _infoLabel.Width = _footer.ClientSize.Width - 32;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(this);
    }

    private async Task BeginAsync()
    {
        var lang = LanguageManager.Instance;
        if (_programs.Count == 1)
        {
            var program = _programs[0];
            if (!string.IsNullOrWhiteSpace(program.UninstallString) && !program.NoRemove)
            {
                SetPhase(Phase.Confirm);
                _headerLabel.Text = lang.F("UninstallWizard/captionQuestionUninstall", program.DisplayName);
                _headerSub.Text = program.DisplayVersion;
                _bodyLabel.Text = lang.T("UninstallWizard/statusUninstallInfo",
                    "Removal Wizard first launches the associated default uninstall program and cleans up leftovers afterwards.");
                return;
            }
        }
        else
        {
            SetPhase(Phase.Confirm);
            _headerLabel.Text = lang.F("UninstallWizard/captionQuestionUninstallBatch", _programs.Count);
            _headerSub.Text = string.Join(", ", _programs.Take(3).Select(p => p.DisplayName)) +
                              (_programs.Count > 3 ? "..." : "");
            _bodyLabel.Text = lang.T("UninstallWizard/statusUninstallInfoBatch",
                "Removal Wizard first launches the associated default uninstall program for each of the app. Then it displays the list of all leftovers to clean up.");
            return;
        }

        // No uninstall command: go straight to leftover scan.
        await ScanAsync();
    }

    private void SetPhase(Phase phase)
    {
        _phase = phase;
        var lang = LanguageManager.Instance;
        _progress.Visible = phase is Phase.Running or Phase.Scanning or Phase.Removing;
        _progress.Style = phase == Phase.Removing ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
        if (phase == Phase.Removing)
            _progress.Value = 0;
        _logBox.Visible = phase is Phase.Running or Phase.Removing or Phase.Done;
        _tree.Visible = phase is Phase.Results or Phase.Removing or Phase.Done;
        _primary.Visible = phase is Phase.Confirm or Phase.ScanPrompt or Phase.Results or Phase.Done;
        _secondary.Visible = phase == Phase.ScanPrompt;
        _cancel.Visible = phase is Phase.Confirm or Phase.Running or Phase.ScanPrompt or Phase.Results;
        _saveLogLink.Visible = phase is Phase.Results or Phase.Done;
        _exportLink.Visible = phase is Phase.Results or Phase.Done;

        switch (phase)
        {
            case Phase.Confirm:
                _primary.Text = lang.T("UninstallWizard/btnUninstall", "Uninstall");
                _secondary.Text = lang.T("UninstallWizardConfirmation/btnNo", "No");
                break;
            case Phase.ScanPrompt:
                _primary.Text = lang.T("UninstallWizardConfirmation/btnYes", "Yes");
                _secondary.Text = lang.T("UninstallWizardConfirmation/btnNo", "No");
                break;
            case Phase.Results:
                _primary.Text = lang.T("UninstallWizard/btnFinish", "Finish");
                break;
            case Phase.Done:
                _primary.Text = lang.T("UninstallWizard/btnClose", "Close");
                break;
        }

        LayoutButtons();
    }

    private async void OnPrimary()
    {
        try
        {
            switch (_phase)
            {
                case Phase.Confirm:
                    await RunUninstallAsync();
                    break;

                case Phase.ScanPrompt:
                    _secondary.Visible = false;
                    await ScanAsync();
                    break;

                case Phase.Results:
                    await RemoveLeftoversAsync();
                    break;

                case Phase.Done:
                    DialogResult = DialogResult.OK;
                    Close();
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            _working = false;
            _cts?.Dispose();
            _cts = null;
        }
        catch (Exception ex)
        {
            _working = false;
            _cts?.Dispose();
            _cts = null;
            if (!IsDisposed)
            {
                AppendLog("Error: " + ex.Message);
                MessageDialog.Error(this, AppInfo.Name, ex.Message);
            }
        }
    }

    private void OnCancel()
    {
        var lang = LanguageManager.Instance;

        if (_phase == Phase.Scanning)
        {
            _cts?.Cancel();
            return;
        }

        if (_phase is Phase.Running or Phase.Removing)
        {
            var confirm = MessageDialog.Show(this, lang.T("Messages/msgForceCancelWaiting",
                    "Are you sure you want to force cancel uninstallation of this program?"),
                lang.T("Messages/msgForceCancelWaiting", "Are you sure you want to force cancel uninstallation of this program?"),
                lang.T("Messages/msgForceCancelWaitingFooter",
                    "Force cancellation should be performed only when default uninstall program is freezed or you urgently need to stop all currently running uninstall processes."),
                MessageBoxButtons.YesNo, AppIcons.Warning);
            if (confirm.Result != DialogResult.Yes)
                return;
            _cts?.Cancel();
            _runner.KillAll();
            return;
        }

        DialogResult = DialogResult.Cancel;
        Close();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_working)
            return;

        var lang = LanguageManager.Instance;

        if (_phase == Phase.Scanning)
        {
            var confirmScan = MessageDialog.Show(this,
                lang.T("UninstallWizard/UninstallWizardLocalizedName", "Removal Wizard"),
                lang.T("UninstallWizard/statusScanning", "Scanning for leftover files, folders and registry items..."),
                lang.T("ForceRemovalForm/staticCancel",
                    "The operation is still running. Do you want to stop it and close the window?"),
                MessageBoxButtons.YesNo, AppIcons.Warning);
            if (confirmScan.Result != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _cts?.Cancel();
            e.Cancel = true;
            return;
        }

        var confirm = MessageDialog.Show(this, lang.T("Messages/msgForceCancelWaiting",
                "Are you sure you want to force cancel uninstallation of this program?"),
            lang.T("Messages/msgForceCancelWaiting", "Are you sure you want to force cancel uninstallation of this program?"),
            lang.T("Messages/msgForceCancelWaitingFooter",
                "Force cancellation should be performed only when default uninstall program is freezed or you urgently need to stop all currently running uninstall processes."),
            MessageBoxButtons.YesNo, AppIcons.Warning);
        if (confirm.Result != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        // Let the running operation unwind first; it closes the form itself.
        _closeRequested = _phase == Phase.Removing;
        _cts?.Cancel();
        _runner.KillAll();
        e.Cancel = true;
    }

    private async Task RunUninstallAsync()
    {
        var lang = LanguageManager.Instance;
        _working = true;
        _cts = new CancellationTokenSource();
        SetPhase(Phase.Running);
        _headerLabel.Text = _programs.Count == 1
            ? lang.F("UninstallWizard/captionUninstalling", _programs[0].DisplayName)
            : lang.T("ExplorerBar/itemBatchUninstallInProgressTip",
                "Batch uninstall in progress, please wait while removal of all apps is finished...");
        _bodyLabel.Text = lang.T("UninstallWizard/statusPerformingUninstall",
            "Performing standard uninstall using built-in uninstaller, which may require user interaction.");
        AppendLog("Removal Wizard started");

        foreach (var program in _programs)
        {
            if (_cts.IsCancellationRequested)
                break;

            _headerSub.Text = program.DisplayName;
            AppendLog("=== " + program.DisplayName + " ===");
            var token = _cts.Token;
            var outcome = await Task.Run(() => _runner.Run(program, false, true, token), token);
            if (outcome.Started)
                _uninstalled = true;

            if (outcome.Error.Length > 0)
                AppendLog("Error: " + outcome.Error);

            if (outcome.Cancelled)
            {
                _working = false;
                _cts?.Dispose();
                _cts = null;
                Close();
                return;
            }

            if (!outcome.Started || outcome.ExitCode != 0)
            {
                var failed = MessageDialog.Show(this,
                    lang.T("Messages/msgForceRemovalTitle", "Force Removal").Replace("%s", program.DisplayName),
                    lang.T("Messages/msgForceRemovalTitle", "Force Removal").Replace("%s", program.DisplayName),
                    lang.T("Messages/msgUninstallFailed",
                        "Uninstallation process failed. \nWould you like to clean this program entry from registry and clean up its files?"),
                    MessageBoxButtons.YesNo, AppIcons.Warning);
                if (failed.Result == DialogResult.Yes)
                {
                    _working = false;
                    using var force = new ForceRemovalForm(new[] { program }, _settings);
                    force.ShowDialog(this);
                    _removed = _removed || force.Removed;
                    _working = true;
                    if (force.Removed)
                        continue;
                }
            }
        }

        _working = false;
        _cts.Dispose();
        _cts = null;

        var shouldScan = ShouldScan();
        if (!shouldScan)
        {
            _removed = true;
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        await ScanAsync();
    }

    private bool ShouldScan()
    {
        var lang = LanguageManager.Instance;
        var traced = _programs.Any(p => p.IsTraced);
        if (traced)
            return true;

        switch (_settings.WizardMode)
        {
            case WizardMode.Automatic:
                return true;
            case WizardMode.Disable:
                return false;
        }

        var result = MessageDialog.Show(this,
            lang.T("UninstallWizard/UninstallWizardLocalizedName", "Removal Wizard"),
            lang.T("UninstallWizard/statusLeftOverTracesFound", "Some leftover traces found!"),
            lang.T("UninstallWizardConfirmation/staticConfirm",
                "The built-in uninstaller has finished its job. Would you like to perform a deep scan of your system registry and file system for unused registry entries and files left?"),
            MessageBoxButtons.YesNo, AppIcons.Search,
            checkboxText: lang.T("UninstallWizardConfirmation/checkDontAskAgain",
                "Remember my choice and don't ask me again (may be changed in Preferences)"));

        if (result.Checked)
        {
            _settings.WizardMode = result.Result == DialogResult.Yes ? WizardMode.Automatic : WizardMode.Disable;
            _settings.Save();
        }

        return result.Result == DialogResult.Yes;
    }

    private async Task ScanAsync()
    {
        var lang = LanguageManager.Instance;
        _working = true;
        _cts = new CancellationTokenSource();
        SetPhase(Phase.Scanning);
        _headerLabel.Text = lang.T("UninstallWizard/statusScanning",
            "Scanning for leftover files, folders and registry items...");
        _headerSub.Text = "";
        _bodyLabel.Text = lang.T("UninstallWizard/staticScanStatusGeneratingTracesList",
            "Generating traces list...");
        AppendLog("Scanning for leftovers...");

        var token = _cts.Token;
        var scan = new ScanResult();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allTraces = TraceStore.LoadAll();

        var progress = new Progress<string>(text =>
        {
            if (!IsDisposed)
                _bodyLabel.Text = text;
        });
        void OnProgress(string text) => ((IProgress<string>)progress).Report(text);
        _scanner.Progress += OnProgress;

        try
        {
            await Task.Run(() =>
            {
                foreach (var program in _programs)
                {
                    token.ThrowIfCancellationRequested();
                    var trace = TraceStore.FindFor(allTraces, program.Id, program.DisplayName);

                    var partial = _scanner.Scan(program, trace, token);
                    foreach (var child in partial.FileSystemRoot.Children)
                    {
                        if (seenPaths.Add(child.FullPath))
                            scan.FileSystemRoot.Children.Add(child);
                    }

                    foreach (var child in partial.RegistryRoot.Children)
                    {
                        if (seenKeys.Add(child.FullPath))
                            scan.RegistryRoot.Children.Add(child);
                    }
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            _working = false;
            _cts?.Dispose();
            _cts = null;
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }
        catch (Exception ex)
        {
            AppendLog("Scan error: " + ex.Message);
        }
        finally
        {
            _scanner.Progress -= OnProgress;
        }

        _scan = scan;
        _scan.FileCount = scan.FileSystemRoot.Children.Sum(c => c.TotalCount);
        _scan.RegistryCount = scan.RegistryRoot.Children.Sum(c => c.TotalCount);
        _scan.TotalSize = scan.FileSystemRoot.TotalSize;
        _tree.Load(scan);
        _working = false;
        _cts.Dispose();
        _cts = null;

        if (!scan.HasItems)
        {
            SetPhase(Phase.Done);
            _headerLabel.Text = lang.T("UninstallWizard/statusNothingFound", "No leftover traces found.");
            _bodyLabel.Text = _programs.Count == 1
                ? lang.F("UninstallWizard/staticScanStatusRemoved", _programs[0].DisplayName)
                : lang.T("UninstallWizard/staticScanStatusRemovedBatch",
                    "All selected leftovers have been successfully removed.");
            _infoLabel.Text = "";
            _tree.Visible = false;
        }
        else
        {
            SetPhase(Phase.Results);
            _headerLabel.Text = lang.T("UninstallWizard/statusLeftOverTracesFound", "Some leftover traces found!");
            _bodyLabel.Text = lang.T("UninstallWizard/staticScanStatusFound",
                "CleanRift detected that the following program traces still remain on your computer. Please review them carefully and once you are ready to remove selected items, click 'Finish'.");
            UpdateFooterInfo();
        }
    }

    private void UpdateFooterInfo()
    {
        if (_phase is not (Phase.Results or Phase.Done))
            return;
        var (files, registry, size) = _tree.GetCheckedSummary();
        _infoLabel.Text = LanguageManager.Instance.F("UninstallWizard/staticOverallInfo",
            files, InstalledProgram.FormatSize(size), registry);
    }

    private async Task RemoveLeftoversAsync()
    {
        var lang = LanguageManager.Instance;
        var items = _tree.CollectChecked();
        if (items.Count == 0)
        {
            AppendLog("Nothing selected for removal.");
            SetPhase(Phase.Done);
            _headerLabel.Text = lang.T("UninstallWizard/statusNothingFound", "No leftover traces found.");
            return;
        }

        _working = true;
        _cts = new CancellationTokenSource();
        SetPhase(Phase.Removing);
        _headerLabel.Text = lang.T("UninstallWizard/statusDeletingProgramTraces", "Deleting program traces...");
        _bodyLabel.Text = lang.T("UninstallWizard/statusDeletingProgramTraces", "Deleting program traces...");
        _progress.Style = ProgressBarStyle.Marquee;

        var engine = new RemovalEngine();
        engine.Progress += text => AppendLog(text);
        var token = _cts.Token;
        JobResult? result = null;
        try
        {
            result = await Task.Run(() => engine.RemoveItems(items, token), token);
        }
        catch (OperationCanceledException)
        {
            _working = false;
            _cts?.Dispose();
            _cts = null;
            if (_closeRequested)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }

            SetPhase(Phase.Results);
            _headerLabel.Text = lang.T("UninstallWizard/statusLeftOverTracesFound", "Some leftover traces found!");
            _bodyLabel.Text = lang.T("UninstallWizard/staticScanStatusFound",
                "CleanRift detected that the following program traces still remain on your computer. Please review them carefully and once you are ready to remove selected items, click 'Finish'.");
            UpdateFooterInfo();
            return;
        }
        catch (Exception ex)
        {
            _working = false;
            _cts?.Dispose();
            _cts = null;
            _progress.Style = ProgressBarStyle.Continuous;
            AppendLog("Removal error: " + ex.Message);
            MessageDialog.Error(this, AppInfo.Name, ex.Message);
            SetPhase(Phase.Results);
            UpdateFooterInfo();
            return;
        }

        foreach (var line in engine.Log)
            AppendLog(line);

        if (result != null)
            AppendLog($"Removed: {result.Deleted}, scheduled for reboot: {result.ScheduledForReboot}, failed: {result.Failed}");

        // Forget trace data of successfully removed traced programs.
        if (result is { Failed: 0 })
        {
            foreach (var program in _programs.Where(p => p.IsTraced))
            {
                TraceStore.Delete(program.Id, program.TraceFile);
                program.IsTraced = false;
            }
        }

        _removed = result is { Failed: 0 };
        _working = false;
        _cts.Dispose();
        _cts = null;

        SetPhase(Phase.Done);
        if (_removed)
        {
            _headerLabel.Text = lang.T("UninstallWizard/statusAllTracesRemoved", "All Traces Removed");
            _bodyLabel.Text = _programs.Count == 1
                ? lang.F("UninstallWizard/staticScanStatusRemoved", _programs[0].DisplayName)
                : lang.T("UninstallWizard/staticScanStatusRemovedBatch",
                    "All selected leftovers have been successfully removed.");
        }
        else
        {
            _headerLabel.Text = lang.T("DelayedFilesRemovalDialog/Title",
                "Some files were not deleted");
            _bodyLabel.Text = lang.T("Messages/msgPartialRemoval",
                "Some selected items could not be removed. Review the details below.");
        }

        if (_removed)
        {
            _tree.Visible = false;
            _tree.Clear();
            _infoLabel.Text = "";
        }
        else
        {
            UpdateFooterInfo();
        }

        if (result is { Failed: > 0 })
        {
            var details = string.Join(Environment.NewLine, result.Errors.Take(10));
            if (result.ScheduledForReboot > 0)
                details += Environment.NewLine + Environment.NewLine +
                           $"{result.ScheduledForReboot} item(s) are locked and will be removed after the next reboot.";
            MessageDialog.Show(this, lang.T("DelayedFilesRemovalDialog/Title", "Some files were not deleted"),
                lang.T("DelayedFilesRemovalDialog/Title", "Some files were not deleted"), details,
                MessageBoxButtons.OK, AppIcons.Warning);
        }
        else if (result is { ScheduledForReboot: > 0 })
        {
            MessageDialog.Info(this, AppInfo.Name,
                result.ScheduledForReboot + " " +
                lang.T("Messages/msgPendingReboot", "Some files will be removed after the next reboot"));
        }
    }

    private void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        _log.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
        if (_logBox.IsHandleCreated)
        {
            _logBox.AppendText(_log[^1] + Environment.NewLine);
            _logBox.ScrollToCaret();
        }
    }

    private void SaveLog()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = "cleanrift-uninstall-log.log"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                File.WriteAllText(dialog.FileName, string.Join(Environment.NewLine, _log), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                MessageDialog.Error(this, AppInfo.Name, ex.Message);
            }
        }
    }

    private void ExportXml()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
            FileName = "cleanrift-scan.xml"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                _tree.SaveXml(dialog.FileName, _programs.Count == 1 ? _programs[0].DisplayName : "Multiple");
            }
            catch (Exception ex)
            {
                MessageDialog.Error(this, AppInfo.Name, ex.Message);
            }
        }
    }
}
