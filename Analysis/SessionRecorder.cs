using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;
using PerfOverlay.Sensors;

namespace PerfOverlay.Analysis;

/// <summary>
/// Zeichnet Spielsitzungen als CSV auf – eine Zeile pro Sekunde, Auswertung mit Auswertung.html.
///
/// Eine Sitzung beginnt, sobald ein Spiel Frames liefert, und endet, wenn das Spiel beendet wird, ein anderes Spiel
/// in den Vordergrund kommt, 10 Minuten lang keine Frames kommen oder das Overlay beendet wird. Lange Ladepausen
/// erscheinen als Lücke in der Datei, teilen die Sitzung aber nicht. Format: Semikolon als Trenner, Dezimalkomma – öffnet sich
/// in deutschem Excel direkt korrekt. Zeilen mit "#" am Anfang sind Metadaten.
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    private static readonly TimeSpan RowInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RowTolerance = TimeSpan.FromMilliseconds(150);
    // Eine Sitzung endet, wenn das Spiel beendet ist (geprüft nach ein paar Sekunden ohne Bilder) – nicht schon nach
    // einer langen Ladepause: Manche Spiele liefern beim Laden eines Gebiets über 30 s keine Bilder.
    private static readonly TimeSpan GoneCheckAfter = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AliveCheckInterval = TimeSpan.FromSeconds(2);
    // Läuft das Spiel noch, liefert aber gar nichts mehr (minimiert, Spieler weg), endet die Sitzung nach 10 Minuten
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PerfOverlay", "Sitzungen");

    private readonly string _folder;
    private readonly string? _fixedName;
    private readonly Func<int, bool> _isProcessAlive;

    /// <param name="folder">Zielordner (Standard: Dokumente\PerfOverlay\Sitzungen).</param>
    /// <param name="fixedName">Fester Dateiname statt Zeitstempel (nur für die Vorschau-Beispieldaten).</param>
    /// <param name="isProcessAlive">Läuft der Prozess noch? (Standard: Windows fragen; für Tests ersetzbar)</param>
    public SessionRecorder(string? folder = null, string? fixedName = null, Func<int, bool>? isProcessAlive = null)
    {
        _folder = folder ?? Folder;
        _fixedName = fixedName;
        _isProcessAlive = isProcessAlive ?? ProcessAlive;
    }

    /// <summary>Prozess läuft noch; ohne Zugriff (geschützte Spiele) im Zweifel ja – dann greift die 10-Minuten-Grenze.</summary>
    internal static bool ProcessAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            try { return !p.HasExited; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return true; }
        }
        catch (ArgumentException)
        {
            return false;   // kein Prozess mit dieser ID
        }
    }

    private DateTime _lastAliveCheck;

    private StreamWriter? _writer;
    private DateTime _sessionStart;
    private DateTime _lastFrames;
    private DateTime _lastRow;
    private int _gamePid;
    private double _lastSpikeTime = double.MinValue;

    public bool Enabled { get; set; } = true;
    public bool Recording => _writer != null;
    public string? CurrentFile { get; private set; }

    public static readonly string[] Columns =
    [
        "Zeit", "Sekunde", "Spiel", "FPS", "FPS_Spiel", "Low1_FPS", "Frametime_ms", "P99_ms",
        "CPU_Busy_ms", "GPU_Busy_ms", "GPU_Wartet_ms", "Engpass", "Urteil", "Hauptthread_Proz",
        "CPU_Proz", "PKern_max_Proz", "CPU_GHz", "CPU_Temp", "CPU_W",
        "GPU_Proz", "GPU_MHz", "GPU_Temp", "GPU_W", "VRAM_GB", "VRAM_ausgelagert_GB",
        "GPU_V", "GPU_Hotspot", "GPU_Speicher_Temp", "GPU_Luefter_Proz", "GPU_Luefter_U_min", "CPU_Luefter_U_min",
        "RAM_GB", "RAM_Proz", "Nachladen_s", "Disk_Proz", "Ruckler", "Pacing_ms",
        "Ping_Router_ms", "Ping_Internet_ms", "Jitter_Internet_ms", "Verlust_Internet_Proz", "Netz_Down_MBs", "Netz_Up_MBs",
        "Verbindungen", "Tote_Verbindungen", "DPC_max_Proz", "Commit_Proz", "Spiel_RAM_GB",
        "Meldungen",
    ];

    public void Record(Snapshot s, Verdict v)
    {
        if (!Enabled)
        {
            Stop();
            return;
        }

        var f = s.Frames;
        // Frames eines anderen Prozesses während einer Sitzung: Nur ein anderes Spiel im Vordergrund beendet
        // die Sitzung. Alt+Tab in eine App mit eigener Grafikausgabe (Browser, Discord, Claude) zählt wie
        // "Spiel liefert gerade nichts" – sonst würde jede kurze Unterbrechung die Aufzeichnung zerteilen.
        if (f != null && _writer != null && f.Pid != _gamePid)
        {
            if (IsGameInForeground(s, f)) Stop();
            else f = null;
        }
        if (f != null)
        {
            if (_writer == null && IsGameInForeground(s, f)) Start(s, f);
            if (_writer != null) _lastFrames = s.Time;
        }
        else if (_writer != null && s.Time - _lastFrames > GoneCheckAfter)
        {
            bool idleTooLong = s.Time - _lastFrames > IdleTimeout;
            bool gone = false;
            if (!idleTooLong && s.Time - _lastAliveCheck >= AliveCheckInterval)
            {
                _lastAliveCheck = s.Time;
                gone = !_isProcessAlive(_gamePid);
            }
            if (idleTooLong || gone)
            {
                Stop();
                return;
            }
        }

        // Toleranz für den Messtakt: kommt er ein paar Millisekunden zu früh, fehlte sonst eine ganze Sekunde
        if (_writer == null || f == null || s.Time - _lastRow < RowInterval - RowTolerance) return;
        _lastRow = s.Time;

        // Ruckler über ihren Zeitstempel zählen: Ein Frame wird erst bewertet, wenn seine Nachfolger da sind,
        // und taucht daher u. U. erst einen Takt später auf – so wird er weder doppelt gezählt noch übersprungen.
        int spikes = f.SpikeTimes.Count(t => t > _lastSpikeTime);
        if (f.SpikeTimes.Length > 0) _lastSpikeTime = Math.Max(_lastSpikeTime, f.SpikeTimes.Max());
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
            N(s.GpuThermal?.CoreVoltage, 3), N(s.GpuThermal?.HotspotC, 0), N(s.GpuThermal?.MemoryJunctionC, 0),
            N(gpu?.FanPercent, 0), N(s.GpuThermal?.FanRpm, 0), N(th?.FanRpm, 0),
            N(s.Memory.UsedGB, 1), N(s.Memory.LoadPercent, 0), N(s.Memory.PagesInPerSec, 0), N(s.Disk.ActivePercent, 0),
            spikes.ToString(CultureInfo.InvariantCulture),
            N(f.PacingMs, 2),
            N(s.Network?.Router?.AvgMs, 1), N(s.Network?.Internet?.AvgMs, 1), N(s.Network?.Internet?.JitterMs, 1),
            N(s.Network?.Internet?.LossPercent, 0), N(s.Network?.DownMBps, 2), N(s.Network?.UpMBps, 2),
            N(s.GameConnections?.Total, 0), N(s.GameConnections?.CloseWait, 0), N(cpu.DpcMaxPercent, 1), N(s.Memory.CommitPercent, 0), N(s.GameMemoryGB, 2),
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
        if (s.Cpu.IsHybrid) _writer.WriteLine("# Hybrid-CPU: ja (P- und E-Kerne)");
        if (s.Gpu != null) _writer.WriteLine($"# GPU: {s.Gpu.Name}");
        if (s.MemoryConfig != null) _writer.WriteLine($"# RAM: {s.MemoryConfig.Summary}");
        _writer.WriteLine($"# Windows: {Environment.OSVersion.Version}");
        if (s.Timer != null) _writer.WriteLine($"# Zeitgeber: {s.Timer.Summary}");
        // Zeigt der Auswertung auch, dass die Aufzeichnung aus einer Version mit eigener GPU-Abfrage stammt
        _writer.WriteLine($"# CPU-Sensoren: {s.CpuThermal?.Source ?? "keine"}");
        foreach (var (label, value) in s.System?.Entries() ?? [])
            _writer.WriteLine($"# {label}: {value}");
        _writer.WriteLine(string.Join(';', Columns));

        _sessionStart = s.Time;
        _lastRow = s.Time - RowInterval;
        _gamePid = f.Pid;
        // Ruckler, die vor Sitzungsbeginn liegen (PresentMon hält 10 s Verlauf), nicht mitzählen
        _lastSpikeTime = f.SpikeTimes.Length > 0 ? f.SpikeTimes.Max() : double.MinValue;
    }

    /// <summary>
    /// Neue Sitzung nur für ein Spiel im Vordergrund – Browser, Launcher und Apps mit DirectX-Oberfläche
    /// (Steam, Discord, Electron-Apps) liefern ebenfalls Frames.
    /// </summary>
    private static bool IsGameInForeground(Snapshot s, FrameStats f) => f.Pid == s.ForegroundPid && !GameFilter.IsNonGame(f.App);

    /// <summary>Kürzere Sitzungen (kurz angeklickt, Alt+Tab) werden wieder gelöscht.</summary>
    private static readonly TimeSpan MinimumSession = TimeSpan.FromSeconds(60);

    public void Stop()
    {
        if (_writer == null) return;
        _writer.Dispose();
        _writer = null;
        if (_lastRow - _sessionStart < MinimumSession && CurrentFile != null)
        {
            try { File.Delete(CurrentFile); }
            catch (IOException) { /* bleibt liegen – harmlos */ }
        }
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
