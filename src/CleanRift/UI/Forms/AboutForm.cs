using System.Diagnostics;
using System.Drawing;
using CleanRift.Core;
using CleanRift.UI.Controls;

namespace CleanRift.UI.Forms;

/// <summary>
/// About dialog: product identity, beta warning, MIT license and local help.
/// It intentionally contains no links to third-party products.
/// </summary>
public sealed class AboutForm : Form
{
    private readonly Icon? _windowIcon;
    private readonly Image? _iconImage;

    public AboutForm()
    {
        var lang = LanguageManager.Instance;
        var theme = Theme.Current;
        var spanish = lang.LangId is 1034 or 3082;

        Text = spanish ? "Acerca de CleanRift" : "About CleanRift";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(400, 320);
        BackColor = theme.Window;
        ForeColor = theme.Text;
        Font = new Font("Segoe UI", 9f);
        _windowIcon = AppIcons.AppIcon();
        Icon = _windowIcon;
        AutoScaleMode = AutoScaleMode.Dpi;

        _iconImage = _windowIcon?.ToBitmap();
        var icon = new PictureBox
        {
            Image = _iconImage,
            Location = new Point(14, 10),
            Size = new Size(42, 42),
            SizeMode = PictureBoxSizeMode.Zoom
        };
        Controls.Add(icon);

        var title = new Label
        {
            Text = AppInfo.Name,
            Location = new Point(68, 6),
            AutoSize = true,
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            UseMnemonic = false
        };
        Controls.Add(title);

        var version = new Label
        {
            Text = (spanish ? "Versión " : "Version ") + AppInfo.Version,
            Location = new Point(70, 36),
            AutoSize = true,
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.Muted,
            UseMnemonic = false
        };
        Controls.Add(version);

        var tagline = new Label
        {
            Text = spanish
                ? "Desinstala programas por completo, rastrea instalaciones y gestiona el arranque."
                : "Remove programs completely, trace installations and manage startup items.",
            Location = new Point(70, 56),
            AutoSize = false,
            Width = ClientSize.Width - 84,
            Height = 30,
            ForeColor = theme.Muted,
            UseMnemonic = false
        };
        Controls.Add(tagline);

        var separator = new Panel
        {
            Location = new Point(0, 92),
            Size = new Size(ClientSize.Width, 1),
            BackColor = theme.Border
        };
        Controls.Add(separator);

        var description = new Label
        {
            Text = spanish
                ? "CleanRift encuentra programas instalados, elimina sus restos y revisa los cambios " +
                  "que hace cada instalación."
                : "CleanRift finds installed programs, removes their leftovers and reviews the " +
                  "changes made by each installation.",
            Location = new Point(16, 102),
            AutoSize = false,
            Width = ClientSize.Width - 32,
            Height = 42,
            UseMnemonic = false
        };
        Controls.Add(description);

        var beta = new Label
        {
            Text = spanish
                ? "Versión beta. Las operaciones de borrado son irreversibles: haz una copia " +
                  "de seguridad de los datos importantes antes de continuar."
                : "Beta software. Removal operations are irreversible: back up important data " +
                  "before you continue.",
            Location = new Point(16, 150),
            AutoSize = false,
            Width = ClientSize.Width - 32,
            Height = 42,
            ForeColor = theme.Text,
            UseMnemonic = false
        };
        Controls.Add(beta);

        var license = new Label
        {
            Text = spanish
                ? "Licencia MIT. Proyecto independiente, sin relación con ningún fabricante " +
                  "de software de terceros."
                : "MIT License. Independent project, not affiliated with any third-party " +
                  "software vendor.",
            Location = new Point(16, 198),
            AutoSize = false,
            Width = ClientSize.Width - 32,
            Height = 32,
            ForeColor = theme.Muted,
            Font = new Font("Segoe UI", 8.5f),
            UseMnemonic = false
        };
        Controls.Add(license);

        var licenseLink = new LinkLabel
        {
            Text = spanish ? "Ver licencia" : "View license",
            Location = new Point(16, 236),
            AutoSize = true
        };
        licenseLink.LinkClicked += (_, _) => OpenLocal("LICENSE.txt");
        Controls.Add(licenseLink);

        var helpLink = new LinkLabel
        {
            Text = spanish ? "Guía rápida" : "Quick start",
            Location = new Point(116, 236),
            AutoSize = true
        };
        helpLink.LinkClicked += (_, _) => OpenLocal("README.txt");
        Controls.Add(helpLink);

        var copyright = new Label
        {
            Text = (spanish ? "© 2026 colaboradores de CleanRift" : "© 2026 CleanRift contributors"),
            Location = new Point(16, 258),
            AutoSize = true,
            ForeColor = theme.Muted,
            Font = new Font("Segoe UI", 8f),
            UseMnemonic = false
        };
        Controls.Add(copyright);

        var footer = new Panel
        {
            Location = new Point(0, 278),
            Size = new Size(ClientSize.Width, 1),
            BackColor = theme.Border
        };
        Controls.Add(footer);

        var ok = new Button
        {
            Text = lang.T("Misc/btnOK", "OK"),
            DialogResult = DialogResult.OK,
            Size = new Size(84, 26),
            Location = new Point(ClientSize.Width - 16 - 84, ClientSize.Height - 12 - 26),
            FlatStyle = FlatStyle.System
        };
        Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = ok;

        Theme.ApplyDialogTheme(this);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(this);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _iconImage?.Dispose();
            _windowIcon?.Dispose();
        }
    }

    private void OpenLocal(string fileName)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, fileName);
            if (!File.Exists(path))
            {
                MessageDialog.Info(this, AppInfo.Name,
                    (LanguageManager.Instance.LangId is 1034 or 3082)
                        ? "El archivo no está disponible en esta instalación."
                        : "The file is not available in this installation.");
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch
        {
            // A missing default handler must not crash the dialog.
        }
    }
}
