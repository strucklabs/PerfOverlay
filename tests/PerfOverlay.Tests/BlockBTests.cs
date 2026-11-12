using System.IO;
using PerfOverlay.Analysis;
using PerfOverlay.Sensors;

namespace PerfOverlay.Tests;

/// <summary>Treiber-DPCs, Speicherzusage, VSync/G-Sync-Muster und Bufferbloat (Plan Engpass-Erweiterung, Block B).</summary>
public class BlockBTests
{
    private static readonly DateTime T0 = new(2026, 11, 9, 20, 0, 0);

    private static Snapshot Snap(DateTime time, FrameStats? frames = null, double dpc = 0.5, double commitGB = 18,
        int hz = 144, NetworkData? net = null) =>
        new(time,
            new CpuData(30, Enumerable.Repeat(30.0, 20).ToArray(), 4500, 5100) { DpcMaxPercent = dpc },
            new MemoryData(12, 32, 40, commitGB, 40, 0),
            new DiskData(1, 0.1, 1),
            new GpuData { Name = "Test-GPU", Source = "NVML", Load = 95, TempC = 65 },
            [],
            frames,
            frames != null ? "aktiv" : "bereit",
            true, null, null)
        {
            ForegroundPid = frames?.Pid ?? 0,
            HwInfoStatus = HwInfoStatus.Active,
            System = new SystemInfo { Display = new DisplayInfo(2560, 1440, hz, hz) },
            Network = net,
        };

    private static FrameStats Game(double fps = 100, int syncInterval = 0, (double, double)[]? frametimes = null, double[]? spikeAges = null) =>
        new(100, "Spiel.exe", fps, fps * 0.8, 1000 / fps, 1000 / fps, 1200 / fps, 1000 / fps * 0.5, 0.1, 1000 / fps * 0.95, 0.1)
        {
            AppFps = fps,
            SyncInterval = syncInterval,
            Frametimes = frametimes ?? [],
            SpikeAges = spikeAges ?? [],
        };

    private static List<Note> Run(Func<int, Snapshot> at, int ticks)
    {
        var a = new BottleneckAnalyzer();
        Verdict? v = null;
        for (int i = 0; i < ticks; i++) v = a.Analyze(at(i));
        return v!.Notes.ToList();
    }

    // ------------------------------------------------------------------ K4 DPC

    [Fact]
    public void Dauerhaft_hohe_DPC_Last_ergibt_einen_Hinweis()
    {
        var notes = Run(i => Snap(T0.AddSeconds(i), dpc: 26), 7);
        var n = Assert.Single(notes, n => n.Key == "dpc");
        Assert.Contains("26 %", n.Text);
        Assert.DoesNotContain(Run(i => Snap(T0.AddSeconds(i), dpc: 26), 3), n => n.Key == "dpc");   // noch nicht 5 s
        Assert.DoesNotContain(Run(i => Snap(T0.AddSeconds(i), dpc: 20), 10), n => n.Key == "dpc");   // unter der festen Schwelle 25 %
    }

    [Fact]
    public void DPC_Schwelle_richtet_sich_nach_der_ueblichen_Last()
    {
        // 6 min bei 18 % (Grundwert), dann 10 s bei 26 %: unter Grundwert + 10 → kein Hinweis
        Assert.DoesNotContain(Run(i => Snap(T0.AddSeconds(i), dpc: i < 360 ? 18 : 26), 370), n => n.Key == "dpc");
        // dasselbe mit 30 %: darüber → Hinweis
        Assert.Contains(Run(i => Snap(T0.AddSeconds(i), dpc: i < 360 ? 18 : 30), 370), n => n.Key == "dpc");
        // schon der Grundwert ist sehr hoch → Hinweis, obwohl nichts darüber hinausragt
        var high = Assert.Single(Run(i => Snap(T0.AddSeconds(i), dpc: 38), 310), n => n.Key == "dpc");
        Assert.Equal("Treiber belegen dauerhaft einen Kern (~38 % DPC)", high.Text);
    }

    [Fact]
    public void Windrose_Ruckler_werden_selten_den_Treibern_zugeordnet()
    {
        // Echte Sitzung: DPC im Spiel meist 12–16 %, ohne Zusammenhang mit den Rucklern. Mit fester Schwelle 20 %
        // landeten viele Ruckler bei "Treiber-Unterbrechung (DPC)".
        var rows = Windrose();
        var a = new BottleneckAnalyzer();
        int dpcStutters = 0, dpcHints = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            int prevSpikes = i > 0 ? rows[i - 1].Spikes : 0;
            var v = a.Analyze(Snap(T0.AddSeconds(rows[i].Second),
                Game(spikeAges: Enumerable.Repeat(1.0, prevSpikes).ToArray()), dpc: rows[i].Dpc));
            if (prevSpikes > 0 && v.Notes.Any(n => n.Key == "stutter" && n.Text.Contains("Treiber"))) dpcStutters++;
            if (v.Notes.Any(n => n.Key == "dpc")) dpcHints++;
        }
        Assert.InRange(dpcStutters, 0, 29);
        Assert.Equal(0, dpcHints);
    }

    private static List<(double Second, double Dpc, int Spikes)> Windrose()
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Daten", "windrose.csv"))
            .Select(l => l.TrimStart('﻿')).Where(l => !l.StartsWith('#')).ToList();
        var header = lines[0].Split(';');
        int sec = Array.IndexOf(header, "Sekunde"), dpc = Array.IndexOf(header, "DPC_max_Proz"), sp = Array.IndexOf(header, "Ruckler");
        var de = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        return lines.Skip(1).Select(l => l.Split(';'))
            .Where(c => c[dpc] != "")
            .Select(c => (double.Parse(c[sec], de), double.Parse(c[dpc], de), c[sp] == "" ? 0 : int.Parse(c[sp])))
            .ToList();
    }

    [Fact]
    public void Ruckler_bei_hoher_DPC_Last_wird_den_Treibern_zugeordnet()
    {
        // Ruckler 1 s alt, im selben Moment 25 % DPC auf einem Kern
        var notes = Run(i => Snap(T0.AddSeconds(i), Game(spikeAges: i == 4 ? [1.0] : []), dpc: i == 3 ? 25 : 0.5), 5);
        var stutter = Assert.Single(notes, n => n.Key == "stutter");
        Assert.Contains("Treiber-Unterbrechung (DPC)", stutter.Text);

        // Nach 6 min mit üblich 18 % reichen 25 % nicht mehr, 30 % schon
        string Cause(double spikeDpc) => Assert.Single(
            Run(i => Snap(T0.AddSeconds(i), Game(spikeAges: i == 364 ? [1.0] : []), dpc: i == 363 ? spikeDpc : 18), 365),
            n => n.Key == "stutter").Text;
        Assert.DoesNotContain("Treiber", Cause(25));
        Assert.Contains("Treiber-Unterbrechung (DPC)", Cause(30));
    }

    // ------------------------------------------------------------------ Ruckler bei vollem Hauptthread (Plan Probelauf CPU, E3)

    private static string StutterAt(double mainThread, double pagesIn = 0) => Assert.Single(
        Run(i => Snap(T0.AddSeconds(i), Game(spikeAges: i == 4 ? [1.0] : [])) with
        {
            MainThread = new ThreadLoad(i == 3 ? mainThread : 60, null),
            Memory = new MemoryData(12, 32, 40, 18, 40, i == 3 ? pagesIn : 0),
        }, 5),
        n => n.Key == "stutter").Text;

    [Fact]
    public void Ruckler_bei_vollem_Hauptthread_wird_benannt()
    {
        Assert.Contains("Hauptthread voll (Spielberechnung)", StutterAt(97));
        Assert.Contains("unklar", StutterAt(80));
    }

    [Fact]
    public void Nachladen_bleibt_vor_dem_Hauptthread()
    {
        var text = StutterAt(97, pagesIn: 1500);
        Assert.Contains("Nachladen vom Laufwerk", text);
        Assert.DoesNotContain("Hauptthread", text);
    }

    // ------------------------------------------------------------------ GPU-Temperaturdrosselung

    private static Snapshot Thermal(int i, bool flag) => Snap(T0.AddSeconds(i * 0.5)) with
    {
        Gpu = new GpuData { Name = "Test-GPU", Source = "NVML", Load = 95, TempC = 61,
            Throttle = flag ? PerfOverlay.Native.Nvml.ThrottleReasons.SwThermalSlowdown : PerfOverlay.Native.Nvml.ThrottleReasons.None },
    };

    [Fact]
    public void Einzelnes_Temperatur_Flag_der_GPU_ist_keine_Drosselung()
    {
        // Echte Sitzungen: einmal pro Sitzung ein Messtakt mit dem Flag, bei 60 °C und vollem Takt
        var a = new BottleneckAnalyzer();
        for (int i = 0; i < 20; i++)
            Assert.DoesNotContain(a.Analyze(Thermal(i, i == 10)).Notes, n => n.Key == "gpu-thermal");
    }

    [Fact]
    public void Anhaltende_Temperaturdrosselung_der_GPU_wird_gemeldet()
    {
        var notes = Run(i => Thermal(i, true), 8);   // 3,5 s
        Assert.Single(notes, n => n.Key == "gpu-thermal");
        Assert.DoesNotContain(Run(i => Thermal(i, true), 5), n => n.Key == "gpu-thermal");   // erst 2 s
    }

    // ------------------------------------------------------------------ K5 Commit

    [Fact]
    public void Speicherzusage_ueber_90_Prozent_ergibt_nach_5_Sekunden_einen_Hinweis()
    {
        var notes = Run(i => Snap(T0.AddSeconds(i), commitGB: 37), 7);   // 37 von 40 GB = 92,5 %
        var n = Assert.Single(notes, n => n.Key == "commit");
        Assert.Equal("Speicher fast voll (93 %) – Auslagerungsdatei vergrößern?", n.Text);   // ohne bekannten Verbraucher
        Assert.DoesNotContain(Run(i => Snap(T0.AddSeconds(i), commitGB: 30), 10), n => n.Key == "commit");
    }

    [Fact]
    public void Auslagerungsdatei_des_Testrechners_ist_lesbar()
    {
        var (size, used) = MemorySensor.PageFiles();
        Assert.NotNull(size);
        Assert.InRange(used ?? -1, 0, size!.Value + 0.01);
    }

    [Fact]
    public void Speicher_Hinweis_nennt_den_groessten_Verbraucher()
    {
        var hog = MemoryHogs.Largest([("Comfy Desktop", 20L << 30), ("python", 6L << 30), ("Comfy Desktop", 4L << 30),
                                      ("Memory Compression", 30L << 30), ("chrome", 3L << 30)]);
        Assert.Equal(new MemoryHog("Comfy Desktop", 24), hog);
        Assert.Equal("Speicher fast voll (91 %) – Comfy Desktop belegt 24 GB", BottleneckAnalyzer.CommitNote(91.2, hog));
        Assert.Null(MemoryHogs.Largest([("klein", 100L << 20)]));   // unter 0,5 GB: niemand Bestimmtes
        Assert.NotNull(MemoryHogs.Largest());                         // echte Prozessliste ist lesbar
    }

    // ------------------------------------------------------------------ K10 Sync

    [Fact]
    public void FPS_ohne_VSync_ueber_der_Bildwiederholrate()
    {
        Assert.Equal(BottleneckAnalyzer.SyncProblem.AboveRefresh, BottleneckAnalyzer.SyncProblemOf(Game(200), 144));
        Assert.Equal(BottleneckAnalyzer.SyncProblem.None, BottleneckAnalyzer.SyncProblemOf(Game(140), 144));
        Assert.Equal(BottleneckAnalyzer.SyncProblem.None, BottleneckAnalyzer.SyncProblemOf(Game(200, syncInterval: 1), 144));
        Assert.Equal(BottleneckAnalyzer.SyncProblem.None, BottleneckAnalyzer.SyncProblemOf(Game(200), 0));   // Hz unbekannt

        var notes = Run(i => Snap(T0.AddSeconds(i), Game(200)), 7);
        Assert.Contains("Limit 141 FPS", Assert.Single(notes, n => n.Key == "sync-above").Text);
    }

    [Fact]
    public void VSync_springt_zwischen_voller_und_halber_Rate()
    {
        // 144 Hz: abwechselnd 6,9 ms und 13,9 ms
        var halving = Enumerable.Range(0, 200).Select(i => (Age: 5 - i * 0.025, Ms: i % 3 == 0 ? 13.9 : 6.9)).ToArray();
        var steady = Enumerable.Range(0, 200).Select(i => (Age: 5 - i * 0.025, Ms: 6.94)).ToArray();
        Assert.Equal(BottleneckAnalyzer.SyncProblem.Halving, BottleneckAnalyzer.SyncProblemOf(Game(110, 1, halving), 144));
        Assert.Equal(BottleneckAnalyzer.SyncProblem.None, BottleneckAnalyzer.SyncProblemOf(Game(144, 1, steady), 144));

        var notes = Run(i => Snap(T0.AddSeconds(i), Game(110, 1, halving)), 5);
        Assert.Contains("144 ↔ 72 FPS", Assert.Single(notes, n => n.Key == "sync-halving").Text);
    }

    // ------------------------------------------------------------------ K12 Bufferbloat

    [Fact]
    public void Bufferbloat_ab_30_ms_Anstieg_bei_Datenverkehr()
    {
        var t = new BufferbloatTracker();
        for (int i = 0; i < 20; i++) t.Add(T0.AddSeconds(i), 15, loaded: false);
        for (int i = 20; i < 26; i++) t.Add(T0.AddSeconds(i), 80, loaded: true);
        Assert.Equal(65, t.Increase(T0.AddSeconds(26)));

        var ok = new BufferbloatTracker();
        for (int i = 0; i < 20; i++) ok.Add(T0.AddSeconds(i), 15, loaded: false);
        for (int i = 20; i < 26; i++) ok.Add(T0.AddSeconds(i), 25, loaded: true);   // +10 ms: gut eingestellter Router
        Assert.Null(ok.Increase(T0.AddSeconds(26)));

        var few = new BufferbloatTracker();
        for (int i = 0; i < 20; i++) few.Add(T0.AddSeconds(i), 15, loaded: false);
        few.Add(T0.AddSeconds(20), 200, loaded: true);   // ein einzelner Ausreißer reicht nicht
        Assert.Null(few.Increase(T0.AddSeconds(21)));
    }

    [Fact]
    public void Bufferbloat_erscheint_als_Hinweis()
    {
        NetworkData Net(int i) => i < 20
            ? new NetworkData(null, new PingStats(15, 1, 0), true, 0.1, 0.05) { InternetLastMs = 15 }
            : new NetworkData(null, new PingStats(40, 20, 0), true, 8, 0.5) { InternetLastMs = 90 };
        var notes = Run(i => Snap(T0.AddSeconds(i), net: Net(i)), 27);
        Assert.Contains("+75 ms", Assert.Single(notes, n => n.Key == "net-bufferbloat").Text);
        Assert.Equal(NoteCategory.Hint, NoteCategories.Of("net-bufferbloat"));
    }

    [Fact]
    public void Echte_DPC_Messung_liefert_einen_Wert()
    {
        using var pdh = new PerfOverlay.Native.PdhQuery();
        var cpu = new CpuSensor(pdh);
        pdh.Collect();
        Thread.Sleep(300);
        pdh.Collect();
        Assert.InRange(cpu.Read().DpcMaxPercent ?? -1, 0, 100);
    }
}
