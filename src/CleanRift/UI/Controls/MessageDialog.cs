using System.Drawing;
using CleanRift.Core;
using CleanRift.UI;

namespace CleanRift.UI.Controls;

public sealed class MessageDialogResult
{
    public DialogResult Result { get; init; }
    public bool Checked { get; init; }
}

/// <summary>
/// Themed replacement for MessageBox, used for instruction dialogs with a
/// title, bold instruction, description and optional "I understand" checkbox.
/// </summary>
public sealed class MessageDialog : Form
{
    private readonly CheckBox? _check;
    private readonly Label _body;

    private MessageDialog(string title, string header, string text, MessageBoxButtons buttons,
        int iconId, string? checkboxText, string? footer, string? acceptText, string? cancelText)
    {
        var theme = Theme.Current;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = theme.Window;
        ForeColor = theme.Text;
        Font = new Font("Segoe UI", 9f);
        Icon = AppIcons.AppIcon();

        var width = 500;
        var left = 24;
        var textLeft = iconId > 0 ? 78 : left;
        var textWidth = width - textLeft - 24;
        var y = 22;

        if (iconId > 0)
        {
            var picture = new PictureBox
            {
                Image = AppIcons.Get(iconId, 40),
                Location = new Point(left, y - 2),
                Size = new Size(40, 40),
                SizeMode = PictureBoxSizeMode.Zoom
            };
            Controls.Add(picture);
        }

        var headerLabel = new Label
        {
            Text = header,
            Location = new Point(textLeft, y),
            AutoSize = false,
            Width = textWidth,
            Height = 24,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = theme.Text,
            UseMnemonic = false
        };
        Controls.Add(headerLabel);
        y += 30;

        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
        var bodyHeight = string.IsNullOrEmpty(text)
            ? 0
            : TextRenderer.MeasureText(text, Font, new Size(textWidth, 1000), flags).Height;
        _body = new Label
        {
            Text = text,
            Location = new Point(textLeft, y),
            AutoSize = false,
            Width = textWidth,
            Height = Math.Max(18, bodyHeight),
            ForeColor = theme.Text,
            UseMnemonic = false
        };
        Controls.Add(_body);
        y += _body.Height + 6;

        if (!string.IsNullOrEmpty(footer))
        {
            var footerLabel = new Label
            {
                Text = footer,
                Location = new Point(textLeft, y),
                AutoSize = false,
                Width = textWidth,
                ForeColor = theme.Muted,
                Font = new Font("Segoe UI", 8.5f),
                UseMnemonic = false
            };
            var footerHeight = TextRenderer.MeasureText(footer, footerLabel.Font,
                new Size(textWidth, 1000), flags).Height;
            footerLabel.Height = Math.Max(16, footerHeight);
            Controls.Add(footerLabel);
            y += footerLabel.Height + 6;
        }

        if (!string.IsNullOrEmpty(checkboxText))
        {
            _check = new CheckBox
            {
                Text = checkboxText,
                Location = new Point(left, y),
                AutoSize = true,
                ForeColor = theme.Text
            };
            Controls.Add(_check);
            y += 30;
        }

        var buttonRow = y + 6;
        var buttonWidth = 96;
        var buttonsList = CreateButtons(buttons, acceptText, cancelText);
        var x = width - 24 - buttonWidth;
        foreach (var spec in buttonsList.AsEnumerable().Reverse())
        {
            var button = new Button
            {
                Text = spec.Text,
                DialogResult = spec.Result,
                Location = new Point(x, buttonRow),
                Size = new Size(buttonWidth, 30),
                FlatStyle = FlatStyle.System
            };
            Controls.Add(button);
            if (spec.IsDefault)
                AcceptButton = button;
            if (spec.Result == DialogResult.Cancel)
                CancelButton = button;
            x -= buttonWidth + 8;
        }

        var separator = new Panel
        {
            Location = new Point(0, buttonRow - 12),
            Size = new Size(width, 1),
            BackColor = theme.Border
        };
        Controls.Add(separator);

        ClientSize = new Size(width, buttonRow + 46);
        Theme.ApplyDialogTheme(this);
    }

    private static List<(string Text, DialogResult Result, bool IsDefault)> CreateButtons(
        MessageBoxButtons buttons, string? acceptText, string? cancelText)
    {
        var lang = LanguageManager.Instance;
        var ok = acceptText ?? lang.T("Misc/btnOK", "OK");
        var cancel = cancelText ?? lang.T("Misc/btnCancel", "Cancel");
        var yes = acceptText ?? lang.T("Misc/btnYes", "Yes");
        var no = cancelText ?? lang.T("Misc/btnNo", "No");

        return buttons switch
        {
            MessageBoxButtons.OK => new List<(string, DialogResult, bool)> { (ok, DialogResult.OK, true) },
            MessageBoxButtons.OKCancel => new List<(string, DialogResult, bool)>
            {
                (ok, DialogResult.OK, true), (cancel, DialogResult.Cancel, false)
            },
            MessageBoxButtons.YesNo => new List<(string, DialogResult, bool)>
            {
                (yes, DialogResult.Yes, true), (no, DialogResult.No, false)
            },
            MessageBoxButtons.YesNoCancel => new List<(string, DialogResult, bool)>
            {
                (yes, DialogResult.Yes, true), (no, DialogResult.No, false), (cancel, DialogResult.Cancel, false)
            },
            _ => new List<(string, DialogResult, bool)> { (ok, DialogResult.OK, true) }
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(this);
    }

    public static MessageDialogResult Show(IWin32Window? owner, string title, string header, string text,
        MessageBoxButtons buttons = MessageBoxButtons.OK, int iconId = AppIcons.Info,
        string? checkboxText = null, string? footer = null, string? acceptText = null,
        string? cancelText = null)
    {
        using var dialog = new MessageDialog(title, header, text, buttons, iconId, checkboxText,
            footer, acceptText, cancelText);
        var result = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return new MessageDialogResult { Result = result, Checked = dialog._check?.Checked ?? false };
    }

    public static void Info(IWin32Window? owner, string title, string text, string? header = null)
    {
        Show(owner, title, header ?? title, text, MessageBoxButtons.OK, AppIcons.Info);
    }

    public static void Error(IWin32Window? owner, string title, string text, string? header = null)
    {
        Show(owner, title, header ?? title, text, MessageBoxButtons.OK, AppIcons.Error);
    }

    public static MessageDialogResult Confirm(IWin32Window? owner, string title, string header, string text,
        string? footer = null, string? acceptText = null)
    {
        return Show(owner, title, header, text, MessageBoxButtons.YesNo, AppIcons.Warning,
            footer: footer, acceptText: acceptText);
    }

    public static string? Prompt(IWin32Window? owner, string title, string header, string label,
        string initial = "")
    {
        using var prompt = new PromptDialog(title, header, label, initial);
        return prompt.ShowDialog(owner) == DialogResult.OK ? prompt.Value : null;
    }

    private sealed class PromptDialog : Form
    {
        private readonly TextBox _text;

        public string Value => _text.Text.Trim();

        public PromptDialog(string title, string header, string label, string initial)
        {
            var theme = Theme.Current;
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(460, 168);
            BackColor = theme.Window;
            ForeColor = theme.Text;
            Font = new Font("Segoe UI", 9f);
            Icon = AppIcons.AppIcon();

            var titleLabel = new Label
            {
                Text = header,
                Location = new Point(20, 18),
                AutoSize = false,
                Width = 420,
                Height = 34,
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold)
            };
            var labelControl = new Label
            {
                Text = label,
                Location = new Point(20, 54),
                AutoSize = true,
                ForeColor = theme.Muted
            };
            _text = new TextBox
            {
                Text = initial,
                Location = new Point(20, 76),
                Width = 420,
                BorderStyle = BorderStyle.FixedSingle
            };
            var ok = new Button
            {
                Text = LanguageManager.Instance.T("Misc/btnOK", "OK"),
                DialogResult = DialogResult.OK,
                Location = new Point(270, 118),
                Size = new Size(80, 30),
                FlatStyle = FlatStyle.System
            };
            var cancel = new Button
            {
                Text = LanguageManager.Instance.T("Misc/btnCancel", "Cancel"),
                DialogResult = DialogResult.Cancel,
                Location = new Point(360, 118),
                Size = new Size(80, 30),
                FlatStyle = FlatStyle.System
            };

            Controls.AddRange(new Control[] { titleLabel, labelControl, _text, ok, cancel });
            Theme.ApplyDialogTheme(this);
            AcceptButton = ok;
            CancelButton = cancel;
            Shown += (_, _) => { _text.Focus(); _text.SelectAll(); };
        }
    }
}
