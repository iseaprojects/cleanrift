using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace CleanRift.Core;

/// <summary>
/// Optional elevated scheduled task that lets CleanRift start without the UAC
/// confirmation dialog. It is created only from Preferences, runs the current
/// executable with /tasksch and signals Local\CleanRift_SkipUAC_Launched.
/// </summary>
public static class SkipUacTask
{
    private const string Prefix = "CleanRift_SkipUAC_";
    private const string LaunchEventName = @"Local\CleanRift_SkipUAC_Launched";

    private const string Description =
        "This elevated scheduled task is created by CleanRift to launch the app without the " +
        "User Account Control (UAC) confirmation dialog. It can be turned off in CleanRift Preferences.";

    public static string Name => Prefix + Environment.MachineName;

    public static bool Exists() => Query();

    /// <summary>
    /// Creates or removes the task. The caller must already be elevated; it is
    /// enabled from Preferences and from the elevated startup.
    /// </summary>
    public static bool SetEnabled(bool enabled, string executable, out string error)
    {
        error = "";
        if (!enabled)
        {
            DeleteTask(Name, out error);
            return string.IsNullOrEmpty(error);
        }

        var xml = BuildTaskXml(executable);
        var file = Path.Combine(Path.GetTempPath(),
            "CleanRift_SkipUAC_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 4096, FileOptions.WriteThrough);
            using (var writer = new StreamWriter(stream, Encoding.Unicode, 4096, leaveOpen: true))
            {
                writer.Write(xml);
                writer.Flush();
            }

            stream.Flush(flushToDisk: true);
            var result = RunSchTasks($"/Create /TN \"{Name}\" /XML \"{file}\" /F");
            if (result.ExitCode != 0)
                error = FirstLine(result.Output);
            return result.ExitCode == 0;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // ignore temp cleanup failures
            }
        }
    }

    /// <summary>
    /// Updates an existing task if the setting is on. Called by the elevated
    /// instance so the task always points at the current executable.
    /// </summary>
    public static void Ensure(string executable)
    {
        var result = RunSchTasks($"/Query /TN \"{Name}\"");
        if (result.ExitCode != 0)
            return;
        SetEnabled(true, executable, out _);
    }

    /// <summary>
    /// Starts the task and waits for the elevated instance to signal the event.
    /// </summary>
    public static bool TryRunAndWait(int milliseconds, out string error)
    {
        error = "";
        if (!Query())
            return false;

        try
        {
            using var launched = new EventWaitHandle(false, EventResetMode.AutoReset,
                LaunchEventName);
            launched.Reset();

            var run = RunSchTasks($"/Run /TN \"{Name}\"");
            if (run.ExitCode != 0)
            {
                error = FirstLine(run.Output);
                return false;
            }

            return launched.WaitOne(milliseconds);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool Query() => RunSchTasks($"/Query /TN \"{Name}\"").ExitCode == 0;

    private static void DeleteTask(string name, out string error)
    {
        error = "";
        // Query first so a missing task is not reported as an error; the
        // localized schtasks message varies between Windows languages.
        if (RunSchTasks($"/Query /TN \"{name}\"").ExitCode != 0)
            return;

        var result = RunSchTasks($"/Delete /TN \"{name}\" /F");
        if (result.ExitCode != 0)
            error = FirstLine(result.Output);
    }

    private static string BuildTaskXml(string executable)
    {
        var user = WindowsIdentity.GetCurrent().Name;
        var command = SecurityElementEscape(executable);
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Author>CleanRift Project</Author>
    <Description>{SecurityElementEscape(Description)}</Description>
  </RegistrationInfo>
  <Principals>
    <Principal id="Author">
      <UserId>{SecurityElementEscape(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>true</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{command}</Command>
      <Arguments>/tasksch</Arguments>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static string SecurityElementEscape(string value) =>
        System.Security.SecurityElement.Escape(value) ?? "";

    private static (int ExitCode, string Output) RunSchTasks(string arguments)
    {
        var (exitCode, stdout, stderr) = ProcessUtil.Run(
            Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments, 30000);
        return (exitCode, stdout + stderr);
    }

    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }

        return "";
    }
}
