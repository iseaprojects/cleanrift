using Microsoft.Win32;

namespace CleanRift.Core;

/// <summary>
/// Optional per-user Explorer context-menu entry for setup files. It is
/// registered under HKCU only when the user enables it in Preferences.
/// </summary>
public static class ShellIntegration
{
    private const string Verb = "CleanRift.InstallTrace";
    private static readonly string[] Classes = { "exefile", "Msi.Package" };

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\exefile\shell\" + Verb);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled, string executable, string label, out string error)
    {
        error = "";
        try
        {
            foreach (var itemClass in Classes)
            {
                var path = @"Software\Classes\" + itemClass + @"\shell\" + Verb;
                if (enabled)
                {
                    using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
                    if (key == null)
                    {
                        error = "Could not create the Explorer menu entry";
                        return false;
                    }

                    key.SetValue("", label, RegistryValueKind.String);
                    key.SetValue("Icon", executable, RegistryValueKind.String);
                    key.SetValue("Position", "Top", RegistryValueKind.String);
                    using var command = key.CreateSubKey("command", writable: true);
                    command?.SetValue("", "\"" + executable + "\" /install_and_trace \"%1\"",
                        RegistryValueKind.String);
                }
                else
                {
                    try
                    {
                        Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                    }
                    catch
                    {
                        // The entry may not exist.
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
