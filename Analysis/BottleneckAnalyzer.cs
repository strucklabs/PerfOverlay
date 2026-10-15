using PerfOverlay.Sensors;
using Throttle = PerfOverlay.Native.Nvml.ThrottleReasons;

namespace PerfOverlay.Analysis;

public enum Severity { Good, Info, Warn, Bad }

/// <param name="Key">Stabile Kennung (unabhängig von Messwerten im Text), damit das NoteLog Wiederholungen erkennt.</param>
public sealed record Note(string Key, string Text, Severity Severity);

public sealed record Verdict(string Title, string Detail, Severity Severity, IReadOnlyList<Note> Notes);

/// <summary>
/// Bestimmt den aktuellen Engpass.
///
/// Primär (mit PresentMon): Anteil von GPU-Busy bzw. CPU-Busy an der Frametime.
///   GPU arbeitet ≥ 90 % der Frametime          → GPU-limitiert (der Normalfall beim Gaming)
///   CPU arbeitet ≥ 85 %, GPU deutlich weniger   → CPU-limitiert (GPU wartet auf Draw Calls)
///   beide haben Luft, CPU wartet                → FPS-Limit / VSync
///
/// Fallback (ohne Frame-Daten): GPU-Auslastung und Last des stärksten Kerns. Unsicher, weil der
/// Scheduler einen vollen Render-Thread auf mehrere Kerne verteilt (Thread-Hopping).
/// </summary>
public sealed class BottleneckAnalyzer
{
    private Verdict? _shown;
    private string? _candidate;
    private int _candidateCount;

    public Verdict Analyze(Snapshot s)
    {
        var raw = Evaluate(s);

        // Hysterese: Der Titel wechselt erst, wenn das neue Ergebnis zweimal in Folge auftritt.
        if (_shown == null || raw.Title == _shown.Title)
        {
            _candidate = null;
            _shown = raw;
            return raw;
        }
        if (raw.Title == _candidate && ++_candidateCount >= 2)
        {
            _candidate = null;
            _shown = raw;
            return raw;
        }
        if (raw.Title != _candidate)
        {
            _candidate = raw.Title;
            _candidateCount = 1;
        }
        // Alten Titel behalten, Hinweise aber aktuell halten
        return _shown with { Notes = raw.Notes };
    }

    private readonly Dictionary<string, DateTime> _conditionSince = new();
    private DateTime? _startTime;

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

        // --- P-State: taktet die GPU passend zur Last? (P0 = Volllast, P8 = Idle) ---
        if (gpu?.PState is int ps)
        {
            if (Sustained("pstate-idle-high", ps <= 2 && gpuLoad < 10, s.Time, TimeSpan.FromSeconds(60)))
                notes.Add(new("pstate-idle-high",
                    $"GPU taktet im Leerlauf nicht herunter (P{ps}, {gpu.PowerW:0} W) – z. B. Multi-Monitor, hohe Hz oder Hintergrundprogramm",
                    Severity.Info));
            if (Sustained("pstate-load-low", ps >= 5 && gpuLoad > 50 && s.Frames != null, s.Time, TimeSpan.FromSeconds(5)))
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

        if (gpu?.VramUsedGB is double used && gpu.VramTotalGB is double total && used / total > 0.95)
            notes.Add(new("vram", $"VRAM fast voll ({used:0.0}/{total:0.0} GB)", Severity.Warn));

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
        if (s.MemoryConfig is { } ram && s.Time - _startTime < TimeSpan.FromSeconds(10))
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
        bool diskSlow = s.Disk.ActivePercent > 80 && s.Disk.LatencyMs > 20;
        if (diskSlow)
            notes.Add(new("disk", $"Datenträger ausgelastet ({s.Disk.LatencyMs:0} ms Latenz)", Severity.Warn));

        // --- Andere Prozesse auf der GPU ---
        if (s.Frames != null)
        {
            var other = s.GpuTopProcesses.FirstOrDefault(p => p.Pid != s.Frames.Pid && p.Load >= 15);
            if (other != null)
                notes.Add(new("gpu-foreign", $"Fremdlast auf GPU: {other.Name} {other.Load:0} %", Severity.Warn));
        }

        var f = s.Frames;
        if (f != null && f.AvgMs > 0 && !double.IsNaN(f.GpuBusyMs) && !double.IsNaN(f.CpuBusyMs))
        {
            // --- Ruckler ---
            if (f.P99Ms > 25 && f.P99Ms > f.MedianMs * 2.5)
            {
                string cause = memoryPressure ? " – vermutlich RAM/Auslagerung"
                             : diskSlow || heavyPaging ? " – vermutlich Nachladen vom Datenträger"
                             : " – z. B. Shader-Kompilierung oder Streaming";
                notes.Add(new("stutter", $"Ruckler: 1 % der Frames ≥ {f.P99Ms:0} ms{cause}", Severity.Warn));
            }

            double gpuShare = f.GpuBusyMs / f.AvgMs;
            double cpuShare = f.CpuBusyMs / f.AvgMs;
            double cpuWaitShare = double.IsNaN(f.CpuWaitMs) ? 0 : f.CpuWaitMs / f.AvgMs;
            string shares = $"Frame {f.AvgMs:0.0} ms · CPU {f.CpuBusyMs:0.0} ms · GPU {f.GpuBusyMs:0.0} ms";

            if (gpuShare >= 0.90)
                return new("GPU-LIMITIERT", $"{shares} – Grafikkarte voll ausgelastet", Severity.Good, notes);
            if (cpuShare >= 0.85 && gpuShare < 0.80)
                return new("CPU-LIMITIERT", $"{shares} – GPU wartet {f.GpuWaitMs:0.0} ms/Frame auf die CPU", Severity.Warn, notes);
            if (cpuWaitShare >= 0.25 && gpuShare < 0.85)
                return new("FPS-LIMIT / VSYNC", $"{shares} – CPU und GPU haben Reserven", Severity.Good, notes);
            return new("GEMISCHT", $"{shares} – kein klarer Engpass (CPU {cpuShare:P0}, GPU {gpuShare:P0})", Severity.Info, notes);
        }

        // --- Fallback ohne Frame-Daten ---
        string why = s.FrameSourceRunning ? "keine 3D-Anwendung erkannt" : s.FrameSourceStatus;
        if (gpuLoad >= 95)
            return new("GPU-LIMITIERT", $"GPU {gpuLoad:0} % (geschätzt, {why})", Severity.Good, notes);
        if (s.Cpu.MaxCoreLoad >= 90 && gpuLoad is > 20 and < 85)
            return new("CPU-LIMITIERT?", $"ein Kern bei {s.Cpu.MaxCoreLoad:0} %, GPU nur {gpuLoad:0} % (geschätzt, {why})", Severity.Warn, notes);
        if (s.Cpu.TotalLoad >= 85)
            return new("CPU AUSGELASTET", $"CPU gesamt {s.Cpu.TotalLoad:0} % ({why})", Severity.Warn, notes);
        return new("KEIN ENGPASS", $"System hat Reserven ({why})", Severity.Good, notes);
    }
}
