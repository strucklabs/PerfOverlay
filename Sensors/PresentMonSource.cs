using System.Diagnostics;
using System.Globalization;
using System.IO;
using PerfOverlay.Native;

namespace PerfOverlay.Sensors;

/// <summary>
/// Startet Intels PresentMon (Konsolenversion) als Kindprozess und liest dessen CSV-Ausgabe live von STDOUT.
/// PresentMon wertet per ETW die Present-Events von DirectX/Vulkan/OpenGL aus und liefert pro Frame
/// u. a. Frametime, CPU-Busy und GPU-Busy – die Grundlage für eine echte Engpass-Bestimmung.
/// </summary>
internal sealed class PresentMonSource : IDisposable
{
    private const string SessionName = "PerfOverlay";
    private const double HistorySeconds = 10;     // Fenster für 1%-Low und Median
    private const double AverageSeconds = 1.5;    // Fenster für FPS und Mittelwerte

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private readonly Dictionary<int, ProcessFrames> _processes = new();

    private Process? _process;
    private volatile bool _headerSeen;
    private volatile bool _exited;
    private volatile bool _accessDenied;
    private volatile string? _lastError;

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "tools", "PresentMon.exe");

    private double _startedAt;

    /// <summary>
    /// PresentMon schreibt die CSV-Kopfzeile erst beim ersten Frame. Ohne 3D-Anwendung kommt also nichts –
    /// läuft der Prozess nach einigen Sekunden aber noch (kein "access denied"), ist die Messung bereit.
    /// </summary>
    private bool Settled => _clock.Elapsed.TotalSeconds - _startedAt > 3;

    public bool Running => _process != null && !_exited && !_accessDenied && (_headerSeen || Settled);

    public string Status =>
        _accessDenied ? "FPS-Messung braucht Admin-Rechte" :
        !File.Exists(ExePath) ? "tools\\PresentMon.exe fehlt" :
        _process == null ? "nicht gestartet" :
        _exited ? (_lastError ?? "PresentMon wurde beendet") :
        _headerSeen ? "aktiv" :
        Settled ? "bereit" : "startet …";

    public void Start()
    {
        if (_process != null || !File.Exists(ExePath)) return;

        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
                 {
                     "--output_stdout", "--no_console_stats",
                     "--session_name", SessionName, "--stop_existing_session",
                     "--no_track_input",
                     "--exclude", "dwm.exe",
                     "--exclude", Path.GetFileName(Environment.ProcessPath ?? "PerfOverlay.exe"),
                 })
            psi.ArgumentList.Add(arg);

        try
        {
            _startedAt = _clock.Elapsed.TotalSeconds;
            _process = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            _lastError = "PresentMon-Start fehlgeschlagen: " + ex.Message;
            _exited = true;
            return;
        }

        ChildProcessJob.Add(_process);
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => _exited = true;
        _process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            if (e.Data.Contains("access denied", StringComparison.OrdinalIgnoreCase)) _accessDenied = true;
            else if (e.Data.StartsWith("error:", StringComparison.OrdinalIgnoreCase)) _lastError = e.Data[6..].Trim();
        };
        _process.BeginErrorReadLine();

        var reader = _process.StandardOutput;
        new Thread(() => ReadLoop(reader)) { IsBackground = true, Name = "PresentMon reader" }.Start();
    }

    private void ReadLoop(StreamReader reader)
    {
        int iApp = -1, iPid = -1, iMs = -1, iCpuBusy = -1, iCpuWait = -1, iGpuBusy = -1, iGpuWait = -1;
        int columns = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!_headerSeen)
            {
                if (!line.StartsWith("Application,", StringComparison.Ordinal)) continue;
                var header = line.Split(',');
                int Col(params string[] names) => names.Select(n => Array.IndexOf(header, n)).FirstOrDefault(i => i >= 0, -1);
                columns = header.Length;
                iApp = Col("Application");
                iPid = Col("ProcessID");
                iMs = Col("MsBetweenPresents", "FrameTime", "MsBetweenAppStart");
                iCpuBusy = Col("MsCPUBusy", "CPUBusy");
                iCpuWait = Col("MsCPUWait", "CPUWait");
                iGpuBusy = Col("MsGPUBusy", "GPUBusy");
                iGpuWait = Col("MsGPUWait", "GPUWait");
                _headerSeen = true;
                continue;
            }

            var f = line.Split(',');
            if (f.Length < columns || !int.TryParse(f[iPid], out int pid)) continue;
            double ms = Num(f, iMs);
            if (!(ms > 0 && ms < 5000)) continue;

            double now = _clock.Elapsed.TotalSeconds;
            var frame = new Frame(now, ms, Num(f, iCpuBusy), Num(f, iCpuWait), Num(f, iGpuBusy), Num(f, iGpuWait));

            lock (_lock)
            {
                if (!_processes.TryGetValue(pid, out var proc))
                    _processes[pid] = proc = new ProcessFrames(iApp >= 0 ? f[iApp] : $"PID {pid}");
                proc.Frames.Enqueue(frame);
                while (proc.Frames.Count > 0 && proc.Frames.Peek().T < now - HistorySeconds)
                    proc.Frames.Dequeue();
            }
        }
    }

    private static double Num(string[] f, int i) =>
        i >= 0 && double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;

    /// <summary>
    /// Statistik für den Prozess im Vordergrund (das Spiel). Hat der keine Frames,
    /// wird der Prozess mit den meisten aktuellen Frames genommen.
    /// </summary>
    public FrameStats? GetStats(int preferredPid)
    {
        lock (_lock)
        {
            double now = _clock.Elapsed.TotalSeconds;

            foreach (var stale in _processes.Where(p => p.Value.Frames.Count == 0 || p.Value.Frames.Last().T < now - 5).Select(p => p.Key).ToList())
                _processes.Remove(stale);

            int Recent(ProcessFrames p) => p.Frames.Count(fr => fr.T >= now - AverageSeconds);

            int pid = 0;
            ProcessFrames? target = null;
            if (_processes.TryGetValue(preferredPid, out var fg) && Recent(fg) >= 5)
            {
                pid = preferredPid;
                target = fg;
            }
            else
            {
                foreach (var (p, frames) in _processes)
                {
                    int n = Recent(frames);
                    if (n >= 10 && (target == null || n > Recent(target)))
                    {
                        pid = p;
                        target = frames;
                    }
                }
            }
            if (target == null) return null;

            var recent = target.Frames.Where(fr => fr.T >= now - AverageSeconds).ToList();
            if (recent.Count < 3) return null;

            var sorted = target.Frames.Select(fr => fr.Ms).Order().ToArray();
            double median = sorted[sorted.Length / 2];
            double p99 = sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * 0.99) - 1, 0, sorted.Length - 1)];
            double avgMs = recent.Average(fr => fr.Ms);

            static double Avg(IEnumerable<double> values)
            {
                var valid = values.Where(v => !double.IsNaN(v)).ToList();
                return valid.Count > 0 ? valid.Average() : double.NaN;
            }

            return new FrameStats(
                pid, target.App,
                1000 / avgMs, 1000 / p99,
                avgMs, median, p99,
                Avg(recent.Select(fr => fr.CpuBusy)),
                Avg(recent.Select(fr => fr.CpuWait)),
                Avg(recent.Select(fr => fr.GpuBusy)),
                Avg(recent.Select(fr => fr.GpuWait)));
        }
    }

    public void Dispose()
    {
        if (_process == null) return;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch { /* bereits beendet */ }

        // Ein gekillter PresentMon lässt seine ETW-Session offen – sauber schließen.
        try
        {
            var psi = new ProcessStartInfo(ExePath, $"--terminate_existing_session --session_name {SessionName}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var cleanup = Process.Start(psi);
            cleanup?.WaitForExit(3000);
        }
        catch { /* best effort */ }

        _process.Dispose();
        _process = null;
    }

    private readonly record struct Frame(double T, double Ms, double CpuBusy, double CpuWait, double GpuBusy, double GpuWait);

    private sealed class ProcessFrames(string app)
    {
        public string App { get; } = app;
        public Queue<Frame> Frames { get; } = new();
    }
}
