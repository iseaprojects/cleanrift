using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace CleanRift.Core;

/// <summary>
/// CleanRift starts as invoker and immediately relaunches itself elevated: first
/// through the optional SkipUAC scheduled task, otherwise through ShellExecute
/// with the runas verb. The elevated copy receives /admin (or /tasksch when
/// started by the scheduled task).
/// </summary>
public static class ElevationBootstrap
{
    public static bool IsElevated { get; } = CheckElevated();

    private static bool CheckElevated()
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

    /// <summary>
    /// Relaunches the executable elevated, preserving the jump-list switches so
    /// /install_from_task_bar and friends still work after elevation.
    /// </summary>
    public static bool RelaunchElevated(IEnumerable<string> switches)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
            return false;

        var arguments = new StringBuilder();
        foreach (var item in switches)
        {
            if (item.Equals("/admin", StringComparison.OrdinalIgnoreCase) ||
                item.Equals("/tasksch", StringComparison.OrdinalIgnoreCase))
                continue;
            if (arguments.Length > 0)
                arguments.Append(' ');
            arguments.Append(Quote(item));
        }

        if (arguments.Length > 0)
            arguments.Append(' ');
        arguments.Append("/admin");

        try
        {
            using var launched = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments.ToString(),
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
            return launched != null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user declined the elevation prompt.
            Log("Elevation cancelled by the user.");
            return false;
        }
        catch (Exception ex)
        {
            Log("Elevation failed: " + ex.Message);
            return false;
        }
    }

    private static string Quote(string value) =>
        value.Contains(' ') ? "\"" + value + "\"" : value;

    private static void Log(string message)
    {
        try
        {
            var dir = AppSettings.DataDirectory;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "elevation.log"),
                $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics only.
        }
    }
}
