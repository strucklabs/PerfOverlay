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

        Snapshot? snapshot = null;
        Verdict? verdict = null;
        for (int i = 0; i < Math.Max(2, seconds * 2); i++)
        {
            await Task.Delay(500);
            snapshot = hub.Sample();
            verdict = analyzer.Analyze(snapshot);
            log.AppendLine($"[{snapshot.Time:HH:mm:ss.f}] {verdict.Title} | {verdict.Detail}");
        }

        log.AppendLine();
        log.AppendLine(JsonSerializer.Serialize(snapshot, json));
        log.AppendLine(JsonSerializer.Serialize(verdict, json));
        File.WriteAllText(Path.Combine(dir, "selftest.txt"), log.ToString());

        var window = new OverlayWindow();
        // Beispielmeldungen, damit das Bild auch aktive und vergangene Einträge zeigt
        var demo = verdict! with
        {
            Notes =
            [
                new Note("demo-ram", "RAM knapp (91 % belegt, 812 Seiten/s nachgeladen)", Severity.Warn),
                new Note("demo-power", "GPU am Power-Limit (normal unter Volllast)", Severity.Info),
            ],
        };
        window.LogNotes(demo, snapshot!.Time.AddSeconds(-83));
        window.LogNotes(verdict! with { Notes = [demo.Notes[1]] }, snapshot.Time.AddSeconds(-82));
        window.Render(snapshot, verdict!, window.LogNotes(verdict! with { Notes = [demo.Notes[1]] }, snapshot.Time));
        window.SaveImage(Path.Combine(dir, "selftest.png"));
    }
}
