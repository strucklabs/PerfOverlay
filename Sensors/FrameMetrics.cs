namespace PerfOverlay.Sensors;

/// <summary>
/// Kennzahlen auf Frame-Ebene. Reine Funktionen ohne Zustand – damit testbar.
///
/// Ruckler: Das Auge nimmt kein FPS-Mittel wahr, sondern wie lange ein einzelnes Bild stehen bleibt.
/// Ein Frame gilt als Ruckler, wenn er deutlich länger ist als seine direkten Nachbarn (nicht als ein
/// Mittel über Sekunden): So zählen Ladebildschirme oder Zwischensequenzen mit gleichmäßig niedrigen FPS
/// nicht mit, ein einzelner Hänger mitten im Spiel aber schon.
///
/// Frame-Pacing: durchschnittlicher Sprung der Frametime von Bild zu Bild. Gleichmäßige Ausgabe liegt
/// nahe 0; ständiger Wechsel (z. B. 8 / 25 / 8 / 25 ms) wirkt unruhig, ohne dass ein einzelner Ruckler auffällt.
/// </summary>
public static class FrameMetrics
{
    /// <summary>Nachbarn je Seite, mit denen ein Frame verglichen wird.</summary>
    public const int Neighborhood = 10;

    /// <summary>
    /// Ruckler: mindestens so viel länger als der Median der Nachbarn … (1,8 statt 2,0: ein doppelt angezeigtes
    /// Bild bei 60 FPS dauert 33,3 ms – genau das Doppelte von 16,7 ms – und soll sicher erkannt werden)
    /// </summary>
    public const double SpikeFactor = 1.8;

    /// <summary>… und absolut mindestens so lang (darunter kaum wahrnehmbar, z. B. 12 statt 7 ms bei 144 FPS).</summary>
    public const double SpikeMinMs = 20;

    /// <summary>Ein Frame wird erst bewertet, wenn so viele Nachfolger da sind – sonst fehlt der Vergleich nach vorn.</summary>
    public const int MinFollowing = 3;

    /// <summary>Indizes der Ruckler-Frames in einer chronologisch sortierten Liste von Frametimes (ms).</summary>
    public static List<int> FindSpikes(IReadOnlyList<double> ms)
    {
        var spikes = new List<int>();
        var neighbors = new List<double>(2 * Neighborhood);
        for (int i = 0; i < ms.Count; i++)
        {
            if (ms.Count - 1 - i < MinFollowing) break;
            if (ms[i] < SpikeMinMs) continue;

            neighbors.Clear();
            int from = Math.Max(0, i - Neighborhood), to = Math.Min(ms.Count - 1, i + Neighborhood);
            for (int j = from; j <= to; j++)
                if (j != i) neighbors.Add(ms[j]);
            if (neighbors.Count < 6) continue;

            neighbors.Sort();
            double local = neighbors[neighbors.Count / 2];   // Median: robust gegen einen zweiten Ausreißer daneben
            if (ms[i] >= local * SpikeFactor) spikes.Add(i);
        }
        return spikes;
    }

    /// <summary>
    /// Ø Sprung der Frametime von Bild zu Bild in ms. Ruckler und der Übergang zu ihnen bleiben außen vor –
    /// sie werden separat gezählt und würden das Pacing sonst verzerren.
    /// </summary>
    public static double Pacing(IReadOnlyList<double> ms, IReadOnlySet<int> spikes)
    {
        double sum = 0;
        int n = 0;
        for (int i = 1; i < ms.Count; i++)
        {
            if (spikes.Contains(i) || spikes.Contains(i - 1)) continue;
            sum += Math.Abs(ms[i] - ms[i - 1]);
            n++;
        }
        return n > 0 ? sum / n : double.NaN;
    }

    /// <summary>Unter dieser Schwankung von Bild zu Bild ist die Ausgabe immer ruhig – das nimmt niemand wahr.</summary>
    public const double PacingNoticeableMs = 1.0;

    /// <summary>
    /// Einstufung des Pacings relativ zur Frametime: unter 10 % ruhig, ab 25 % unruhig. Schwankungen unter 1 ms
    /// gelten immer als ruhig – sonst wären Menüs und Ladebildschirme mit mehreren hundert FPS "unruhig".
    /// </summary>
    public static string PacingRating(double pacingMs, double frametimeMs)
    {
        if (double.IsNaN(pacingMs) || frametimeMs <= 0) return "";
        if (pacingMs < PacingNoticeableMs) return "ruhig";
        double ratio = pacingMs / frametimeMs;
        return ratio < 0.10 ? "ruhig" : ratio < 0.25 ? "leicht unruhig" : "unruhig";
    }
}
