namespace PerfOverlay.Analysis;

/// <summary>
/// Engpass: Meldungen, die erklären, warum die Bildrate begrenzt ist oder einbricht (Drosselung, Ruckler, Speicher) –
/// sie stehen in allen Ansichten oben. Hinweis: alles andere (Konfiguration, Gesundheit, Verbindung) – nur in der
/// Analyse-Ansicht unter "Details".
/// </summary>
public enum NoteCategory { Bottleneck, Hint }

public static class NoteCategories
{
    /// <summary>
    /// Alle Kennungen, die der BottleneckAnalyzer vergibt. Ein Test prüft, dass keine fehlt – eine neue Meldung
    /// muss hier bewusst eingeordnet werden.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, NoteCategory> All = new Dictionary<string, NoteCategory>
    {
        // Drosselung und Leistungsgrenzen
        ["cpu-throttle"] = NoteCategory.Bottleneck,
        ["cpu-hot"] = NoteCategory.Bottleneck,
        ["cpu-perflimit"] = NoteCategory.Bottleneck,
        ["gpu-thermal"] = NoteCategory.Bottleneck,
        ["gpu-hwslowdown"] = NoteCategory.Bottleneck,
        ["gpu-powerbrake"] = NoteCategory.Bottleneck,
        ["gpu-powercap"] = NoteCategory.Bottleneck,
        ["pstate-load-low"] = NoteCategory.Bottleneck,
        // Bildausgabe und Speicher
        ["stutter"] = NoteCategory.Bottleneck,
        ["pacing"] = NoteCategory.Bottleneck,
        ["vram"] = NoteCategory.Bottleneck,
        ["ram"] = NoteCategory.Bottleneck,
        ["gpu-foreign"] = NoteCategory.Bottleneck,
        ["timer"] = NoteCategory.Bottleneck,

        // Hinweise
        ["cpu-foreign"] = NoteCategory.Hint,
        ["framegen"] = NoteCategory.Hint,
        ["presentmode"] = NoteCategory.Hint,
        ["pagein"] = NoteCategory.Hint,
        ["disk"] = NoteCategory.Hint,
        ["ram-channel"] = NoteCategory.Hint,
        ["ram-xmp"] = NoteCategory.Hint,
        ["pcie-gen"] = NoteCategory.Hint,
        ["pcie-width"] = NoteCategory.Hint,
        ["pstate-idle-high"] = NoteCategory.Hint,
        ["gpu-memtemp"] = NoteCategory.Hint,
        ["gpu-hotspot"] = NoteCategory.Hint,
        ["amd-prd"] = NoteCategory.Hint,
        ["hwinfo"] = NoteCategory.Hint,
        ["net-router"] = NoteCategory.Hint,
        ["net-internet"] = NoteCategory.Hint,
        ["net-transfer"] = NoteCategory.Hint,
        // Tote Verbindungen beweisen allein keinen FPS-Verlust – den Zusammenhang mit Rucklern zeigt die Auswertung
        ["net-closewait"] = NoteCategory.Hint,
        // Systemeinstellungen
        ["monitor-hz"] = NoteCategory.Hint,
        ["power-battery"] = NoteCategory.Hint,
        ["power-saving"] = NoteCategory.Hint,
        ["rebar"] = NoteCategory.Hint,
        // Block B: Treiber, Speicherzusage, Bildwiederholrate, Router
        ["dpc"] = NoteCategory.Hint,
        ["commit"] = NoteCategory.Hint,
        ["sync-above"] = NoteCategory.Hint,
        ["sync-halving"] = NoteCategory.Hint,
        ["net-bufferbloat"] = NoteCategory.Hint,
    };

    /// <summary>Unbekannte Kennungen gelten als Hinweis – lieber zu wenig oben als zu viel.</summary>
    public static NoteCategory Of(string key) => All.TryGetValue(key, out var c) ? c : NoteCategory.Hint;
}
