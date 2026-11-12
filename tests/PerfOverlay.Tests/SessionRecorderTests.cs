using System.IO;
using PerfOverlay.Analysis;
using PerfOverlay.Sensors;

namespace PerfOverlay.Tests;

/// <summary>Sitzungsaufzeichnung: Start/Ende, Alt+Tab, Ruckler-Zählung. Schreibt in einen temporären Ordner.</summary>
public sealed class SessionRecorderTests : IDisposable
{
    private const int GamePid = 100, ClaudePid = 200, OtherGamePid = 300;
    private static readonly DateTime T0 = new(2026, 10, 16, 20, 0, 0);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "PerfOverlayTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    private static FrameStats Frames(int pid, string app, params double[] spikeTimes) =>
        new(pid, app, 120, 100, 8.3, 8.3, 10, 5, 0.5, 8, 0) { AppFps = 120, SpikeTimes = spikeTimes };

    private static Snapshot Snap(double seconds, FrameStats? frames, int foreground) =>
        new(T0.AddSeconds(seconds),
            new CpuData(30, [30, 30], 4500, 5000),
            new MemoryData(12, 32, 40, 18, 55, 0),
            new DiskData(1, 0.1, 1),
            null, [], frames, "aktiv", true, null, null)
        { ForegroundPid = foreground };

    private static readonly Verdict Verdict = new("GPU-LIMITIERT", "", Severity.Good, []) { Kind = BottleneckKind.Gpu };

    private string[] Files() => Directory.Exists(_folder) ? Directory.GetFiles(_folder, "*.csv") : [];

    private static int SpikeSum(string file) =>
        File.ReadAllLines(file).Where(l => !l.StartsWith('#')).Skip(1)
            .Sum(l => int.Parse(l.Split(';')[Array.IndexOf(SessionRecorder.Columns, "Ruckler")]));

    [Fact]
    public void Alt_Tab_in_App_mit_eigener_Grafikausgabe_beendet_die_Spielsitzung_nicht()
    {
        using var recorder = new SessionRecorder(_folder, isProcessAlive: _ => true);
        double t = 0;
        for (; t < 70; t += 0.5) recorder.Record(Snap(t, Frames(GamePid, "Warframe.x64.exe"), GamePid), Verdict);
        // 10 s in der Claude-App, die selbst Frames liefert
        for (; t < 80; t += 0.5) recorder.Record(Snap(t, Frames(ClaudePid, "claude.exe"), ClaudePid), Verdict);
        for (; t < 100; t += 0.5) recorder.Record(Snap(t, Frames(GamePid, "Warframe.x64.exe"), GamePid), Verdict);
        recorder.Stop();

        var file = Assert.Single(Files());
        var rows = File.ReadAllLines(file).Where(l => !l.StartsWith('#')).Skip(1).ToList();
        Assert.DoesNotContain(rows, l => l.Contains("claude.exe"));
        // Eine durchgehende Sitzung bis zum Ende – nicht bei Sekunde 70 abgebrochen
        int secondCol = Array.IndexOf(SessionRecorder.Columns, "Sekunde");
        Assert.True(rows.Max(l => int.Parse(l.Split(';')[secondCol])) >= 95);
    }

    [Theory]
    [InlineData("EasyTune.exe")]
    [InlineData("MSIAfterburner.exe")]
    [InlineData("HWiNFO64.exe")]
    public void Hardware_Tools_im_Vordergrund_werden_nicht_als_Spiel_aufgezeichnet(string app)
    {
        // Tuning-Programme zeichnen ihre Oberfläche über die GPU und bleiben beim Einstellen lange im Vordergrund
        using var recorder = new SessionRecorder(_folder, isProcessAlive: _ => true);
        for (double t = 0; t < 120; t += 0.5) recorder.Record(Snap(t, Frames(GamePid, app), GamePid), Verdict);
        recorder.Stop();
        Assert.Empty(Files());
    }

    [Fact]
    public void Leicht_schwankender_Messtakt_ergibt_trotzdem_eine_Zeile_pro_Sekunde()
    {
        // Der 500-ms-Takt kommt mal ein paar Millisekunden zu früh, mal zu spät
        using var recorder = new SessionRecorder(_folder, isProcessAlive: _ => true);
        var rnd = new Random(42);
        for (int i = 0; i < 240; i++)
        {
            double t = i * 0.5 + (rnd.NextDouble() - 0.5) * 0.010;
            recorder.Record(Snap(Math.Max(0, t), Frames(GamePid, "Warframe.x64.exe"), GamePid), Verdict);
        }
        recorder.Stop();

        var rows = File.ReadAllLines(Assert.Single(Files())).Where(l => !l.StartsWith('#')).Skip(1).Count();
        Assert.InRange(rows, 118, 121);   // 120 s → 120 Zeilen, ohne Toleranz nur 95
    }

    [Fact]
    public void Wechsel_zu_anderem_Spiel_im_Vordergrund_beginnt_neue_Sitzung()
    {
        using var recorder = new SessionRecorder(_folder, isProcessAlive: _ => true);
        double t = 0;
        for (; t < 70; t += 0.5) recorder.Record(Snap(t, Frames(GamePid, "Warframe.x64.exe"), GamePid), Verdict);
        for (; t < 140; t += 0.5) recorder.Record(Snap(t, Frames(OtherGamePid, "Cyberpunk2077.exe"), OtherGamePid), Verdict);
        recorder.Stop();

        Assert.Equal(2, Files().Length);
    }

    [Fact]
    public void Ruckler_vor_Sitzungsbeginn_werden_nicht_gezaehlt()
    {
        using var recorder = new SessionRecorder(_folder, isProcessAlive: _ => true);
        // PresentMon hält 10 s Verlauf: beim Start sind schon zwei ältere Ruckler (Zeit 3 und 4) enthalten
        double t = 0;
        for (; t < 30; t += 0.5) recorder.Record(Snap(t, Frames(GamePid, "Spiel.exe", 3, 4), GamePid), Verdict);
        // während der Sitzung kommt einer dazu
        for (; t < 70; t += 0.5) recorder.Record(Snap(t, Frames(GamePid, "Spiel.exe", 3, 4, 50), GamePid), Verdict);
        recorder.Stop();

        Assert.Equal(1, SpikeSum(Assert.Single(Files())));
    }

    [Fact]
    public void Lange_Ladepause_teilt_die_Sitzung_nicht()
    {
        // Wie Windrose beim Gebietswechsel: 47 s ohne Bilder, das Spiel läuft aber weiter
        using var recorder = new SessionRecorder(_folder, isProcessAlive: _ => true);
        double t = 0;
        for (; t < 70; t += 0.5) recorder.Record(Snap(t, Frames(GamePid, "Windrose-Win64-Shipping.exe"), GamePid), Verdict);
        for (; t < 117; t += 0.5) recorder.Record(Snap(t, null, GamePid), Verdict);
        for (; t < 160; t += 0.5) recorder.Record(Snap(t, Frames(GamePid, "Windrose-Win64-Shipping.exe"), GamePid), Verdict);
        recorder.Stop();
        Assert.Single(Files());
    }

    [Fact]
    public void Beendetes_Spiel_beendet_die_Sitzung_nach_wenigen_Sekunden()
    {
        bool alive = true;
        using var recorder = new SessionRecorder(_folder, isProcessAlive: _ => alive);
        double t = 0;
        for (; t < 70; t += 0.5) recorder.Record(Snap(t, Frames(GamePid, "Spiel.exe"), GamePid), Verdict);
        alive = false;
        for (; t < 80; t += 0.5) recorder.Record(Snap(t, null, 0), Verdict);
        Assert.False(recorder.Recording);
        Assert.Single(Files());
    }

    [Fact]
    public void Ohne_Bilder_endet_die_Sitzung_nach_zehn_Minuten()
    {
        using var recorder = new SessionRecorder(_folder, isProcessAlive: _ => true);
        double t = 0;
        for (; t < 70; t += 0.5) recorder.Record(Snap(t, Frames(GamePid, "Spiel.exe"), GamePid), Verdict);
        for (; t < 70 + 9 * 60; t += 1) recorder.Record(Snap(t, null, 0), Verdict);
        Assert.True(recorder.Recording);                 // 9 min Pause: läuft noch
        for (; t < 70 + 11 * 60; t += 1) recorder.Record(Snap(t, null, 0), Verdict);
        Assert.False(recorder.Recording);
    }

    [Fact]
    public void Prozesspruefung_erkennt_laufende_und_fehlende_Prozesse()
    {
        Assert.True(SessionRecorder.ProcessAlive(Environment.ProcessId));
        Assert.False(SessionRecorder.ProcessAlive(int.MaxValue - 1));
    }

    [Fact]
    public void Kurze_Sitzungen_werden_verworfen()
    {
        using var recorder = new SessionRecorder(_folder, isProcessAlive: _ => true);
        for (double t = 0; t < 20; t += 0.5) recorder.Record(Snap(t, Frames(GamePid, "Spiel.exe"), GamePid), Verdict);
        recorder.Stop();
        Assert.Empty(Files());
    }
}
