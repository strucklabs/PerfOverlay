using PerfOverlay.Analysis;
using PerfOverlay.Sensors;

namespace PerfOverlay.Tests;

public class PingSeriesTests
{
    [Fact]
    public void Ping_Jitter_und_Verlust_werden_berechnet()
    {
        var series = new PingSeries();
        foreach (double? rtt in new double?[] { 10, 20, null, 10, 20 }) series.Add(rtt);

        var stats = series.Stats()!;
        Assert.Equal(15, stats.AvgMs);
        Assert.Equal(10, stats.JitterMs);   // |20-10|, |10-20|, |20-10| → Ø 10
        Assert.Equal(20, stats.LossPercent);
    }

    [Fact]
    public void Nur_die_letzten_Messungen_zaehlen()
    {
        var series = new PingSeries(capacity: 5);
        for (int i = 0; i < 5; i++) series.Add(null);   // anfangs Netz weg
        for (int i = 0; i < 5; i++) series.Add(12);     // jetzt stabil
        Assert.Equal(0, series.Stats()!.LossPercent);
    }

    [Fact]
    public void Ohne_jede_Antwort_kein_Mittelwert_aber_voller_Verlust()
    {
        var series = new PingSeries();
        for (int i = 0; i < 5; i++) series.Add(null);
        var stats = series.Stats()!;
        Assert.Null(stats.AvgMs);
        Assert.Equal(100, stats.LossPercent);
        Assert.False(series.EverAnswered);
    }
}

public class NetworkNoteTests
{
    private static readonly DateTime T0 = new(2026, 10, 16, 20, 0, 0);

    private static Snapshot Snap(double seconds, NetworkData net, bool gaming = true) =>
        new(T0.AddSeconds(seconds),
            new CpuData(30, [30, 30], 4500, 5000),
            new MemoryData(12, 32, 40, 18, 55, 0),
            new DiskData(1, 0.1, 1),
            null, [],
            gaming ? new FrameStats(1, "Spiel.exe", 120, 100, 8.3, 8.3, 10, 4, 4, 8.2, 0) { AppFps = 120 } : null,
            "aktiv", true, null, null)
        { Network = net, HwInfoStatus = HwInfoStatus.Active };

    private static IReadOnlyList<Note> NotesAfter(NetworkData net, bool gaming = true)
    {
        var analyzer = new BottleneckAnalyzer();
        Verdict v = null!;
        for (double t = 0; t <= 8; t += 0.5) v = analyzer.Analyze(Snap(t, net, gaming));
        return v.Notes;
    }

    private static readonly PingStats Good = new(12, 1, 0);

    [Fact]
    public void Instabiles_WLAN_wird_dem_Router_zugeordnet()
    {
        var notes = NotesAfter(new NetworkData(new PingStats(9, 12, 5), new PingStats(30, 14, 5), true, 0.2, 0.1));
        Assert.Contains(notes, n => n.Key == "net-router");
        Assert.DoesNotContain(notes, n => n.Key == "net-internet");   // Ursache liegt schon im eigenen Netz
    }

    [Fact]
    public void Stabiler_Router_aber_schwankendes_Internet_liegt_hinter_dem_Router()
    {
        var notes = NotesAfter(new NetworkData(new PingStats(1, 0.3, 0), new PingStats(45, 25, 2), true, 0.2, 0.1));
        Assert.Contains(notes, n => n.Key == "net-internet" && n.Text.Contains("hinter dem Router"));
    }

    [Fact]
    public void Router_der_nie_antwortet_loest_keinen_Hinweis_aus()
    {
        // Manche Router blockieren Ping grundsätzlich – 100 % "Verlust" dorthin sagt nichts aus
        var notes = NotesAfter(new NetworkData(new PingStats(null, null, 100), Good, false, 0.2, 0.1));
        Assert.DoesNotContain(notes, n => n.Key.StartsWith("net-"));
    }

    [Fact]
    public void Grosser_Download_beim_Spielen_wird_mit_Verursacher_gemeldet()
    {
        var net = new NetworkData(Good, Good, true, 25, 0.3) { TopTransfer = new ProcessLoad(42, "steam", 0, 24) };
        var note = Assert.Single(NotesAfter(net), n => n.Key == "net-transfer");
        Assert.Contains("vermutlich steam", note.Text);
    }

    [Fact]
    public void Download_ohne_Spiel_ist_kein_Hinweis()
    {
        var net = new NetworkData(Good, Good, true, 25, 0.3);
        Assert.DoesNotContain(NotesAfter(net, gaming: false), n => n.Key == "net-transfer");
    }

    [Fact]
    public void Gute_Verbindung_ergibt_keine_Hinweise()
    {
        var notes = NotesAfter(new NetworkData(new PingStats(1, 0.3, 0), Good, true, 0.2, 0.1));
        Assert.DoesNotContain(notes, n => n.Key.StartsWith("net-"));
    }
}
