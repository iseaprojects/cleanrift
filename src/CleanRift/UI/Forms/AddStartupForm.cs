using System.Diagnostics;
using System.Drawing;
using CleanRift.Core;
using CleanRift.UI.Controls;

namespace CleanRift.UI.Forms;

/// <summary>
/// "Add a new startup program" dialog: startup type, executable path,
/// optional arguments and the highest-privileges flag for scheduled tasks.
/// </summary>
public sealed class AddStartupForm : Form
{
    private static readonly StartupAddType[] Types =
    {
        StartupAddType.CurrentUserRun,
        StartupAddType.AllUsersRun,
        StartupAddType.CurrentUserFolder,
        StartupAddType.AllUsersFolder,
        StartupAddType.ScheduledTask
    };

    private readonly ComboBox _type;
    private readonly TextBox _path;
    private readonly TextBox _arguments;
    private readonly CheckBox _highest;

    public AddStartupForm()
    {
        var lang = LanguageManager.Instance;
        var theme = Theme.Current;

        Text = lang.T("DlgAddNewStartupProgram/DlgAddNewStartupProgramCaption", "Add a new startup program");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 300);
        BackColor = theme.Window;
        ForeColor = theme.Text;
        Font = new Font("Segoe UI", 9f);
        Icon = AppIcons.AppIcon();
        AutoScaleMode = AutoScaleMode.Dpi;

        var header = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(ClientSize.Width, 50),
            BackColor = theme.WindowAlt
        };
        var headerIcon = new PictureBox
        {
            Image = AppIcons.Get(AppIcons.Add, 28),
            Location = new Point(14, 11),
            Size = new Size(28, 28),
            SizeMode = PictureBoxSizeMode.Zoom
        };
        var headerLabel = new Label
        {
            Text = lang.T("DlgAddNewStartupProgram/DlgAddNewStartupProgramCaption",
                "Add a new startup program"),
            Location = new Point(52, 15),
            AutoSize = true,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold)
        };
        header.Controls.AddRange(new Control[] { headerIcon, headerLabel });

        var typeLabel = new Label
        {
            Text = lang.T("DlgAddNewStartupProgram/labelStartupType", "Startup type:"),
            Location = new Point(16, 62),
            AutoSize = true
        };
        _type = new ComboBox
        {
            Location = new Point(16, 82),
            Width = ClientSize.Width - 32,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _type.Items.Add(lang.T("ExplorerBar/itemSourceRun_HKCU", "Current User Registry Run Key"));
        _type.Items.Add(lang.T("ExplorerBar/itemSourceRun_HKLM", "All Users Registry Run Key"));
        _type.Items.Add(lang.T("ExplorerBar/itemSourceStartMenu_Current", "Current User Autorun Start Menu"));
        _type.Items.Add(lang.T("ExplorerBar/itemSourceStartMenu_All", "All Users Autorun Start Menu"));
        _type.Items.Add(lang.T("MainMenu/mnuToolsTaskScheduler", "Task Scheduler"));
        _type.SelectedIndex = 0;

        var pathLabel = new Label
        {
            Text = lang.T("DlgAddNewStartupProgram/labelPathToExecutableFile",
                "Path to executable file:"),
            Location = new Point(16, 114),
            AutoSize = true
        };
        _path = new TextBox
        {
            Location = new Point(16, 134),
            Width = ClientSize.Width - 32 - 40,
            BorderStyle = BorderStyle.FixedSingle
        };
        var browse = new Button
        {
            Text = "...",
            Location = new Point(ClientSize.Width - 16 - 34, 133),
            Size = new Size(34, 24),
            FlatStyle = FlatStyle.System
        };
        browse.Click += (_, _) => Browse();

        var argumentsLabel = new Label
        {
            Text = lang.T("DlgAddNewStartupProgram/labelArguments", "Arguments (optional):"),
            Location = new Point(16, 166),
            AutoSize = true
        };
        _arguments = new TextBox
        {
            Location = new Point(16, 186),
            Width = ClientSize.Width - 32,
            BorderStyle = BorderStyle.FixedSingle
        };

        _highest = new CheckBox
        {
            Text = lang.T("DlgAddNewStartupProgram/checkHighestPriviledges",
                "Run task with the highest privileges"),
            Location = new Point(16, 216),
            AutoSize = true,
            Enabled = false,
            UseMnemonic = false
        };
        _type.SelectedIndexChanged += (_, _) =>
            _highest.Enabled = _type.SelectedIndex == Types.Length - 1;

        var separator = new Panel
        {
            Location = new Point(0, 238),
            Size = new Size(ClientSize.Width, 1),
            BackColor = theme.Border
        };

        var ok = new Button
        {
            Text = lang.T("Misc/btnOK", "OK"),
            Size = new Size(86, 30),
            Location = new Point(ClientSize.Width - 16 - 86 - 8 - 86, 250),
            FlatStyle = FlatStyle.System
        };
        var cancel = new Button
        {
            Text = lang.T("Misc/btnCancel", "Cancel"),
            DialogResult = DialogResult.Cancel,
            Size = new Size(86, 30),
            Location = new Point(ClientSize.Width - 16 - 86, 250),
            FlatStyle = FlatStyle.System
        };
        ok.Click += (_, _) => Save();

        Controls.AddRange(new Control[]
        {
            header, typeLabel, _type, pathLabel, _path, browse, argumentsLabel, _arguments,
            _highest, separator, ok, cancel
        });

        // The controls are laid out for a roomier canvas, so scale them down to
        // the compact client size while keeping the fonts readable.
        ScaleDescendants(this, 386f / 460f, 247f / 300f);
        ClientSize = new Size(386, 247);

        AcceptButton = ok;
        CancelButton = cancel;
        Theme.ApplyDialogTheme(this);
        Shown += (_, _) => _path.Focus();
    }

    private static void ScaleDescendants(Control parent, float sx, float sy)
    {
        foreach (Control control in parent.Controls)
        {
            control.Location = new Point((int)Math.Round(control.Left * sx),
                (int)Math.Round(control.Top * sy));
            control.Size = new Size(Math.Max(1, (int)Math.Round(control.Width * sx)),
                Math.Max(1, (int)Math.Round(control.Height * sy)));
            if (control.HasChildren)
                ScaleDescendants(control, sx, sy);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(this);
        Theme.ApplyDarkInputThemes(this);
    }

    private void Browse()
    {
        var lang = LanguageManager.Instance;
        using var dialog = new OpenFileDialog
        {
            Title = lang.T("DlgAddNewStartupProgram/labelPathToExecutableFile",
                "Path to executable file:"),
            Filter = lang.T("DlgAddNewStartupProgram/filterExecutableFiles", "Executable Files") +
                     " (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|" +
                     lang.T("DlgAddNewStartupProgram/filterAllFiles", "All Files") + " (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _path.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(_arguments.Text))
                _arguments.Text = "";
        }
    }

    private void Save()
    {
        var target = _path.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(target))
        {
            MessageDialog.Info(this, Text, "Please choose an executable file.");
            return;
        }

        var type = Types[Math.Max(0, _type.SelectedIndex)];
        var name = BuildName(target);
        var arguments = _arguments.Text.Trim();

        if (!StartupManager.Add(type, name, target, arguments, _highest.Checked,
                out var error, out var needsElevation))
        {
            if (needsElevation || error.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                type is StartupAddType.AllUsersRun or StartupAddType.AllUsersFolder
                    or StartupAddType.ScheduledTask)
            {
                var result = Elevation.ExecuteStartup(new StartupJob
                {
                    Action = "add",
                    AddType = type.ToString(),
                    Name = name,
                    Target = target,
                    Arguments = arguments,
                    HighestPrivileges = _highest.Checked
                });
                if (result is { Success: true })
                {
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }

                if (result?.Error is { Length: > 0 })
                    error = result.Error;
            }

            if (!string.IsNullOrWhiteSpace(error))
                MessageDialog.Error(this, Text, error);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private static string BuildName(string target)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(target);
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
                return info.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(info.ProductName))
                return info.ProductName.Trim();
        }
        catch
        {
            // ignore
        }

        return Path.GetFileNameWithoutExtension(target);
    }
}

