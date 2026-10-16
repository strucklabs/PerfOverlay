using PerfOverlay.Sensors;

namespace PerfOverlay.Tests;

/// <summary>
/// Ruckler- und Pacing-Erkennung an typischen Frametime-Verläufen. Anlass: Eine echte 20-Minuten-Sitzung
/// in Warframe zählte über 1000 "Ruckler", gut die Hälfte davon in Ladebildschirmen mit gleichmäßig ~20 FPS.
/// </summary>
public class FrameMetricsTests
{
    private static List<double> Steady(double ms, int count) => Enumerable.Repeat(ms, count).ToList();

    [Fact]
    public void Ladebildschirm_mit_gleichmaessig_niedrigen_FPS_ist_kein_Ruckler()
    {
        var frames = Steady(50, 60);   // 20 FPS, aber gleichmäßig
        Assert.Empty(FrameMetrics.FindSpikes(frames));
    }

    [Fact]
    public void Wechsel_von_120_FPS_ins_Ladebild_erzeugt_keine_Ruckler_Serie()
    {
        var frames = Steady(8.3, 120).Concat(Steady(50, 60)).ToList();
        Assert.Empty(FrameMetrics.FindSpikes(frames));
    }

    [Fact]
    public void Einzelner_Haenger_im_Spiel_wird_erkannt()
    {
        var frames = Steady(8.3, 100);
        frames[50] = 60;
        Assert.Equal([50], FrameMetrics.FindSpikes(frames));
    }

    [Fact]
    public void Doppelt_angezeigtes_Bild_bei_60_FPS_wird_erkannt()
    {
        var frames = Steady(16.67, 60);
        frames[30] = 33.3;   // genau das Doppelte – mit Faktor 2,0 würde es knapp verfehlt
        Assert.Equal([30], FrameMetrics.FindSpikes(frames));
    }

    [Fact]
    public void Kleiner_Ausreisser_bei_hohen_FPS_bleibt_unter_der_Wahrnehmungsschwelle()
    {
        var frames = Steady(6.9, 100);   // 144 FPS
        frames[50] = 14;                 // doppelt so lang, aber absolut unter 20 ms
        Assert.Empty(FrameMetrics.FindSpikes(frames));
    }

    [Fact]
    public void Zwei_Haenger_direkt_nacheinander_werden_beide_erkannt()
    {
        var frames = Steady(8.3, 100);
        frames[50] = 45;
        frames[51] = 45;
        Assert.Equal([50, 51], FrameMetrics.FindSpikes(frames));
    }

    [Fact]
    public void Neueste_Frames_werden_erst_mit_Nachfolgern_bewertet()
    {
        var frames = Steady(8.3, 50);
        frames[^1] = 60;   // ganz am Ende: Vergleich nach vorn fehlt noch
        Assert.Empty(FrameMetrics.FindSpikes(frames));

        frames.AddRange(Steady(8.3, FrameMetrics.MinFollowing));   // ein Takt später sind Nachfolger da
        Assert.Equal([49], FrameMetrics.FindSpikes(frames));
    }

    [Fact]
    public void Gleichmaessige_Ausgabe_hat_ruhiges_Pacing()
    {
        var frames = Enumerable.Range(0, 200).Select(i => 8.3 + (i % 2 == 0 ? 0.2 : -0.2)).ToList();
        double pacing = FrameMetrics.Pacing(frames, new HashSet<int>());
        Assert.Equal("ruhig", FrameMetrics.PacingRating(pacing, 8.3));
    }

    [Fact]
    public void Wechselnde_Frametimes_haben_unruhiges_Pacing_ohne_Ruckler()
    {
        // 8 / 18 ms im Wechsel: kein Einzelframe fällt auf, die Ausgabe wirkt trotzdem unruhig
        var frames = Enumerable.Range(0, 200).Select(i => i % 2 == 0 ? 8.0 : 18.0).ToList();
        Assert.Empty(FrameMetrics.FindSpikes(frames));
        double pacing = FrameMetrics.Pacing(frames, new HashSet<int>());
        Assert.Equal(10, pacing, 3);
        Assert.Equal("unruhig", FrameMetrics.PacingRating(pacing, 13));
    }

    [Fact]
    public void Ruckler_verzerren_das_Pacing_nicht()
    {
        var frames = Steady(8.3, 100);
        frames[50] = 80;
        var spikes = FrameMetrics.FindSpikes(frames).ToHashSet();
        Assert.Equal(0, FrameMetrics.Pacing(frames, spikes), 3);
    }
}
