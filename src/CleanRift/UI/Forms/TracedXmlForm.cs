using System.Drawing;
using System.Text;
using System.Xml.Linq;
using CleanRift.Core;
using CleanRift.UI.Controls;

namespace CleanRift.UI.Forms;

/// <summary>
/// Read-only viewer for the XML file of a traced installation. The document
/// has the same structure TraceStore writes to disk.
/// </summary>
public sealed class TracedXmlForm : Form
{
    private readonly TraceInfo _trace;
    private readonly TextBox _text;

    public TracedXmlForm(TraceInfo trace)
    {
        _trace = trace;
        var lang = LanguageManager.Instance;
        var theme = Theme.Current;

        Text = lang.F("TracedXMLDialog/TracedXMLDialogCaption", trace.DisplayName);
        ClientSize = new Size(700, 540);
        MinimumSize = new Size(480, 360);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = theme.Window;
        ForeColor = theme.Text;
        Font = new Font("Segoe UI", 9f);
        Icon = AppIcons.AppIcon();
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;

        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Padding = new Padding(12, 6, 12, 0),
            Text = trace.DisplayName,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            UseMnemonic = false
        };

        _text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 8.5f),
            BackColor = theme.ListBack,
            ForeColor = theme.Text,
            BorderStyle = BorderStyle.None,
            Text = BuildXml(trace).ToString()
        };

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 2, 12, 8) };
        host.Controls.Add(_text);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(12, 8, 12, 10) };
        var save = new Button
        {
            Text = lang.T("InstallTracker/btnSaveLog", "Save Log..."),
            Size = new Size(110, 30),
            Location = new Point(12, 10),
            FlatStyle = FlatStyle.System
        };
        var close = new Button
        {
            Text = lang.T("Misc/btnClose", "Close"),
            DialogResult = DialogResult.OK,
            Size = new Size(90, 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.System
        };
        footer.Controls.Add(save);
        footer.Controls.Add(close);
        footer.Resize += (_, _) => close.Location = new Point(footer.ClientSize.Width - 12 - close.Width, 10);
        save.Click += (_, _) => SaveXml();

        Controls.Add(host);
        Controls.Add(footer);
        Controls.Add(header);
        Theme.ApplyDialogTheme(this);
        AcceptButton = close;
        CancelButton = close;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(this);
    }

    private void SaveXml()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
            FileName = SafeName(_trace.DisplayName) + "_Traces.xml"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, BuildXml(_trace).ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            MessageDialog.Error(this, Text, ex.Message);
        }
    }

    private static string SafeName(string name)
    {
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        return string.IsNullOrWhiteSpace(safe) ? "trace" : safe;
    }

    private static XDocument BuildXml(TraceInfo trace)
    {
        // CleanRift trace shape:
        // <CleanRiftTraces version="1"><Folders><Folder>…</Folder></Folders>
        // <Files><File>…</File></Files><RegistryKeys><Key>…</Key></RegistryKeys>
        // <RegistryValues><Value>name<Key>path</Key></Value></RegistryValues>
        var folders = new XElement("Folders");
        foreach (var folder in trace.Folders.Distinct(StringComparer.OrdinalIgnoreCase))
            folders.Add(new XElement("Folder", folder));

        var files = new XElement("Files");
        foreach (var file in trace.Files.Distinct(StringComparer.OrdinalIgnoreCase))
            files.Add(new XElement("File", file));

        var keys = new XElement("RegistryKeys");
        foreach (var key in trace.RegistryKeys.Distinct(StringComparer.OrdinalIgnoreCase))
            keys.Add(new XElement("Key", key));

        var values = new XElement("RegistryValues");
        foreach (var (key, name) in trace.RegistryValues
                     .Distinct()
                     .OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(new XElement("Value", name, new XElement("Key", key)));
        }

        var root = new XElement("CleanRiftTraces",
            new XAttribute("version", "1"),
            folders, files, keys, values);

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }
}
