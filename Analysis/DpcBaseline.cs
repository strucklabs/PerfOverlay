namespace PerfOverlay.Analysis;

/// <summary>
/// Übliche DPC-/Interrupt-Last der letzten Minuten (Plan Probelauf Windrose, W2). Manche Systeme liegen im Spiel
/// dauerhaft bei 12–16 % auf einem Kern, ohne dass das ruckelt. Auffällig ist erst, was deutlich darüber liegt:
/// ab max(25 %, Grundwert + 10 Punkte). Solange noch keine 5 Minuten Daten vorliegen, gilt die feste Schwelle 25 %.
/// </summary>
public sealed class DpcBaseline
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    /// <summary>Untergrenze und Schwelle ohne Grundwert.</summary>
    public const double FixedPercent = 25;
    /// <summary>So viele Prozentpunkte über dem Grundwert gilt die Last als erhöht.</summary>
    public const double AbovePercent = 10;
    /// <summary>Liegt schon der Grundwert so hoch, ist das selbst ein Befund.</summary>
    public const double HighBaselinePercent = 35;

    private readonly Queue<(DateTime Time, double Dpc)> _samples = new();
    private DateTime? _first;

    public void Add(DateTime time, double dpc)
    {
        _first ??= time;
        _samples.Enqueue((time, dpc));
        while (_samples.Count > 0 && time - _samples.Peek().Time > Window) _samples.Dequeue();
    }

    /// <summary>Median der letzten 5 Minuten; null, solange noch keine 5 Minuten gemessen sind.</summary>
    public double? Baseline(DateTime now)
    {
        if (_first is not DateTime first || now - first < Window || _samples.Count == 0) return null;
        var sorted = _samples.Select(s => s.Dpc).Order().ToArray();
        int n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
    }

    /// <summary>Ab diesem Wert gilt die DPC-Last als erhöht.</summary>
    public static double Threshold(double? baseline) =>
        baseline is double b ? Math.Max(FixedPercent, b + AbovePercent) : FixedPercent;
}
