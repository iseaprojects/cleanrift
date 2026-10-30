using System.Globalization;
using System.Net;
using System.Text;

namespace CleanRift.Core;

/// <summary>
/// Writes self-contained HTML reports for the installed programs and startup
/// lists. The markup and styles are CleanRift's own and do not reference any
/// third-party product.
/// </summary>
public static class HtmlExporter
{
    private const string ProductName = "CleanRift";

    private const string ProgramsHeader = """
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta name="generator" content="CleanRift 1.0">
        <title>%title%</title>
        <style>
        body{font-family:"Segoe UI",Tahoma,sans-serif;font-size:9pt;color:#1f2a2e;margin:24px;}
        h1{font-size:16pt;margin:0 0 4px;color:#0f6b78;}
        .meta{color:#5b6b70;margin:0 0 18px;}
        table{border-collapse:collapse;width:100%;max-width:980px;}
        th{background:#e6f2f4;text-align:left;padding:8px 10px;border-bottom:2px solid #0f6b78;font-weight:600;}
        td{padding:8px 10px;border-bottom:1px solid #d9e4e7;vertical-align:top;}
        tr:nth-child(even) td{background:#f7fafb;}
        .size,.date,.traced{white-space:nowrap;}
        .footer{margin-top:18px;color:#5b6b70;font-size:8pt;}
        .badge32{color:#6b7c81;}
        </style>
        </head>
        <body>
        <h1>%title%</h1>
        <p class="meta">%info%</p>
        <table>
        <thead><tr><th colspan="2">%program_name%</th><th class="size">%size%</th><th class="date">%installed_on%</th><th class="traced">%traced%</th></tr></thead>
        <tbody>

        """;

    private const string ProgramsRow = """
        <tr>%padding%<td>%program_name%</td><td class="size">%size%</td><td class="date">%installed_on%</td><td class="traced">%traced%</td></tr>

        """;

    private const string StartupHeader = """
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta name="generator" content="CleanRift 1.0">
        <title>%title%</title>
        <style>
        body{font-family:"Segoe UI",Tahoma,sans-serif;font-size:9pt;color:#1f2a2e;margin:24px;}
        h1{font-size:16pt;margin:0 0 4px;color:#0f6b78;}
        .meta{color:#5b6b70;margin:0 0 18px;}
        table{border-collapse:collapse;width:100%;max-width:980px;}
        th{background:#e6f2f4;text-align:left;padding:8px 10px;border-bottom:2px solid #0f6b78;font-weight:600;}
        td{padding:8px 10px;border-bottom:1px solid #d9e4e7;vertical-align:top;}
        tr:nth-child(even) td{background:#f7fafb;}
        .status,.source,.company{white-space:nowrap;}
        .footer{margin-top:18px;color:#5b6b70;font-size:8pt;}
        </style>
        </head>
        <body>
        <h1>%title%</h1>
        <p class="meta">%info%</p>
        <table>
        <thead><tr><th colspan="2">%program_name%</th><th class="status">%status%</th><th class="source">%source%</th><th class="company">%company%</th></tr></thead>
        <tbody>

        """;

    private const string StartupRow = """
        <tr>%padding%<td>%program_name%</td><td class="status">%status%</td><td class="source">%source%</td><td class="company">%company%</td></tr>

        """;

    private const string Footer = """
        </tbody></table><hr class="footer"><p class="footer">%footer_text%</p></body></html>
        """;

    private const string PaddingCell = """<td class="pad"></td>""";
    private const string Bit32Markup = """ <span class="badge32">%s</span>""";

    public static string ExportPrograms(string filePath, IReadOnlyList<InstalledProgram> programs)
    {
        var lang = LanguageManager.Instance;
        var apps = programs.Where(p => !p.IsChild)
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var header = PrepareTemplate(ProgramsHeader);
        header = Replace(header, "%title%", Escape(lang.F("HTMLReport/installedPrograms", apps.Count)));
        header = Replace(header, "%info%", BuildInfo());
        header = Replace(header, "%program_name%", Escape(lang.T("ListCtrl/columnProgramName", "Program name")));
        header = Replace(header, "%size%", Escape(lang.T("ListCtrl/columnSize", "Size")));
        header = Replace(header, "%installed_on%",
            Escape(lang.T("ListCtrl/columnInstallationDate", "Installed on")));
        header = Replace(header, "%traced%", Escape(lang.T("ListCtrl/columnTraced", "Traced")));

        var builder = new StringBuilder();
        builder.Append(header);

        foreach (var program in apps)
        {
            AppendProgramRow(builder, program);
            foreach (var child in programs.Where(p => p.IsChild && p.GroupKey == program.Id)
                         .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase))
                AppendProgramRow(builder, child);
        }

        builder.Append(BuildFooter());
        Write(filePath, builder.ToString());
        return filePath;
    }

    public static string ExportStartup(string filePath, IReadOnlyList<StartupItem> startupItems)
    {
        var lang = LanguageManager.Instance;
        var header = PrepareTemplate(StartupHeader);
        header = Replace(header, "%title%", Escape(lang.T("HTMLReport/startupPrograms", "Startup programs")));
        header = Replace(header, "%info%", BuildInfo());
        header = Replace(header, "%program_name%", Escape(lang.T("ListCtrl/columnProgramName", "Program name")));
        header = Replace(header, "%status%", Escape(lang.T("ListCtrl/columnStatus", "Status")));
        header = Replace(header, "%source%", Escape(lang.T("ListCtrl/columnSource", "Source")));
        header = Replace(header, "%company%", Escape(lang.T("ListCtrl/columnCompany", "Company")));

        var builder = new StringBuilder();
        builder.Append(header);

        foreach (var item in startupItems.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var row = PrepareTemplate(StartupRow);
            row = Replace(row, "%padding%", "");
            row = Replace(row, "%program_name%", Escape(item.Name));
            row = Replace(row, "%status%", Escape(StatusText(item)));
            row = Replace(row, "%source%", Escape(item.SourceText));
            row = Replace(row, "%company%", Escape(item.Publisher));
            builder.Append(row);
        }

        builder.Append(BuildFooter());
        Write(filePath, builder.ToString());
        return filePath;
    }

    private static void AppendProgramRow(StringBuilder builder, InstalledProgram program)
    {
        var lang = LanguageManager.Instance;
        var row = PrepareTemplate(ProgramsRow);
        row = Replace(row, "%padding%", string.Concat(Enumerable.Repeat(PaddingCell, Math.Max(0, program.Indent))));

        var name = Escape(program.DisplayName);
        if (SettingsMark32Bit() && !program.Is64Bit && !program.IsStoreApp)
            name += Replace(Bit32Markup, "%s", Escape(lang.T("ListCtrl/mark32bitItem", "(32-bit)")));
        row = Replace(row, "%program_name%", name);
        row = Replace(row, "%size%", Escape(program.SizeText));
        row = Replace(row, "%installed_on%", Escape(program.InstallDateText));
        row = Replace(row, "%traced%", Escape(program.IsTraced
            ? lang.T("Misc/btnYes", "Yes")
            : lang.T("Misc/btnNo", "No")));
        builder.Append(row);
    }

    private static bool SettingsMark32Bit()
    {
        try
        {
            return AppSettings.Load().Mark32Bit;
        }
        catch
        {
            return true;
        }
    }

    private static string StatusText(StartupItem item)
    {
        var lang = LanguageManager.Instance;
        if (!item.Enabled)
            return lang.T("ExplorerBar/itemStatusDisabled", "Disabled");
        if (item.Invalid)
            return lang.T("ExplorerBar/itemStatusInvalid", "Invalid");
        return item.Running
            ? lang.T("ExplorerBar/itemStatusRunning", "Running")
            : lang.T("ExplorerBar/itemStatusNotRunning", "Not running");
    }

    private static string BuildInfo()
    {
        var lang = LanguageManager.Instance;
        return Escape(lang.T("HTMLReport/computer", "Computer")) + ": " +
               Escape(Environment.MachineName);
    }

    private static string BuildFooter()
    {
        var lang = LanguageManager.Instance;
        var text = lang.F("HTMLReport/generatedByOn", ProductName,
            DateTime.Now.ToString("F", CultureInfo.CurrentCulture));
        return Replace(Footer, "%footer_text%", Escape(text));
    }

    private static string PrepareTemplate(string template)
    {
        if (!IsRightToLeft(LanguageManager.Instance.LangId))
            return template;
        return template.Replace("<html>", "<html dir=\"rtl\">");
    }

    private static bool IsRightToLeft(int langId)
    {
        var primary = langId & 0x3FF;
        // Arabic, Hebrew, Persian and Urdu.
        return primary is 0x01 or 0x0D or 0x29 or 0x20;
    }

    private static string Replace(string template, string token, string value) =>
        template.Replace(token, value, StringComparison.Ordinal);

    private static string Escape(string value) => WebUtility.HtmlEncode(value ?? "");

    private static void Write(string filePath, string content)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, content, new UTF8Encoding(true));
    }
}
