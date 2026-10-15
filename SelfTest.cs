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
}
