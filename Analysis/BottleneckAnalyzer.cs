using PerfOverlay.Sensors;
using Throttle = PerfOverlay.Native.Nvml.ThrottleReasons;

namespace PerfOverlay.Analysis;

public enum Severity { Good, Info, Warn, Bad }

/// <summary>Grundkategorie des Engpasses – Basis für die Anteile über die letzten 30 Sekunden.</summary>
public enum BottleneckKind { None, Gpu, Cpu, Limit, Mixed }

/// <param name="Key">Stabile Kennung (unabhängig von Messwerten im Text), damit das NoteLog Wiederholungen erkennt.</param>
public sealed record Note(string Key, string Text, Severity Severity)
{
    public NoteCategory Category => NoteCategories.Of(Key);
}

public sealed record Verdict(string Title, string Detail, Severity Severity, IReadOnlyList<Note> Notes)
{
    public BottleneckKind Kind { get; init; }

    /// <summary>Konkreter Tipp passend zum Engpass.</summary>
    public string? Tip { get; init; }

    /// <summary>Anteile der Kategorien in den letzten 30 Sekunden mit Frame-Daten (Summe 1), leer ohne Spiel.</summary>
    public IReadOnlyDictionary<BottleneckKind, double> Shares { get; init; } = new Dictionary<BottleneckKind, double>();
}

/// <summary>
/// Bestimmt den aktuellen Engpass.
///
/// Primär (mit PresentMon), bezogen auf die Frametime der vom Spiel gerenderten Frames:
///   GPU arbeitet ≥ 90 %                                  → GPU-limitiert (der Normalfall beim Gaming)
///   CPU wartet ≥ 25 % (auf Begrenzer/VSync)              → FPS-Limit bzw. VSync
///   GPU ≥ 15 % untätig oder CPU-Busy ≥ 85 %, CPU wartet nicht → CPU-limitiert
///   sonst                                                → gemischt
/// "GPU wartet" ist dabei das zuverlässigere CPU-Signal: CPU-Busy enthält bei manchen Engines auch
/// das Warten auf die GPU (beobachtet in Warframe: CPU 8,6 ms bei 8,9 ms Frametime, obwohl GPU-limitiert).
///
/// Fallback (ohne Frame-Daten): GPU-Auslastung und Last des stärksten P-Kerns – unsicher wegen Thread-Hopping.
/// </summary>
public sealed class BottleneckAnalyzer
{
    private static readonly TimeSpan ShareWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromSeconds(15);

    private Verdict? _shown;
    private string? _candidate;
    private int _candidateCount;

    private readonly Queue<(DateTime Time, BottleneckKind Kind)> _kinds = new();
    private readonly Queue<Sample> _history = new();
    private readonly Dictionary<string, DateTime> _conditionSince = new();
    private string? _cpuSensorState;
    private DateTime _hwInfoStatusSince;

    /// <summary>Kennzahlen je Messtakt – um Ruckler im Nachhinein einer Ursache zuzuordnen.</summary>
    private readonly record struct Sample(DateTime Time, double PagesIn, double DiskActive, double CpuTotal, double MaxPerfCore, bool VramSpill,
        bool DpcHigh, double CommitPercent);

    private readonly BufferbloatTracker _bloat = new();

    /// <summary>
    /// Übliche DPC-/Interrupt-Last der letzten 5 Minuten. Ein Ruckler gilt als von Treibern verursacht, wenn die
    /// Last im selben Moment deutlich darüber liegt (≥ max(25 %, Grundwert + 10)); 5 s am Stück ergeben einen
    /// Hinweis. Windows misst in Schritten eines Zeittakts (≈ 1,6 % je Sekunde), einzelne Sekunden mit 12–16 %
    /// sind auf manchen Systemen normal.
    /// </summary>
    private readonly DpcBaseline _dpc = new();

    /// <summary>
    /// Sitzungen werden aufgezeichnet (Tray-Menü). Ohne FPS-Messung beginnt keine Sitzung – dann sagt der Tipp das,
    /// damit niemand nach einer Aufzeichnung sucht, die es nicht gibt.
    /// </summary>
    public bool RecordingEnabled { get; set; } = true;

    public Verdict Analyze(Snapshot s)
    {
        var raw = Evaluate(s);

        // Anteile über 30 s aus den ungeglätteten Ergebnissen, nur solange ein Spiel Frames liefert
        if (raw.Kind != BottleneckKind.None) _kinds.Enqueue((s.Time, raw.Kind));
        while (_kinds.Count > 0 && s.Time - _kinds.Peek().Time > ShareWindow) _kinds.Dequeue();
        var shares = _kinds.Count == 0
            ? new Dictionary<BottleneckKind, double>()
            : _kinds.GroupBy(k => k.Kind).ToDictionary(g => g.Key, g => (double)g.Count() / _kinds.Count);
        raw = raw with { Shares = shares };

        // Hysterese: Der Titel wechselt erst, wenn das neue Ergebnis zweimal in Folge auftritt.
        if (_shown == null || raw.Title == _shown.Title)
        {
            _candidate = null;
            return _shown = raw;
        }
        if (raw.Title == _candidate && ++_candidateCount >= 2)
        {
            _candidate = null;
            return _shown = raw;
        }
        if (raw.Title != _candidate)
        {
            _candidate = raw.Title;
            _candidateCount = 1;
        }
        // Alten Titel behalten, Hinweise und Anteile aber aktuell halten
        return _shown with { Notes = raw.Notes, Shares = shares };
    }

    /// <summary>True, wenn die Bedingung seit mindestens <paramref name="duration"/> ununterbrochen gilt.</summary>
    private bool Sustained(string key, bool condition, DateTime now, TimeSpan duration)
    {
        if (!condition)
        {
            _conditionSince.Remove(key);
            return false;
        }
        if (!_conditionSince.TryGetValue(key, out var since))
            _conditionSince[key] = since = now;
        return now - since >= duration;
    }

    private Verdict Evaluate(Snapshot s)
    {
        var notes = new List<Note>();
        var gpu = s.Gpu;
        double gpuLoad = gpu?.Load ?? 0;
        var f = s.Frames;

        // --- P-State: taktet die GPU passend zur Last? (P0 = Volllast, P8 = Idle) ---
        if (gpu?.PState is int ps)
        {
            if (Sustained("pstate-idle-high", ps <= 2 && gpuLoad < 10, s.Time, TimeSpan.FromSeconds(60)))
                notes.Add(new("pstate-idle-high",
                    $"GPU taktet im Leerlauf nicht herunter (P{ps}, {gpu.PowerW:0} W) – z. B. Multi-Monitor, hohe Hz oder Hintergrundprogramm",
                    Severity.Info));
            if (Sustained("pstate-load-low", ps >= 5 && gpuLoad > 50 && f != null, s.Time, TimeSpan.FromSeconds(5)))
                notes.Add(new("pstate-load-low",
                    $"GPU unter Last in Stromsparstufe P{ps} – Energieverwaltung im NVIDIA-Treiber prüfen",
                    Severity.Warn));
        }

        // --- GPU-Drosselung (nur NVML liefert Gründe) ---
        if (gpu != null && gpuLoad > 30)
        {
            if (gpu.Throttle.HasFlag(Throttle.HwThermalSlowdown) || gpu.Throttle.HasFlag(Throttle.SwThermalSlowdown))
                notes.Add(new("gpu-thermal", "GPU drosselt wegen Temperatur", Severity.Bad));
            if (gpu.Throttle.HasFlag(Throttle.HwPowerBrakeSlowdown))
                notes.Add(new("gpu-powerbrake", "GPU-Notbremse (Netzteil/Stromversorgung)", Severity.Bad));
            else if (gpu.Throttle.HasFlag(Throttle.HwSlowdown))
                notes.Add(new("gpu-hwslowdown", "GPU Hardware-Drosselung", Severity.Bad));
            if (gpu.Throttle.HasFlag(Throttle.SwPowerCap) && gpuLoad > 90)
                notes.Add(new("gpu-powercap", "GPU am Power-Limit (normal unter Volllast)", Severity.Info));

            if (gpuLoad > 60 && gpu.PcieWidth is int w && gpu.PcieMaxWidth is int mw && w < mw)
                notes.Add(new("pcie-width", $"PCIe nur x{w} statt x{mw}", Severity.Warn));
            else if (gpuLoad > 60 && gpu.PcieGen is int g && gpu.PcieMaxGen is int mg && g < mg)
                notes.Add(new("pcie-gen", $"PCIe nur Gen {g} statt Gen {mg}", Severity.Info));
        }

        // --- GPU-Temperaturen aus HWiNFO: Speicher-Junction und Hotspot ---
        if (s.GpuThermal is { } gt && gpuLoad > 60)
        {
            // GDDR6X (RTX 3080/3090, 40er) drosselt um 110 °C – oft durch gealterte Wärmeleitpads
            if (gt.MemoryJunctionC is double memJunction && memJunction >= 100)
                notes.Add(new("gpu-memtemp", $"GPU-Speicher sehr heiß ({memJunction:0} °C) – Wärmeleitpads/Gehäuselüftung prüfen",
                    memJunction >= 106 ? Severity.Bad : Severity.Warn));
            // Großer Abstand Hotspot ↔ GPU-Temperatur deutet auf ungleichmäßigen Kontakt des Kühlers
            if (gt.HotspotC is double hot && gpu?.TempC is double core && hot - core >= 20)
                notes.Add(new("gpu-hotspot", $"Hotspot {hot - core:0} °C über GPU-Temperatur – Wärmeleitpaste/Anpressdruck prüfen", Severity.Warn));
            // AMD meldet keine Drosselgründe; Radeon-Karten senken ab 110 °C Hotspot ("Junction") den Takt
            if (gpu?.Source == "ADLX" && gt.HotspotC is >= 110)
                notes.Add(new("gpu-thermal", "GPU drosselt wegen Temperatur", Severity.Bad));
        }

        // --- VRAM: voll ist erst ein Problem, wenn das Spiel in den RAM auslagert ---
        bool vramFull = gpu?.VramUsedGB is double used && gpu.VramTotalGB is double total && used / total > 0.90;
        bool vramSpill = vramFull && s.GameSharedVramGB > 1.0;
        if (vramSpill)
            notes.Add(new("vram", $"VRAM voll – Spiel lagert {s.GameSharedVramGB:0.0} GB in den RAM aus (Ruckler möglich)", Severity.Warn));
        else if (vramFull && gpu!.VramUsedGB / gpu.VramTotalGB > 0.95)
            notes.Add(new("vram", $"VRAM fast voll ({gpu.VramUsedGB:0.0}/{gpu.VramTotalGB:0.0} GB)", Severity.Info));

        // --- CPU-Drosselung: genau mit HWiNFO (fertige Gründe), mit PawnIO aus Windows-Leistungslimit und Temperatur
        //     abgeleitet, ohne CPU-Sensoren nur über das Windows-Leistungslimit ---
        bool perfLimited = Sustained("cpu-perflimit", s.Cpu.PerfLimitPercent is < 90 && s.Cpu.TotalLoad > 30, s.Time, TimeSpan.FromSeconds(3));
        if (s.CpuThermal is { } cpuThermal)
        {
            // Quellen ohne fertige Gründe (PawnIO): Grund aus Temperatur ableiten
            if (perfLimited && cpuThermal.Source != "HWiNFO")
                notes.Add(DerivedThrottleNote(s.Cpu.PerfLimitPercent!.Value, cpuThermal));

            // Stromlimits (Intel ICCmax/EDP, AMD TDC/EDC) zählen erst bei hoher Last: Das Intel-Flag
            // "Electrical Design Point/Other" ist bei Alder/Raptor Lake auch bei 14 % Last gesetzt, und
            // AMD-EDC wird bei kurzen Lastspitzen einzelner Kerne erreicht, ohne dass das Spiel bremst.
            var reasons = cpuThermal.ThrottleReasons
                .Where(r => !r.StartsWith("Stromlimit") || s.Cpu.TotalLoad >= 80)
                .ToList();
            if (reasons.Count > 0 && s.Cpu.TotalLoad > 15)
            {
                bool thermal = reasons.Any(r => r.StartsWith("Temperatur") || r.StartsWith("Spannungswandler"));
                notes.Add(new("cpu-throttle", $"CPU drosselt: {string.Join(", ", reasons)}",
                    thermal ? Severity.Bad : Severity.Warn));
            }
            // AMD "Power Reporting Deviation": nur unter Volllast aussagekräftig. Deutlich unter 100 % heißt,
            // das Mainboard meldet der CPU zu wenig Strom – sie boostet dann mit mehr Leistung als vorgesehen.
            if (cpuThermal.IsAmd && cpuThermal.PowerReportingDeviation is double prd && prd < 90 && s.Cpu.TotalLoad >= 80)
                notes.Add(new("amd-prd",
                    $"Mainboard meldet CPU-Strom zu niedrig (Power Reporting Deviation {prd:0} %)", Severity.Info));

            // Ryzen boostet bewusst bis an TjMax – dort zählen nur die Drosselungs-Flags.
            // Intel: Warnung, wenn weniger als 5 °C Abstand zur Maximaltemperatur bleiben.
            if (!cpuThermal.IsAmd && cpuThermal.TempC is double cpuTemp)
            {
                if (cpuThermal.HeadroomC is double headroom ? headroom <= 5 : cpuTemp >= 95)
                    notes.Add(new("cpu-hot",
                        cpuThermal.TjMaxC is double tj
                            ? $"CPU nahe Maximaltemperatur ({cpuTemp:0} von {tj:0} °C)"
                            : $"CPU sehr heiß ({cpuTemp:0} °C)",
                        Severity.Bad));
            }
        }
        else if (perfLimited)
        {
            notes.Add(new("cpu-perflimit",
                $"CPU begrenzt auf {s.Cpu.PerfLimitPercent:0} % (Power- oder Temperaturlimit)", Severity.Warn));
        }

        // --- CPU-Sensoren (PawnIO, sonst HWiNFO): beim Start und bei jedem Zustandswechsel kurz melden, woher die
        //     CPU-Werte kommen bzw. was fehlt. Danach hält das NoteLog die Meldung noch sichtbar, ohne eine Zeile
        //     dauerhaft zu belegen. Die GPU-Werte liest PerfOverlay selbst – dafür braucht es keinen Hinweis. ---
        var (state, key, text) = CpuSensorNote(s);
        if (state != _cpuSensorState)
        {
            _cpuSensorState = state;
            _hwInfoStatusSince = s.Time;
        }
        if (text != null && s.Time - _hwInfoStatusSince < TimeSpan.FromSeconds(15))
            notes.Add(new(key, text, Severity.Info));

        // --- RAM-Bestückung: ändert sich nicht. Das NoteLog blendet solche Dauerhinweise nur zeitweise ein. ---
        bool xmpOff = false;
        if (s.MemoryConfig is { } ram)
        {
            xmpOff = ram.RatedMTs > ram.ConfiguredMTs + 100 || (ram.RatedMTs == null && ram.RunsAtJedecBase && !ram.IsSoDimm);
            if (ram.RatedMTs is int rated && ram.ConfiguredMTs < rated - 100)
                notes.Add(new("ram-xmp", $"RAM läuft mit {ram.ConfiguredMTs} statt {rated} MT/s – XMP/EXPO im BIOS aktivieren", Severity.Warn));
            else if (ram.RatedMTs == null && ram.RunsAtJedecBase && !ram.IsSoDimm)
                notes.Add(new("ram-xmp", $"RAM läuft mit Standardtakt {ram.ConfiguredMTs} MT/s – XMP/EXPO im BIOS prüfen", Severity.Info));

            if (ram.SameChannelPair)
                notes.Add(new("ram-channel", "Alle RAM-Riegel im selben Kanal – Slots laut Handbuch nutzen (meist A2/B2)", Severity.Warn));
            else if (ram.Channels == ChannelMode.Single && ram.Modules.Count == 1 && ram.SlotCount > 1)
                notes.Add(new("ram-channel", "Nur ein RAM-Riegel: Single-Channel, halbe Speicherbandbreite", Severity.Warn));
            else if (ram.Channels == ChannelMode.Single && ram.SlotCount > 1)
                notes.Add(new("ram-channel", "RAM im Single-Channel-Betrieb – halbe Speicherbandbreite", Severity.Warn));
        }

        // --- Speicher & Datenträger ---
        // "Pages Input/sec" zählt auch Spieldaten, die per Memory-Mapping vom Laufwerk gelesen werden.
        // Ein hoher Wert allein ist daher normales Asset-Streaming; echte RAM-Knappheit erst bei hoher Belegung.
        var mem = s.Memory;
        bool heavyPaging = mem.PagesInPerSec > 500;
        bool memoryPressure = mem.LoadPercent >= 90 || (heavyPaging && mem.LoadPercent >= 80);
        if (memoryPressure)
            notes.Add(new("ram", $"RAM knapp ({mem.LoadPercent:0} % belegt, {mem.PagesInPerSec:0} Seiten/s nachgeladen)", Severity.Warn));
        else if (mem.PagesInPerSec > 2000)
            notes.Add(new("pagein", $"Starkes Nachladen vom Laufwerk ({mem.PagesInPerSec:0} Seiten/s) – meist Level-/Asset-Streaming", Severity.Info));
        if (s.Disk.ActivePercent > 80 && s.Disk.LatencyMs > 20)
            notes.Add(new("disk", $"Datenträger ausgelastet ({s.Disk.LatencyMs:0} ms Latenz)", Severity.Warn));

        // Zugesicherter Speicher: Ist er fast ausgeschöpft, kann Windows keinen Speicher mehr zusagen. Meist läuft
        // nebenbei ein großes Programm – deshalb den größten Verbraucher nennen. Wird die Auslagerungsdatei von
        // Windows verwaltet, wächst sie mit – deshalb erst nach einigen Sekunden.
        if (Sustained("commit", mem.CommitPercent >= 90, s.Time, TimeSpan.FromSeconds(5)))
            notes.Add(new("commit", CommitNote(mem.CommitPercent, s.TopMemory), Severity.Warn));

        // Treiber, die einen Kern mit DPCs/Interrupts belegen: deutlich über der üblichen Last dieses Systems,
        // oder schon die übliche Last ist sehr hoch
        bool dpcHigh = false;
        if (s.Cpu.DpcMaxPercent is double dpc)
        {
            double? dpcBase = _dpc.Baseline(s.Time);
            dpcHigh = dpc >= DpcBaseline.Threshold(dpcBase);
            _dpc.Add(s.Time, dpc);
            if (Sustained("dpc", dpcHigh, s.Time, TimeSpan.FromSeconds(5)))
                notes.Add(new("dpc", $"Treiber blockieren einen Kern ({dpc:0} % DPC)", Severity.Warn));
            else if (dpcBase >= DpcBaseline.HighBaselinePercent)
                notes.Add(new("dpc", $"Treiber belegen dauerhaft einen Kern (~{dpcBase:0} % DPC)", Severity.Warn));
        }
        else Sustained("dpc", false, s.Time, TimeSpan.Zero);

        // Verlauf für die Ruckler-Zuordnung
        _history.Enqueue(new Sample(s.Time, mem.PagesInPerSec, s.Disk.ActivePercent, s.Cpu.TotalLoad, s.Cpu.MaxPerfCoreLoad, vramSpill,
            dpcHigh, mem.CommitPercent));
        while (_history.Count > 0 && s.Time - _history.Peek().Time > HistoryWindow) _history.Dequeue();

        // --- Verbindung: bremst keine FPS, verursacht aber Lag in Online-Spielen ---
        AddNetworkNotes(s, notes);

        // --- Systemeinstellungen: Monitor, Energie, Resizable BAR ---
        if (s.System is { } sys) AddSystemNotes(sys, notes);

        // --- Prozessorgrafik statt Grafikkarte (außerhalb des Spiel-Blocks, damit die Zeitmessung ohne Spiel zurückgesetzt wird) ---
        if (Sustained("gpu-integrated", f != null && s.GameGpuMismatch != null, s.Time, TimeSpan.FromSeconds(5)))
            notes.Add(new("gpu-integrated", "Spiel nutzt die Prozessorgrafik statt der Grafikkarte", Severity.Bad));

        // --- Andere Prozesse auf der GPU ---
        if (f != null)
        {
            var other = s.GpuTopProcesses.FirstOrDefault(p => p.Pid != f.Pid && p.Load >= 15);
            if (other != null)
                notes.Add(new("gpu-foreign", $"Fremdlast auf GPU: {other.Name} {other.Load:0} %", Severity.Warn));

            if (s.Timer is { IsSlow: true } timer)
                notes.Add(new("timer", TimerNote(timer), Severity.Warn));

            if (s.GameMemoryLeak is { } leak)
                notes.Add(new("game-memleak", MemoryLeakNote(leak), Severity.Warn));

            if (s.GameConnections is { Stale: >= DeadConnectionThreshold } conn)
                notes.Add(new("net-closewait",
                    $"Spiel hält {conn.CloseWait} tote Verbindungen offen (CLOSE_WAIT) – Fehler im Spiel, kann Ruckler verursachen; "
                    + "ein Neustart des Spiels hilft vorübergehend", Severity.Warn));
        }

        if (f != null && f.AvgMs > 0 && !double.IsNaN(f.GpuBusyMs) && !double.IsNaN(f.CpuBusyMs))
            return WithSystemTip(EvaluateFrames(s, f, notes, xmpOff), s, f);

        // --- Messung läuft, aber kein Spiel im Vordergrund: kein Engpass-Urteil, nur Hinweise ---
        if (s.FrameSourceRunning)
            return new("KEIN SPIEL", s.IgnoredApp != null
                ? $"Im Vordergrund: {s.IgnoredApp} (kein Spiel, wird nicht bewertet)"
                : "Wartet auf ein Spiel im Vordergrund", Severity.Info, notes);

        // --- Fallback ohne Frame-Messung (z. B. ohne Admin-Rechte): Schätzung aus der Auslastung ---
        return WithoutRecordingTip(Estimate(s, notes, gpuLoad), s);
    }

    private static Verdict Estimate(Snapshot s, List<Note> notes, double gpuLoad)
    {
        string why = s.FrameSourceStatus;
        if (gpuLoad >= 95)
            return new("GPU-LIMITIERT", $"GPU {gpuLoad:0} % (geschätzt, {why})", Severity.Good, notes);
        // Bei Hybrid-CPUs zählen nur die P-Kerne: Ein voller E-Kern (z. B. Hintergrund-Update) bremst kein Spiel.
        string coreKind = s.Cpu.IsHybrid ? "ein P-Kern" : "ein Kern";
        if (s.Cpu.MaxPerfCoreLoad >= 90 && gpuLoad is > 20 and < 85)
            return new("CPU-LIMITIERT?", $"{coreKind} bei {s.Cpu.MaxPerfCoreLoad:0} %, GPU nur {gpuLoad:0} % (geschätzt, {why})", Severity.Warn, notes);
        if (s.Cpu.TotalLoad >= 85)
            return new("CPU AUSGELASTET", $"CPU gesamt {s.Cpu.TotalLoad:0} % ({why})", Severity.Warn, notes);
        return new("KEIN ENGPASS", $"System hat Reserven ({why})", Severity.Good, notes);
    }

    /// <summary>
    /// Ohne FPS-Messung erkennt PerfOverlay kein Spiel, also beginnt auch keine Aufzeichnung. Der Tipp steht in allen
    /// Ansichten – in den ersten Sekunden ("startet …") noch nicht, da läuft die Messung gerade an.
    /// </summary>
    private Verdict WithoutRecordingTip(Verdict v, Snapshot s)
    {
        if (!RecordingEnabled || s.FrameSourceStatus.StartsWith("startet", StringComparison.Ordinal)) return v;
        return v with
        {
            Tip = s.FrameSourceStatus.Contains("Admin", StringComparison.Ordinal)
                ? "Keine Aufzeichnung – als Admin starten"
                : $"Keine Aufzeichnung – {s.FrameSourceStatus}",
        };
    }

    /// <summary>
    /// Woher kommen die CPU-Werte, und was fehlt? Reihenfolge: PawnIO (installiert und Admin) vor HWiNFO.
    /// Solange PawnIO nutzbar ist, aber noch keine Werte geliefert hat (erste Sekunden), gibt es keinen Hinweis –
    /// sonst stünde kurz ein irreführender "HWiNFO starten" da.
    /// </summary>
    internal static (string State, string Key, string? Text) CpuSensorNote(Snapshot s)
    {
        if (s.CpuThermal?.Source == "PawnIO") return ("pawnio", "cpu-pawnio", "CPU-Werte über PawnIO");
        if (s.HwInfoStatus == HwInfoStatus.Active) return ("hwinfo", "hwinfo", null);
        if (s.PawnIo == PawnIoState.Active) return ("pawnio-start", "cpu-pawnio", null);
        return s.HwInfoStatus switch
        {
            HwInfoStatus.NoSharedMemory => ("hwinfo-sm", "hwinfo", "HWiNFO: Sensorfenster öffnen und „Shared Memory Support“ aktivieren"),
            HwInfoStatus.Stopped => ("hwinfo-stopped", "hwinfo", "HWiNFO liefert nichts mehr – Shared Memory neu aktivieren (Gratisversion: 12 h)"),
            _ when s.PawnIo == PawnIoState.NeedsAdmin => ("pawnio-admin", "hwinfo", "CPU-Temperatur: als Admin starten (PawnIO)"),
            _ => ("none", "hwinfo", "CPU-Temperatur: HWiNFO starten oder PawnIO installieren"),
        };
    }

    /// <summary>
    /// Drosselgrund ohne HWiNFO (Plan Sensoren ohne HWiNFO, S4): Windows meldet über "% Performance Limit", dass die
    /// CPU unter ihrem Nenntakt bleibt, aber nicht warum. Liegt die Temperatur höchstens 5 °C unter der
    /// Maximaltemperatur (bzw. ohne bekanntes TjMax ab 95 °C), ist es die Temperatur, sonst ein Strom- oder
    /// Leistungslimit. Der Text beginnt wie bei HWiNFO mit "CPU drosselt:", damit die Auswertung ihn mitzählt.
    /// </summary>
    internal static Note DerivedThrottleNote(double limitPercent, CpuThermalData t)
    {
        bool hot = t.HeadroomC is double headroom ? headroom <= 5 : t.TempC >= 95;
        string reason = hot ? "Temperatur" : "Strom-/Leistungslimit";
        return new("cpu-throttle", $"CPU drosselt: {reason} (begrenzt auf {limitPercent:0} %)", hot ? Severity.Bad : Severity.Warn);
    }

    /// <summary>"Spielspeicher +40 MB/min seit 35 min, voll in ~2 h" – kurz genug für eine Overlay-Zeile.</summary>
    internal static string MemoryLeakNote(MemoryLeak leak)
    {
        string text = $"Spielspeicher +{leak.RateMBPerMin:0} MB/min seit {leak.SinceMinutes:0} min";
        if (leak.HoursToFull is double h)
            text += h < 1 ? $", voll in ~{h * 60:0} min" : $", voll in ~{h:0.#} h";
        return text;
    }

    /// <summary>Meldung zur Speicherzusage – mit dem größten Verbraucher, sonst mit dem Rat zur Auslagerungsdatei.</summary>
    internal static string CommitNote(double percent, MemoryHog? top) => top != null
        ? $"Speicher fast voll ({percent:0} %) – {top.Name} belegt {top.GB:0} GB"
        : $"Speicher fast voll ({percent:0} %) – Auslagerungsdatei vergrößern?";

    /// <summary>
    /// Tipps, die Systemeinstellungen einbeziehen (Plan Engpass-Erweiterung, T1–T3 und T7). Akku und
    /// Stromsparmodus gehen allem anderen vor, denn sie bremsen unabhängig vom Engpass.
    /// </summary>
    internal static Verdict WithSystemTip(Verdict v, Snapshot s, FrameStats f)
    {
        if (s.System is not { } sys) return v;
        if (sys.Power is { OnBattery: true })
            return v with { Tip = "Netzteil anschließen – im Akkubetrieb ist die Leistung begrenzt" };
        if (sys.Power is { Saving: true })
            return v with { Tip = "Energiesparmodus ausschalten – er drosselt CPU und GPU" };

        int hz = sys.Display?.CurrentHz ?? 0;
        if (v.Kind == BottleneckKind.Limit && sys.Display is { BelowMax: true } d && f.SyncInterval >= 1
            && Math.Abs(f.Fps - d.CurrentHz) <= d.CurrentHz * 0.05)
            return v with { Tip = $"Monitor kann {d.MaxHz} Hz – in Windows umstellen" };
        if (v.Kind == BottleneckKind.Limit && hz >= 30 && f.SyncInterval == 0 && f.Fps > hz * 1.05)
            return v with { Tip = $"Mit G-Sync/FreeSync: Limit auf {hz - 3} FPS setzen" };
        if (v.Kind == BottleneckKind.Cpu && sys.Power is { Plan: PowerPlanKind.Balanced, OverlayBestPerformance: not true })
            return v with { Tip = "Mehr FPS: Energieplan „Höchstleistung“ testen" };
        return v;
    }

    /// <summary>
    /// Einstellungen, die Leistung kosten können. Alle als Hinweis: Sie erklären nicht den Engpass des Moments,
    /// lassen sich aber leicht beheben. Speicher-Integrität und HAGS sind reine Info (Selbsttest, Sitzungskopf).
    /// </summary>
    internal static void AddSystemNotes(SystemInfo sys, List<Note> notes)
    {
        if (sys.Display is { BelowMax: true } d)
            notes.Add(new("monitor-hz",
                $"Monitor auf {d.CurrentHz} statt {d.MaxHz} Hz – in Windows umstellen",
                Severity.Warn));

        if (sys.Power is { } p)
        {
            if (p.OnBattery == true)
                notes.Add(new("power-battery", "Akkubetrieb begrenzt die Leistung – Netzteil anschließen",
                    Severity.Warn));
            if (p.BatterySaver)
                notes.Add(new("power-saving", "Stromsparmodus von Windows drosselt CPU und GPU", Severity.Warn));
            else if (p.Plan == PowerPlanKind.PowerSaver)
                notes.Add(new("power-saving", $"Energieplan „{p.PlanName}“ drosselt CPU und GPU – „Ausbalanciert“ wählen", Severity.Warn));
        }

        if (sys.ReBar == false)
            notes.Add(new("rebar", "Resizable BAR aus – im BIOS aktivieren",
                Severity.Info));
    }

    /// <summary>
    /// Ab so vielen Verbindungen, die länger als 30 s in CLOSE_WAIT hängen, gilt es als Fehler im Spiel.
    /// Bewusst niedrig: Wartet das Spiel schon auf wenige tote Verbindungen, kann es bereits ruckeln.
    /// </summary>
    public const int DeadConnectionThreshold = 3;

    /// <summary>Meldung zum langsamen Zeitgeber – mit Ursache, sofern sie (mit Admin-Rechten) bekannt ist.</summary>
    internal static string TimerNote(TimerInfo t)
    {
        string cost = $"Langsamer Zeitgeber {t.Name} aktiv ({t.NsPerCall:0} ns statt etwa 30 ns pro Zeitabfrage) – kostet FPS";
        return t.UsePlatformClock switch
        {
            true => cost + "; Ursache: Startoption useplatformclock – als Admin „bcdedit /deletevalue useplatformclock“ und neu starten",
            false => cost + "; Windows hält den TSC für ungeeignet – BIOS- und Windows-Updates prüfen",
            _ => cost + "; Ursache mit Admin-Rechten genauer bestimmbar",
        };
    }

    private Verdict EvaluateFrames(Snapshot s, FrameStats f, List<Note> notes, bool xmpOff)
    {
        // --- Frame Generation: Analyse beruht nur auf den vom Spiel gerenderten Frames ---
        if (f.GeneratedShare > 0.3)
            notes.Add(new("framegen", $"Frame Generation aktiv – {f.Fps:0} FPS angezeigt, {f.AppFps:0} FPS vom Spiel gerendert", Severity.Info));

        // --- Darstellungsmodus: Kopie über den Desktop-Compositor kostet Latenz ---
        if (f.PresentMode.Contains("Copy", StringComparison.OrdinalIgnoreCase))
            notes.Add(new("presentmode",
                $"Darstellung per Kopie ({f.PresentMode}) – höhere Latenz; Vollbild oder randlos mit Flip-Modell nutzen", Severity.Warn));

        // --- Frame-Pacing: gleichmäßige Bildausgabe? (Ladephasen mit sehr niedrigen FPS ausgenommen) ---
        bool unevenPacing = FrameMetrics.PacingRating(f.PacingMs, f.MedianMs) == "unruhig" && f.Fps >= 40;
        if (Sustained("pacing", unevenPacing, s.Time, TimeSpan.FromSeconds(3)))
            notes.Add(new("pacing",
                $"Unruhiges Frame-Pacing (±{f.PacingMs:0.0} ms von Bild zu Bild) – FPS-Limit, VSync und G-Sync/FreeSync prüfen",
                Severity.Info));

        // --- G-Sync/FreeSync/VSync: passt die Bildrate zur Bildwiederholrate? ---
        int hz = s.System?.Display?.CurrentHz ?? 0;
        var sync = SyncProblemOf(f, hz);
        if (Sustained("sync-above", sync == SyncProblem.AboveRefresh, s.Time, TimeSpan.FromSeconds(5)))
            notes.Add(new("sync-above", $"FPS über {hz} Hz – G-Sync/FreeSync: Limit {hz - 3} FPS", Severity.Info));
        if (Sustained("sync-halving", sync == SyncProblem.Halving, s.Time, TimeSpan.FromSeconds(3)))
            notes.Add(new("sync-halving", $"VSync springt {hz} ↔ {hz / 2} FPS – G-Sync/FreeSync nutzen", Severity.Warn));

        // --- Ruckler mit Ursache: was passierte im selben Moment? ---
        var spikes = f.SpikeAges.Where(age => age <= 10).ToList();
        if (spikes.Count > 0)
        {
            var causes = spikes
                .Select(age => StutterCause(s.Time - TimeSpan.FromSeconds(age)))
                .GroupBy(c => c)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count()}× {g.Key}");
            notes.Add(new("stutter", $"Ruckler: {spikes.Count}× in 10 s – {string.Join(", ", causes)}", Severity.Warn));
        }

        double gpuShare = f.GpuBusyMs / f.AvgMs;
        double cpuShare = f.CpuBusyMs / f.AvgMs;
        double cpuWaitShare = double.IsNaN(f.CpuWaitMs) ? 0 : f.CpuWaitMs / f.AvgMs;
        double gpuIdleShare = double.IsNaN(f.GpuWaitMs) ? 0 : f.GpuWaitMs / f.AvgMs;
        string shares = $"Frame {f.AvgMs:0.0} ms · CPU {f.CpuBusyMs:0.0} ms · GPU {f.GpuBusyMs:0.0} ms";
        string thread = s.MainThread is { Percent: >= 50 } mt ? $" · Hauptthread {mt.Percent:0} %" : "";

        if (gpuShare >= 0.90)
            return new("GPU-LIMITIERT", $"{shares} – Grafikkarte voll ausgelastet", Severity.Good, notes)
            {
                Kind = BottleneckKind.Gpu,
                Tip = "Mehr FPS: DLSS/FSR, Schatten oder Raytracing reduzieren",
            };

        bool vsync = f.SyncInterval >= 1;
        Verdict Limited(string detail) =>
            new(vsync ? "VSYNC" : "FPS-LIMIT", detail, Severity.Good, notes)
            {
                Kind = BottleneckKind.Limit,
                Tip = vsync ? "Begrenzt durch VSync auf die Bildwiederholrate" : "Begrenzt durch ein FPS-Limit – kein Engpass",
            };

        if (cpuWaitShare >= 0.25 && gpuShare < 0.85)
            return Limited($"{shares} – CPU und GPU haben Reserven");

        if (gpuShare < 0.85 && (gpuIdleShare >= 0.15 || cpuShare >= 0.85))
        {
            // Nicht jede "GPU wartet"-Situation ist ein CPU-Limit: Viele Spiele (beobachtet: Warframe mit
            // 60-FPS-Limit) warten für ihren Begrenzer innerhalb des CPU-Frames. PresentMon zählt das als
            // CPU-Busy, obwohl die CPU kaum arbeitet. Ein echtes CPU-Limit braucht daher eine ausgelastete CPU:
            // den Hauptthread (genau) oder ersatzweise einen P-Kern (unschärfer wegen Thread-Hopping).
            bool cpuSaturated = s.MainThread is { } main ? main.Percent >= 70 : s.Cpu.MaxPerfCoreLoad >= 85;
            if (!cpuSaturated || (s.MainThread == null && IsSteadyCap(f)))
            {
                string why = s.MainThread is { } m
                    ? $"Hauptthread nur {m.Percent:0} % – Begrenzer im Spiel"
                    : $"stabil bei {1000 / f.MedianMs:0} FPS – Begrenzer im Spiel";
                return Limited($"{shares} – {why}");
            }

            // Bei CPU-Limit die größten anderen CPU-Verbraucher nennen
            var background = s.BackgroundCpu.Where(p => p.Percent >= 5).Take(2).ToList();
            if (background.Count > 0)
                notes.Add(new("cpu-foreign", "Hintergrundlast: " + string.Join(", ", background.Select(p => $"{p.Name} {p.Percent:0} %")), Severity.Info));

            string tip = xmpOff
                ? "Mehr FPS: XMP/EXPO aktivieren, Sichtweite/NPC-Dichte senken"
                : "Mehr FPS: Sichtweite/NPC-Dichte senken, Hintergrundprogramme schließen";
            return new("CPU-LIMITIERT", $"{shares} – GPU wartet {f.GpuWaitMs:0.0} ms{thread}", Severity.Warn, notes)
            {
                Kind = BottleneckKind.Cpu,
                Tip = tip,
            };
        }

        return new("GEMISCHT", $"{shares} – kein klarer Engpass (CPU {cpuShare:P0}, GPU {gpuShare:P0}){thread}", Severity.Info, notes)
        {
            Kind = BottleneckKind.Mixed,
            Tip = "Engpass wechselt je nach Szene – Anteile im Balken beachten",
        };
    }

    /// <summary>
    /// Verbindungsprobleme eingrenzen: Schwankt schon der Weg zum Router, liegt es am eigenen Netz (oft WLAN);
    /// ist der Router stabil, das Internet-Ziel aber nicht, liegt es dahinter (Anbieter, Leitung).
    /// Zusätzlich: großer Datentransfer nebenbei, der den Ping während des Spielens hochtreibt.
    /// Alles erst nach einigen Sekunden, damit ein einzelner verlorener Ping keinen Hinweis auslöst.
    /// </summary>
    private void AddNetworkNotes(Snapshot s, List<Note> notes)
    {
        if (s.Network is not { } net) return;
        var router = net.Router;
        var internet = net.Internet;

        // Router: im eigenen Netz sind wenige ms normal, LAN meist < 2 ms, WLAN bis ~5 ms
        bool routerBad = net.RouterAnswers && router != null
            && (router.LossPercent >= 3 || router.JitterMs >= 8 || router.AvgMs >= 20);
        // Internet: schwankender Ping stört mehr als ein konstant etwas höherer
        bool internetBad = internet != null
            && (internet.LossPercent >= 3 || internet.JitterMs >= 15 || internet.AvgMs >= 100);

        // Beide immer auswerten, damit die Zeitmessung von "Sustained" für beide aktuell bleibt
        bool routerSustained = Sustained("net-router", routerBad, s.Time, TimeSpan.FromSeconds(5));
        bool internetSustained = Sustained("net-internet", internetBad && !routerBad, s.Time, TimeSpan.FromSeconds(5));
        if (routerSustained)
            notes.Add(new("net-router",
                $"Verbindung zum Router instabil (Ping {router!.AvgMs:0} ms ±{router.JitterMs:0}, Verlust {router.LossPercent:0} %) – WLAN? LAN-Kabel verwenden",
                Severity.Warn));
        else if (internetSustained)
            notes.Add(new("net-internet",
                internet!.AvgMs is double ms
                    ? $"Internetverbindung schwankt (Ping {ms:0} ms ±{internet.JitterMs:0}, Verlust {internet.LossPercent:0} %) – Problem liegt hinter dem Router"
                    : $"Internet-Ziel antwortet nicht (Verlust {internet.LossPercent:0} %)",
                Severity.Warn));

        // Bufferbloat: Steigt der Ping, sobald viel übertragen wird, puffert der Router zu viel
        bool loaded = net.DownMBps >= 3 || net.UpMBps >= 1;
        if (net.InternetLastMs is double last) _bloat.Add(s.Time, last, loaded);
        if (_bloat.Increase(s.Time) is double rise)
            notes.Add(new("net-bufferbloat", $"Bufferbloat: Ping +{rise:0} ms bei Downloads – QoS im Router", Severity.Warn));

        // Großer Transfer nebenbei während des Spielens (z. B. Spiele-Update, Cloud-Sync)
        bool busy = s.Frames != null && loaded;
        if (Sustained("net-transfer", busy, s.Time, TimeSpan.FromSeconds(5)))
        {
            string who = net.TopTransfer is { } p ? $" (vermutlich {p.Name})" : "";
            string rate = net.DownMBps >= 3 ? $"Download {net.DownMBps:0.0} MB/s" : $"Upload {net.UpMBps:0.0} MB/s";
            notes.Add(new("net-transfer", $"{rate} nebenbei{who} – kann den Ping erhöhen", Severity.Info));
        }
    }

    public enum SyncProblem { None, AboveRefresh, Halving }

    /// <summary>
    /// Typische Fehleinstellungen rund um VSync und G-Sync/FreeSync:
    /// AboveRefresh – ohne VSync deutlich mehr FPS als Hz: mit G-Sync/FreeSync verlässt das Spiel dann den
    /// VRR-Bereich (Bildrisse); ein FPS-Limit knapp unter der Bildwiederholrate hält es darin.
    /// Halving – mit VSync schafft das Spiel die Hz nicht ganz und springt zwischen voller und halber Rate
    /// (z. B. 144 ↔ 72 FPS), was trotz hoher Durchschnitts-FPS ruckelt.
    /// </summary>
    public static SyncProblem SyncProblemOf(FrameStats f, int hz)
    {
        if (hz < 30) return SyncProblem.None;
        if (f.SyncInterval == 0 && f.Fps > hz * 1.05) return SyncProblem.AboveRefresh;
        if (f.SyncInterval >= 1 && f.Frametimes.Length >= 60)
        {
            double period = 1000.0 / hz;
            int full = f.Frametimes.Count(t => Math.Abs(t.Ms - period) <= period * 0.2);
            int half = f.Frametimes.Count(t => Math.Abs(t.Ms - 2 * period) <= period * 0.3);
            double n = f.Frametimes.Length;
            if (full / n >= 0.15 && half / n >= 0.15) return SyncProblem.Halving;
        }
        return SyncProblem.None;
    }

    private static readonly double[] CommonCaps = [30, 40, 45, 48, 50, 60, 72, 75, 90, 100, 120, 144, 165, 170, 180, 200, 240, 360];

    /// <summary>
    /// Frametime liegt stabil (Mittel ≈ Median) auf einem typischen FPS-Limit (± 2 %).
    /// Ein echtes CPU-Limit schwankt mit der Szene und trifft selten genau 60,0 oder 144,0 FPS.
    /// </summary>
    private static bool IsSteadyCap(FrameStats f)
    {
        double fps = 1000 / f.MedianMs;
        bool steady = Math.Abs(f.AvgMs - f.MedianMs) / f.MedianMs < 0.05;
        return steady && CommonCaps.Any(cap => Math.Abs(fps - cap) / cap < 0.02);
    }

    /// <summary>Ordnet einen Ruckler dem nächstgelegenen Messtakt (± 0,75 s) zu.</summary>
    private string StutterCause(DateTime time)
    {
        var near = _history
            .Where(h => Math.Abs((h.Time - time).TotalSeconds) <= 0.75)
            .ToList();
        if (near.Any(h => h.VramSpill)) return "VRAM-Auslagerung";
        if (near.Any(h => h.PagesIn > 500 || h.DiskActive > 50)) return "Nachladen vom Laufwerk";
        if (near.Any(h => h.DpcHigh)) return "Treiber-Unterbrechung (DPC)";
        if (near.Any(h => h.CommitPercent >= 95)) return "Speicherzusage am Limit";
        if (near.Any(h => h.CpuTotal >= 90 || h.MaxPerfCore >= 98)) return "CPU-Spitze";
        return "unklar (z. B. Shader-Kompilierung)";
    }
}
