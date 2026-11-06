using PerfOverlay.Analysis;
using PerfOverlay.Sensors;

namespace PerfOverlay.Tests;

public class TimerSourceTests
{
    [Theory]
    [InlineData(10_000_000, 30, TimerKind.Tsc)]        // Normalfall: TSC, von Windows auf 10 MHz umgerechnet
    [InlineData(14_318_180, 900, TimerKind.Hpet)]      // useplatformclock gesetzt
    [InlineData(3_579_545, 1200, TimerKind.AcpiPm)]    // ACPI-Timer
    [InlineData(10_000_000, 850, TimerKind.Hpet)]      // umgerechnete Frequenz, aber teure Abfragen
    [InlineData(2_995_200_000, 25, TimerKind.Tsc)]     // ältere Windows-Versionen melden die TSC-Frequenz direkt
    public void Zeitgeber_wird_aus_Frequenz_und_Kosten_eingeordnet(long hz, double ns, TimerKind expected) =>
        Assert.Equal(expected, TimerSource.Classify(hz, ns));

    [Theory]
    [InlineData("identifier              {current}\r\nuseplatformclock        Yes\r\n", true)]
    [InlineData("Bezeichner              {current}\r\nuseplatformclock        Ja\r\n", true)]
    [InlineData("identifier              {current}\r\nuseplatformclock        No\r\n", false)]
    [InlineData("identifier              {current}\r\nnx                      OptIn\r\n", false)]   // Eintrag fehlt = nicht gesetzt
    public void Startoption_useplatformclock_wird_sprachunabhaengig_erkannt(string output, bool expected) =>
        Assert.Equal(expected, TimerSource.ParseUsePlatformClock(output));

    [Fact]
    public void Diese_Messung_auf_dem_Testrechner_ist_plausibel()
    {
        var t = TimerSource.Detect();
        Assert.True(t.FrequencyHz > 0);
        Assert.InRange(t.NsPerCall, 1, 100_000);
    }

    [Theory]
    [InlineData(true, "bcdedit /deletevalue useplatformclock")]
    [InlineData(false, "BIOS- und Windows-Updates")]
    [InlineData(null, "mit Admin-Rechten genauer bestimmbar")]
    public void Meldung_nennt_die_Ursache_soweit_bekannt(bool? usePlatformClock, string expected)
    {
        var text = BottleneckAnalyzer.TimerNote(new TimerInfo(TimerKind.Hpet, TimerSource.HpetHz, 900, usePlatformClock));
        Assert.Contains("HPET", text);
        Assert.Contains(expected, text);
    }

    [Fact]
    public void Langsamer_Zeitgeber_wird_nur_mit_laufendem_Spiel_als_Engpass_gemeldet()
    {
        var hpet = new TimerInfo(TimerKind.Hpet, TimerSource.HpetHz, 900, true);
        var t0 = new DateTime(2026, 11, 6, 20, 0, 0);
        Snapshot Snap(FrameStats? f) => new(t0,
            new CpuData(30, [30, 30], 4500, 5000),
            new MemoryData(12, 32, 40, 18, 55, 0),
            new DiskData(1, 0.1, 1),
            new GpuData { Name = "Test-GPU", Source = "NVML", Load = 95, TempC = 65 }, [],
            f, "aktiv", true, null, null)
        { Timer = hpet };

        var game = new FrameStats(1, "Spiel.exe", 120, 100, 8.3, 8.3, 10, 4, 4, 8.2, 0) { AppFps = 120 };
        var withGame = new BottleneckAnalyzer().Analyze(Snap(game));
        var note = Assert.Single(withGame.Notes, n => n.Key == "timer");
        Assert.Equal(NoteCategory.Bottleneck, note.Category);

        Assert.DoesNotContain(new BottleneckAnalyzer().Analyze(Snap(null)).Notes, n => n.Key == "timer");
    }
}
