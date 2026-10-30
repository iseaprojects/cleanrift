using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CleanRift.Core;

public sealed class UninstallOutcome
{
    public bool Started { get; set; }
    public bool Cancelled { get; set; }
    public bool TimedOut { get; set; }
    public int ExitCode { get; set; }
    public string Error { get; set; } = "";
    public string CommandLine { get; set; } = "";
}

/// <summary>
/// Launches a program's own uninstaller and waits for the whole process tree.
/// MSI products are uninstalled with "msiexec /X{ProductCode}" using the
/// registered UninstallString / ModifyPath values.
/// </summary>
public sealed class UninstallRunner
{
    private readonly List<Process> _running = new();

    public event Action<string>? Log;

    public UninstallOutcome Run(InstalledProgram program, bool quiet, bool waitForExit,
        CancellationToken token)
    {
        var outcome = new UninstallOutcome();
        var commandLine = BuildCommandLine(program, quiet);
        outcome.CommandLine = commandLine;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            outcome.Error = "The program does not provide an uninstall command.";
            return outcome;
        }

        Log?.Invoke("Uninstall command: " + commandLine);

        try
        {
            var (fileName, arguments) = SplitCommand(commandLine);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                outcome.Error = "Invalid uninstall command.";
                return outcome;
            }

            if (program.IsStoreApp)
            {
                // Store packages are removed through PowerShell. Extract the
                // inner command instead of nesting the whole invocation, and
                // strip the quotes that came from the registered UninstallString.
                fileName = ProcessUtil.PowerShellPath;
                var inner = commandLine.Trim();
                var idx = inner.IndexOf("Get-AppxPackage", StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                    inner = inner[idx..];
                if (inner.Length >= 2 && inner[0] == '"' && inner[^1] == '"')
                    inner = inner[1..^1];
                else if (inner.EndsWith('"'))
                    inner = inner[..^1];
                inner = inner.Trim();
                if (inner.Length == 0)
                {
                    outcome.Error = "Invalid Microsoft Store removal command.";
                    return outcome;
                }

                arguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden " +
                            "-Command \"" + inner.Replace("\"", "\\\"") + "\"";
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                WorkingDirectory = ResolveWorkingDirectory(program)
            };

            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
            {
                // The uninstaller requires elevation.
                psi.Verb = "runas";
                process = Process.Start(psi);
            }

            if (process == null)
            {
                outcome.Error = "Could not start the uninstall process.";
                return outcome;
            }

            outcome.Started = true;
            lock (_running)
                _running.Add(process);
            Log?.Invoke("Started process " + process.Id);

            if (!waitForExit)
            {
                outcome.ExitCode = 0;
                return outcome;
            }

            var deadline = DateTime.UtcNow.AddHours(4);
            var rootExitedAt = DateTime.MinValue;
            var graceGiven = false;

            while (!token.IsCancellationRequested)
            {
                bool rootAlive;
                try { rootAlive = !process.HasExited; }
                catch { rootAlive = false; }

                if (!rootAlive && rootExitedAt == DateTime.MinValue)
                    rootExitedAt = DateTime.UtcNow;

                if (rootAlive)
                {
                    if (DateTime.UtcNow > deadline)
                    {
                        outcome.TimedOut = true;
                        break;
                    }

                    Thread.Sleep(400);
                    continue;
                }

                var liveChildren = CountLiveChildren(process.Id);
                if (liveChildren == 0 && !graceGiven && DateTime.UtcNow - rootExitedAt < TimeSpan.FromSeconds(2))
                {
                    // Some uninstallers spawn a detached child right before exiting.
                    Thread.Sleep(600);
                    continue;
                }

                if (liveChildren == 0)
                {
                    graceGiven = true;
                    break;
                }

                if (DateTime.UtcNow > deadline)
                {
                    outcome.TimedOut = true;
                    break;
                }

                Thread.Sleep(400);
            }

            if (token.IsCancellationRequested)
            {
                outcome.Cancelled = true;
                Log?.Invoke("Cancelling uninstall process tree...");
                ProcessUtil.KillTree(process.Id);
            }

            try
            {
                if (process.HasExited)
                    outcome.ExitCode = process.ExitCode;
            }
            catch
            {
                // ignore
            }

            lock (_running)
                _running.Remove(process);
            process.Dispose();
            return outcome;
        }
        catch (Exception ex)
        {
            outcome.Error = ex.Message;
            return outcome;
        }
    }

    public void KillAll()
    {
        lock (_running)
        {
            foreach (var process in _running.ToList())
            {
                try { ProcessUtil.KillTree(process.Id); } catch { }
                finally { process.Dispose(); }
            }

            _running.Clear();
        }
    }

    private static int CountLiveChildren(int pid)
    {
        var count = 0;
        foreach (var process in ProcessUtil.GetProcessTree(pid))
        {
            try
            {
                if (!process.HasExited && process.Id != pid)
                    count++;
            }
            catch
            {
                // ignore
            }
            finally
            {
                process.Dispose();
            }
        }

        return count;
    }

    public static string BuildCommandLine(InstalledProgram program, bool quiet)
    {
        var raw = quiet && !string.IsNullOrWhiteSpace(program.QuietUninstallString)
            ? program.QuietUninstallString
            : program.UninstallString;

        if (program.IsStoreApp)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                var package = (program.PackageFullName ?? "").Replace("'", "''");
                return "-WindowStyle Hidden \"Get-AppxPackage -name '" + package +
                       "' | Remove-AppxPackage\"";
            }

            return raw;
        }

        raw = raw?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        raw = Environment.ExpandEnvironmentVariables(raw);

        // MSI products: normalize /I{ProductCode} to /X{ProductCode}.
        var guidMatch = Regex.Match(raw, @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");
        var isMsi = raw.Contains("msiexec", StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("MsiExec", StringComparison.OrdinalIgnoreCase) ||
                    program.WindowsInstaller;

        if (isMsi && guidMatch.Success)
        {
            var product = guidMatch.Value.ToUpperInvariant();
            var switches = quiet ? " /qn" : "";
            return $"msiexec.exe /X{product}{switches}";
        }

        if (isMsi && raw.EndsWith(".msi\"", StringComparison.OrdinalIgnoreCase))
        {
            var switches = quiet ? " /qn" : "";
            return $"msiexec.exe /X{raw}{switches}";
        }

        // Remove a trailing /I switch used by some MSI registrations.
        raw = Regex.Replace(raw, @"\s*/I(\s|$)", " /X ", RegexOptions.IgnoreCase);

        // Many uninstall strings are registered as: "C:\...\unins000.exe" /SILENT
        if (!raw.StartsWith('"') && !raw.StartsWith('\'') && raw.Contains(' ') &&
            !File.Exists(raw))
        {
            // Leave as-is: the caller splits exe and arguments.
        }

        return raw;
    }

    public static (string fileName, string arguments) SplitCommand(string commandLine)
    {
        var text = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 0)
                return (text[1..end], text[(end + 1)..].Trim());
        }

        // Look for known executables to avoid splitting on a path with spaces.
        var exeIndex = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex > 0)
            return (text[..(exeIndex + 4)].Trim('"', ' '), text[(exeIndex + 4)..].Trim());

        var space = text.IndexOf(' ');
        return space > 0
            ? (text[..space], text[(space + 1)..].Trim())
            : (text, "");
    }

    private static string ResolveWorkingDirectory(InstalledProgram program)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(program.InstallLocation) &&
                Directory.Exists(program.InstallLocation))
                return program.InstallLocation;
            if (!string.IsNullOrWhiteSpace(program.DisplayIcon))
            {
                var dir = Path.GetDirectoryName(program.DisplayIcon.Trim('"'));
                if (dir != null && Directory.Exists(dir))
                    return dir;
            }
        }
        catch
        {
            // ignore
        }

        return Environment.CurrentDirectory;
    }
}
