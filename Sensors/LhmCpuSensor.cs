using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using PerfOverlay.Native;

namespace PerfOverlay.Sensors;

/// <summary>
/// CPU-Temperatur, CPU-Leistung und CPU-Lüfter über LibreHardwareMonitorLib und den Treiber PawnIO – nur wenn
/// PawnIO bereits installiert ist und PerfOverlay mit Admin-Rechten läuft. PerfOverlay installiert PawnIO nie
/// selbst. Eine Abfrage dauert bis zu ~300 ms, deshalb liest ein eigener Hintergrund-Takt (1 s). Ob PawnIO
/// (nachträglich) installiert wurde, wird alle 30 s geprüft – ohne Neustart von PerfOverlay.
/// </summary>
internal sealed class LhmCpuSensor : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DetectInterval = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private volatile CpuThermalData? _latest;
    private volatile bool _installed;

    public LhmCpuSensor() => _loop = Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Letzter Stand (null ohne PawnIO, ohne Admin-Rechte oder vor der ersten Messung).</summary>
    public CpuThermalData? Latest => _latest;

    /// <summary>PawnIO ist installiert (unabhängig davon, ob PerfOverlay Admin-Rechte hat).</summary>
    public bool Installed => _installed;

    /// <summary>Der Treiber legt bei der Installation einen Dienst "PawnIO" an (Start bei Bedarf).</summary>
    internal static bool IsInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key != null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Warten, bis PawnIO da ist (und Admin-Rechte vorhanden sind)
        while (!ct.IsCancellationRequested)
        {
            _installed = IsInstalled();
            if (_installed && Win32.IsElevated()) break;
            try { await Task.Delay(DetectInterval, ct); } catch (OperationCanceledException) { return; }
        }

        Computer? computer = null;
        try
        {
            computer = new Computer { IsCpuEnabled = true, IsMotherboardEnabled = true };
            computer.Open();
            var cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            var superIo = computer.Hardware.Where(h => h.HardwareType == HardwareType.Motherboard)
                .SelectMany(h => h.SubHardware).FirstOrDefault(h => h.HardwareType == HardwareType.SuperIO);
            if (cpu == null) return;

            using var timer = new PeriodicTimer(Interval);
            do
            {
                cpu.Update();
                superIo?.Update();
                var sensors = cpu.Sensors.Concat(superIo?.Sensors ?? []).Select(s => (s.SensorType, s.Name, s.Value));
                _latest = Extract(cpu.Name, sensors);
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);   // z. B. Treiber nicht startbar – HWiNFO bleibt als Quelle
        }
        finally
        {
            _latest = null;
            try { computer?.Close(); } catch { /* beim Beenden egal */ }
        }
    }

    /// <summary>
    /// Werte heraussuchen – rein rechnerisch, damit testbar. Intel: "CPU Package" und "Distance to TjMax";
    /// AMD: "Core (Tctl/Tdie)" und "Package". Der CPU-Lüfter ist bei fast allen Mainboards der erste Lüfter des
    /// Lüfterchips (Anschluss CPU_FAN); er zählt nur, wenn er dreht.
    /// </summary>
    internal static CpuThermalData? Extract(string cpuName, IEnumerable<(SensorType Type, string Name, float? Value)> sensors)
    {
        var list = sensors.ToList();
        double? Find(SensorType type, params string[] names)
        {
            foreach (var name in names)
                if (list.FirstOrDefault(s => s.Type == type && s.Name == name).Value is float v && !float.IsNaN(v))
                    return v;
            return null;
        }

        bool isAmd = cpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) || cpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);
        double? temp = Find(SensorType.Temperature, "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Core (Tdie)");
        double? coreMax = Find(SensorType.Temperature, "Core Max");
        double? power = Find(SensorType.Power, "CPU Package", "Package");
        if (temp == null && power == null) return null;

        // Intel: TjMax = Kerntemperatur + Abstand dazu (am heißesten Kern); AMD: aus dem Modellnamen
        double? tjMax = null;
        if (!isAmd)
        {
            var pairs = list.Where(s => s.Type == SensorType.Temperature && s.Name.EndsWith(" Distance to TjMax"))
                .Select(d => (Core: Find(SensorType.Temperature, d.Name[..^" Distance to TjMax".Length]), Distance: (double?)d.Value))
                .Where(p => p.Core != null && p.Distance != null)
                .ToList();
            if (pairs.Count > 0) tjMax = Math.Round(pairs.Average(p => p.Core!.Value + p.Distance!.Value));
        }
        else tjMax = HwInfoSensor.AmdTjMax(cpuName);

        double? fan = Find(SensorType.Fan, "Fan #1") is double rpm && rpm > 0 ? rpm : null;
        return new CpuThermalData("PawnIO", temp, coreMax, power, null, [], tjMax, isAmd, null, fan);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
