namespace PerfOverlay.Analysis;

/// <summary>Eine Meldung, wie sie im Overlay angezeigt wird.</summary>
public sealed record LoggedNote(string Text, Severity Severity, bool Active, TimeSpan SinceLast, int Count);

/// <summary>
/// Hält Meldungen fest, damit kurze Ereignisse (z. B. ein Nachlade-Spike) nicht nach 0,5 s wieder verschwinden.
/// Aktive Meldungen stehen oben ("jetzt"), vergangene bleiben gedimmt mit "vor m:ss" stehen,
/// bis sie älter als <see cref="Retention"/> sind oder von neueren verdrängt werden.
/// </summary>
public sealed class NoteLog
{
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

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
            e.LastSeen = now;
        }

        foreach (var (key, e) in _entries.ToList())
        {
            if (!activeKeys.Contains(key)) e.Active = false;
            if (!e.Active && now - e.LastSeen > Retention) _entries.Remove(key);
        }

        return _entries.Values
            .OrderByDescending(e => e.Active)
            .ThenByDescending(e => e.Active ? (int)e.Severity : 0)
            .ThenByDescending(e => e.LastSeen)
            .Take(maxShown)
            .Select(e => new LoggedNote(e.Text, e.Severity, e.Active, now - e.LastSeen, e.Count))
            .ToList();
    }

    private sealed class Entry
    {
        public string Text = "";
        public Severity Severity;
        public DateTime LastSeen;
        public int Count;
        public bool Active;
    }
}
