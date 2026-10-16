using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PerfOverlay.Analysis;
using PerfOverlay.Native;
using PerfOverlay.Sensors;

namespace PerfOverlay;

/// <summary>
/// "PerfOverlay.exe --selftest [Sekunden]": misst ohne sichtbares Fenster und schreibt
/// selftest.txt (Rohwerte als JSON) und selftest.png (gerendertes Overlay) neben die EXE.
/// </summary>
internal static class SelfTest
{
    public static async Task RunAsync(int seconds)
    {
        var json = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters = { new JsonStringEnumConverter() },
        };
        var log = new StringBuilder();
        var dir = AppContext.BaseDirectory;

        using var hub = new SensorHub();
        hub.StartFrameCapture();
        var analyzer = new BottleneckAnalyzer();
        log.AppendLine($"Admin: {Win32.IsElevated()}   NVML: {hub.HasNvml}   PresentMon: {PresentMonSource.ExePath}");
        using (var icon = OverlayWindow.LoadTrayIcon())
            log.AppendLine($"Tray-Icon: {(icon == System.Drawing.SystemIcons.Application ? "Standard (eigenes fehlt!)" : $"eigenes, {icon.Width}×{icon.Height}")}");

        // Echte Meldungen der Messung sammeln – das Bild zeigt am Ende denselben Stand wie das Overlay
        var window = new OverlayWindow();
        IReadOnlyList<LoggedNote> notes = [];
        Snapshot? snapshot = null;
        Verdict? verdict = null;
        for (int i = 0; i < Math.Max(2, seconds * 2); i++)
        {
            await Task.Delay(500);
            snapshot = hub.Sample();
            verdict = analyzer.Analyze(snapshot);
            notes = window.LogNotes(verdict, snapshot.Time);
            string active = verdict.Notes.Count > 0 ? "  ▲ " + string.Join(" ▲ ", verdict.Notes.Select(n => n.Text)) : "";
            log.AppendLine($"[{snapshot.Time:HH:mm:ss.f}] {verdict.Title} | {verdict.Detail}{active}");
        }

        log.AppendLine();
        log.AppendLine(JsonSerializer.Serialize(snapshot, json));
        log.AppendLine(JsonSerializer.Serialize(verdict, json));

        log.AppendLine();
        log.AppendLine("=== System ===");
        log.AppendLine($"Windows: {Environment.OSVersion.VersionString}   Kerne (logisch): {Environment.ProcessorCount}");
        log.AppendLine();
        log.AppendLine("=== HWiNFO: CPU- und GPU-Sensoren (Rohdaten) ===");
        log.AppendLine(HwInfoSensor.DumpReadings());
        File.WriteAllText(Path.Combine(dir, "selftest.txt"), log.ToString());

        window.Render(snapshot!, verdict!, notes);
        window.SaveImage(Path.Combine(dir, "selftest.png"));
    }

    /// <summary>
    /// "PerfOverlay.exe --preview": rendert das Overlay mit KÜNSTLICHEN Frame-Daten (CPU-limitierte Szene
    /// mit Rucklern) nach preview.png – nur zur Kontrolle der Darstellung ohne laufendes Spiel.
    /// Echte Hardwarewerte (CPU, GPU, RAM) stammen weiterhin von diesem PC.
    /// </summary>
    public static async Task PreviewAsync()
    {
        using var hub = new SensorHub();
        var analyzer = new BottleneckAnalyzer();
        var window = new OverlayWindow();
        var rnd = new Random(42);
        IReadOnlyList<LoggedNote> notes = [];
        Snapshot? snapshot = null;
        Verdict? verdict = null;

        for (int tick = 0; tick < 24; tick++)
        {
            await Task.Delay(100);
            var real = hub.Sample();
            bool cpuPhase = tick % 6 != 0;   // überwiegend CPU-, gelegentlich GPU-limitiert
            double ms = cpuPhase ? 8.3 : 7.0;
            var times = Enumerable.Range(0, 600)
                .Select(i => (Age: 5.0 - i * (5.0 / 600), Ms: ms + rnd.NextDouble() * 1.2 + (i is 180 or 420 ? 30 : 0)))
                .ToArray();
            var frames = new FrameStats(4711, "DemoSpiel.exe", 1000 / ms, 1000 / 14.0, ms, ms + 0.5, 14,
                cpuPhase ? ms * 0.95 : ms * 0.5, 0.2, cpuPhase ? ms * 0.62 : ms * 0.97, cpuPhase ? ms * 0.33 : 0.1)
            {
                AppFps = 1000 / ms,
                PresentMode = "Hardware: Independent Flip",
                SyncInterval = 0,
                Frametimes = times,
                SpikeAges = [5.0 - 180 * (5.0 / 600), 5.0 - 420 * (5.0 / 600)],
            };
            snapshot = real with
            {
                Time = real.Time.AddSeconds(tick * 1.25),
                Frames = frames,
                FrameSourceRunning = true,
                FrameSourceStatus = "aktiv",
                MainThread = new ThreadLoad(cpuPhase ? 97 : 71, "RenderThread"),
                BackgroundCpu = [new ProcessLoad(1, "chrome", 9), new ProcessLoad(2, "Discord", 6)],
            };
            verdict = analyzer.Analyze(snapshot);
            notes = window.LogNotes(verdict, snapshot.Time);
        }

        window.Render(snapshot!, verdict!, notes);
        window.SaveImage(Path.Combine(AppContext.BaseDirectory, "preview.png"));

        var baseSnapshot = hub.Sample();
        WriteSampleSession(baseSnapshot, "preview-sitzung-vorher.csv", cpuSpeedup: 1.0, seed: 1);
        WriteSampleSession(baseSnapshot, "preview-sitzung-nachher.csv", cpuSpeedup: 1.18, seed: 2);
    }

    /// <summary>
    /// Künstliche 3-Minuten-Sitzung zum Testen von Auswertung.html: 60 s GPU-limitiert, 60 s CPU-limitiert
    /// (offene Welt, mit Rucklern), 60 s im 60-FPS-Limit. cpuSpeedup simuliert z. B. aktiviertes XMP.
    /// </summary>
    private static void WriteSampleSession(Snapshot real, string fileName, double cpuSpeedup, int seed)
    {
        var rnd = new Random(seed);
        var analyzer = new BottleneckAnalyzer();
        using var recorder = new SessionRecorder(AppContext.BaseDirectory, fileName);
        var start = new DateTime(2026, 10, 16, 20, 0, 0).AddMinutes(seed * 10);

        for (int tick = 0; tick < 360; tick++)
        {
            double t = tick * 0.5;
            int phase = t < 60 ? 0 : t < 120 ? 1 : 2;
            double jitter = rnd.NextDouble();
            double ms = phase switch
            {
                0 => 9.0 + jitter * 0.8,                       // GPU-limitiert, ~105 FPS
                1 => (12.5 + jitter * 2.5) / cpuSpeedup,       // CPU-limitiert, profitiert von schnellerem RAM
                _ => 16.67,                                    // 60-FPS-Limit
            };
            double gpuBusy = phase == 0 ? ms * 0.97 : phase == 1 ? 8.6 : 9.0;
            double cpuBusy = phase == 1 ? ms * 0.95 : phase == 0 ? ms * 0.6 : 16.4;
            bool spike = phase == 1 && rnd.NextDouble() < 0.08;
            var frames = new FrameStats(4711, "DemoSpiel.exe", 1000 / ms, 1000 / (ms * (spike ? 3 : 1.35)), ms, ms, ms * 1.35,
                cpuBusy, phase == 2 ? 0.1 : 0.3, gpuBusy, Math.Max(0, ms - gpuBusy))
            {
                AppFps = 1000 / ms,
                SyncInterval = 0,
                SpikeAges = spike ? [0.2] : [],
            };
            var snapshot = real with
            {
                Time = start.AddSeconds(t),
                Frames = frames,
                FrameSourceRunning = true,
                FrameSourceStatus = "aktiv",
                Cpu = real.Cpu with { TotalLoad = phase == 1 ? 38 + jitter * 6 : 22 + jitter * 5 },
                Gpu = real.Gpu == null ? null : real.Gpu with
                {
                    Load = Math.Min(99, gpuBusy / ms * 100),
                    TempC = 62 + Math.Min(t, 120) / 12,
                    PowerW = 180 + gpuBusy / ms * 130,
                },
                MainThread = new ThreadLoad(phase == 1 ? 94 + jitter * 5 : phase == 0 ? 62 : 30, null),
                BackgroundCpu = [],
                ForegroundPid = frames.Pid,   // das Demo-Spiel gilt als Vordergrundfenster
                // "nachher" simuliert ein GPU-Undervolting: niedrigere Spannung, kühlerer Speicher
                GpuThermal = new GpuThermalData(
                    cpuSpeedup > 1 ? 0.850 : 1.050,
                    62 + Math.Min(t, 120) / 12 + (cpuSpeedup > 1 ? 9 : 14),
                    cpuSpeedup > 1 ? 88 + Math.Min(t, 120) / 20 : 96 + Math.Min(t, 120) / 15),
            };
            recorder.Record(snapshot, analyzer.Analyze(snapshot));
        }
    }
}
