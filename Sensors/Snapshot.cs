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
    double? PowerReportingDeviation = null) // AMD: < 100 % = Mainboard meldet der CPU zu wenig Strom
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
    public string Source { get; init; } = "";   // "NVML" oder "Windows"
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
}

/// <summary>Auslastung des stärksten Threads eines Prozesses in % eines Kerns.</summary>
public sealed record ThreadLoad(double Percent, string? Name);

public sealed record ProcessLoad(int Pid, string Name, double Percent);

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
}
