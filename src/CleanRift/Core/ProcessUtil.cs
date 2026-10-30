using System.Diagnostics;
using System.Text;

namespace CleanRift.Core;

public static class ProcessUtil
{
    /// <summary>Absolute path to the in-box tools so a file next to the app
    /// cannot hijack an elevated process through the current directory.</summary>
    public static string SchTasksPath => SystemTool("schtasks.exe");

    public static string PowerShellPath => SystemTool(@"WindowsPowerShell\v1.0\powershell.exe");

    public static string SystemTool(string name)
    {
        var candidate = Path.Combine(Environment.SystemDirectory, name);
        return File.Exists(candidate) ? candidate : name;
    }

    public static (int exitCode, string stdout, string stderr) Run(string fileName, string arguments,
        int timeoutMs = 30000, string? workingDirectory = null)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
            };
            using var process = Process.Start(psi);
            if (process == null)
                return (-1, "", "Failed to start process");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { process.WaitForExit(2000); } catch { }
                return (-1, ReadCompleted(stdoutTask), ReadCompleted(stderrTask) + "\nTimed out");
            }

            // A child process can inherit the redirected handles and keep them
            // open after the root exits. Never block forever waiting for EOF.
            return (process.ExitCode, ReadCompleted(stdoutTask), ReadCompleted(stderrTask));
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    private static string ReadCompleted(Task<string> task)
    {
        try
        {
            return task.Wait(5000) ? task.Result : "";
        }
        catch
        {
            return "";
        }
    }

    public static string RunPowerShell(string script, int timeoutMs = 30000)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var (_, stdout, _) = Run(PowerShellPath,
            $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", timeoutMs);
        return stdout;
    }

    public static bool RunElevated(string fileName, string arguments, bool wait = true)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas"
            };
            using var process = Process.Start(psi);
            if (process == null)
                return false;
            if (wait)
                process.WaitForExit();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsRunning(string processName)
    {
        Process[] processes = Array.Empty<Process>();
        try
        {
            processes = Process.GetProcessesByName(processName);
            return processes.Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    public static List<Process> GetProcessTree(int rootPid)
    {
        var result = new List<Process>();
        var snapshot = IntPtr.Zero;
        try
        {
            snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
                return result;

            var entries = new List<(uint pid, uint parent, string name)>();
            var entry = new NativeMethods.PROCESSENTRY32 { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PROCESSENTRY32>() };
            if (NativeMethods.Process32First(snapshot, ref entry))
            {
                do
                {
                    entries.Add((entry.th32ProcessID, entry.th32ParentProcessID, entry.szExeFile));
                } while (NativeMethods.Process32Next(snapshot, ref entry));
            }

            var pidSet = new HashSet<uint> { (uint)rootPid };
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var e in entries)
                {
                    if (pidSet.Contains(e.parent) && pidSet.Add(e.pid))
                        changed = true;
                }
            }

            foreach (var pid in pidSet)
            {
                try
                {
                    var process = Process.GetProcessById((int)pid);
                    result.Add(process);
                }
                catch
                {
                    // process already exited
                }
            }
        }
        catch
        {
            // best effort
        }
        finally
        {
            if (snapshot != IntPtr.Zero && snapshot != new IntPtr(-1))
                NativeMethods.CloseHandle(snapshot);
        }

        return result;
    }

    public static void KillTree(int pid)
    {
        foreach (var process in GetProcessTree(pid))
        {
            try { process.Kill(); } catch { } finally { process.Dispose(); }
        }
    }

    public static bool WaitForTree(int pid, int timeoutMs, Func<bool>? cancel = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (cancel?.Invoke() == true)
                return false;

            var alive = false;
            foreach (var process in GetProcessTree(pid))
            {
                try
                {
                    if (!process.HasExited)
                        alive = true;
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

            if (!alive)
                return true;
            Thread.Sleep(300);
        }

        return false;
    }

    public static string? GetExecutableFromCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;
        var text = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 1)
                return text[1..end];
        }

        var exeIndex = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex > 0)
            return text[..(exeIndex + 4)].Trim().Trim('"');

        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    public static string Quote(string path) => path.Contains(' ') ? "\"" + path + "\"" : path;
}
