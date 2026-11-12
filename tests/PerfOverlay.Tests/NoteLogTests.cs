using PerfOverlay.Analysis;

namespace PerfOverlay.Tests;

/// <summary>Dauerhinweise nur zeitweise anzeigen (Plan Probelauf Windrose, W3).</summary>
public class NoteLogTests
{
    private static readonly DateTime T0 = new(2026, 11, 12, 16, 0, 0);
    private static readonly Note Monitor = new("monitor-hz", "Monitor läuft mit 120 statt 144 Hz", Severity.Info);
    private static Note Dead(int n) => new("net-closewait", $"Spiel hält {n} tote Verbindungen offen (CLOSE_WAIT)", Severity.Warn);

    /// <summary>Spielt jede Sekunde die Meldungen ein und zählt, wie lange der Text sichtbar war.</summary>
    private static int VisibleSeconds(Func<int, Note[]> at, int seconds, string textPart)
    {
        var log = new NoteLog();
        int visible = 0;
        for (int i = 0; i < seconds; i++)
            if (log.Update(at(i), T0.AddSeconds(i), int.MaxValue).Any(n => n.Text.Contains(textPart))) visible++;
        return visible;
    }

    [Fact]
    public void Monitor_Hinweis_erscheint_in_56_Minuten_hoechstens_7_mal_30_Sekunden()
    {
        int visible = VisibleSeconds(_ => [Monitor], 56 * 60, "Monitor");
        Assert.InRange(visible, 30, 7 * 30);
        Assert.Equal(6 * 30, visible);   // Minute 0, 10, 20, 30, 40, 50
    }

    [Fact]
    public void Dauerhinweis_ist_beim_ersten_Auftreten_sofort_zu_sehen()
    {
        var log = new NoteLog();
        Assert.Single(log.Update([Monitor], T0, 10));
        Assert.Single(log.Update([Monitor], T0.AddSeconds(29), 10));
        Assert.Empty(log.Update([Monitor], T0.AddSeconds(30), 10));
    }

    [Fact]
    public void Andere_Anzahl_toter_Verbindungen_zeigt_den_Hinweis_erneut()
    {
        var log = new NoteLog();
        for (int i = 0; i <= 200; i++) log.Update([Dead(3)], T0.AddSeconds(i), 10);
        Assert.Empty(log.Update([Dead(3)], T0.AddSeconds(201), 10));
        Assert.Single(log.Update([Dead(5)], T0.AddSeconds(202), 10));
    }

    [Fact]
    public void Schwankende_Anzahl_blendet_hoechstens_alle_2_Minuten_ein()
    {
        // 3 ↔ 4 im Sekundentakt: ohne Grenze stünde der Hinweis dauerhaft da
        int visible = VisibleSeconds(i => [Dead(3 + i % 2)], 10 * 60, "tote Verbindungen");
        Assert.InRange(visible, 30, 5 * 30);
    }

    [Fact]
    public void Schwankende_Messwerte_zaehlen_nicht_als_Aenderung()
    {
        int visible = VisibleSeconds(i => [new Note("commit", $"Speicher fast voll ({90 + i % 5} %)", Severity.Warn)], 9 * 60, "Speicher");
        Assert.Equal(30, visible);
    }

    [Fact]
    public void Kurz_unterbrochener_Dauerhinweis_belegt_keine_Zeile()
    {
        // tote Verbindungen fallen nach 5 min kurz unter die Schwelle – kein "vor 0:01"
        var log = new NoteLog();
        for (int i = 0; i < 300; i++) log.Update([Dead(3)], T0.AddSeconds(i), 10);
        Assert.Empty(log.Update([], T0.AddSeconds(300), 10));
    }

    [Fact]
    public void Ereignis_Meldungen_bleiben_unveraendert()
    {
        int visible = VisibleSeconds(_ => [new Note("stutter", "Ruckler: 2× in 10 s", Severity.Warn)], 20 * 60, "Ruckler");
        Assert.Equal(20 * 60, visible);
    }

    [Fact]
    public void Alle_Dauerhinweise_sind_bekannte_Kennungen()
    {
        foreach (var key in NoteLog.Recurring) Assert.True(NoteCategories.All.ContainsKey(key), key);
    }
}
