using System.Runtime.InteropServices;

namespace CleanRift.Core;

/// <summary>
/// Creates a Windows System Restore point before a destructive operation.
/// System Restore may be disabled by policy; failures never block removal.
/// </summary>
public static class SystemRestore
{
    private const int BeginSystemChange = 100;
    private const int EndSystemChange = 101;
    private const int ModifySettings = 12;
    private static readonly object Gate = new();
    private static DateTime _lastAttemptUtc = DateTime.MinValue;

    public static bool TryCreate(string description, out string error)
    {
        error = "";
        lock (Gate)
        {
            // Avoid one restore point per item when several removals happen in
            // a row (and avoid hammering the service).
            if (DateTime.UtcNow - _lastAttemptUtc < TimeSpan.FromMinutes(5))
                return true;
            _lastAttemptUtc = DateTime.UtcNow;
        }

        if (!OperatingSystem.IsWindows())
            return false;

        var info = new RestorePointInfo
        {
            EventType = BeginSystemChange,
            RestorePointType = ModifySettings,
            SequenceNumber = 0,
            Description = string.IsNullOrWhiteSpace(description)
                ? "CleanRift removal"
                : description.Length > 255 ? description[..255] : description
        };

        try
        {
            if (!SRSetRestorePointW(ref info, out var status))
            {
                error = "System Restore returned error " + status.Status;
                return false;
            }

            var end = info;
            end.EventType = EndSystemChange;
            SRSetRestorePointW(ref end, out _);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RestorePointInfo
    {
        public int EventType;
        public int RestorePointType;
        public long SequenceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StateManagerStatus
    {
        public int Status;
        public long SequenceNumber;
    }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SRSetRestorePointW(ref RestorePointInfo restorePoint,
        out StateManagerStatus status);
}
