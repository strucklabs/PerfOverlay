using PerfOverlay.Analysis;
using PerfOverlay.Sensors;

namespace PerfOverlay.Tests;

public class SystemInfoTests
{
    [Theory]
    [InlineData(60, 144, true)]
    [InlineData(120, 144, true)]
    [InlineData(144, 144, false)]
    [InlineData(59, 60, false)]      // 59,94/60 Hz
    [InlineData(165, 170, false)]    // übertakteter Modus
    public void Monitor_unter_seiner_Moeglichkeit(int current, int max, bool below) =>
        Assert.Equal(below, new DisplayInfo(2560, 1440, current, max).BelowMax);

    [Theory]
    [InlineData(256.0, 10240.0, false)]
    [InlineData(16384.0, 10240.0, true)]
    [InlineData(null, 10240.0, null)]
    [InlineData(256.0, 256.0, null)]   // kleine Karte: nicht aussagekräftig
    public void ReBar_aus_der_BAR1_Groesse(double? bar1, double? vram, bool? expected) =>
        Assert.Equal(expected, SystemInfoSensor.ReBarFromBar1(bar1, vram));

    [Fact]
    public void HAGS_und_Speicher_Integritaet_einordnen()
    {
        Assert.True(SystemInfoSensor.HagsFromRegistry(2));
        Assert.False(SystemInfoSensor.HagsFromRegistry(1));
        Assert.Null(SystemInfoSensor.HagsFromRegistry(null));
        Assert.True(SystemInfoSensor.MemoryIntegrityFromServices([1, 2]));
        Assert.False(SystemInfoSensor.MemoryIntegrityFromServices([1]));
        Assert.False(SystemInfoSensor.MemoryIntegrityFromServices(null));
    }

    [Fact]
    public void Energieplaene_werden_an_der_Kennung_erkannt()
    {
        Assert.Equal(PowerPlanKind.PowerSaver, SystemInfoSensor.PlanKind(SystemInfoSensor.PowerSaver));
        Assert.Equal(PowerPlanKind.HighPerformance, SystemInfoSensor.PlanKind(SystemInfoSensor.HighPerformance));
        Assert.Equal(PowerPlanKind.Other, SystemInfoSensor.PlanKind(Guid.NewGuid()));
    }

    private static List<Note> Notes(SystemInfo sys)
    {
        var notes = new List<Note>();
        BottleneckAnalyzer.AddSystemNotes(sys, notes);
        return notes;
    }

    [Fact]
    public void Gut_eingestelltes_System_erzeugt_keine_Meldung()
    {
        var sys = new SystemInfo
        {
            Display = new DisplayInfo(3440, 1440, 144, 144),
            Power = new PowerInfo(PowerPlanKind.Balanced, "Ausbalanciert", null, false, false),
            MemoryIntegrity = true,   // nur Info – nie eine Meldung
            Hags = false,
            ReBar = true,
        };
        Assert.Empty(Notes(sys));
    }

    [Fact]
    public void Problemfaelle_erzeugen_je_einen_Hinweis()
    {
        var sys = new SystemInfo
        {
            Display = new DisplayInfo(3440, 1440, 60, 144),
            Power = new PowerInfo(PowerPlanKind.PowerSaver, "Energiesparmodus", null, true, false),
            ReBar = false,
        };
        var keys = Notes(sys).Select(n => n.Key).ToList();
        Assert.Equal(["monitor-hz", "power-battery", "power-saving", "rebar"], keys);
        Assert.All(keys, k => Assert.Equal(NoteCategory.Hint, NoteCategories.Of(k)));
        Assert.Contains("60 statt 144 Hz", Notes(sys)[0].Text);
    }

    [Fact]
    public void Stromsparmodus_von_Windows_ersetzt_den_Plan_Hinweis()
    {
        var sys = new SystemInfo { Power = new PowerInfo(PowerPlanKind.PowerSaver, "Energiesparmodus", null, null, true) };
        var note = Assert.Single(Notes(sys));
        Assert.Contains("Stromsparmodus", note.Text);
    }

    [Fact]
    public void Kopfzeilen_nennen_alle_Angaben()
    {
        var sys = new SystemInfo
        {
            Display = new DisplayInfo(3440, 1440, 120, 144),
            Power = new PowerInfo(PowerPlanKind.HighPerformance, "Höchstleistung", null, null, false),
            MemoryIntegrity = false,
            ReBar = false,
        };
        var entries = sys.Entries().ToDictionary(e => e.Label, e => e.Value);
        Assert.Equal("3440×1440, 120 Hz (möglich 144 Hz)", entries["Monitor"]);
        Assert.Equal("Höchstleistung", entries["Energieplan"]);
        Assert.Equal("aus", entries["Speicher-Integrität"]);
        Assert.Equal("Windows-Standard", entries["HAGS"]);
        Assert.Equal("aus", entries["Resizable BAR"]);
    }

    [Fact]
    public void Echte_Messung_auf_dem_Testrechner_ist_plausibel()
    {
        var sys = new SystemInfoSensor(null, null).Read(DateTime.Now);
        Assert.NotNull(sys.Display);
        Assert.InRange(sys.Display!.CurrentHz, 1, 1000);
        Assert.True(sys.Display.MaxHz >= sys.Display.CurrentHz);
        Assert.NotNull(sys.Power);
        Assert.False(string.IsNullOrWhiteSpace(sys.Power!.PlanName));
    }
}
