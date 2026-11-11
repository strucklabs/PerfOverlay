using System.Diagnostics;
using PerfOverlay.Native;

namespace PerfOverlay.Sensors;

/// <summary>NVIDIA-Telemetrie über NVML (User Mode, kommt mit dem Treiber).</summary>
internal sealed class NvmlGpuSensor : IDisposable
{
    private readonly IntPtr _device;
    private readonly string _name;
    private readonly double? _maxCoreMhz;
    private readonly int? _maxGen;
    private readonly int? _maxWidth;

    private NvmlGpuSensor(IntPtr device)
    {
        _device = device;
        _name = Nvml.GetName(device);
        _maxCoreMhz = Nvml.nvmlDeviceGetMaxClockInfo(device, Nvml.NVML_CLOCK_GRAPHICS, out uint mc) == 0 ? mc : null;
        _maxGen = Nvml.nvmlDeviceGetMaxPcieLinkGeneration(device, out uint g) == 0 ? (int)g : null;
        _maxWidth = Nvml.nvmlDeviceGetMaxPcieLinkWidth(device, out uint w) == 0 ? (int)w : null;
        try
        {
            if (Nvml.nvmlDeviceGetBAR1MemoryInfo(device, out var bar1) == 0) Bar1TotalMB = bar1.Total / (1024.0 * 1024);
            if (Nvml.nvmlDeviceGetMemoryInfo(device, out var mem) == 0) VramTotalMB = mem.Total / (1024.0 * 1024);
        }
        catch (EntryPointNotFoundException) { /* sehr alter Treiber */ }
    }

    public string Name => _name;

    /// <summary>Größe des BAR1-Fensters (für die Erkennung von Resizable BAR).</summary>
    public double? Bar1TotalMB { get; }
    public double? VramTotalMB { get; }

    public static NvmlGpuSensor? TryCreate()
    {
        try
        {
            if (Nvml.nvmlInit_v2() != 0) return null;
            if (Nvml.nvmlDeviceGetCount_v2(out uint count) != 0 || count == 0 ||
                Nvml.nvmlDeviceGetHandleByIndex_v2(0, out IntPtr device) != 0)
            {
                Nvml.nvmlShutdown();
                return null;
            }
            return new NvmlGpuSensor(device);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;   // keine NVIDIA-Karte / kein Treiber
        }
    }

    public GpuData Read()
    {
        var d = _device;
        double? Opt(int status, double value) => status == 0 ? value : null;

        int sUtil = Nvml.nvmlDeviceGetUtilizationRates(d, out var util);
        int sMem = Nvml.nvmlDeviceGetMemoryInfo(d, out var mem);
        int sTemp = Nvml.nvmlDeviceGetTemperature(d, Nvml.NVML_TEMPERATURE_GPU, out uint temp);
        int sClk = Nvml.nvmlDeviceGetClockInfo(d, Nvml.NVML_CLOCK_GRAPHICS, out uint clk);
        int sMemClk = Nvml.nvmlDeviceGetClockInfo(d, Nvml.NVML_CLOCK_MEM, out uint memClk);
        int sPow = Nvml.nvmlDeviceGetPowerUsage(d, out uint mw);
        int sLim = Nvml.nvmlDeviceGetEnforcedPowerLimit(d, out uint limMw);
        int sFan = Nvml.nvmlDeviceGetFanSpeed(d, out uint fan);
        int sPs = Nvml.nvmlDeviceGetPerformanceState(d, out int pstate);
        int sThr = Nvml.nvmlDeviceGetCurrentClocksThrottleReasons(d, out ulong thr);
        int sGen = Nvml.nvmlDeviceGetCurrPcieLinkGeneration(d, out uint gen);
        int sWid = Nvml.nvmlDeviceGetCurrPcieLinkWidth(d, out uint wid);

        const double GB = 1024.0 * 1024 * 1024;
        return new GpuData
        {
            Name = _name,
            Source = "NVML",
            Load = Opt(sUtil, util.Gpu),
            CoreMhz = Opt(sClk, clk),
            MaxCoreMhz = _maxCoreMhz,
            MemMhz = Opt(sMemClk, memClk),
            TempC = Opt(sTemp, temp),
            PowerW = Opt(sPow, mw / 1000.0),
            PowerLimitW = Opt(sLim, limMw / 1000.0),
            FanPercent = Opt(sFan, fan),
            VramUsedGB = Opt(sMem, mem.Used / GB),
            VramTotalGB = Opt(sMem, mem.Total / GB),
            PState = sPs == 0 ? pstate : null,
            Throttle = sThr == 0 ? (Nvml.ThrottleReasons)thr : Nvml.ThrottleReasons.None,
            PcieGen = sGen == 0 ? (int)gen : null,
            PcieWidth = sWid == 0 ? (int)wid : null,
            PcieMaxGen = _maxGen,
            PcieMaxWidth = _maxWidth,
        };
    }

    private bool _rpmUnsupported;

    /// <summary>
    /// Lüfterdrehzahl in U/min (Mittel über alle Lüfter) – ab etwa Treiber R535 direkt aus NVML, ohne HWiNFO.
    /// Ältere Treiber kennen die Funktion nicht, dann bleibt der Wert leer.
    /// </summary>
    public double? ReadFanRpm()
    {
        if (_rpmUnsupported) return null;
        try
        {
            if (Nvml.nvmlDeviceGetNumFans(_device, out uint count) != 0 || count == 0) return null;
            var speeds = new List<double>();
            for (uint i = 0; i < count; i++)
            {
                var info = new Nvml.FanSpeedInfo { Version = Nvml.FanSpeedInfoV1, Fan = i };
                if (Nvml.nvmlDeviceGetFanSpeedRPM(_device, ref info) == 0) speeds.Add(info.Speed);
            }
            return speeds.Count > 0 ? speeds.Average() : null;
        }
        catch (EntryPointNotFoundException)
        {
            _rpmUnsupported = true;   // alter Treiber – nicht bei jedem Takt erneut versuchen
            return null;
        }
    }

    public void Dispose() => Nvml.nvmlShutdown();
}

/// <summary>
/// Herstellerunabhängige GPU-Zähler von Windows (WDDM) – dieselben, die der Task-Manager nutzt.
/// Funktionieren auch für AMD und Intel und liefern die GPU-Last pro Prozess.
/// </summary>
internal sealed class WindowsGpuSensor
{
    private readonly PdhQuery _pdh;
    private readonly IntPtr _engine3D;
    private readonly IntPtr _dedicated;
    private readonly Dictionary<int, string> _names = new();

    public WindowsGpuSensor(PdhQuery pdh)
    {
        _pdh = pdh;
        _engine3D = pdh.Add(@"\GPU Engine(*engtype_3D)\Utilization Percentage");
        _dedicated = pdh.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _processShared = pdh.Add(@"\GPU Process Memory(*)\Shared Usage");
    }

    private readonly IntPtr _processShared;

    /// <summary>
    /// Grafikspeicher, den ein Prozess im normalen RAM hält ("Shared GPU Memory"). Ist der VRAM voll,
    /// lagert der Treiber Texturen dorthin aus – der Zugriff über PCIe ist um ein Vielfaches langsamer.
    /// Instanzname z. B. "pid_1234_luid_0x00000000_0x0000C2B1_phys_0".
    /// </summary>
    public double? SharedUsageGB(int pid)
    {
        if (pid <= 0 || _processShared == IntPtr.Zero) return null;
        string prefix = $"pid_{pid}_";
        double bytes = _pdh.GetArray(_processShared)
            .Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Sum(x => x.Value);
        return bytes / (1024.0 * 1024 * 1024);
    }

    public bool Available => _engine3D != IntPtr.Zero;

    /// <summary>3D-Last eines Prozesses je Grafikadapter (Schlüssel: LUID wie "0x00000000_0x0000C2B1").</summary>
    public Dictionary<string, double> LoadByAdapter(int pid)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (pid <= 0 || !Available) return result;
        string prefix = $"pid_{pid}_";
        foreach (var (name, value) in _pdh.GetArray(_engine3D))
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var parts = name.Split('_');
            if (parts.Length < 5) continue;
            string luid = parts[3] + "_" + parts[4];
            result[luid] = result.GetValueOrDefault(luid) + value;
        }
        return result;
    }

    /// <summary>Instanzname z. B. "pid_1234_luid_0x00000000_0x0000C2B1_phys_0_eng_0_engtype_3D".</summary>
    public (double AdapterLoad, double VramUsedGB, List<GpuProcessLoad> TopProcesses) Read()
    {
        var perAdapter = new Dictionary<string, double>();
        var perPid = new Dictionary<int, double>();

        foreach (var (name, value) in _pdh.GetArray(_engine3D))
        {
            var parts = name.Split('_');
            if (parts.Length < 5 || parts[0] != "pid" || !int.TryParse(parts[1], out int pid)) continue;
            string luid = parts[3] + "_" + parts[4];
            perAdapter[luid] = perAdapter.GetValueOrDefault(luid) + value;
            perPid[pid] = perPid.GetValueOrDefault(pid) + value;
        }

        double vram = _pdh.GetArray(_dedicated).Select(x => x.Value).DefaultIfEmpty(0).Max() / (1024.0 * 1024 * 1024);
        double load = perAdapter.Values.DefaultIfEmpty(0).Max();

        var top = perPid
            .Where(x => x.Value >= 1)
            .OrderByDescending(x => x.Value)
            .Take(3)
            .Select(x => new GpuProcessLoad(x.Key, ProcessName(x.Key), Math.Min(100, x.Value)))
            .ToList();

        return (Math.Min(100, load), vram, top);
    }

    private string ProcessName(int pid)
    {
        if (_names.TryGetValue(pid, out var name)) return name;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch { name = $"PID {pid}"; }
        if (_names.Count > 500) _names.Clear();
        return _names[pid] = name;
    }
}
