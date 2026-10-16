using PerfOverlay.Sensors;

namespace PerfOverlay.Tests;

public class MemoryConfigTests
{
    [Theory]
    [InlineData("CMK32GX4M2E3200C16", 3200)]        // Corsair Vengeance LPX (Marcos Kit)
    [InlineData("CMH32GX5M2B6000C30", 6000)]        // Corsair DDR5
    [InlineData("F4-3600C16D-32GVKC", 3600)]        // G.Skill DDR4
    [InlineData("F5-6000J3038F16GX2-TZ5N", 6000)]   // G.Skill DDR5 ("J" statt "C")
    [InlineData("KF436C17BBK2/32", 3600)]           // Kingston Fury DDR4
    [InlineData("KF560C36BBEK2-32", 6000)]          // Kingston Fury DDR5
    [InlineData("HX432C16PB3K2/16", 3200)]          // Kingston HyperX
    [InlineData("BL2K16G36C16U4B", 3600)]           // Crucial Ballistix
    [InlineData("TF3D432G3200HC16FDC01", 3200)]     // TeamGroup
    public void Nenntakt_aus_Teilenummer(string partNumber, int expected) =>
        Assert.Equal(expected, MemoryConfig.RatedFromPartNumber(partNumber));

    [Theory]
    [InlineData("CT16G4DFD832A")]      // Crucial Standardmodul
    [InlineData("M378A2K43EB1-CWE")]   // Samsung OEM
    [InlineData("")]
    public void Unbekannte_Teilenummer_liefert_null(string partNumber) =>
        Assert.Null(MemoryConfig.RatedFromPartNumber(partNumber));

    [Fact]
    public void Fehlender_Takt_gilt_nicht_als_JEDEC_Standardtakt()
    {
        // Manche Systeme melden über WMI keinen Takt (0) – das darf keinen XMP-Hinweis auslösen
        var config = new MemoryConfig([new MemoryModule("DIMM1", "BANK 0", 16, 0, "", "", 0, null)],
            2, "DDR4", false, 0, null, ChannelMode.Single, false);
        Assert.False(config.RunsAtJedecBase);
        Assert.Equal("1×16 GB DDR4 Single", config.Summary);
    }
}
