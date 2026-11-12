namespace PerfOverlay.Analysis;

/// <summary>Eine Meldung, wie sie im Overlay angezeigt wird.</summary>
public sealed record LoggedNote(string Text, Severity Severity, bool Active, TimeSpan SinceLast, int Count)
{
    public NoteCategory Category { get; init; } = NoteCategory.Hint;
}

/// <summary>
/// Hält Meldungen fest, damit kurze Ereignisse (z. B. ein Nachlade-Spike) nicht nach 0,5 s wieder verschwinden.
/// Aktive Meldungen stehen oben ("jetzt"), vergangene bleiben gedimmt mit "vor m:ss" stehen,
/// bis sie älter als <see cref="Retention"/> sind oder von neueren verdrängt werden.
/// <para>
/// Dauerhinweise (Einstellungen und Zustände, die sich während des Spielens nicht ändern) belegen sonst dauerhaft eine
/// Zeile. Sie erscheinen beim ersten Auftreten <see cref="RecurringShow"/> lang, bei einer Änderung des Inhalts erneut
/// und sonst alle <see cref="RecurringEvery"/>. Das betrifft nur die Anzeige – die Aufzeichnung bekommt alle Meldungen.
/// </para>
/// </summary>
public sealed class NoteLog
{
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RecurringShow = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RecurringEvery = TimeSpan.FromMinutes(10);
    /// <summary>Schwankt der Inhalt (z. B. 3 ↔ 4 Verbindungen), erscheint der Hinweis höchstens so oft erneut.</summary>
    public static readonly TimeSpan RecurringChangeGap = TimeSpan.FromMinutes(2);

    /// <summary>Kennungen der Dauerhinweise (Plan Probelauf Windrose, W3).</summary>
    public static readonly IReadOnlySet<string> Recurring = new HashSet<string>
    {
        "monitor-hz", "rebar", "power-saving", "power-battery", "net-closewait", "commit", "game-memleak", "timer",
        "pcie-gen", "pcie-width", "ram-xmp", "ram-channel",
    };

    private readonly Dictionary<string, Entry> _entries = new();

    public IReadOnlyList<LoggedNote> Update(IReadOnlyList<Note> current, DateTime now, int maxShown)
    {
        var activeKeys = new HashSet<string>();
        foreach (var note in current)
        {
            activeKeys.Add(note.Key);
            if (!_entries.TryGetValue(note.Key, out var e))
                _entries[note.Key] = e = new Entry();
            if (!e.Active) e.Count++;          // neues Auftreten (vorher inaktiv oder neu)
            e.Active = true;
            e.Text = note.Text;                 // während aktiv: aktuellen Wert zeigen, danach eingefroren
            e.Severity = note.Severity;
            e.Category = note.Category;
            e.LastSeen = now;
            if (Recurring.Contains(note.Key)) Schedule(e, Pattern(note.Key, note.Text), now);
        }

        foreach (var (key, e) in _entries.ToList())
        {
            if (!activeKeys.Contains(key)) e.Active = false;
            if (!e.Active && now - e.LastSeen > Retention) _entries.Remove(key);
        }

        return _entries.Values
            .Where(e => !e.Hidden)
            .OrderByDescending(e => e.Active)
            .ThenByDescending(e => e.Active ? (int)e.Severity : 0)
            .ThenByDescending(e => e.LastSeen)
            .Take(maxShown)
            .Select(e => new LoggedNote(e.Text, e.Severity, e.Active, now - e.LastSeen, e.Count) { Category = e.Category })
            .ToList();
    }

    /// <summary>
    /// Entscheidet, ob ein Dauerhinweis gerade sichtbar ist. Ist er beim Verschwinden ausgeblendet, bleibt er das –
    /// ein kurz unterbrochener Dauerhinweis soll nicht als "vor 0:05" eine Zeile belegen.
    /// </summary>
    private static void Schedule(Entry e, string pattern, DateTime now)
    {
        bool start = e.ShownSince is not DateTime since
            || now - since >= RecurringEvery
            || (pattern != e.Pattern && now - since >= RecurringChangeGap);
        if (start) e.ShownSince = now;
        bool visible = now - e.ShownSince!.Value < RecurringShow;
        if (start || visible) e.Pattern = pattern;   // Änderungen während der Einblendung sind schon zu sehen
        e.Hidden = !visible;
    }

    /// <summary>
    /// Was als "anderer Inhalt" zählt: bei toten Verbindungen die Anzahl, sonst nur der Wortlaut – schwankende
    /// Messwerte (Prozent, MB/min) sollen den Hinweis nicht jede Sekunde neu einblenden.
    /// </summary>
    private static string Pattern(string key, string text) =>
        key == "net-closewait" ? text : System.Text.RegularExpressions.Regex.Replace(text, @"[\d.,]+", "#");

    private sealed class Entry
    {
        public string Text = "";
        public Severity Severity;
        public NoteCategory Category;
        public DateTime LastSeen;
        public int Count;
        public bool Active;
        public DateTime? ShownSince;
        public string? Pattern;
        public bool Hidden;
    }
}
