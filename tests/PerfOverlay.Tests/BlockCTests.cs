using System.Diagnostics;
using PerfOverlay.Analysis;
using PerfOverlay.Sensors;

namespace PerfOverlay.Tests;

/// <summary>Speicherleck im Spiel und Prozessorgrafik statt Grafikkarte (Plan Engpass-Erweiterung, Block C).</summary>
public class BlockCTests
{
    private static readonly DateTime T0 = new(2026, 11, 9, 20, 0, 0);

    private static MemoryLeak? Track(Func<double, double> gbAtMinute, double minutes)
    {
        var t = new MemoryLeakTracker();
        for (double m = 0; m <= minutes; m += 5 / 60.0) t.Add(42, T0.AddMinutes(m), gbAtMinute(m));
        return t.Detect(T0.AddMinutes(minutes));
    }

    [Fact]
    public void Stetig_wachsender_Speicher_ist_ein_Leck()
    {
        var leak = Track(m => 3 + m * 0.05, 30);   // 3 GB + 50 MB pro Minute
        Assert.NotNull(leak);
        Assert.Equal(3.0, leak!.FromGB, 1);
        Assert.Equal(4.5, leak.ToGB, 1);
        Assert.Equal(30, leak.Minutes, 0);
    }

    [Fact]
    public void Stufen_mit_Plateau_sind_kein_Leck()
    {
        // Neues Gebiet geladen: +2 GB nach 10 min, danach konstant
        Assert.Null(Track(m => m < 10 ? 3 : 5, 30));
        // Anstieg in den ersten 20 min, in den letzten 10 min flach
        Assert.Null(Track(m => 3 + Math.Min(m, 20) * 0.05, 30));
    }

    [Fact]
    public void Zu_kurz_oder_zu_wenig_Wachstum_ist_kein_Leck()
    {
        Assert.Null(Track(m => 3 + m * 0.05, 15));    // erst 15 min
        Assert.Null(Track(m => 3 + m * 0.01, 40));    // nur 10 MB pro Minute
    }

    [Fact]
    public void Prozesswechsel_setzt_den_Verlauf_zurueck()
    {
        var t = new MemoryLeakTracker();
        for (double m = 0; m <= 30; m += 5 / 60.0) t.Add(42, T0.AddMinutes(m), 3 + m * 0.05);
        t.Add(43, T0.AddMinutes(30.1), 2);
        Assert.Null(t.Detect(T0.AddMinutes(30.1)));
    }

    [Fact]
    public void Eigener_Prozess_hat_messbaren_privaten_Speicher()
    {
        double? gb = GameMemorySensor.PrivateGB(Environment.ProcessId);
        Assert.InRange(gb ?? -1, 0.001, 64);
    }

    // ------------------------------------------------------------------ K8 Prozessorgrafik

    private static readonly GpuAdapter Igpu = new("0x00000000_0x0000A001", "Intel(R) UHD Graphics 770", true);
    private static readonly GpuAdapter Dgpu = new("0x00000000_0x0000B002", "NVIDIA GeForce RTX 3080", false);

    [Fact]
    public void Spiel_auf_der_Prozessorgrafik_wird_erkannt()
    {
        var mismatch = GpuAdapters.Check(new Dictionary<string, double> { ["0x00000000_0x0000a001"] = 80, ["0x00000000_0x0000B002"] = 1 }, [Igpu, Dgpu]);
        Assert.Equal(new GpuMismatch("Intel(R) UHD Graphics 770", "NVIDIA GeForce RTX 3080"), mismatch);
    }

    [Fact]
    public void Kein_Fehlalarm_auf_Grafikkarte_ohne_Grafikkarte_oder_ohne_Last()
    {
        // Rendert auf der Grafikkarte (Optimus-Notebook: Bild über die interne, Last auf der dedizierten)
        Assert.Null(GpuAdapters.Check(new Dictionary<string, double> { [Igpu.Luid] = 3, [Dgpu.Luid] = 90 }, [Igpu, Dgpu]));
        // Nur Prozessorgrafik vorhanden – dann gibt es nichts Besseres
        Assert.Null(GpuAdapters.Check(new Dictionary<string, double> { [Igpu.Luid] = 90 }, [Igpu]));
        // Kaum Last (z. B. Menü)
        Assert.Null(GpuAdapters.Check(new Dictionary<string, double> { [Igpu.Luid] = 2 }, [Igpu, Dgpu]));
    }

    [Theory]
    [InlineData("Intel(R) UHD Graphics 770", 0x8086u, 128UL * 1024 * 1024, true)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", 0x8086u, 16UL << 30, false)]
    [InlineData("AMD Radeon(TM) Graphics", 0x1002u, 512UL * 1024 * 1024, true)]
    [InlineData("NVIDIA GeForce RTX 3080", 0x10DEu, 10UL << 30, false)]
    public void Interne_Grafik_wird_eingeordnet(string name, uint vendor, ulong dedicated, bool integrated) =>
        Assert.Equal(integrated, GpuAdapters.IsIntegrated(name, vendor, dedicated));

    [Fact]
    public void Adapterliste_des_Testrechners_ist_lesbar()
    {
        var all = GpuAdapters.All;
        Assert.NotEmpty(all);
        Assert.All(all, a => Assert.Matches("^0x[0-9A-F]{8}_0x[0-9A-F]{8}$", a.Luid));
    }

    // ------------------------------------------------------------------ Meldungen

    private static Snapshot Snap(DateTime time, MemoryLeak? leak = null, GpuMismatch? gpu = null) =>
        new(time,
            new CpuData(30, Enumerable.Repeat(30.0, 20).ToArray(), 4500, 5100),
            new MemoryData(12, 32, 40, 18, 55, 0),
            new DiskData(1, 0.1, 1),
            new GpuData { Name = "Test-GPU", Source = "NVML", Load = 95, TempC = 65 }, [],
            new FrameStats(100, "Spiel.exe", 100, 80, 10, 10, 12, 5, 0.1, 9.5, 0.1) { AppFps = 100 },
            "aktiv", true, null, null)
        {
            ForegroundPid = 100,
            HwInfoStatus = HwInfoStatus.Active,
            GameMemoryLeak = leak,
            GameGpuMismatch = gpu,
        };

    [Fact]
    public void Meldungen_fuer_Leck_und_Prozessorgrafik()
    {
        var a = new BottleneckAnalyzer();
        Verdict? v = null;
        for (int i = 0; i < 7; i++)
            v = a.Analyze(Snap(T0.AddSeconds(i), new MemoryLeak(3, 4.5, 30), new GpuMismatch("Intel UHD", "RTX 3080")));
        var leak = Assert.Single(v!.Notes, n => n.Key == "game-memleak");
        Assert.Contains("3,0 → 4,5 GB in 30 min", leak.Text);
        Assert.Equal(NoteCategory.Bottleneck, NoteCategories.Of(Assert.Single(v.Notes, n => n.Key == "gpu-integrated").Key));

        var b = new BottleneckAnalyzer();
        for (int i = 0; i < 7; i++) v = b.Analyze(Snap(T0.AddSeconds(i)));
        Assert.DoesNotContain(v!.Notes, n => n.Key is "game-memleak" or "gpu-integrated");
    }
}
