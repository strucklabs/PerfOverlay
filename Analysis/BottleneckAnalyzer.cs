using PerfOverlay.Sensors;
using Throttle = PerfOverlay.Native.Nvml.ThrottleReasons;

namespace PerfOverlay.Analysis;

public enum Severity { Good, Info, Warn, Bad }

/// <summary>Grundkategorie des Engpasses – Basis für die Anteile über die letzten 30 Sekunden.</summary>
public enum BottleneckKind { None, Gpu, Cpu, Limit, Mixed }

/// <param name="Key">Stabile Kennung (unabhängig von Messwerten im Text), damit das NoteLog Wiederholungen erkennt.</param>
public sealed record Note(string Key, string Text, Severity Severity);

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
    private DateTime? _startTime;

    /// <summary>Kennzahlen je Messtakt – um Ruckler im Nachhinein einer Ursache zuzuordnen.</summary>
    private readonly record struct Sample(DateTime Time, double PagesIn, double DiskActive, double CpuTotal, double MaxPerfCore, bool VramSpill);

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

        // --- VRAM: voll ist erst ein Problem, wenn das Spiel in den RAM auslagert ---
        bool vramFull = gpu?.VramUsedGB is double used && gpu.VramTotalGB is double total && used / total > 0.90;
        bool vramSpill = vramFull && s.GameSharedVramGB > 1.0;
        if (vramSpill)
            notes.Add(new("vram", $"VRAM voll – Spiel lagert {s.GameSharedVramGB:0.0} GB in den RAM aus (Ruckler möglich)", Severity.Warn));
        else if (vramFull && gpu!.VramUsedGB / gpu.VramTotalGB > 0.95)
            notes.Add(new("vram", $"VRAM fast voll ({gpu.VramUsedGB:0.0}/{gpu.VramTotalGB:0.0} GB)", Severity.Info));

        // --- CPU-Drosselung: genau mit HWiNFO, sonst über das Windows-Leistungslimit ---
        if (s.CpuThermal is { } cpuThermal)
        {
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
        else if (Sustained("cpu-perflimit", s.Cpu.PerfLimitPercent is < 90 && s.Cpu.TotalLoad > 30, s.Time, TimeSpan.FromSeconds(3)))
        {
            notes.Add(new("cpu-perflimit",
                $"CPU begrenzt auf {s.Cpu.PerfLimitPercent:0} % (Power-/Temperaturlimit, Details mit HWiNFO)", Severity.Warn));
        }

        // --- RAM-Bestückung: ändert sich nicht, daher nur in den ersten Sekunden melden.
        //     Das NoteLog hält die Meldung danach noch 10 Minuten sichtbar. ---
        _startTime ??= s.Time;
        bool xmpOff = false;
        if (s.MemoryConfig is { } ram)
        {
            xmpOff = ram.RatedMTs > ram.ConfiguredMTs + 100 || (ram.RatedMTs == null && ram.RunsAtJedecBase && !ram.IsSoDimm);
            if (s.Time - _startTime < TimeSpan.FromSeconds(10))
            {
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
        }

        // --- Speicher & Datenträger ---
        // "Pages Input/sec" zählt auch Spieldaten, die per Memory-Mapping von der SSD gelesen werden.
        // Ein hoher Wert allein ist daher normales Asset-Streaming; echte RAM-Knappheit erst bei hoher Belegung.
        var mem = s.Memory;
        bool heavyPaging = mem.PagesInPerSec > 500;
        bool memoryPressure = mem.LoadPercent >= 90 || (heavyPaging && mem.LoadPercent >= 80);
        if (memoryPressure)
            notes.Add(new("ram", $"RAM knapp ({mem.LoadPercent:0} % belegt, {mem.PagesInPerSec:0} Seiten/s nachgeladen)", Severity.Warn));
        else if (mem.PagesInPerSec > 2000)
            notes.Add(new("pagein", $"Starkes Nachladen von SSD ({mem.PagesInPerSec:0} Seiten/s) – meist Level-/Asset-Streaming", Severity.Info));
        if (s.Disk.ActivePercent > 80 && s.Disk.LatencyMs > 20)
            notes.Add(new("disk", $"Datenträger ausgelastet ({s.Disk.LatencyMs:0} ms Latenz)", Severity.Warn));

        // Verlauf für die Ruckler-Zuordnung
        _history.Enqueue(new Sample(s.Time, mem.PagesInPerSec, s.Disk.ActivePercent, s.Cpu.TotalLoad, s.Cpu.MaxPerfCoreLoad, vramSpill));
        while (_history.Count > 0 && s.Time - _history.Peek().Time > HistoryWindow) _history.Dequeue();

        // --- Andere Prozesse auf der GPU ---
        if (f != null)
        {
            var other = s.GpuTopProcesses.FirstOrDefault(p => p.Pid != f.Pid && p.Load >= 15);
            if (other != null)
                notes.Add(new("gpu-foreign", $"Fremdlast auf GPU: {other.Name} {other.Load:0} %", Severity.Warn));
        }

        if (f != null && f.AvgMs > 0 && !double.IsNaN(f.GpuBusyMs) && !double.IsNaN(f.CpuBusyMs))
            return EvaluateFrames(s, f, notes, xmpOff);

        // --- Fallback ohne Frame-Daten ---
        string why = s.FrameSourceRunning ? "keine 3D-Anwendung erkannt" : s.FrameSourceStatus;
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

    private Verdict EvaluateFrames(Snapshot s, FrameStats f, List<Note> notes, bool xmpOff)
    {
        // --- Frame Generation: Analyse beruht nur auf den vom Spiel gerenderten Frames ---
        if (f.GeneratedShare > 0.3)
            notes.Add(new("framegen", $"Frame Generation aktiv – {f.Fps:0} FPS angezeigt, {f.AppFps:0} FPS vom Spiel gerendert", Severity.Info));

        // --- Darstellungsmodus: Kopie über den Desktop-Compositor kostet Latenz ---
        if (f.PresentMode.Contains("Copy", StringComparison.OrdinalIgnoreCase))
            notes.Add(new("presentmode",
                $"Darstellung per Kopie ({f.PresentMode}) – höhere Latenz; Vollbild oder randlos mit Flip-Modell nutzen", Severity.Warn));

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

        if (cpuWaitShare >= 0.25 && gpuShare < 0.85)
        {
            bool vsync = f.SyncInterval >= 1;
            return new(vsync ? "VSYNC" : "FPS-LIMIT", $"{shares} – CPU und GPU haben Reserven", Severity.Good, notes)
            {
                Kind = BottleneckKind.Limit,
                Tip = vsync ? "Begrenzt durch VSync auf die Bildwiederholrate" : "Begrenzt durch ein FPS-Limit – kein Engpass",
            };
        }

        if (gpuShare < 0.85 && (gpuIdleShare >= 0.15 || cpuShare >= 0.85))
        {
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

    /// <summary>Ordnet einen Ruckler dem nächstgelegenen Messtakt (± 0,75 s) zu.</summary>
    private string StutterCause(DateTime time)
    {
        var near = _history
            .Where(h => Math.Abs((h.Time - time).TotalSeconds) <= 0.75)
            .ToList();
        if (near.Any(h => h.VramSpill)) return "VRAM-Auslagerung";
        if (near.Any(h => h.PagesIn > 500 || h.DiskActive > 50)) return "Nachladen von SSD";
        if (near.Any(h => h.CpuTotal >= 90 || h.MaxPerfCore >= 98)) return "CPU-Spitze";
        return "unklar (z. B. Shader-Kompilierung)";
    }
}
