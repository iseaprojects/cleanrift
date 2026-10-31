using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace CleanRift.Core;

/// <summary>
/// Real-time registry monitor based on ETW's kernel registry provider. It
/// records keys and values created during the tracked installation, while the
/// snapshot diff remains the fallback when ETW is unavailable (for example
/// without administrator rights or when another kernel logger is active).
/// </summary>
public sealed class LiveRegistryMonitor : IDisposable
{
    private const string SessionName = "CleanRiftRegistryTracker";
    private const string MachinePrefix = @"\REGISTRY\MACHINE\";
    private const string UserPrefix = @"\REGISTRY\USER\";

    private readonly object _lock = new();
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Key, string Name)> _values =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _baselineKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _baselineValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _log;
    private readonly string _currentUserSid;
    private readonly HashSet<int> _trackedPids = new();

    private TraceEventSession? _session;
    private Thread? _thread;
    private int _rootPid;
    private string _rootImageName = "";
    private long _events;
    private bool _started;
    private bool _disposed;

    public event Action<LiveChange>? Change;

    public LiveRegistryMonitor(Action<string>? log = null)
    {
        _log = log;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            _currentUserSid = identity.User?.Value ?? "";
        }
        catch
        {
            _currentUserSid = "";
        }
    }

    public bool Active { get; private set; }
    public bool Failed { get; private set; }

    public long EventCount => Interlocked.Read(ref _events);

    public int KeyCount
    {
        get { lock (_lock) return _keys.Count; }
    }

    public int ValueCount
    {
        get { lock (_lock) return _values.Count; }
    }

    /// <summary>
    /// Marks the setup process and records its current descendants. Registry
    /// events from unrelated processes are ignored from this point on.
    /// </summary>
    public void SetRootProcess(int pid)
    {
        if (pid <= 0)
            return;

        lock (_lock)
        {
            _rootPid = pid;
            _trackedPids.Add(pid);
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            lock (_lock)
                _rootImageName = process.ProcessName + ".exe";
        }
        catch
        {
            // The process may have exited immediately.
        }

        foreach (var child in ProcessUtil.GetProcessTree(pid))
        {
            try
            {
                lock (_lock)
                    _trackedPids.Add(child.Id);
            }
            catch
            {
                // Ignore a process that disappeared.
            }
            finally
            {
                child.Dispose();
            }
        }
    }

    /// <summary>Starts the ETW session and returns true when it is active.</summary>
    public bool Start(InstallSnapshot? baseline = null)
    {
        if (_started)
            return Active;

        _started = true;
        if (baseline != null)
        {
            lock (_lock)
            {
                _baselineKeys.UnionWith(baseline.Keys);
                _baselineValues.UnionWith(baseline.Values);
            }
        }

        if (!Elevation.IsElevated)
        {
            Failed = true;
            _log?.Invoke("Registry live tracker needs administrator rights; using snapshot only.");
            return false;
        }

        try
        {
            var session = new TraceEventSession(SessionName, TraceEventSessionOptions.Create);
            _session = session;
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Registry);

            // The default real-time parser deliberately omits registry name
            // tracking. Ask for it explicitly so SetValue/DeleteValue events can
            // resolve their key handles to full key names.
            var kernel = new KernelTraceEventParser(session.Source,
                KernelTraceEventParser.ParserTrackingOptions.Default);
            kernel.RegistryCreate += OnCreateKey;
            kernel.RegistryDelete += OnDeleteKey;
            kernel.RegistrySetValue += OnSetValue;
            kernel.RegistryDeleteValue += OnDeleteValue;
            kernel.ProcessStart += OnProcessStart;
            kernel.ProcessStop += OnProcessStop;

            _thread = new Thread(() =>
            {
                try
                {
                    session.Source.Process();
                }
                catch (Exception ex)
                {
                    if (!_disposed)
                    {
                        Failed = true;
                        _log?.Invoke("Registry live tracker stopped: " + ex.Message);
                    }
                }
            })
            {
                IsBackground = true,
                Name = "CleanRift registry ETW"
            };
            _thread.Start();
            Active = true;
            _log?.Invoke("Registry live tracker started.");
            return true;
        }
        catch (Exception ex)
        {
            Failed = true;
            Active = false;
            _log?.Invoke("Registry live tracker unavailable (" + ex.Message +
                         "); using snapshot only.");
            DisposeSession();
            return false;
        }
    }

    public void Stop()
    {
        Active = false;
        var thread = _thread;
        _thread = null;

        try
        {
            _session?.Stop();
        }
        catch
        {
            // The session may already have stopped.
        }

        try
        {
            thread?.Join(3000);
        }
        catch
        {
            // Best-effort shutdown.
        }

        DisposeSession();
    }

    /// <summary>Adds still-existing live changes to the snapshot diff.</summary>
    public void MergeInto(SnapshotDiff diff)
    {
        List<string> keys;
        List<(string Key, string Name)> values;
        lock (_lock)
        {
            keys = _keys.ToList();
            values = _values.Values.ToList();
        }

        var knownKeys = new HashSet<string>(diff.RegistryKeys, StringComparer.OrdinalIgnoreCase);
        var knownValues = new HashSet<string>(
            diff.RegistryValues.Select(v => v.Key + "\u0001" + v.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            try
            {
                if (InstallTracker.IsNoiseRegistryPath(key) ||
                    PathSafety.IsProtectedRegistryKey(key, out _) ||
                    !TraceStore.RegistryPathExists(key) ||
                    !knownKeys.Add(key))
                    continue;
                diff.RegistryKeys.Add(key);
            }
            catch
            {
                // Ignore keys that disappeared during the merge.
            }
        }

        foreach (var (key, name) in values)
        {
            try
            {
                if (InstallTracker.IsNoiseRegistryPath(key) ||
                    PathSafety.IsProtectedRegistryKey(key, forValue: true, out _) ||
                    !RegistryValueExists(key, name) ||
                    !knownValues.Add(key + "\u0001" + name))
                    continue;
                diff.RegistryValues.Add((key, name));
            }
            catch
            {
                // Ignore values that disappeared during the merge.
            }
        }
    }

    private void OnProcessStart(ProcessTraceData data)
    {
        try
        {
            var name = Path.GetFileName(data.ImageFileName);
            lock (_lock)
            {
                if (_rootPid == 0)
                    return;
                if (_trackedPids.Contains(data.ParentID) ||
                    IsInstallerWorker(name) ||
                    (!string.IsNullOrEmpty(_rootImageName) &&
                     name.Equals(_rootImageName, StringComparison.OrdinalIgnoreCase)))
                    _trackedPids.Add(data.ProcessID);
            }
        }
        catch
        {
            // Ignore malformed process events.
        }
    }

    private void OnProcessStop(ProcessTraceData data)
    {
        try
        {
            lock (_lock)
                _trackedPids.Remove(data.ProcessID);
        }
        catch
        {
            // Ignore malformed process events.
        }
    }

    private static bool IsInstallerWorker(string name) =>
        name.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TiWorker.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TrustedInstaller.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("dism.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("dismhost.exe", StringComparison.OrdinalIgnoreCase);

    private bool IsTrackedProcess(int pid)
    {
        lock (_lock)
            return _rootPid != 0 && _trackedPids.Contains(pid);
    }

    private void OnCreateKey(RegistryTraceData data)
    {
        try
        {
            if (!IsTrackedProcess(data.ProcessID))
                return;
            var key = NormalizeKeyName(data.KeyName, _currentUserSid);
            if (key == null || !IsTrackableKey(key, forValue: false))
                return;

            Interlocked.Increment(ref _events);
            var added = false;
            lock (_lock)
            {
                if (!_baselineKeys.Contains(key))
                    added = _keys.Add(key);
            }
            if (added)
                Change?.Invoke(new LiveChange("Registry key", key, true));
        }
        catch
        {
            // A malformed event must not stop the ETW pump.
        }
    }

    private void OnDeleteKey(RegistryTraceData data)
    {
        try
        {
            if (!IsTrackedProcess(data.ProcessID))
                return;
            var key = NormalizeKeyName(data.KeyName, _currentUserSid);
            if (key == null || !IsTrackableKey(key, forValue: false))
                return;

            Interlocked.Increment(ref _events);
            RemoveKeyTree(key);
            Change?.Invoke(new LiveChange("Registry delete", key, true));
        }
        catch
        {
            // Ignore malformed events.
        }
    }

    private void OnSetValue(RegistryTraceData data)
    {
        try
        {
            if (!IsTrackedProcess(data.ProcessID))
                return;
            var key = NormalizeKeyName(data.KeyName, _currentUserSid);
            if (key == null || !IsTrackableKey(key, forValue: true))
                return;

            var name = data.ValueName ?? "";
            var id = key + "\u0001" + name;
            Interlocked.Increment(ref _events);
            var added = false;
            lock (_lock)
            {
                if (!_baselineKeys.Contains(key) && !_baselineValues.Contains(id))
                    _keys.Add(key);
                if (!_baselineValues.Contains(id))
                {
                    added = !_values.ContainsKey(id);
                    _values[id] = (key, name);
                }
            }
            if (added)
                Change?.Invoke(new LiveChange("Registry value",
                    key + "\\" + name, true));
        }
        catch
        {
            // Ignore malformed events.
        }
    }

    private void OnDeleteValue(RegistryTraceData data)
    {
        try
        {
            if (!IsTrackedProcess(data.ProcessID))
                return;
            var key = NormalizeKeyName(data.KeyName, _currentUserSid);
            if (key == null || !IsTrackableKey(key, forValue: true))
                return;

            Interlocked.Increment(ref _events);
            var name = data.ValueName ?? "";
            lock (_lock)
                _values.Remove(key + "\u0001" + name);
            Change?.Invoke(new LiveChange("Registry delete value",
                key + "\\" + name, true));
        }
        catch
        {
            // Ignore malformed events.
        }
    }

    private void RemoveKeyTree(string key)
    {
        var prefix = key + "\\";
        lock (_lock)
        {
            _keys.RemoveWhere(k =>
                k.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            foreach (var id in _values.Keys.ToList())
            {
                var existing = _values[id].Key;
                if (existing.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                    existing.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    _values.Remove(id);
            }
        }
    }

    private static bool IsTrackableKey(string key, bool forValue)
    {
        if (InstallTracker.IsNoiseRegistryPath(key) ||
            PathSafety.IsProtectedRegistryKey(key, forValue, out _))
            return false;

        const string machine = @"HKEY_LOCAL_MACHINE\SOFTWARE";
        const string user = @"HKEY_CURRENT_USER\SOFTWARE";
        return key.Equals(machine, StringComparison.OrdinalIgnoreCase) ||
               key.StartsWith(machine + "\\", StringComparison.OrdinalIgnoreCase) ||
               key.Equals(user, StringComparison.OrdinalIgnoreCase) ||
               key.StartsWith(user + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Converts the ETW registry paths to the HKEY_* form used by the rest of
    /// the application. Other users' hives and non-software trees are ignored.
    /// </summary>
    public static string? NormalizeKeyName(string? raw, string? currentUserSid = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = raw.Trim();
        if (text.StartsWith(MachinePrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = @"HKEY_LOCAL_MACHINE\" + text[MachinePrefix.Length..];
        }
        else if (text.StartsWith(UserPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = text[UserPrefix.Length..];
            var slash = rest.IndexOf('\\');
            if (slash <= 0)
                return null;

            var sid = rest[..slash];
            var subKey = rest[(slash + 1)..];
            if (!string.IsNullOrEmpty(currentUserSid) &&
                !sid.Equals(currentUserSid, StringComparison.OrdinalIgnoreCase))
                return null;

            text = @"HKEY_CURRENT_USER\" + subKey;
        }
        else if (text.StartsWith(@"SOFTWARE\", StringComparison.OrdinalIgnoreCase))
        {
            // Some kernel registry events carry a hive-relative key name.
            // Resolve it by checking which hive actually contains the key;
            // the stored casing is a useful tie-breaker when both do.
            var machineKey = @"HKEY_LOCAL_MACHINE\" + text;
            var userKey = @"HKEY_CURRENT_USER\" + text;
            var machineExists = TraceStore.RegistryPathExists(machineKey);
            var userExists = TraceStore.RegistryPathExists(userKey);
            text = machineExists && !userExists
                ? machineKey
                : userExists && !machineExists
                    ? userKey
                    : text.StartsWith("SOFTWARE\\", StringComparison.Ordinal)
                        ? machineKey
                        : userKey;
        }

        text = text.TrimEnd('\\');
        const string machine = @"HKEY_LOCAL_MACHINE\SOFTWARE";
        const string user = @"HKEY_CURRENT_USER\SOFTWARE";
        if (!text.Equals(machine, StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith(machine + "\\", StringComparison.OrdinalIgnoreCase) &&
            !text.Equals(user, StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith(user + "\\", StringComparison.OrdinalIgnoreCase))
            return null;

        return text;
    }

    private static bool RegistryValueExists(string keyPath, string valueName)
    {
        var (hive, subKey) = TraceStore.SplitRegistryPath(keyPath);
        if (hive == null || string.IsNullOrEmpty(subKey))
            return false;

        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64,
                     Microsoft.Win32.RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive.Value, view);
                using var key = baseKey.OpenSubKey(subKey);
                if (key == null)
                    continue;
                if (key.GetValueNames().Any(n =>
                        string.Equals(n, valueName, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch
            {
                // Ignore unreadable views.
            }
        }

        return false;
    }

    private void DisposeSession()
    {
        try
        {
            _session?.Dispose();
        }
        catch
        {
            // Best effort.
        }

        _session = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }
}
