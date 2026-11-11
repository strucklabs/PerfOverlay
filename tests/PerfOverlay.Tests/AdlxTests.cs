using System.IO;
using PerfOverlay.Analysis;
using PerfOverlay.Sensors;

namespace PerfOverlay.Tests;

public class AdlxTests
{
    [Fact]
    public void Eigene_Abfrage_hat_Vorrang_HWiNFO_fuellt_Luecken()
    {
        var own = new GpuThermalData(null, null, null, 1500);      // z. B. NVML: nur die Lüfterdrehzahl
        var hwInfo = new GpuThermalData(0.85, 90, 88, 1480);
        var merged = SensorHub.MergeThermal(own, hwInfo)!;
        Assert.Equal(1500, merged.FanRpm);
        Assert.Equal(0.85, merged.CoreVoltage);
        Assert.Equal(90, merged.HotspotC);
        Assert.Equal(88, merged.MemoryJunctionC);
    }

    [Fact]
    public void Ohne_eine_der_Quellen_bleibt_die_andere()
    {
        var adlx = new GpuThermalData(1.10, 95, 88, 1500);
        Assert.Same(adlx, SensorHub.MergeThermal(adlx, null));
        Assert.Same(adlx, SensorHub.MergeThermal(null, adlx));
        Assert.Null(SensorHub.MergeThermal(null, null));
    }

    [Fact]
    public void NVML_liefert_die_Luefterdrehzahl_ohne_HWiNFO()
    {
        using var nvml = NvmlGpuSensor.TryCreate();
        if (nvml == null) return;   // keine NVIDIA-Karte auf diesem Rechner
        var rpm = nvml.ReadFanRpm();
        // Karten im Lüfter-Stopp-Modus melden 0, sonst 300–5000 U/min; null nur bei sehr alten Treibern
        if (rpm is double r) Assert.InRange(r, 0, 6000);
    }

    [Fact]
    public void LibreHardwareMonitor_Sensoren_werden_nach_Typ_und_Name_gewaehlt()
    {
        var data = LhmGpuSensor.Extract([
            (LibreHardwareMonitor.Hardware.SensorType.Temperature, "GPU Core", 60f),
            (LibreHardwareMonitor.Hardware.SensorType.Voltage, "GPU Core Voltage", 0.85f),
            (LibreHardwareMonitor.Hardware.SensorType.Temperature, "GPU Hot Spot", 74.5f),
            (LibreHardwareMonitor.Hardware.SensorType.Temperature, "GPU Memory Junction", 86f),
            (LibreHardwareMonitor.Hardware.SensorType.Fan, "GPU Fan", 1650f),
        ])!;
        Assert.Equal(0.85, data.CoreVoltage!.Value, 3);
        Assert.Equal(74.5, data.HotspotC!.Value, 1);
        Assert.Equal(86, data.MemoryJunctionC);
        Assert.Equal(1650, data.FanRpm);
        // RTX 50: kein Hotspot über NVAPI – fehlt einfach, ohne Fehler
        var rtx50 = LhmGpuSensor.Extract([(LibreHardwareMonitor.Hardware.SensorType.Voltage, "GPU Core Voltage", 0.9f)])!;
        Assert.Null(rtx50.HotspotC);
        Assert.Null(LhmGpuSensor.Extract([]));
    }

    [Fact]
    public void LibreHardwareMonitor_liefert_auf_NVIDIA_Werte_ohne_HWiNFO()
    {
        if (NvmlGpuSensor.TryCreate() is not { } nvml) return;   // keine NVIDIA-Karte
        nvml.Dispose();
        using var lhm = new LhmGpuSensor();
        for (int i = 0; i < 50 && lhm.Latest == null; i++) Thread.Sleep(100);
        var d = Assert.IsType<GpuThermalData>(lhm.Latest);
        Assert.InRange(d.CoreVoltage ?? -1, 0.5, 1.3);
        Assert.InRange(d.MemoryJunctionC ?? -1, 10, 120);
    }

    [Fact]
    public void Ohne_AMD_Treiber_kein_Sensor_und_kein_Absturz()
    {
        // Auf Rechnern ohne Radeon-Treiber fehlt amdadlx64.dll – das muss still null ergeben
        if (File.Exists(Path.Combine(Environment.SystemDirectory, "amdadlx64.dll"))) return;
        Assert.Null(AdlxGpuSensor.TryCreate());
        Assert.Contains("nicht gefunden", AdlxGpuSensor.Status);
    }

    [Fact]
    public void AMD_Hotspot_ab_110_Grad_gilt_als_Drosselung()
    {
        var analyzer = new BottleneckAnalyzer();
        var t0 = new DateTime(2026, 10, 17, 20, 0, 0);
        var snap = new Snapshot(t0,
            new CpuData(30, [30, 30], 4500, 5000),
            new MemoryData(12, 32, 40, 18, 55, 0),
            new DiskData(1, 0.1, 1),
            new GpuData { Name = "Radeon", Source = "ADLX", Load = 99, TempC = 85 }, [],
            null, "aktiv", true, null, null)
        { GpuThermal = new GpuThermalData(1.1, 110, 90, 2000) };
        Assert.Contains(analyzer.Analyze(snap).Notes, n => n.Key == "gpu-thermal");
    }
}
