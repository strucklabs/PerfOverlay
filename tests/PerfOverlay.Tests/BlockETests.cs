using PerfOverlay.Analysis;
using PerfOverlay.Sensors;

namespace PerfOverlay.Tests;

/// <summary>Tipps mit Systemeinstellungen (Plan Engpass-Erweiterung, Block E).</summary>
public class BlockETests
{
    private static readonly DateTime T0 = new(2026, 11, 9, 21, 0, 0);

    private static Snapshot Snap(SystemInfo sys, FrameStats f) =>
        new(T0,
            new CpuData(30, Enumerable.Repeat(30.0, 20).ToArray(), 4500, 5100),
            new MemoryData(12, 32, 40, 18, 55, 0),
            new DiskData(1, 0.1, 1),
            new GpuData { Name = "Test-GPU", Source = "NVML", Load = 60, TempC = 65 }, [],
            f, "aktiv", true, null, null)
        { ForegroundPid = f.Pid, HwInfoStatus = HwInfoStatus.Active, System = sys };

    private static FrameStats Frames(double fps, int sync) =>
        new(100, "Spiel.exe", fps, fps * 0.8, 1000 / fps, 1000 / fps, 1200 / fps, 5, 0.1, 6, 2) { AppFps = fps, SyncInterval = sync };

    private static Verdict Tip(SystemInfo sys, BottleneckKind kind, double fps = 120, int sync = 1)
    {
        var f = Frames(fps, sync);
        var v = new Verdict("X", "", Severity.Good, []) { Kind = kind, Tip = "Standard" };
        return BottleneckAnalyzer.WithSystemTip(v, Snap(sys, f), f);
    }

    private static readonly PowerInfo HighPerf = new(PowerPlanKind.HighPerformance, "Höchstleistung", null, null, false);

    [Fact]
    public void VSync_auf_zu_niedriger_Bildwiederholrate()
    {
        var sys = new SystemInfo { Display = new DisplayInfo(3440, 1440, 120, 144), Power = HighPerf };
        Assert.Equal("Monitor kann 144 Hz – in Windows umstellen", Tip(sys, BottleneckKind.Limit).Tip);
        Assert.Equal("Standard", Tip(sys, BottleneckKind.Limit, fps: 60).Tip);   // FPS-Limit im Spiel, nicht VSync an der Hz-Grenze
        Assert.Equal("Standard", Tip(sys, BottleneckKind.Gpu).Tip);
    }

    [Fact]
    public void FPS_Limit_ueber_der_Bildwiederholrate()
    {
        var sys = new SystemInfo { Display = new DisplayInfo(2560, 1440, 144, 144), Power = HighPerf };
        Assert.Equal("Mit G-Sync/FreeSync: Limit auf 141 FPS setzen", Tip(sys, BottleneckKind.Limit, fps: 200, sync: 0).Tip);
        Assert.Equal("Standard", Tip(sys, BottleneckKind.Limit, fps: 141, sync: 0).Tip);
    }

    [Fact]
    public void CPU_Limit_mit_ausbalanciertem_Energieplan()
    {
        var balanced = new SystemInfo { Power = new PowerInfo(PowerPlanKind.Balanced, "Ausbalanciert", null, null, false) };
        Assert.Equal("Mehr FPS: Energieplan „Höchstleistung“ testen", Tip(balanced, BottleneckKind.Cpu).Tip);
        // Windows-Energiemodus "Beste Leistung" gleicht das aus
        var best = new SystemInfo { Power = new PowerInfo(PowerPlanKind.Balanced, "Ausbalanciert", true, null, false) };
        Assert.Equal("Standard", Tip(best, BottleneckKind.Cpu).Tip);
        Assert.Equal("Standard", Tip(new SystemInfo { Power = HighPerf }, BottleneckKind.Cpu).Tip);
    }

    [Fact]
    public void Akku_und_Stromsparmodus_gehen_allem_vor()
    {
        var battery = new SystemInfo { Display = new DisplayInfo(1920, 1080, 60, 144), Power = new PowerInfo(PowerPlanKind.Balanced, "Ausbalanciert", null, true, false) };
        Assert.StartsWith("Netzteil anschließen", Tip(battery, BottleneckKind.Limit).Tip);
        var saver = new SystemInfo { Power = new PowerInfo(PowerPlanKind.PowerSaver, "Energiesparmodus", null, false, false) };
        Assert.StartsWith("Energiesparmodus ausschalten", Tip(saver, BottleneckKind.Gpu).Tip);
    }

    [Fact]
    public void Ganzer_Ablauf_setzt_den_Tipp_im_Urteil()
    {
        var sys = new SystemInfo { Power = new PowerInfo(PowerPlanKind.PowerSaver, "Energiesparmodus", null, false, false) };
        var v = new BottleneckAnalyzer().Analyze(Snap(sys, Frames(100, 0)));
        Assert.StartsWith("Energiesparmodus ausschalten", v.Tip);
    }
}
