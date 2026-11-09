namespace PerfOverlay.Sensors;

public sealed record CpuData(
    double TotalLoad,        // % (wie Task-Manager, "% Processor Utility")
    double[] CoreLoads,      // % pro logischem Kern
    double AvgMhz,           // effektiver Takt, Mittel über alle Kerne
    double MaxMhz)           // effektiver Takt, schnellster Kern
{
    public double MaxCoreLoad => CoreLoads.Length > 0 ? CoreLoads.Max() : 0;

    /// <summary>Windows "% Performance Limit": 100 = unbegrenzt, darunter bremst Power-/Temperaturlimit.</summary>
    public double? PerfLimitPercent { get; init; }

    /// <summary>Effizienzklasse je logischem Kern (gleiche Reihenfolge wie CoreLoads); höher = Performance-Kern.</summary>
    public int[] CoreClasses { get; init; } = [];

    /// <summary>Hybrid-CPU mit P- und E-Kernen (Intel ab 12. Generation).</summary>
    public bool IsHybrid { get; init; }

    private int TopClass => CoreClasses.Length > 0 ? CoreClasses.Max() : 0;

    /// <summary>Stärkster P-Kern – bei Hybrid-CPUs der für Spiele relevante Wert, sonst wie MaxCoreLoad.</summary>
    public double MaxPerfCoreLoad => IsHybrid && CoreClasses.Length == CoreLoads.Length
        ? CoreLoads.Where((_, i) => CoreClasses[i] == TopClass).DefaultIfEmpty(0).Max()
        : MaxCoreLoad;

    public double MaxEffCoreLoad => IsHybrid && CoreClasses.Length == CoreLoads.Length
        ? CoreLoads.Where((_, i) => CoreClasses[i] != TopClass).DefaultIfEmpty(0).Max()
        : 0;

    public bool IsPerfCore(int i) => !IsHybrid || i >= CoreClasses.Length || CoreClasses[i] == TopClass;
}

public enum HwInfoStatus
{
    Active,          // liefert Daten
    NotRunning,      // HWiNFO läuft nicht
    NoSharedMemory,  // läuft, aber Sensorfenster/„Shared Memory Support“ fehlt
    Stopped,         // lieferte, hat aber aufgehört (beendet oder 12-h-Grenze der Gratisversion)
}

/// <summary>
/// GPU-Werte, die NVML auf GeForce-Karten nicht liefert – aus HWiNFO.
/// Speicher-Junction (GDDR6X drosselt um 110 °C) und Hotspot sind bei RTX 30/40 oft die heißesten Stellen.
/// </summary>
public sealed record GpuThermalData(double? CoreVoltage, double? HotspotC, double? MemoryJunctionC, double? FanRpm = null);

/// <summary>CPU-Werte, die nur mit Ring-0-Zugriff lesbar sind – hier aus dem Shared Memory von HWiNFO.</summary>
public sealed record CpuThermalData(
    string Source,
    double? TempC,                          // Package-Temperatur (Intel) bzw. Tctl/Tdie (AMD)
    double? CoreMaxC,                       // heißester Kern
    double? PowerW,                         // Package-Leistung
    double? PowerLimitW,                    // PL1
    IReadOnlyList<string> ThrottleReasons,  // aktive Drosselungsgründe, bereits auf Deutsch zusammengefasst
    double? TjMaxC,                         // Maximaltemperatur der CPU (Intel: aus "Distance to TjMAX" berechnet)
    bool IsAmd,                             // Ryzen boostet bis an TjMax – hohe Temperatur ist dort Normalbetrieb
    double? PowerReportingDeviation = null, // AMD: < 100 % = Mainboard meldet der CPU zu wenig Strom
    double? FanRpm = null)                  // CPU-Lüfter am Mainboard-Sensor (U/min)
{
    /// <summary>Wie weit die CPU noch von ihrer Maximaltemperatur entfernt ist.</summary>
    public double? HeadroomC => TempC is double t && TjMaxC is double tj ? tj - t : null;
}

public sealed record MemoryData(
    double UsedGB,
    double TotalGB,
    double LoadPercent,
    double CommitGB,
    double CommitLimitGB,
    double PagesInPerSec);   // Hard Faults: Seiten, die von der Platte nachgeladen werden

public sealed record DiskData(
    double ActivePercent,    // höchste Aktivität über alle physischen Laufwerke
    double LatencyMs,        // höchste mittlere Latenz pro Transfer
    double MBps);

public sealed record GpuData
{
    public string Name { get; init; } = "GPU";
    public string Source { get; init; } = "";   // "NVML" (NVIDIA), "ADLX" (AMD) oder "Windows"
    public double? Load { get; init; }
    public double? CoreMhz { get; init; }
    public double? MaxCoreMhz { get; init; }
    public double? MemMhz { get; init; }
    public double? TempC { get; init; }
    public double? PowerW { get; init; }
    public double? PowerLimitW { get; init; }
    public double? FanPercent { get; init; }
    public double? VramUsedGB { get; init; }
    public double? VramTotalGB { get; init; }
    public int? PState { get; init; }
    public int? PcieGen { get; init; }
    public int? PcieWidth { get; init; }
    public int? PcieMaxGen { get; init; }
    public int? PcieMaxWidth { get; init; }
    public Native.Nvml.ThrottleReasons Throttle { get; init; }
}

public sealed record GpuProcessLoad(int Pid, string Name, double Load);

public sealed record FrameStats(
    int Pid,
    string App,
    double Fps,
    double Low1Fps,          // "1% Low": aus dem 99. Perzentil der Frametimes
    double AvgMs,
    double MedianMs,
    double P99Ms,
    double CpuBusyMs,
    double CpuWaitMs,
    double GpuBusyMs,
    double GpuWaitMs)
{
    /// <summary>FPS ohne generierte Frames (Frame Generation); sonst gleich Fps.</summary>
    public double AppFps { get; init; }
    public double GeneratedShare { get; init; }
    public string PresentMode { get; init; } = "";
    public int SyncInterval { get; init; } = -1;    // ≥ 1 = VSync
    public bool AllowsTearing { get; init; }
    /// <summary>Frametimes der letzten Sekunden: (Alter in s, Frametime in ms), älteste zuerst.</summary>
    public (double Age, double Ms)[] Frametimes { get; init; } = [];
    /// <summary>Alter (s) der Ruckler-Frames im Verlaufsfenster.</summary>
    public double[] SpikeAges { get; init; } = [];
    /// <summary>Zeitpunkte der Ruckler auf der fortlaufenden Uhr der Frame-Quelle – zum lückenlosen Zählen.</summary>
    public double[] SpikeTimes { get; init; } = [];
    /// <summary>Ø Sprung der Frametime von Bild zu Bild (ms) in den letzten Sekunden, ohne Ruckler.</summary>
    public double PacingMs { get; init; } = double.NaN;
}

/// <summary>Auslastung des stärksten Threads eines Prozesses in % eines Kerns.</summary>
public sealed record ThreadLoad(double Percent, string? Name);

/// <param name="Percent">CPU-Anteil an der Gesamt-CPU.</param>
/// <param name="TransferMBps">Datendurchsatz des Prozesses (Datei- und Netzwerk-E/A zusammen), MB/s.</param>
public sealed record ProcessLoad(int Pid, string Name, double Percent, double TransferMBps = 0);

/// <summary>Verbindungsqualität: Ping zum Router und ins Internet, Datendurchsatz aller Adapter.</summary>
public sealed record NetworkData(
    PingStats? Router,
    PingStats? Internet,
    bool RouterAnswers,      // manche Router beantworten Ping nie – dann sagt fehlende Antwort nichts aus
    double DownMBps,
    double UpMBps)
{
    /// <summary>Größter Datendurchsatz außer dem Spiel – bei hohem Download vermutlich der Verursacher.</summary>
    public ProcessLoad? TopTransfer { get; init; }
}

public sealed record Snapshot(
    DateTime Time,
    CpuData Cpu,
    MemoryData Memory,
    DiskData Disk,
    GpuData? Gpu,
    IReadOnlyList<GpuProcessLoad> GpuTopProcesses,
    FrameStats? Frames,
    string FrameSourceStatus,
    bool FrameSourceRunning,
    CpuThermalData? CpuThermal,
    MemoryConfig? MemoryConfig)
{
    /// <summary>Stärkster Thread des Spiels (100 % = ein Kern voll).</summary>
    public ThreadLoad? MainThread { get; init; }

    /// <summary>Größte andere CPU-Verbraucher in % der Gesamt-CPU.</summary>
    public IReadOnlyList<ProcessLoad> BackgroundCpu { get; init; } = [];

    /// <summary>Grafikspeicher des Spiels, der in den normalen RAM ausgelagert ist.</summary>
    public double? GameSharedVramGB { get; init; }

    /// <summary>Prozess des Fensters im Vordergrund (zum Messzeitpunkt).</summary>
    public int ForegroundPid { get; init; }

    /// <summary>GPU-Spannung, Hotspot und Speichertemperatur aus HWiNFO (null ohne HWiNFO).</summary>
    public GpuThermalData? GpuThermal { get; init; }

    /// <summary>Anwendung im Vordergrund, deren Frames verworfen wurden, weil sie kein Spiel ist (Browser, KI-App …).</summary>
    public string? IgnoredApp { get; init; }

    /// <summary>Zeitgeber hinter QueryPerformanceCounter (TSC/HPET/ACPI), einmal beim Start gemessen.</summary>
    public TimerInfo? Timer { get; init; }

    public HwInfoStatus HwInfoStatus { get; init; } = HwInfoStatus.NotRunning;

    public NetworkData? Network { get; init; }

    /// <summary>TCP-Verbindungen des Spiels, davon tote (CLOSE_WAIT); null ohne Spiel.</summary>
    public ConnectionData? GameConnections { get; init; }

    /// <summary>Monitor, Energieplan, Speicher-Integrität, HAGS, Resizable BAR.</summary>
    public SystemInfo? System { get; init; }
}
