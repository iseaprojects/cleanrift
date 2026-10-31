using System.Diagnostics;
using System.Drawing;
using CleanRift.Core;
using CleanRift.UI.Controls;

namespace CleanRift.UI.Forms;

/// <summary>
/// Lists removal batches kept in quarantine and lets the user restore or
/// permanently delete them.
/// </summary>
public sealed class QuarantineForm : Form
{
    private readonly ListView _list;
    private readonly Label _info;
    private readonly Button _restore;
    private readonly Button _delete;
    private readonly Button _open;
    private readonly Button _close;

    public QuarantineForm()
    {
        var lang = LanguageManager.Instance;
        var theme = Theme.Current;

        Text = lang.T("Quarantine/Caption", "Quarantine and undo");
        ClientSize = new Size(760, 430);
        MinimumSize = new Size(620, 340);
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
            Image = AppIcons.Get(AppIcons.Delete, 32),
            Location = new Point(16, 11),
            Size = new Size(32, 32),
            SizeMode = PictureBoxSizeMode.Zoom
        };
        var title = new Label
        {
            Text = lang.T("Quarantine/Caption", "Quarantine and undo"),
            Location = new Point(60, 10),
            AutoSize = false,
            Height = 26,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            UseMnemonic = false
        };
        var subtitle = new Label
        {
            Text = lang.T("Quarantine/Description",
                "Removed files and registry entries are kept here until you delete them."),
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
        _list.Columns.Add(lang.T("Quarantine/ColumnDate", "Created"), 140);
        _list.Columns.Add(lang.T("Quarantine/ColumnDescription", "Operation"), 300);
        _list.Columns.Add(lang.T("Quarantine/ColumnFiles", "Files"), 70,
            HorizontalAlignment.Right);
        _list.Columns.Add(lang.T("Quarantine/ColumnRegistry", "Registry"), 80,
            HorizontalAlignment.Right);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => RestoreSelected();
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
        _restore = Button(lang.T("Quarantine/btnRestore", "Restore"), 100);
        _delete = Button(lang.T("Quarantine/btnDelete", "Delete permanently"), 130);
        _open = Button(lang.T("Quarantine/btnOpenFolder", "Open folder"), 110);
        _close = Button(lang.T("Misc/btnClose", "Close"), 90);
        _close.DialogResult = DialogResult.Cancel;
        _restore.Click += (_, _) => RestoreSelected();
        _delete.Click += (_, _) => DeleteSelected();
        _open.Click += (_, _) => OpenFolder();
        footer.Controls.AddRange(new Control[] { _restore, _delete, _open, _close, _info });
        footer.Resize += (_, _) =>
        {
            var y = footer.ClientSize.Height - 40;
            _close.Location = new Point(footer.ClientSize.Width - 16 - _close.Width, y);
            _open.Location = new Point(_close.Left - 8 - _open.Width, y);
            _delete.Location = new Point(_open.Left - 8 - _delete.Width, y);
            _restore.Location = new Point(_delete.Left - 8 - _restore.Width, y);
            _info.Width = Math.Max(80, _restore.Left - 24);
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
        if (_list.Columns.Count < 4)
            return;
        var files = 80;
        var registry = 90;
        var date = 140;
        _list.Columns[0].Width = date;
        _list.Columns[2].Width = files;
        _list.Columns[3].Width = registry;
        _list.Columns[1].Width = Math.Max(180,
            _list.ClientSize.Width - date - files - registry - 4);
    }

    private void Reload()
    {
        var lang = LanguageManager.Instance;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var batch in QuarantineStore.ListBatches())
        {
            var item = new ListViewItem(batch.CreatedUtc.ToLocalTime().ToString("g"));
            item.SubItems.Add(batch.Description);
            item.SubItems.Add(batch.FileCount.ToString());
            item.SubItems.Add(batch.RegistryCount.ToString());
            item.Tag = batch;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        AdjustColumns();
        _info.Text = _list.Items.Count == 0
            ? lang.T("Quarantine/Empty", "Quarantine is empty.")
            : lang.F("Quarantine/Count", _list.Items.Count);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var enabled = _list.SelectedItems.Count > 0;
        _restore.Enabled = enabled;
        _delete.Enabled = enabled;
        _open.Enabled = Directory.Exists(QuarantineStore.Root);
    }

    private QuarantineBatchInfo? SelectedBatch() => _list.SelectedItems.Count == 0
        ? null
        : _list.SelectedItems[0].Tag as QuarantineBatchInfo;

    private void RestoreSelected()
    {
        var batch = SelectedBatch();
        if (batch == null)
            return;
        var lang = LanguageManager.Instance;
        var confirm = MessageDialog.Confirm(this,
            lang.T("Quarantine/Caption", "Quarantine and undo"),
            lang.T("Quarantine/btnRestore", "Restore"),
            lang.T("Quarantine/RestoreConfirm",
                "Restore all items from this operation? Existing files are never overwritten."));
        if (confirm.Result != DialogResult.Yes)
            return;

        if (QuarantineStore.RestoreBatch(batch.Id, out var error))
        {
            ActionHistory.Add(new ActionHistoryEntry
            {
                Kind = ActionHistoryKind.Restore,
                Title = batch.Description,
                QuarantineBatchId = batch.Id
            });
            ActionHistory.MarkUndoneByBatch(batch.Id);
        }
        else
        {
            MessageDialog.Error(this, AppInfo.Name,
                string.IsNullOrWhiteSpace(error)
                    ? lang.T("Quarantine/RestorePartial",
                        "Some items could not be restored.")
                    : error);
        }

        Reload();
    }

    private void DeleteSelected()
    {
        var batch = SelectedBatch();
        if (batch == null)
            return;
        var lang = LanguageManager.Instance;
        var confirm = MessageDialog.Show(this,
            lang.T("Quarantine/Caption", "Quarantine and undo"),
            lang.T("Quarantine/DeleteConfirm", "Delete this quarantine permanently?"),
            lang.T("Quarantine/DeleteDescription",
                "This cannot be undone. The files and registry backups will be erased."),
            MessageBoxButtons.YesNo, AppIcons.Warning);
        if (confirm.Result != DialogResult.Yes)
            return;

        if (QuarantineStore.DeleteBatch(batch.Id, out var error))
        {
            ActionHistory.Add(new ActionHistoryEntry
            {
                Kind = ActionHistoryKind.Delete,
                Title = batch.Description,
                QuarantineBatchId = batch.Id
            });
        }
        else
        {
            MessageDialog.Error(this, AppInfo.Name, error);
        }

        Reload();
    }

    private void OpenFolder()
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
