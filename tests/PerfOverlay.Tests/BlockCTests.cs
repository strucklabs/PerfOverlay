using System.IO;
using System.Diagnostics;
using PerfOverlay.Analysis;
using PerfOverlay.Sensors;

namespace PerfOverlay.Tests;

/// <summary>Speicherleck im Spiel und Prozessorgrafik statt Grafikkarte (Plan Engpass-Erweiterung, Block C).</summary>
public class BlockCTests
{
    private static readonly DateTime T0 = new(2026, 11, 9, 20, 0, 0);

    /// <summary>Echte Messdaten: Windrose-Probelauf (56 min, Spiel-RAM pendelt durch Laden/Freigeben zwischen 6 und 18 GB).</summary>
    internal static List<(double Second, double GB)> WindroseRam()
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Daten", "windrose.csv"))
            .Where(l => !l.StartsWith('#')).ToList();
        var header = lines[0].Split(';');
        int sec = Array.IndexOf(header, "Sekunde"), ram = Array.IndexOf(header, "Spiel_RAM_GB");
        var de = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        return lines.Skip(1).Select(l => l.Split(';'))
            .Where(c => c[ram] != "")
            .Select(c => (double.Parse(c[sec], de), double.Parse(c[ram], de)))
            .ToList();
    }

    /// <summary>Spielt die Sekundenwerte (plus optionalem künstlichem Leck ab Minute 15) ein und prüft jede Minute.</summary>
    private static List<(int Minute, MemoryLeak Leak)> Replay(double leakMBPerMin)
    {
        var tracker = new MemoryLeakTracker();
        var alarms = new List<(int, MemoryLeak)>();
        int lastMinute = -1;
        foreach (var (second, gb) in WindroseRam())
        {
            double minute = second / 60;
            double value = gb + Math.Max(0, minute - 15) * leakMBPerMin / 1024;
            var time = T0.AddSeconds(second);
            tracker.Add(42, time, value);
            if ((int)minute != lastMinute)
            {
                lastMinute = (int)minute;
                if (tracker.Detect(time, freeCommitGB: 10) is { } leak) alarms.Add((lastMinute, leak));
            }
        }
        return alarms;
    }

    [Fact]
    public void Windrose_Laden_und_Freigeben_ist_kein_Leck()
    {
        // Vorher: Fehlalarm "6,5 → 17,4 GB" in Minute 24–31, obwohl der Speicher längst flach war
        Assert.Empty(Replay(0));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(60)]
    public void Kuenstliches_Leck_in_echten_Daten_wird_erkannt(double rate)
    {
        var alarms = Replay(rate);
        Assert.NotEmpty(alarms);
        Assert.True(alarms[0].Minute <= 15 + 30, $"erst in Minute {alarms[0].Minute} erkannt");
        // gemessene Rate passt zur eingebauten (±25 %; das echte Grundrauschen kommt hinzu)
        Assert.InRange(alarms[0].Leak.RateMBPerMin, rate * 0.75, rate * 1.25 + 15);
    }

    [Fact]
    public void Stetig_wachsender_Speicher_ohne_Spruenge_ist_ein_Leck()
    {
        var t = new MemoryLeakTracker();
        MemoryLeak? leak = null;
        for (double m = 0; m <= 45; m += 5 / 60.0)
        {
            t.Add(42, T0.AddMinutes(m), 3 + m * 0.05);   // 3 GB + 51 MB pro Minute
            leak = t.Detect(T0.AddMinutes(m), freeCommitGB: 2);
        }
        Assert.NotNull(leak);
        Assert.InRange(leak!.RateMBPerMin, 45, 56);
        Assert.InRange(leak.HoursToFull ?? -1, 0.5, 0.8);   // 2 GB frei / 51 MB/min ≈ 40 min
    }

    [Fact]
    public void Stufen_mit_Plateau_sind_kein_Leck()
    {
        var t = new MemoryLeakTracker();
        MemoryLeak? leak = null;
        for (double m = 0; m <= 60; m += 5 / 60.0)
        {
            double gb = 3 + Math.Floor(m / 12) * 2 + (m * 7 % 1 - 0.5) * 0.6;   // alle 12 min +2 GB, dazwischen Rauschen
            t.Add(42, T0.AddMinutes(m), gb);
            leak ??= t.Detect(T0.AddMinutes(m));
        }
        Assert.Null(leak);
    }

    [Fact]
    public void Prozesswechsel_setzt_den_Verlauf_zurueck()
    {
        var t = new MemoryLeakTracker();
        for (double m = 0; m <= 45; m += 5 / 60.0) t.Add(42, T0.AddMinutes(m), 3 + m * 0.05);
        t.Add(43, T0.AddMinutes(45.1), 2);
        Assert.Null(t.Detect(T0.AddMinutes(45.1)));
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
            v = a.Analyze(Snap(T0.AddSeconds(i), new MemoryLeak(40, 35, 2), new GpuMismatch("Intel UHD", "RTX 3080")));
        var leak = Assert.Single(v!.Notes, n => n.Key == "game-memleak");
        Assert.Equal("Spielspeicher +40 MB/min seit 35 min, voll in ~2 h", leak.Text);
        Assert.Equal(NoteCategory.Bottleneck, NoteCategories.Of(Assert.Single(v.Notes, n => n.Key == "gpu-integrated").Key));

        var b = new BottleneckAnalyzer();
        for (int i = 0; i < 7; i++) v = b.Analyze(Snap(T0.AddSeconds(i)));
        Assert.DoesNotContain(v!.Notes, n => n.Key is "game-memleak" or "gpu-integrated");
    }
}
