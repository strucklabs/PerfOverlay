using PerfOverlay.Analysis;
using PerfOverlay.Sensors;

namespace PerfOverlay.Tests;

/// <summary>Engpass-Urteile und Hinweise an nachgestellten echten Situationen.</summary>
public class BottleneckAnalyzerTests
{
    private static readonly DateTime T0 = new(2026, 10, 16, 20, 0, 0);

    /// <summary>Unauffälliger Basis-Snapshot; einzelne Werte werden pro Test überschrieben.</summary>
    private static Snapshot Snap(DateTime time, FrameStats? frames = null, ThreadLoad? mainThread = null,
        HwInfoStatus hwInfo = HwInfoStatus.Active) =>
        new(time,
            new CpuData(30, Enumerable.Repeat(30.0, 20).ToArray(), 4500, 5100),
            new MemoryData(12, 32, 40, 18, 55, 0),
            new DiskData(1, 0.1, 1),
            new GpuData { Name = "Test-GPU", Source = "NVML", Load = 60, TempC = 65 },
            [],
            frames,
            frames != null ? "aktiv" : "bereit",
            true,
            null,
            null)
        {
            MainThread = mainThread,
            ForegroundPid = frames?.Pid ?? 0,
            HwInfoStatus = hwInfo,
        };

    private static FrameStats Frames(double frametime, double cpuBusy, double cpuWait, double gpuBusy, double gpuWait) =>
        new(100, "Spiel.exe", 1000 / frametime, 1000 / (frametime * 1.2), frametime, frametime, frametime * 1.2,
            cpuBusy, cpuWait, gpuBusy, gpuWait)
        {
            AppFps = 1000 / frametime,
            SyncInterval = 0,
        };

    private static Verdict Run(Func<int, Snapshot> snapshotAt, int ticks = 3)
    {
        var analyzer = new BottleneckAnalyzer();
        Verdict? v = null;
        for (int i = 0; i < ticks; i++) v = analyzer.Analyze(snapshotAt(i));
        return v!;
    }

    [Fact]
    public void Warframe_60_FPS_Limit_mit_wenig_Hauptthread_Last_ist_FPS_Limit_nicht_CPU_Limit()
    {
        // Echte Werte: Frametime 16,6 ms, CPU-Busy 16,4 ms (Begrenzer wartet im CPU-Frame), GPU 9,6 ms, Hauptthread 28 %
        var frames = Frames(16.6, 16.4, 0.2, 9.6, 7.0);
        var v = Run(i => Snap(T0.AddSeconds(i * 0.5), frames, new ThreadLoad(28, null)));
        Assert.Equal(BottleneckKind.Limit, v.Kind);
        Assert.Equal("FPS-LIMIT", v.Title);
    }

    [Fact]
    public void Ausgelasteter_Hauptthread_bei_wartender_GPU_ist_CPU_Limit()
    {
        var frames = Frames(12, 11.4, 0.2, 7.5, 4.5);
        var v = Run(i => Snap(T0.AddSeconds(i * 0.5), frames, new ThreadLoad(96, "RenderThread")));
        Assert.Equal(BottleneckKind.Cpu, v.Kind);
        Assert.Contains("Hauptthread 96 %", v.Detail);
    }

    [Fact]
    public void Voll_ausgelastete_GPU_ist_GPU_Limit()
    {
        // Warframe ohne Limit: GPU-Busy = Frametime, GPU wartet 0 ms
        var frames = Frames(8.9, 8.6, 0.1, 8.9, 0);
        var v = Run(i => Snap(T0.AddSeconds(i * 0.5), frames, new ThreadLoad(40, null)));
        Assert.Equal(BottleneckKind.Gpu, v.Kind);
    }

    [Theory]
    [InlineData(HwInfoStatus.NotRunning, "HWiNFO starten oder PawnIO installieren")]
    [InlineData(HwInfoStatus.NoSharedMemory, "Shared Memory Support")]
    [InlineData(HwInfoStatus.Stopped, "12 h")]
    public void HWiNFO_Hinweis_passt_zum_Zustand(HwInfoStatus status, string expected)
    {
        var v = Run(i => Snap(T0.AddSeconds(i * 0.5), hwInfo: status), ticks: 1);
        var note = Assert.Single(v.Notes, n => n.Key == "hwinfo");
        Assert.Contains(expected, note.Text);
    }

    private static Snapshot WithCpu(Snapshot s, PawnIoState pawn, CpuThermalData? cpu) => s with { PawnIo = pawn, CpuThermal = cpu };

    [Fact]
    public void PawnIO_hat_Vorrang_und_wird_genannt()
    {
        var pawnCpu = new CpuThermalData("PawnIO", 55, 59, 55, null, [], 100, false);
        var v = Run(i => WithCpu(Snap(T0.AddSeconds(i * 0.5), hwInfo: HwInfoStatus.Active), PawnIoState.Active, pawnCpu), ticks: 1);
        Assert.Equal("CPU-Werte über PawnIO", Assert.Single(v.Notes, n => n.Key == "cpu-pawnio").Text);
        Assert.DoesNotContain(v.Notes, n => n.Key == "hwinfo");
    }

    [Fact]
    public void PawnIO_installiert_aber_ohne_Admin()
    {
        var v = Run(i => WithCpu(Snap(T0.AddSeconds(i * 0.5), hwInfo: HwInfoStatus.NotRunning), PawnIoState.NeedsAdmin, null), ticks: 1);
        Assert.Equal("CPU-Temperatur: als Admin starten (PawnIO)", Assert.Single(v.Notes, n => n.Key == "hwinfo").Text);
    }

    [Fact]
    public void Solange_PawnIO_noch_startet_kein_irrefuehrender_Hinweis()
    {
        var v = Run(i => WithCpu(Snap(T0.AddSeconds(i * 0.5), hwInfo: HwInfoStatus.NotRunning), PawnIoState.Active, null), ticks: 1);
        Assert.DoesNotContain(v.Notes, n => n.Key is "hwinfo" or "cpu-pawnio");
    }

    [Fact]
    public void LibreHardwareMonitor_CPU_Werte_werden_ausgewertet()
    {
        var T = LibreHardwareMonitor.Hardware.SensorType.Temperature;
        var intel = LhmCpuSensor.Extract("13th Gen Intel Core i5-13600KF", [
            (T, "CPU Package", 55f), (T, "Core Max", 59f),
            (T, "P-Core #2", 59f), (T, "P-Core #2 Distance to TjMax", 41f),
            (T, "P-Core #1", 50f), (T, "P-Core #1 Distance to TjMax", 50f),
            (LibreHardwareMonitor.Hardware.SensorType.Power, "CPU Package", 55.5f),
            (LibreHardwareMonitor.Hardware.SensorType.Fan, "Fan #1", 1298f),
            (LibreHardwareMonitor.Hardware.SensorType.Fan, "Fan #2", 0f)])!;
        Assert.Equal("PawnIO", intel.Source);
        Assert.Equal(55, intel.TempC);
        Assert.Equal(100, intel.TjMaxC);
        Assert.Equal(55.5, intel.PowerW!.Value, 1);
        Assert.Equal(1298, intel.FanRpm);
        Assert.False(intel.IsAmd);

        var amd = LhmCpuSensor.Extract("AMD Ryzen 7 7800X3D 8-Core Processor", [
            (T, "Core (Tctl/Tdie)", 81f), (LibreHardwareMonitor.Hardware.SensorType.Power, "Package", 88f),
            (LibreHardwareMonitor.Hardware.SensorType.Fan, "Fan #1", 0f)])!;
        Assert.True(amd.IsAmd);
        Assert.Equal(81, amd.TempC);
        Assert.Null(amd.FanRpm);   // Lüfter steht (0 U/min) – nicht als Wert zeigen
        Assert.Null(LhmCpuSensor.Extract("Intel", []));
    }

    [Fact]
    public void HWiNFO_Hinweis_nur_kurz_nach_Start_und_erneut_bei_Zustandswechsel()
    {
        var analyzer = new BottleneckAnalyzer();
        bool HasNote(double seconds, HwInfoStatus status) =>
            analyzer.Analyze(Snap(T0.AddSeconds(seconds), hwInfo: status)).Notes.Any(n => n.Key == "hwinfo");

        Assert.True(HasNote(0, HwInfoStatus.NotRunning));     // Start
        Assert.False(HasNote(20, HwInfoStatus.NotRunning));   // danach still, keine Dauermeldung
        Assert.False(HasNote(30, HwInfoStatus.Active));       // HWiNFO gestartet: kein Hinweis
        Assert.True(HasNote(40, HwInfoStatus.Stopped));       // beendet bzw. 12 h abgelaufen: erneut melden
    }
}
