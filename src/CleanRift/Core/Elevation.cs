using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;

namespace CleanRift.Core;

public sealed class RemovalJobItem
{
    public string Kind { get; set; } = "";
    public string Path { get; set; } = "";
    public string ValueName { get; set; } = "";
}

public sealed class RemovalJob
{
    public string Kind { get; set; } = "";
    public List<RemovalJobItem> Items { get; set; } = new();
    public StartupJob? Startup { get; set; }
}

public sealed class StartupJob
{
    public string Action { get; set; } = "";
    public string ItemKind { get; set; } = "";
    public string Hive { get; set; } = "";
    public bool Is64Bit { get; set; }
    public string ValueName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string TaskPath { get; set; } = "";
    public string TaskName { get; set; } = "";
    public bool Enabled { get; set; }
    public string AddType { get; set; } = "";
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string Arguments { get; set; } = "";
    public bool HighestPrivileges { get; set; }
}

public sealed class JobResult
{
    public bool Success { get; set; }
    public int Deleted { get; set; }
    public int Failed { get; set; }
    public int ScheduledForReboot { get; set; }
    public string Error { get; set; } = "";
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Runs privileged work in a second, elevated instance of the application.
/// The application itself runs as invoker (asInvoker); only operations that
/// Windows refuses are replayed with a UAC prompt.
/// </summary>
public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public static string JobDirectory
    {
        get
        {
            var dir = Path.Combine(Path.GetTempPath(), "CleanRift");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static JobResult? ExecuteRemoval(IEnumerable<RemovalJobItem> items, bool wait = true)
    {
        var job = new RemovalJob { Kind = "removal", Items = items.ToList() };
        return Execute(job, wait);
    }

    public static JobResult? ExecuteStartup(StartupJob startup, bool wait = true)
    {
        var job = new RemovalJob { Kind = "startup", Startup = startup };
        return Execute(job, wait);
    }

    public static JobResult? Execute(RemovalJob job, bool wait = true)
    {
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var jobFile = Path.Combine(JobDirectory, "job_" + id + ".json");
            var resultFile = Path.Combine(JobDirectory, "job_" + id + ".result.json");
            File.WriteAllText(jobFile, JsonSerializer.Serialize(job));

            var exe = Environment.ProcessPath ?? "";
            var prefix = "";
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var entry = Assembly.GetEntryAssembly()?.Location ?? "";
                prefix = "\"" + entry + "\" ";
                exe = "dotnet.exe";
            }

            var psi = new ProcessStartInfo
            {
                FileName = string.IsNullOrEmpty(exe) ? "CleanRift.exe" : exe,
                Arguments = prefix + "--job \"" + jobFile + "\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var process = Process.Start(psi);
            if (process == null)
                return new JobResult { Success = false, Error = "Could not start elevated process" };

            if (!wait)
                return new JobResult { Success = true };

            if (!process.WaitForExit(600000))
            {
                try { process.Kill(); } catch { }
                return new JobResult { Success = false, Error = "Elevated operation timed out" };
            }

            if (File.Exists(resultFile))
            {
                var json = File.ReadAllText(resultFile);
                try { File.Delete(resultFile); } catch { }
                try { File.Delete(jobFile); } catch { }
                return JsonSerializer.Deserialize<JobResult>(json);
            }

            return new JobResult
            {
                Success = process.ExitCode == 0,
                Error = "Elevated operation did not produce a result (exit code " + process.ExitCode + ")"
            };
        }
        catch (Exception ex)
        {
            return new JobResult { Success = false, Error = ex.Message };
        }
    }
}
