using System.IO;

namespace CleanRift.Core;

/// <summary>
/// Real-time filesystem monitor used while a setup program runs. It uses
/// ReadDirectoryChangesW through <see cref="FileSystemWatcher"/>, so changes
/// are observed as they happen instead of only at the end of the snapshot.
/// Files or folders created and then deleted by the installer are removed from
/// the result, because they are not leftovers.
/// </summary>
public sealed class LiveFileMonitor : IDisposable
{
    private readonly object _lock = new();
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _baselineFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _baselineFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _log;
    private readonly Func<string, bool> _ignored;

    private long _created;
    private long _deleted;
    private long _renamed;
    private bool _started;
    private bool _disposed;

    public LiveFileMonitor(Action<string>? log = null, Func<string, bool>? ignored = null)
    {
        _log = log;
        _ignored = ignored ?? InstallTracker.IsNoisePath;
    }

    /// <summary>True when at least one watcher lost events (buffer overflow).</summary>
    public bool Overflowed { get; private set; }

    public long EventCount =>
        Interlocked.Read(ref _created) + Interlocked.Read(ref _deleted) +
        Interlocked.Read(ref _renamed);

    public int FileCount
    {
        get { lock (_lock) return _files.Count; }
    }

    public int FolderCount
    {
        get { lock (_lock) return _folders.Count; }
    }

    public int WatchedRoots
    {
        get { lock (_lock) return _watchers.Count; }
    }

    /// <summary>Starts watching the existing roots and returns the count.</summary>
    public int Start(IEnumerable<string> roots, InstallSnapshot? baseline = null)
    {
        if (_started)
            return WatchedRoots;

        _started = true;
        if (baseline != null)
        {
            lock (_lock)
            {
                _baselineFiles.UnionWith(baseline.Files);
                _baselineFolders.UnionWith(baseline.Folders);
            }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !seen.Add(root) || !Directory.Exists(root))
                continue;

            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.CreationTime | NotifyFilters.LastWrite |
                                   NotifyFilters.Size
                };
                watcher.Created += OnCreated;
                watcher.Deleted += OnDeleted;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;

                lock (_lock)
                    _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                _log?.Invoke("Live monitor could not watch " + root + ": " + ex.Message);
            }
        }

        var count = WatchedRoots;
        _log?.Invoke("Live monitor watching " + count + " filesystem roots in real time.");
        return count;
    }

    public void Stop()
    {
        List<FileSystemWatcher> watchers;
        lock (_lock)
        {
            watchers = _watchers.ToList();
            _watchers.Clear();
        }

        foreach (var watcher in watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
                // Best-effort shutdown.
            }
        }

        _started = false;
    }

    /// <summary>
    /// Adds still-existing live changes to the snapshot diff. The snapshot
    /// remains the source of truth for registry data and for any root the
    /// monitor could not watch.
    /// </summary>
    public void MergeInto(SnapshotDiff diff)
    {
        List<string> files;
        List<string> folders;
        lock (_lock)
        {
            files = _files.ToList();
            folders = _folders.ToList();
        }

        var knownFiles = new HashSet<string>(diff.Files, StringComparer.OrdinalIgnoreCase);
        var knownFolders = new HashSet<string>(diff.Folders, StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            try
            {
                if (Directory.Exists(folder) && !_ignored(folder) && knownFolders.Add(folder))
                    diff.Folders.Add(folder);
            }
            catch
            {
                // Ignore a path that disappeared during the merge.
            }
        }

        foreach (var file in files)
        {
            try
            {
                if (System.IO.File.Exists(file) && !_ignored(file) && knownFiles.Add(file))
                    diff.Files.Add(file);
            }
            catch
            {
                // Ignore a path that disappeared during the merge.
            }
        }
    }

    private void OnCreated(object? sender, FileSystemEventArgs e)
    {
        try
        {
            RecordCreated(e.FullPath);
            Interlocked.Increment(ref _created);
        }
        catch (Exception ex)
        {
            _log?.Invoke("Live monitor event error: " + ex.Message);
        }
    }

    private void OnDeleted(object? sender, FileSystemEventArgs e)
    {
        try
        {
            RemoveTree(e.FullPath);
            Interlocked.Increment(ref _deleted);
        }
        catch (Exception ex)
        {
            _log?.Invoke("Live monitor event error: " + ex.Message);
        }
    }

    private void OnRenamed(object? sender, RenamedEventArgs e)
    {
        try
        {
            RemoveTree(e.OldFullPath);
            RecordCreated(e.FullPath);
            Interlocked.Increment(ref _renamed);
        }
        catch (Exception ex)
        {
            _log?.Invoke("Live monitor event error: " + ex.Message);
        }
    }

    private void OnError(object? sender, ErrorEventArgs e)
    {
        Overflowed = true;
        _log?.Invoke("Live monitor overflowed; snapshot comparison will be used as a fallback: " +
                     e.GetException().Message);
    }

    private void RecordCreated(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return;

        var full = Path.GetFullPath(rawPath).TrimEnd('\\');
        if (full.Length == 0 || _ignored(full))
            return;

        var isDirectory = Directory.Exists(full);
        lock (_lock)
        {
            // A path that existed before the installation is an update, not a
            // newly installed file. Never schedule it for removal.
            if (_baselineFiles.Contains(full) || _baselineFolders.Contains(full))
                return;

            if (isDirectory)
                _folders.Add(full);
            else if (System.IO.File.Exists(full))
                _files.Add(full);
        }
    }

    private void RemoveTree(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return;

        var full = Path.GetFullPath(rawPath).TrimEnd('\\');
        var prefix = full + "\\";
        lock (_lock)
        {
            _files.RemoveWhere(p =>
                p.Equals(full, StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            _folders.RemoveWhere(p =>
                p.Equals(full, StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }
}
