using System.Diagnostics;
using System.Drawing;
using System.Text;
using CleanRift.Core;
using CleanRift.UI;
using CleanRift.UI.Controls;

namespace CleanRift.UI.Forms;

/// <summary>
/// Force Removal / Batch Force Removal: confirms responsibility, optionally
/// terminates running processes, scans all traces (or uses the recorded trace
/// data) and deletes the checked items.
/// </summary>
public sealed class ForceRemovalForm : Form
{
    private enum Phase
    {
        Confirm,
        Scanning,
        Results,
        Removing,
        Done
    }

    private readonly IReadOnlyList<InstalledProgram> _programs;
    private readonly AppSettings _settings;
    private readonly LeftoverScanner _scanner = new();
    private readonly List<string> _log = new();

    private readonly Label _headerLabel;
    private readonly Label _bodyLabel;
    private readonly CheckBox _understand;
    private readonly Panel _content;
    private readonly LeftoverTreeControl _tree;
    private readonly ProgressBar _progress;
    private readonly Label _infoLabel;
    private readonly Button _primary;
    private readonly Button _cancel;

    private Phase _phase = Phase.Confirm;
    private CancellationTokenSource? _cts;
    private ScanResult _scan = new();
    private bool _working;

    public bool Removed { get; private set; }

    public ForceRemovalForm(IReadOnlyList<InstalledProgram> programs, AppSettings settings)
    {
        _programs = programs;
        _settings = settings;
        var lang = LanguageManager.Instance;
        var theme = Theme.Current;

        Text = programs.Count == 1
            ? lang.F("Messages/msgForceRemovalTitle", programs[0].DisplayName)
            : lang.T("Messages/msgForceRemovalTitleBatch", "Batch Force Removal");
        ClientSize = new Size(731, 480);
        MinimumSize = new Size(700, 420);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = theme.Window;
        ForeColor = theme.Text;
        Font = new Font("Segoe UI", 9f);
        Icon = AppIcons.AppIcon();
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;

        var header = new Panel { Dock = DockStyle.Top, Height = 56, Padding = new Padding(16, 8, 16, 4) };
        var icon = new PictureBox
        {
            Image = AppIcons.Get(AppIcons.Delete, 36),
            Location = new Point(16, 12),
            Size = new Size(36, 36),
            SizeMode = PictureBoxSizeMode.Zoom
        };
        _headerLabel = new Label
        {
            Location = new Point(64, 12),
            AutoSize = false,
            Height = 38,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            UseMnemonic = false,
            Text = Text
        };
        header.Controls.AddRange(new Control[] { icon, _headerLabel });
        header.Resize += (_, _) => _headerLabel.Width = header.ClientSize.Width - 84;

        _bodyLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 118,
            Padding = new Padding(16, 0, 16, 4),
            UseMnemonic = false
        };

        _understand = new CheckBox
        {
            Dock = DockStyle.Top,
            Height = 28,
            Padding = new Padding(22, 0, 16, 0),
            Text = lang.T("Messages/msgForceRemovalCheck", "Ok, I understand the responsibility"),
            Checked = false
        };

        _progress = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 10,
            Style = ProgressBarStyle.Marquee,
            Visible = false,
            Margin = new Padding(16, 0, 16, 0)
        };
        var progressHost = new Panel { Dock = DockStyle.Top, Height = 16, Padding = new Padding(16, 2, 16, 2) };
        progressHost.Controls.Add(_progress);

        _content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 2, 16, 2) };
        _tree = new LeftoverTreeControl { Dock = DockStyle.Fill, Visible = false };
        _tree.SummaryChanged += UpdateInfo;
        _content.Controls.Add(_tree);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(16, 4, 16, 8) };
        _infoLabel = new Label
        {
            Location = new Point(16, 8),
            AutoSize = false,
            Height = 20,
            ForeColor = theme.Muted,
            UseMnemonic = false
        };
        _primary = new Button
        {
            Text = lang.T("MainMenu/mnuActionDelete", "&Force Removal").Replace("&", ""),
            Size = new Size(130, 32),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            FlatStyle = FlatStyle.System,
            Enabled = false
        };
        _primary.Click += (_, _) => OnPrimary();
        _cancel = new Button
        {
            Text = lang.T("Misc/btnCancel", "Cancel"),
            Size = new Size(100, 32),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            FlatStyle = FlatStyle.System
        };
        _cancel.Click += (_, _) => OnCancel();
        footer.Controls.AddRange(new Control[] { _infoLabel, _primary, _cancel });
        footer.Resize += (_, _) =>
        {
            _cancel.Location = new Point(footer.ClientSize.Width - 16 - _cancel.Width,
                footer.ClientSize.Height - 44);
            _primary.Location = new Point(_cancel.Left - 8 - _primary.Width,
                footer.ClientSize.Height - 44);
            _infoLabel.Width = footer.ClientSize.Width - 32;
        };

        Controls.Add(_content);
        Controls.Add(footer);
        Controls.Add(progressHost);
        Controls.Add(_understand);
        Controls.Add(_bodyLabel);
        Controls.Add(header);

        _understand.CheckedChanged += (_, _) =>
        {
            if (_phase == Phase.Confirm)
                _primary.Enabled = _understand.Checked;
        };

        var description = new StringBuilder();
        description.AppendLine(lang.T("Messages/msgForceRemovalDescription",
            "Force removal will not run the program's own uninstaller; it will only erase the file system and registry items that belong to it."));
        if (_programs.Any(p => p.IsTraced))
            description.AppendLine(lang.T("Messages/msgForceRemovalDescriptionTraced",
                "Force removal of a traced program will perform ultra fast and thorough removal without running its associated uninstaller."));
        if (_programs.Count > 1)
            description.AppendLine(lang.T("Messages/msgForceRemovalDescriptionBatch",
                "Batch Force Removal is an advanced feature and should be used carefully."));
        if (_programs.Any(p => p.LooksLikeSystemComponent()))
            description.AppendLine(lang.T("Messages/msgForceRemovalDescriptionWindowsComponent",
                "Removing this product may cause some Windows features or other software to not work properly."));

        _bodyLabel.Text = _programs.Count == 1
            ? lang.T("Messages/msgForceRemovalQuestion",
                "Are you sure you want to perform force removal of this program?") +
              Environment.NewLine + Environment.NewLine + description
            : lang.T("Messages/msgForceRemovalQuestionBatch",
                  "Are you sure you want to perform force removal of the following programs?") +
              Environment.NewLine + string.Join(Environment.NewLine,
                  _programs.Take(6).Select(p => "• " + p.DisplayName)) +
              (_programs.Count > 6 ? Environment.NewLine + "..." : "") +
              Environment.NewLine + description;

        FormClosing += OnFormClosing;
        Theme.ApplyDialogTheme(this);
        Shown += (_, _) => CheckRunningProcesses();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(this);
    }

    private void CheckRunningProcesses()
    {
        var lang = LanguageManager.Instance;
        var running = new List<RunningProcessInfo>();
        foreach (var program in _programs)
            running.AddRange(LeftoverScanner.FindRunningProcesses(program));

        running = running.GroupBy(p => p.Pid).Select(g => g.First()).ToList();
        if (running.Count == 0)
            return;

        var text = lang.T("RunningProcessesDialog/Instruction1", "The following processes were detected:") +
                   Environment.NewLine +
                   string.Join(Environment.NewLine, running.Take(10).Select(p =>
                       $"• {p.Name} (PID {p.Pid})")) +
                   Environment.NewLine + Environment.NewLine +
                   lang.T("RunningProcessesDialog/Instruction2",
                       "It's recommended to terminate them manually before removal, but Removal Wizard can force terminate them.");

        var result = MessageDialog.Show(this, lang.T("RunningProcessesDialog/Title",
                "Force terminate running processes?"),
            lang.T("RunningProcessesDialog/Title", "Force terminate running processes?"), text,
            MessageBoxButtons.YesNo, AppIcons.Warning,
            acceptText: lang.T("ForceTerminate", "Terminate"));

        if (result.Result == DialogResult.Yes)
        {
            foreach (var process in running)
            {
                try
                {
                    using var p = Process.GetProcessById(process.Pid);
                    p.Kill();
                    _log.Add("Terminated process: " + process.Name + " (" + process.Pid + ")");
                }
                catch (Exception ex)
                {
                    _log.Add("Could not terminate " + process.Name + ": " + ex.Message);
                }
            }
        }
    }

    private async void OnPrimary()
    {
        try
        {
            switch (_phase)
            {
                case Phase.Confirm:
                    await ScanAsync();
                    break;
                case Phase.Results:
                    await RemoveAsync();
                    break;
                case Phase.Done:
                    DialogResult = DialogResult.OK;
                    Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            _working = false;
            _cts?.Dispose();
            _cts = null;
            if (!IsDisposed)
            {
                _progress.Visible = false;
                MessageDialog.Error(this, AppInfo.Name, ex.Message);
                if (_phase is Phase.Scanning or Phase.Removing)
                    RestoreResultsState();
            }
        }
    }

    private async Task ScanAsync()
    {
        var lang = LanguageManager.Instance;
        _working = true;
        _cts = new CancellationTokenSource();
        _phase = Phase.Scanning;
        _understand.Visible = false;
        _progress.Visible = true;
        _primary.Visible = false;
        _headerLabel.Text = lang.T("UninstallWizard/statusScanning",
            "Scanning for leftover files, folders and registry items...");
        _bodyLabel.Text = lang.T("UninstallWizard/staticScanStatusGeneratingTracesList",
            "Generating traces list...");

        var token = _cts.Token;
        var scan = new ScanResult();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var traces = TraceStore.LoadAll();

        try
        {
            await Task.Run(() =>
            {
                foreach (var program in _programs)
                {
                    token.ThrowIfCancellationRequested();
                    var trace = TraceStore.FindFor(traces, program.Id, program.DisplayName);

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
            _log.Add("Scan error: " + ex.Message);
        }

        _scan = scan;
        _scan.FileCount = scan.FileSystemRoot.Children.Sum(c => c.TotalCount);
        _scan.RegistryCount = scan.RegistryRoot.Children.Sum(c => c.TotalCount);
        _scan.TotalSize = scan.FileSystemRoot.TotalSize;
        _tree.Load(scan);
        _tree.Visible = true;
        _progress.Visible = false;

        _working = false;
        _cts.Dispose();
        _cts = null;

        if (!scan.HasItems)
        {
            _phase = Phase.Done;
            _headerLabel.Text = lang.T("UninstallWizard/statusNothingFound", "No leftover traces found.");
            _bodyLabel.Text = "";
            _primary.Visible = true;
            _primary.Text = lang.T("UninstallWizard/btnClose", "Close");
            _cancel.Visible = false;
            return;
        }

        _phase = Phase.Results;
        _headerLabel.Text = lang.T("UninstallWizard/statusLeftOverTracesFound", "Some leftover traces found!");
        _bodyLabel.Text = lang.T("UninstallWizard/staticScanStatusFound",
            "CleanRift detected that the following program traces still remain on your computer. Please review them carefully and once you are ready to remove selected items, click 'Finish'.");
        _primary.Visible = true;
        _primary.Text = lang.T("UninstallWizard/btnFinish", "Finish");
        _primary.Enabled = true;
        _cancel.Text = lang.T("UninstallWizard/btnClose", "Close");
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        if (_phase is not (Phase.Results or Phase.Done))
            return;
        var (files, registry, size) = _tree.GetCheckedSummary();
        _infoLabel.Text = LanguageManager.Instance.F("UninstallWizard/staticOverallInfo",
            files, InstalledProgram.FormatSize(size), registry);
    }

    private async Task RemoveAsync()
    {
        var lang = LanguageManager.Instance;
        var items = _tree.CollectChecked();
        if (items.Count == 0)
        {
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        _working = true;
        _cts = new CancellationTokenSource();
        _phase = Phase.Removing;
        _primary.Visible = false;
        _cancel.Visible = true;
        _headerLabel.Text = lang.T("UninstallWizard/statusDeletingProgramTraces", "Deleting program traces...");
        _bodyLabel.Text = lang.T("UninstallWizard/statusDeletingProgramTraces", "Deleting program traces...");
        _progress.Visible = true;

        var engine = new RemovalEngine();
        JobResult? result = null;
        var token = _cts.Token;
        try
        {
            result = await Task.Run(() => engine.RemoveItems(items, token), token);
        }
        catch (OperationCanceledException)
        {
            _working = false;
            _cts?.Dispose();
            _cts = null;
            _progress.Visible = false;
            RestoreResultsState();
            return;
        }

        _working = false;
        _cts.Dispose();
        _cts = null;
        _progress.Visible = false;
        _phase = Phase.Done;
        Removed = result is { Failed: 0 };
        if (Removed)
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
        _primary.Visible = true;
        _primary.Enabled = true;
        _primary.Text = lang.T("UninstallWizard/btnClose", "Close");
        _cancel.Visible = false;
        if (Removed)
        {
            _tree.Visible = false;
            _tree.Clear();
            _infoLabel.Text = "";
        }
        else
        {
            UpdateInfo();
        }

        if (Removed)
        {
            foreach (var program in _programs.Where(p => p.IsTraced))
            {
                TraceStore.Delete(program.Id, program.TraceFile);
                program.IsTraced = false;
            }
        }
        else if (result != null)
        {
            var details = string.Join(Environment.NewLine, result.Errors.Take(12));
            if (result.ScheduledForReboot > 0)
                details += Environment.NewLine + Environment.NewLine +
                           $"{result.ScheduledForReboot} item(s) will be removed after the next reboot.";
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

    private void RestoreResultsState()
    {
        var lang = LanguageManager.Instance;
        _phase = Phase.Results;
        _headerLabel.Text = lang.T("UninstallWizard/statusLeftOverTracesFound", "Some leftover traces found!");
        _bodyLabel.Text = lang.T("UninstallWizard/staticScanStatusFound",
            "CleanRift detected that the following program traces still remain on your computer. Please review them carefully and once you are ready to remove selected items, click 'Finish'.");
        _primary.Visible = true;
        _primary.Enabled = true;
        _primary.Text = lang.T("UninstallWizard/btnFinish", "Finish");
        _cancel.Visible = true;
        _cancel.Enabled = true;
        _cancel.Text = lang.T("UninstallWizard/btnClose", "Close");
        UpdateInfo();
    }

    private void OnCancel()
    {
        if (_phase is Phase.Scanning or Phase.Removing)
        {
            _cts?.Cancel();
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
        var confirm = MessageDialog.Show(this,
            lang.T("UninstallWizard/UninstallWizardLocalizedName", "Removal Wizard"),
            lang.T("ForceRemovalForm/captionCancel", "Cancel the operation?"),
            lang.T("ForceRemovalForm/staticCancel",
                "The operation is still running. Do you want to stop it and close the window?"),
            MessageBoxButtons.YesNo, AppIcons.Warning);
        if (confirm.Result != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        // Cancel the work and veto this close; the running operation closes the
        // form once it has unwound (see the OperationCanceledException handlers).
        _cts?.Cancel();
        e.Cancel = true;
    }
}

