using System.Text.Json;
using CleanRift.Core;
using CleanRift.UI;
using CleanRift.UI.Forms;

namespace CleanRift;

internal static class Program
{
    private static readonly JsonSerializerOptions JobJsonOptions = new()
    {
        WriteIndented = false
    };

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Elevated helper mode: apply a removal/startup job produced by the main
        // instance and write the result next to the job file.
        var jobIndex = Array.FindIndex(args, a =>
            a.Equals("--job", StringComparison.OrdinalIgnoreCase));
        if (jobIndex >= 0 && jobIndex + 1 < args.Length)
        {
            RunJob(args[jobIndex + 1]);
            return;
        }

        // CleanRift always runs elevated: a non-elevated copy relaunches itself
        // through the SkipUAC task or ShellExecute("runas") and exits.
        if (!ElevationBootstrap.IsElevated)
        {
            var preferences = AppSettings.Load();
            if (preferences.SkipUac && SkipUacTask.TryRunAndWait(5000, out _))
                return;

            ElevationBootstrap.RelaunchElevated(args);
            return;
        }

        // The scheduled task started by SkipUAC runs the app with /tasksch and
        // signals this event so the launcher knows the elevated instance is up.
        if (HasSwitch(args, "/tasksch"))
            SignalTaskSchedulerLaunch();

        // Single instance: a second process replaces the previous window.
        using var single = SingleInstance.Acquire();
        if (!single.IsFirstInstance)
        {
            single.SignalExisting();
            if (!single.WaitForPrevious())
            {
                MessageBox.Show(
                    "Unknown error while trying to close the previous CleanRift instance. " +
                    "Please restart the app.",
                    "CleanRift", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        Application.ThreadException += (_, e) => ReportException(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportException(e.ExceptionObject as Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        var settings = AppSettings.Load();

        var languageFile = settings.LanguageFile;
        if (string.IsNullOrWhiteSpace(languageFile) || !File.Exists(languageFile))
            languageFile = LanguageManager.PickDefault();
        if (!string.IsNullOrWhiteSpace(languageFile))
        {
            LanguageManager.Instance.Load(languageFile);
            if (string.IsNullOrWhiteSpace(settings.LanguageFile))
            {
                settings.LanguageFile = languageFile;
                settings.Save();
            }
        }

        Theme.Current = Theme.FromScheme(settings.ColorScheme);

        // Keep the SkipUAC task in sync with the executable path when enabled.
        if (settings.SkipUac)
        {
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(executable))
                SkipUacTask.SetEnabled(true, executable, out _);
        }

        // Taskbar jump list: Install and trace / Microsoft Store Apps / Startup.
        if (!string.IsNullOrEmpty(Environment.ProcessPath))
            JumpList.Register(Environment.ProcessPath);

        var target = StartupTarget.None;
        if (HasSwitch(args, "/install_from_task_bar"))
            target = StartupTarget.InstallTrace;
        else if (HasSwitch(args, "/startup_from_task_bar"))
            target = StartupTarget.Startup;
        else if (HasSwitch(args, "/store_apps_from_task_bar"))
            target = StartupTarget.StoreApps;

        using var form = new MainForm(settings, target);
        single.Attach(form);
        Application.Run(form);
    }

    private static bool HasSwitch(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void SignalTaskSchedulerLaunch()
    {
        try
        {
            using var launched = EventWaitHandle.OpenExisting(
                @"Local\CleanRift_SkipUAC_Launched");
            launched.Set();
        }
        catch
        {
            // The launcher may not be waiting (for example when started manually).
        }
    }

    private static void RunJob(string jobFile)
    {
        var result = new JobResult { Success = false };
        var resultFile = "";
        try
        {
            var safe = PathSafety.IsSafeJobFile(jobFile, Elevation.JobDirectory,
                out var fullJob, out var jobError);
            if (!string.IsNullOrEmpty(fullJob) && PathSafety.IsUnder(fullJob, Elevation.JobDirectory))
                resultFile = Path.ChangeExtension(fullJob, ".result.json");

            if (!safe)
            {
                result.Failed = 1;
                result.Error = jobError;
            }
            else
            {
                var json = File.ReadAllText(fullJob);
                var job = JsonSerializer.Deserialize<RemovalJob>(json);
                if (job == null)
                {
                    result.Failed = 1;
                    result.Error = "Invalid job file";
                }
                else if (!PathSafety.ValidateJob(job, out var validationError))
                {
                    result.Failed = 1;
                    result.Error = validationError;
                }
                else
                {
                    result = RemovalEngine.ApplyJob(job);
                }
            }
        }
        catch (Exception ex)
        {
            result = new JobResult
            {
                Success = false,
                Failed = 1,
                Error = ex.Message
            };
            result.Errors.Add(ex.ToString());
        }

        if (string.IsNullOrEmpty(resultFile))
            return;

        try
        {
            AtomicFile.WriteAllText(resultFile, JsonSerializer.Serialize(result, JobJsonOptions));
        }
        catch
        {
            // The caller reports a missing result file.
        }
    }

    private static void ReportException(Exception? exception)
    {
        if (exception == null)
            return;
        try
        {
            var log = Path.Combine(AppSettings.DataDirectory, "error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log,
                "[" + DateTime.Now.ToString("O") + "] " + exception + Environment.NewLine);
        }
        catch
        {
            // ignore logging failures
        }

        try
        {
            MessageBox.Show(exception.Message, "CleanRift",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // ignore
        }
    }
}
