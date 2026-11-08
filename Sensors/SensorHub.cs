using PerfOverlay.Native;

namespace PerfOverlay.Sensors;

/// <summary>Bündelt alle Datenquellen und erzeugt pro Takt einen konsistenten Snapshot.</summary>
internal sealed class SensorHub : IDisposable
{
    private readonly PdhQuery _pdh = new();
    private readonly CpuSensor _cpu;
    private readonly MemorySensor _memory;
    private readonly DiskSensor _disk;
    private readonly WindowsGpuSensor _windowsGpu;
    private readonly NvmlGpuSensor? _nvml;
    private readonly AdlxGpuSensor? _adlx;
    private readonly PresentMonSource _presentMon = new();
    private readonly HwInfoSensor _hwInfo = new();
    private readonly ProcessCpuSensor _processCpu = new();

    // WMI-Abfrage dauert einige 100 ms – im Hintergrund, damit das Overlay sofort startet
    private readonly Task<MemoryConfig?> _memoryConfig = Task.Run(MemoryConfig.Read);
    // Zeitgeber ändert sich nur mit einem Neustart von Windows – eine Messung genügt
    private readonly Task<TimerInfo> _timer = Task.Run(TimerSource.Detect);

    private readonly NetworkSensor _network;
    private readonly ConnectionSensor _connections = new();

    /// <param name="pingTarget">Ping-Ziel im Internet für die Verbindungsanalyse; leer = nicht pingen.</param>
    public SensorHub(string? pingTarget = "1.1.1.1")
    {
        _cpu = new CpuSensor(_pdh);
        _memory = new MemorySensor(_pdh);
        _disk = new DiskSensor(_pdh);
        _windowsGpu = new WindowsGpuSensor(_pdh);
        _network = new NetworkSensor(_pdh, pingTarget);
        _nvml = NvmlGpuSensor.TryCreate();
        if (_nvml == null) _adlx = AdlxGpuSensor.TryCreate(probeFirst: true);
        _pdh.Collect();   // Ratenzähler brauchen zwei Messpunkte
    }

    public bool HasNvml => _nvml != null;
    public AdlxGpuSensor? Adlx => _adlx;

    /// <summary>Startet die Hintergrundmessungen: PresentMon (Frames) und Ping (Verbindung).</summary>
    public void StartFrameCapture()
    {
        _presentMon.Start();
        _network.Start();
    }

    public Snapshot Sample()
    {
        _pdh.Collect();

        var (winLoad, winVram, topProcesses) = _windowsGpu.Available
            ? _windowsGpu.Read()
            : (0, 0, new List<GpuProcessLoad>());

        GpuData? gpu = _nvml?.Read();
        GpuThermalData? adlxThermal = null;
        if (gpu == null && _adlx?.Read() is { } adlx)
        {
            gpu = adlx.Gpu with { Load = adlx.Gpu.Load ?? (_windowsGpu.Available ? winLoad : null) };
            adlxThermal = adlx.Thermal;
        }
        if (gpu == null && _windowsGpu.Available)
            gpu = new GpuData { Name = "GPU", Source = "Windows", Load = winLoad, VramUsedGB = winVram };

        int foregroundPid = Win32.GetForegroundProcessId();
        var frames = _presentMon.GetStats(foregroundPid);
        // Browser, KI-Apps, Terminals, Tools: messen, aber nicht als Spiel bewerten
        string? ignoredApp = null;
        if (frames != null && Analysis.GameFilter.IsNonGame(frames.App))
        {
            ignoredApp = System.IO.Path.GetFileNameWithoutExtension(frames.App);
            frames = null;
        }
        int gamePid = frames?.Pid ?? 0;
        var now = DateTime.Now;

        return new Snapshot(
            now,
            _cpu.Read(),
            _memory.Read(),
            _disk.Read(),
            gpu,
            topProcesses,
            frames,
            _presentMon.Status,
            _presentMon.Running,
            _hwInfo.Read(),
            _memoryConfig.IsCompletedSuccessfully ? _memoryConfig.Result : null)
        {
            MainThread = _processCpu.SampleMainThread(gamePid),
            BackgroundCpu = _processCpu.SampleBackground(gamePid),
            // nach SampleBackground: dort wird auch der Datendurchsatz je Prozess aktualisiert
            Network = _network.Read() with { TopTransfer = _processCpu.TopTransfer(gamePid) },
            GameSharedVramGB = _windowsGpu.Available ? _windowsGpu.SharedUsageGB(gamePid) : null,
            ForegroundPid = foregroundPid,
            GpuThermal = MergeThermal(_hwInfo.Gpu, adlxThermal),   // _hwInfo.Gpu wird beim _hwInfo.Read() oben mitgelesen
            HwInfoStatus = _hwInfo.Status,
            IgnoredApp = ignoredApp,
            Timer = _timer.IsCompletedSuccessfully ? _timer.Result : null,
            GameConnections = _connections.Read(gamePid, now),
        };
    }

    /// <summary>HWiNFO hat Vorrang (falls es läuft und der Nutzer es so eingerichtet hat), ADLX füllt Lücken.</summary>
    internal static GpuThermalData? MergeThermal(GpuThermalData? hwInfo, GpuThermalData? adlx)
    {
        if (hwInfo == null || adlx == null) return hwInfo ?? adlx;
        return new GpuThermalData(
            hwInfo.CoreVoltage ?? adlx.CoreVoltage,
            hwInfo.HotspotC ?? adlx.HotspotC,
            hwInfo.MemoryJunctionC ?? adlx.MemoryJunctionC,
            hwInfo.FanRpm ?? adlx.FanRpm);
    }

    public void Dispose()
    {
        _network.Dispose();
        _hwInfo.Dispose();
        _presentMon.Dispose();
        _nvml?.Dispose();
        _adlx?.Dispose();
        _pdh.Dispose();
    }
}
