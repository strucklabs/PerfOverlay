using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PerfOverlay.Sensors;

/// <summary>
/// CPU-Last auf Prozess- und Thread-Ebene aus den CPU-Zeiten, die Windows pro Thread führt.
///
/// Hauptthread: Ein CPU-Limit entsteht meist durch einen einzelnen Render- oder Spiellogik-Thread.
/// Weil Windows diesen Thread ständig zwischen Kernen verschiebt, sieht man das an den Kernbalken
/// oft nicht – an der CPU-Zeit des Threads selbst aber schon (100 % = ein Kern voll ausgelastet).
///
/// Hintergrund: Die größten anderen CPU-Verbraucher, um bei CPU-Limit Störenfriede zu benennen.
/// </summary>
internal sealed class ProcessCpuSensor
{
    private static readonly TimeSpan BackgroundInterval = TimeSpan.FromSeconds(2);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int _ownPid = Environment.ProcessId;

    private int _threadPid;
    private double _threadSampleTime;
    private Dictionary<int, TimeSpan> _threadTimes = new();
    private readonly Dictionary<int, string?> _threadNames = new();

    private double _processSampleTime;
    private Dictionary<int, TimeSpan> _processTimes = new();
    private List<ProcessLoad> _background = [];

    /// <summary>Stärkster Thread des Prozesses seit dem letzten Aufruf; null beim ersten Aufruf oder ohne Zugriff.</summary>
    public ThreadLoad? SampleMainThread(int pid)
    {
        if (pid <= 0) return null;
        double now = _clock.Elapsed.TotalSeconds;
        var times = new Dictionary<int, TimeSpan>();
        try
        {
            using var process = Process.GetProcessById(pid);
            foreach (ProcessThread thread in process.Threads)
            {
                try { times[thread.Id] = thread.TotalProcessorTime; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally { thread.Dispose(); }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;   // Prozess beendet oder geschützt (z. B. durch Anti-Cheat)
        }

        ThreadLoad? result = null;
        if (pid == _threadPid && now > _threadSampleTime)
        {
            double elapsed = now - _threadSampleTime;
            var top = times
                .Where(t => _threadTimes.ContainsKey(t.Key))
                .Select(t => (Id: t.Key, Percent: (t.Value - _threadTimes[t.Key]).TotalSeconds / elapsed * 100))
                .OrderByDescending(t => t.Percent)
                .FirstOrDefault();
            if (top.Id != 0)
                result = new ThreadLoad(Math.Clamp(top.Percent, 0, 100), ThreadName(top.Id));
        }
        else
        {
            _threadNames.Clear();
        }

        _threadPid = pid;
        _threadSampleTime = now;
        _threadTimes = times;
        return result;
    }

    /// <summary>Größte CPU-Verbraucher außer Spiel und Overlay, in % der Gesamt-CPU (alle 2 s aktualisiert).</summary>
    public IReadOnlyList<ProcessLoad> SampleBackground(int gamePid)
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (now - _processSampleTime < BackgroundInterval.TotalSeconds && _processTimes.Count > 0)
            return _background.Where(p => p.Pid != gamePid).ToList();

        var times = new Dictionary<int, TimeSpan>();
        var names = new Dictionary<int, string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id is 0 or 4 || process.Id == _ownPid) continue;   // Leerlauf, System, Overlay
                try
                {
                    times[process.Id] = process.TotalProcessorTime;
                    names[process.Id] = process.ProcessName;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // geschützter Prozess – überspringen
                }
            }
        }

        if (_processTimes.Count > 0)
        {
            double elapsed = now - _processSampleTime;
            double cores = Environment.ProcessorCount;
            _background = times
                .Where(t => _processTimes.ContainsKey(t.Key))
                .Select(t => new ProcessLoad(t.Key, names[t.Key],
                    (t.Value - _processTimes[t.Key]).TotalSeconds / elapsed / cores * 100))
                .Where(p => p.Percent >= 1)
                .OrderByDescending(p => p.Percent)
                .Take(5)
                .ToList();
        }
        _processTimes = times;
        _processSampleTime = now;
        return _background.Where(p => p.Pid != gamePid).ToList();
    }

    /// <summary>Thread-Namen setzen viele Engines selbst ("RenderThread", "GameThread" …).</summary>
    private string? ThreadName(int threadId)
    {
        if (_threadNames.TryGetValue(threadId, out var cached)) return cached;
        string? name = null;
        IntPtr handle = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, (uint)threadId);
        if (handle != IntPtr.Zero)
        {
            try
            {
                if (GetThreadDescription(handle, out IntPtr text) >= 0 && text != IntPtr.Zero)
                {
                    name = Marshal.PtrToStringUni(text);
                    LocalFree(text);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        return _threadNames[threadId] = string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);

    [DllImport("kernel32.dll")]
    private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
