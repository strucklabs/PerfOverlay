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
