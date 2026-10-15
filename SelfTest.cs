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
        log.AppendLine("=== HWiNFO: CPU-Sensoren (Rohdaten) ===");
        log.AppendLine(HwInfoSensor.DumpCpuReadings());
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
    }
}
