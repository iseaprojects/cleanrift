using System.Drawing;
using CleanRift.Core;
using CleanRift.UI.Controls;

namespace CleanRift.UI.Forms;

/// <summary>
/// Runs standard uninstallers for several selected programs, one after another,
/// with per-program status and cancellation.
/// </summary>
public sealed class UninstallQueueForm : Form
{
    private enum ItemState
    {
        Pending,
        Running,
        Done,
        Failed,
        Cancelled
    }

    private readonly AppSettings _settings;
    private readonly List<InstalledProgram> _programs;
    private readonly UninstallRunner _runner = new();
    private readonly ListView _list;
    private readonly Label _status;
    private readonly Button _start;
    private readonly Button _stop;
    private readonly Button _close;
    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _closeAfterCancel;

    public bool Removed { get; private set; }

    public UninstallQueueForm(IReadOnlyList<InstalledProgram> programs, AppSettings settings)
    {
        _settings = settings;
        _programs = programs.ToList();
        var lang = LanguageManager.Instance;
        var theme = Theme.Current;

        Text = lang.T("Queue/Caption", "Uninstall queue");
        ClientSize = new Size(650, 430);
        MinimumSize = new Size(540, 320);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = theme.Window;
        ForeColor = theme.Text;
        Font = new Font("Segoe UI", 9f);
        Icon = AppIcons.AppIcon();
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;

        var header = new Panel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(16, 8, 16, 4) };
        var icon = new PictureBox
        {
            Image = AppIcons.Get(AppIcons.Uninstaller, 32),
            Location = new Point(16, 10),
            Size = new Size(32, 32),
            SizeMode = PictureBoxSizeMode.Zoom
        };
        var title = new Label
        {
            Text = Text,
            Location = new Point(60, 8),
            AutoSize = false,
            Height = 24,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            UseMnemonic = false
        };
        var subtitle = new Label
        {
            Text = lang.T("Queue/Description",
                "Uninstalls run one after another. Failures do not stop the queue."),
            Location = new Point(60, 31),
            AutoSize = false,
            Height = 18,
            ForeColor = theme.Muted,
            UseMnemonic = false
        };
        header.Controls.AddRange(new Control[] { icon, title, subtitle });
        header.Resize += (_, _) =>
        {
            title.Width = header.ClientSize.Width - 80;
            subtitle.Width = header.ClientSize.Width - 80;
        };

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = theme.ListBack,
            ForeColor = theme.Text
        };
        _list.Columns.Add(lang.T("ListCtrl/columnProgramName", "Program name"), 390);
        _list.Columns.Add(lang.T("Queue/ColumnStatus", "Status"), 110);
        _list.DoubleClick += (_, _) => ShowSelectedError();
        _list.Resize += (_, _) => AdjustColumns();

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            Padding = new Padding(16, 4, 16, 0),
            ForeColor = theme.Muted,
            UseMnemonic = false
        };

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(16, 6, 16, 8) };
        _start = new Button
        {
            Text = lang.T("Queue/btnStart", "Start uninstall"),
            Size = new Size(130, 30),
            FlatStyle = FlatStyle.System
        };
        _stop = new Button
        {
            Text = lang.T("InstallTracker/btnStop", "Stop"),
            Size = new Size(100, 30),
            FlatStyle = FlatStyle.System,
            Enabled = false
        };
        _close = new Button
        {
            Text = lang.T("Misc/btnClose", "Close"),
            Size = new Size(90, 30),
            FlatStyle = FlatStyle.System
        };
        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += (_, _) => Stop();
        _close.Click += (_, _) => Close();
        footer.Controls.AddRange(new Control[] { _start, _stop, _close });
        footer.Resize += (_, _) =>
        {
            var y = footer.ClientSize.Height - 40;
            _close.Location = new Point(footer.ClientSize.Width - 16 - _close.Width, y);
            _stop.Location = new Point(_close.Left - 8 - _stop.Width, y);
            _start.Location = new Point(_stop.Left - 8 - _start.Width, y);
        };

        Controls.Add(_list);
        Controls.Add(_status);
        Controls.Add(footer);
        Controls.Add(header);
        CancelButton = _close;
        FormClosing += OnFormClosing;
        _runner.Log += text =>
        {
            if (IsHandleCreated && InvokeRequired)
            {
                try { BeginInvoke((Action)(() => _status.Text = text)); } catch { }
            }
            else
            {
                _status.Text = text;
            }
        };

        Reload();
        Theme.ApplyDialogTheme(this);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(this);
    }

    private void AdjustColumns()
    {
        if (_list.Columns.Count < 2)
            return;
        var status = Math.Clamp(_list.ClientSize.Width / 5, 90, 140);
        _list.Columns[1].Width = status;
        _list.Columns[0].Width = Math.Max(160,
            _list.ClientSize.Width - status - 4);
    }

    private void Reload()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var program in _programs)
        {
            var item = new ListViewItem(program.DisplayName) { Tag = program };
            item.SubItems.Add(StatusText(ItemState.Pending));
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        AdjustColumns();
        _status.Text = string.Empty;
    }

    private async Task StartAsync()
    {
        if (_running)
            return;

        _running = true;
        _start.Enabled = false;
        _stop.Enabled = true;
        _close.Enabled = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var lang = LanguageManager.Instance;

        foreach (var program in _programs)
        {
            if (token.IsCancellationRequested)
                break;

            var item = Find(program);
            if (item == null || item.SubItems[1].Text == StatusText(ItemState.Done))
                continue;
            SetState(item, ItemState.Running);
            _status.Text = lang.F("Queue/Running", program.DisplayName);

            var quiet = _settings.SilentUninstall &&
                        (!string.IsNullOrWhiteSpace(program.QuietUninstallString) ||
                         program.WindowsInstaller);
            UninstallOutcome outcome;
            try
            {
                outcome = await Task.Run(() => _runner.Run(program, quiet, true, token), token);
            }
            catch (OperationCanceledException)
            {
                SetState(item, ItemState.Cancelled);
                break;
            }
            catch (Exception ex)
            {
                SetState(item, ItemState.Failed);
                item.ToolTipText = ex.Message;
                continue;
            }

            if (outcome.Cancelled)
            {
                SetState(item, ItemState.Cancelled);
                break;
            }

            if (outcome.Started && outcome.ExitCode is 0 or 3010 or 1641 &&
                string.IsNullOrEmpty(outcome.Error))
            {
                SetState(item, ItemState.Done);
                Removed = true;
            }
            else
            {
                SetState(item, ItemState.Failed);
                item.ToolTipText = outcome.Error;
            }
        }

        _running = false;
        _start.Enabled = true;
        _stop.Enabled = false;
        _close.Enabled = true;
        _cts?.Dispose();
        _cts = null;

        var doneText = StatusText(ItemState.Done);
        var failedText = StatusText(ItemState.Failed);
        var done = _list.Items.Cast<ListViewItem>()
            .Count(i => i.SubItems[1].Text == doneText);
        var failed = _list.Items.Cast<ListViewItem>()
            .Count(i => i.SubItems[1].Text == failedText);
        if (done + failed > 0)
        {
            ActionHistory.Add(new ActionHistoryEntry
            {
                Kind = ActionHistoryKind.Queue,
                Title = string.Join(", ", _programs.Take(4).Select(p => p.DisplayName)) +
                        (_programs.Count > 4 ? "..." : ""),
                Deleted = done,
                Failed = failed
            });
        }

        if (_closeAfterCancel)
        {
            _closeAfterCancel = false;
            Close();
            return;
        }

        _status.Text = lang.F("Queue/Finished", done);
    }

    private void Stop()
    {
        if (!_running)
            return;
        var lang = LanguageManager.Instance;
        if (MessageDialog.Confirm(this, Text,
                lang.T("InstallTracker/btnStop", "Stop"),
                lang.T("Queue/StopDescription",
                    "Stop the queue after the current uninstaller has been cancelled?")).Result !=
            DialogResult.Yes)
            return;

        _stop.Enabled = false;
        _cts?.Cancel();
        _runner.KillAll();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_running)
            return;

        var lang = LanguageManager.Instance;
        if (MessageDialog.Confirm(this, Text,
                lang.T("InstallTracker/btnStop", "Stop"),
                lang.T("Queue/CloseDescription",
                    "The queue is still running. Stop it and close the window?")).Result !=
            DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        _closeAfterCancel = true;
        _cts?.Cancel();
        _runner.KillAll();
        e.Cancel = true;
    }

    private ListViewItem? Find(InstalledProgram program) =>
        _list.Items.Cast<ListViewItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, program));

    private void SetState(ListViewItem item, ItemState state)
    {
        item.SubItems[1].Text = StatusText(state);
        item.ForeColor = state switch
        {
            ItemState.Failed => Theme.Current.IsDark ? Color.Salmon : Color.Firebrick,
            ItemState.Done => Theme.Current.IsDark ? Color.LightGreen : Color.DarkGreen,
            _ => Theme.Current.Text
        };
        item.EnsureVisible();
    }

    private static string StatusText(ItemState state)
    {
        var lang = LanguageManager.Instance;
        return state switch
        {
            ItemState.Running => lang.T("Queue/StatusRunning", "Running"),
            ItemState.Done => lang.T("Queue/StatusDone", "Done"),
            ItemState.Failed => lang.T("Queue/StatusFailed", "Failed"),
            ItemState.Cancelled => lang.T("Queue/StatusCancelled", "Cancelled"),
            _ => lang.T("Queue/StatusPending", "Pending")
        };
    }

    private void ShowSelectedError()
    {
        if (_list.SelectedItems.Count == 0)
            return;
        var item = _list.SelectedItems[0];
        if (!string.IsNullOrWhiteSpace(item.ToolTipText) &&
            item.SubItems[1].Text == StatusText(ItemState.Failed))
            MessageDialog.Error(this, AppInfo.Name, item.ToolTipText);
    }
}
