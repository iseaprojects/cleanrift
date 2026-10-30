using CleanRift.Core;
using CleanRift.UI;

namespace CleanRift;

/// <summary>
/// CleanRift single-instance handling. A second process signals the running
/// instance to close and then takes over the mutex, so only one window stays
/// open. The object names are private to CleanRift and do not collide with any
/// other application.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\CleanRift";
    private const string EventName = @"Local\CleanRift_ACTIVATE";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _event;
    private bool _ownsMutex;
    private RegisteredWaitHandle? _wait;
    private Form? _form;
    private bool _closePending;

    public bool IsFirstInstance => _ownsMutex;

    private SingleInstance(Mutex mutex, EventWaitHandle waitEvent, bool ownsMutex)
    {
        _mutex = mutex;
        _event = waitEvent;
        _ownsMutex = ownsMutex;
    }

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(true, MutexName, out var createdNew);
        var waitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        var instance = new SingleInstance(mutex, waitEvent, createdNew);
        if (createdNew)
            instance.Listen();
        return instance;
    }

    /// <summary>Wakes up the running instance.</summary>
    public void SignalExisting()
    {
        try
        {
            _event.Set();
        }
        catch (Exception ex)
        {
            Log("Signal previous instance: " + ex.Message);
        }
    }

    /// <summary>
    /// Waits until the previous instance releases the mutex. Returns true when
    /// this process may continue as the single instance.
    /// </summary>
    public bool WaitForPrevious(int milliseconds = 5000)
    {
        if (_ownsMutex)
            return true;

        try
        {
            if (!_mutex.WaitOne(milliseconds))
                return false;

            _ownsMutex = true;
            _wait?.Unregister(null);
            _wait = null;
            Listen();
            return true;
        }
        catch (AbandonedMutexException)
        {
            // The previous process was terminated without releasing the mutex.
            _ownsMutex = true;
            Listen();
            return true;
        }
        catch (Exception ex)
        {
            Log("Wait previous instance: " + ex.Message);
            return false;
        }
    }

    public void Attach(Form form)
    {
        _form = form;
        if (!_closePending)
            return;
        if (form.IsHandleCreated)
            RequestClose();
        else
            form.HandleCreated += (_, _) => RequestClose();
    }

    private void Listen()
    {
        _wait = ThreadPool.RegisterWaitForSingleObject(_event,
            (_, _) => RequestClose(), null, Timeout.Infinite, false);
    }

    private void RequestClose()
    {
        _closePending = true;
        var form = _form;
        if (form == null || form.IsDisposed)
            return;

        try
        {
            if (form.IsHandleCreated)
            {
                form.BeginInvoke(new Action(() =>
                {
                    if (form is MainForm main)
                        main.RequestExternalClose();
                    else
                        form.Close();
                }));
            }
        }
        catch (Exception ex)
        {
            Log("Close previous instance: " + ex.Message);
        }
    }

    private static void Log(string message)
    {
        try
        {
            var dir = AppSettings.DataDirectory;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "instance.log"),
                $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never prevent startup.
        }
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _wait = null;
        _event.Dispose();
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The mutex was abandoned by another thread.
            }
        }

        _mutex.Dispose();
    }
}
