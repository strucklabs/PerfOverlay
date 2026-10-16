using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;
using PerfOverlay.Sensors;

namespace PerfOverlay.Analysis;

/// <summary>
/// Zeichnet Spielsitzungen als CSV auf – eine Zeile pro Sekunde, Auswertung mit Auswertung.html.
///
/// Eine Sitzung beginnt, sobald ein Spiel Frames liefert, und endet nach 30 s ohne Frames
/// (oder beim Beenden des Overlays). Format: Semikolon als Trenner, Dezimalkomma – öffnet sich
/// in deutschem Excel direkt korrekt. Zeilen mit "#" am Anfang sind Metadaten.
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    private static readonly TimeSpan RowInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(30);
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PerfOverlay", "Sitzungen");

    private readonly string _folder;
    private readonly string? _fixedName;

    /// <param name="folder">Zielordner (Standard: Dokumente\PerfOverlay\Sitzungen).</param>
    /// <param name="fixedName">Fester Dateiname statt Zeitstempel (nur für die Vorschau-Beispieldaten).</param>
    public SessionRecorder(string? folder = null, string? fixedName = null)
    {
        _folder = folder ?? Folder;
        _fixedName = fixedName;
    }

    private StreamWriter? _writer;
    private DateTime _sessionStart;
    private DateTime _lastFrames;
    private DateTime _lastRow;
    private int _gamePid;

    public bool Enabled { get; set; } = true;
    public bool Recording => _writer != null;
    public string? CurrentFile { get; private set; }

    public static readonly string[] Columns =
    [
        "Zeit", "Sekunde", "Spiel", "FPS", "FPS_Spiel", "Low1_FPS", "Frametime_ms", "P99_ms",
        "CPU_Busy_ms", "GPU_Busy_ms", "GPU_Wartet_ms", "Engpass", "Urteil", "Hauptthread_Proz",
        "CPU_Proz", "PKern_max_Proz", "CPU_GHz", "CPU_Temp", "CPU_W",
        "GPU_Proz", "GPU_MHz", "GPU_Temp", "GPU_W", "VRAM_GB", "VRAM_ausgelagert_GB",
        "RAM_GB", "RAM_Proz", "Nachladen_s", "Disk_Proz", "Ruckler", "Meldungen",
    ];

    public void Record(Snapshot s, Verdict v)
    {
        if (!Enabled)
        {
            Stop();
            return;
        }

        var f = s.Frames;
        if (f != null)
        {
            // Spielwechsel = neue Sitzung
            if (_writer != null && f.Pid != _gamePid) Stop();
            if (_writer == null) Start(s, f);
            _lastFrames = s.Time;
        }
        else if (_writer != null && s.Time - _lastFrames > SessionTimeout)
        {
            Stop();
            return;
        }

        if (_writer == null || f == null || s.Time - _lastRow < RowInterval) return;
        double sinceLastRow = (s.Time - _lastRow).TotalSeconds;
        _lastRow = s.Time;

        int spikes = f.SpikeAges.Count(age => age <= sinceLastRow);
        var cpu = s.Cpu;
        var gpu = s.Gpu;
        var th = s.CpuThermal;

        _writer.WriteLine(string.Join(';',
            s.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            N((s.Time - _sessionStart).TotalSeconds, 0),
            Text(f.App),
            N(f.Fps, 1), N(f.AppFps, 1), N(f.Low1Fps, 1), N(f.AvgMs, 2), N(f.P99Ms, 2),
            N(f.CpuBusyMs, 2), N(f.GpuBusyMs, 2), N(f.GpuWaitMs, 2),
            v.Kind.ToString(), Text(v.Title), N(s.MainThread?.Percent, 0),
            N(cpu.TotalLoad, 0), N(cpu.MaxPerfCoreLoad, 0), N(cpu.AvgMhz / 1000, 2), N(th?.TempC, 0), N(th?.PowerW, 0),
            N(gpu?.Load, 0), N(gpu?.CoreMhz, 0), N(gpu?.TempC, 0), N(gpu?.PowerW, 0), N(gpu?.VramUsedGB, 2), N(s.GameSharedVramGB, 2),
            N(s.Memory.UsedGB, 1), N(s.Memory.LoadPercent, 0), N(s.Memory.PagesInPerSec, 0), N(s.Disk.ActivePercent, 0),
            spikes.ToString(CultureInfo.InvariantCulture),
            Text(string.Join(" | ", v.Notes.Select(n => n.Text)))));
    }

    private void Start(Snapshot s, FrameStats f)
    {
        Directory.CreateDirectory(_folder);
        string game = Path.GetFileNameWithoutExtension(f.App);
        foreach (char c in Path.GetInvalidFileNameChars()) game = game.Replace(c, '_');
        CurrentFile = Path.Combine(_folder, _fixedName ?? $"{s.Time:yyyy-MM-dd_HH-mm-ss}_{game}.csv");

        // UTF-8 mit BOM, damit Excel die Umlaute erkennt
        _writer = new StreamWriter(CurrentFile, false, new UTF8Encoding(true)) { AutoFlush = true };
        _writer.WriteLine("# PerfOverlay-Sitzung");
        _writer.WriteLine($"# Spiel: {f.App}");
        _writer.WriteLine($"# Start: {s.Time:yyyy-MM-dd HH:mm:ss}");
        _writer.WriteLine($"# CPU: {CpuName()}");
        if (s.Gpu != null) _writer.WriteLine($"# GPU: {s.Gpu.Name}");
        if (s.MemoryConfig != null) _writer.WriteLine($"# RAM: {s.MemoryConfig.Summary}");
        _writer.WriteLine($"# Windows: {Environment.OSVersion.Version}");
        _writer.WriteLine(string.Join(';', Columns));

        _sessionStart = s.Time;
        _lastRow = s.Time - RowInterval;
        _gamePid = f.Pid;
    }

    public void Stop()
    {
        _writer?.Dispose();
        _writer = null;
        CurrentFile = null;
    }

    public void Dispose() => Stop();

    private static string N(double? value, int decimals) =>
        value is double d && !double.IsNaN(d) && !double.IsInfinity(d) ? Math.Round(d, decimals).ToString(De) : "";

    private static string Text(string s) => s.Replace(';', ',').Replace('\n', ' ').Replace('\r', ' ');

    private static string CpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "unbekannt";
        }
        catch
        {
            return "unbekannt";
        }
    }
}
