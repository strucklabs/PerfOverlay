using LibreHardwareMonitor.Hardware;

namespace PerfOverlay.Sensors;

/// <summary>
/// GPU-Spannung, Hotspot und Speichertemperatur einer NVIDIA-Karte über LibreHardwareMonitorLib (NVAPI) – ohne
/// HWiNFO und ohne Treiber: Nur der GPU-Teil der Bibliothek ist eingeschaltet, der lädt keinen Kerneltreiber.
/// Eine Abfrage dauert bis zu ~80 ms, deshalb liest ein eigener Hintergrund-Takt (1 s) und der Messtakt holt nur
/// den letzten Stand ab. Bei RTX 50 liefert NVAPI keinen Hotspot mehr – der Wert fehlt dann einfach.
/// </summary>
internal sealed class LhmGpuSensor : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private volatile GpuThermalData? _latest;

    public LhmGpuSensor() => _loop = Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Letzter Stand (null bis zur ersten Messung oder ohne Werte).</summary>
    public GpuThermalData? Latest => _latest;

    private async Task RunAsync(CancellationToken ct)
    {
        Computer? computer = null;
        try
        {
            computer = new Computer { IsGpuEnabled = true };
            computer.Open();
            var gpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia);
            if (gpu == null) return;

            using var timer = new PeriodicTimer(Interval);
            do
            {
                gpu.Update();
                _latest = Extract(gpu.Sensors.Select(s => (s.SensorType, s.Name, s.Value)));
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);   // z. B. NVAPI nicht ladbar – die übrigen Werte kommen weiter aus NVML
        }
        finally
        {
            try { computer?.Close(); } catch { /* beim Beenden egal */ }
        }
    }

    /// <summary>Die benötigten Sensoren nach Typ und Name heraussuchen – rein rechnerisch, damit testbar.</summary>
    internal static GpuThermalData? Extract(IEnumerable<(SensorType Type, string Name, float? Value)> sensors)
    {
        double? Find(SensorType type, string name) =>
            sensors.FirstOrDefault(s => s.Type == type && s.Name == name).Value is float v && !float.IsNaN(v) && v > 0 ? v : null;

        var data = new GpuThermalData(
            Find(SensorType.Voltage, "GPU Core Voltage"),
            Find(SensorType.Temperature, "GPU Hot Spot"),
            Find(SensorType.Temperature, "GPU Memory Junction"),
            Find(SensorType.Fan, "GPU Fan"));
        return data is { CoreVoltage: null, HotspotC: null, MemoryJunctionC: null, FanRpm: null } ? null : data;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
