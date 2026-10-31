using System.Diagnostics;
using System.Drawing;
using CleanRift.Core;
using CleanRift.UI.Controls;

namespace CleanRift.UI.Forms;

/// <summary>
/// Shows the local log of removals, reverts and quarantine operations and
/// allows restoring the quarantine batch of a previous action.
/// </summary>
public sealed class ActionHistoryForm : Form
{
    private readonly ListView _list;
    private readonly Label _info;
    private readonly Button _undo;
    private readonly Button _details;
    private readonly Button _open;
    private readonly Button _clear;
    private readonly Button _close;
    private HashSet<string> _availableBatches = new(StringComparer.OrdinalIgnoreCase);

    public ActionHistoryForm()
    {
        var lang = LanguageManager.Instance;
        var theme = Theme.Current;

        Text = lang.T("History/Caption", "Action history");
        ClientSize = new Size(820, 460);
        MinimumSize = new Size(680, 360);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = theme.Window;
        ForeColor = theme.Text;
        Font = new Font("Segoe UI", 9f);
        Icon = AppIcons.AppIcon();
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;

        var header = new Panel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(16, 8, 16, 4) };
        var icon = new PictureBox
        {
            Image = AppIcons.Get(AppIcons.Tools, 32),
            Location = new Point(16, 11),
            Size = new Size(32, 32),
            SizeMode = PictureBoxSizeMode.Zoom
        };
        var title = new Label
        {
            Text = Text,
            Location = new Point(60, 10),
            AutoSize = false,
            Height = 26,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            UseMnemonic = false
        };
        var subtitle = new Label
        {
            Text = lang.T("History/Description",
                "Removals, trace reverts and quarantine operations."),
            Location = new Point(60, 34),
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
        _list.Columns.Add(lang.T("History/ColumnDate", "Date"), 140);
        _list.Columns.Add(lang.T("History/ColumnAction", "Action"), 150);
        _list.Columns.Add(lang.T("History/ColumnDetails", "Details"), 300);
        _list.Columns.Add(lang.T("History/ColumnResult", "Result"), 170);
        _list.Columns.Add(lang.T("History/ColumnUndo", "Undo"), 70);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => ShowDetails();
        _list.Resize += (_, _) => AdjustColumns();

        _info = new Label
        {
            AutoSize = false,
            Location = new Point(16, 16),
            Height = 20,
            ForeColor = theme.Muted,
            UseMnemonic = false
        };

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(16, 6, 16, 8) };
        _undo = Button(lang.T("History/btnUndo", "Undo"), 90);
        _details = Button(lang.T("History/btnDetails", "Details"), 90);
        _open = Button(lang.T("History/btnOpenQuarantine", "Open quarantine"), 120);
        _clear = Button(lang.T("History/btnClear", "Clear history"), 110);
        _close = Button(lang.T("Misc/btnClose", "Close"), 90);
        _close.DialogResult = DialogResult.Cancel;
        _undo.Click += (_, _) => UndoSelected();
        _details.Click += (_, _) => ShowDetails();
        _open.Click += (_, _) => OpenQuarantine();
        _clear.Click += (_, _) => ClearHistory();
        footer.Controls.AddRange(new Control[] { _undo, _details, _open, _clear, _close, _info });
        footer.Resize += (_, _) =>
        {
            var y = footer.ClientSize.Height - 40;
            _close.Location = new Point(footer.ClientSize.Width - 16 - _close.Width, y);
            _clear.Location = new Point(_close.Left - 8 - _clear.Width, y);
            _open.Location = new Point(_clear.Left - 8 - _open.Width, y);
            _details.Location = new Point(_open.Left - 8 - _details.Width, y);
            _undo.Location = new Point(_details.Left - 8 - _undo.Width, y);
            _info.Width = Math.Max(80, _undo.Left - 24);
        };

        Controls.Add(_list);
        Controls.Add(footer);
        Controls.Add(header);
        CancelButton = _close;

        Load += (_, _) => Reload();
        Theme.ApplyDialogTheme(this);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(this);
    }

    private static Button Button(string text, int width) => new()
    {
        Text = text,
        Size = new Size(width, 30),
        FlatStyle = FlatStyle.System,
        Anchor = AnchorStyles.Bottom | AnchorStyles.Left
    };

    private void AdjustColumns()
    {
        if (_list.Columns.Count < 5)
            return;
        var date = 140;
        var action = 150;
        var result = 170;
        var undo = 70;
        _list.Columns[0].Width = date;
        _list.Columns[1].Width = action;
        _list.Columns[3].Width = result;
        _list.Columns[4].Width = undo;
        _list.Columns[2].Width = Math.Max(180,
            _list.ClientSize.Width - date - action - result - undo - 4);
    }

    private void Reload()
    {
        var lang = LanguageManager.Instance;
        _availableBatches = QuarantineStore.ListBatches()
            .Select(batch => batch.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var entry in ActionHistory.Load())
        {
            var item = new ListViewItem(entry.TimestampUtc.ToLocalTime().ToString("g"));
            item.SubItems.Add(ActionName(entry.Kind));
            item.SubItems.Add(entry.Title);
            item.SubItems.Add(ResultText(entry));
            item.SubItems.Add(CanUndo(entry)
                ? lang.T("History/Yes", "Yes")
                : lang.T("History/No", "No"));
            item.Tag = entry;
            if (entry.Undone)
                item.ForeColor = Theme.Current.Muted;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        AdjustColumns();

        _info.Text = _list.Items.Count == 0
            ? lang.T("History/Empty", "No actions recorded yet.")
            : lang.F("History/Count", _list.Items.Count);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var entry = SelectedEntry();
        _undo.Enabled = entry != null && CanUndo(entry);
        _details.Enabled = entry != null;
        _open.Enabled = Directory.Exists(QuarantineStore.Root);
        _clear.Enabled = _list.Items.Count > 0;
    }

    private ActionHistoryEntry? SelectedEntry() => _list.SelectedItems.Count == 0
        ? null
        : _list.SelectedItems[0].Tag as ActionHistoryEntry;

    private bool CanUndo(ActionHistoryEntry entry) =>
        !entry.Undone &&
        !string.IsNullOrWhiteSpace(entry.QuarantineBatchId) &&
        _availableBatches.Contains(entry.QuarantineBatchId);

    private string ActionName(ActionHistoryKind kind)
    {
        var lang = LanguageManager.Instance;
        return kind switch
        {
            ActionHistoryKind.Revert => lang.T("History/ActionRevert", "Trace revert"),
            ActionHistoryKind.Queue => lang.T("History/ActionQueue", "Uninstall queue"),
            ActionHistoryKind.Restore => lang.T("History/ActionRestore", "Quarantine restore"),
            ActionHistoryKind.Delete => lang.T("History/ActionDelete", "Quarantine delete"),
            ActionHistoryKind.RemoveEntry => lang.T("History/ActionRemoveEntry", "Remove entry"),
            _ => lang.T("History/ActionRemoval", "Removal")
        };
    }

    private string ResultText(ActionHistoryEntry entry)
    {
        var lang = LanguageManager.Instance;
        if (entry.Undone)
            return lang.T("History/ResultUndone", "Undone");
        if (entry.Failed > 0)
            return entry.Deleted > 0
                ? lang.T("History/ResultPartial", "Partially completed")
                : lang.T("History/ResultFailed", "Failed");
        return lang.T("History/ResultSuccess", "Completed");
    }

    private void UndoSelected()
    {
        var entry = SelectedEntry();
        if (entry == null || !CanUndo(entry))
        {
            MessageDialog.Show(this, AppInfo.Name,
                LanguageManager.Instance.T("History/btnUndo", "Undo"),
                LanguageManager.Instance.T("History/UndoUnavailable",
                    "This action has no quarantine batch to restore."),
                MessageBoxButtons.OK, AppIcons.Info);
            return;
        }

        var lang = LanguageManager.Instance;
        if (MessageDialog.Confirm(this,
                lang.T("History/Caption", "Action history"),
                lang.T("History/btnUndo", "Undo"),
                lang.T("History/UndoConfirm",
                    "Restore all items from this action?")).Result != DialogResult.Yes)
            return;

        if (QuarantineStore.RestoreBatch(entry.QuarantineBatchId, out var error))
        {
            ActionHistory.MarkUndoneByBatch(entry.QuarantineBatchId);
            Reload();
        }
        else
        {
            MessageDialog.Error(this, AppInfo.Name,
                string.IsNullOrWhiteSpace(error)
                    ? lang.T("Quarantine/RestorePartial",
                        "Some items could not be restored.")
                    : error);
        }
    }

    private void ShowDetails()
    {
        var entry = SelectedEntry();
        if (entry == null)
            return;

        var lang = LanguageManager.Instance;
        var errors = entry.Errors.Count == 0
            ? lang.T("History/None", "None")
            : string.Join(Environment.NewLine, entry.Errors.Take(20));
        var text = lang.F("History/DetailsFormat",
            entry.TimestampUtc.ToLocalTime().ToString("g"),
            ActionName(entry.Kind),
            entry.Title,
            entry.Deleted,
            entry.ScheduledForReboot,
            entry.Failed,
            string.IsNullOrWhiteSpace(entry.QuarantineBatchId)
                ? lang.T("History/None", "None")
                : entry.QuarantineBatchId,
            errors);
        MessageDialog.Show(this, AppInfo.Name,
            lang.T("History/DetailsTitle", "Action details"),
            text, MessageBoxButtons.OK, AppIcons.Info);
    }

    private void ClearHistory()
    {
        var lang = LanguageManager.Instance;
        if (MessageDialog.Show(this,
                lang.T("History/Caption", "Action history"),
                lang.T("History/ClearConfirm", "Delete the entire action history?"),
                lang.T("History/ClearDescription",
                    "Quarantine batches are not deleted; only the list of actions is cleared."),
                MessageBoxButtons.YesNo, AppIcons.Warning).Result != DialogResult.Yes)
            return;

        ActionHistory.Clear();
        Reload();
    }

    private void OpenQuarantine()
    {
        try
        {
            Directory.CreateDirectory(QuarantineStore.Root);
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "explorer.exe"),
                Arguments = "\"" + QuarantineStore.Root + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageDialog.Error(this, AppInfo.Name, ex.Message);
        }
    }
}
