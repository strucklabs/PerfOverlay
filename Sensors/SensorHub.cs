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
    private readonly PresentMonSource _presentMon = new();
    private readonly HwInfoSensor _hwInfo = new();
    private readonly ProcessCpuSensor _processCpu = new();

    // WMI-Abfrage dauert einige 100 ms – im Hintergrund, damit das Overlay sofort startet
    private readonly Task<MemoryConfig?> _memoryConfig = Task.Run(MemoryConfig.Read);

    public SensorHub()
    {
        _cpu = new CpuSensor(_pdh);
        _memory = new MemorySensor(_pdh);
        _disk = new DiskSensor(_pdh);
        _windowsGpu = new WindowsGpuSensor(_pdh);
        _nvml = NvmlGpuSensor.TryCreate();
        _pdh.Collect();   // Ratenzähler brauchen zwei Messpunkte
    }

    public bool HasNvml => _nvml != null;

    public void StartFrameCapture() => _presentMon.Start();

    public Snapshot Sample()
    {
        _pdh.Collect();

        var (winLoad, winVram, topProcesses) = _windowsGpu.Available
            ? _windowsGpu.Read()
            : (0, 0, new List<GpuProcessLoad>());

        GpuData? gpu = _nvml?.Read();
        if (gpu == null && _windowsGpu.Available)
            gpu = new GpuData { Name = "GPU", Source = "Windows", Load = winLoad, VramUsedGB = winVram };

        int foregroundPid = Win32.GetForegroundProcessId();
        var frames = _presentMon.GetStats(foregroundPid);
        int gamePid = frames?.Pid ?? 0;

        return new Snapshot(
            DateTime.Now,
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
            GameSharedVramGB = _windowsGpu.Available ? _windowsGpu.SharedUsageGB(gamePid) : null,
            ForegroundPid = foregroundPid,
            GpuThermal = _hwInfo.Gpu,   // wird beim _hwInfo.Read() oben mitgelesen
            HwInfoStatus = _hwInfo.Status,
        };
    }

    public void Dispose()
    {
        _hwInfo.Dispose();
        _presentMon.Dispose();
        _nvml?.Dispose();
        _pdh.Dispose();
    }
}
