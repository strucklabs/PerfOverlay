using PerfOverlay.Analysis;
using PerfOverlay.Sensors;

namespace PerfOverlay.Tests;

public class GameFilterTests
{
    [Theory]
    [InlineData("claude.exe")]
    [InlineData("ChatGPT.exe")]
    [InlineData("WindowsTerminal.exe")]
    [InlineData("pwsh")]
    [InlineData("chrome.exe")]
    [InlineData("EasyTune.exe")]
    public void KI_Apps_Terminals_Browser_und_Tools_sind_keine_Spiele(string app) =>
        Assert.True(GameFilter.IsNonGame(app));

    [Theory]
    [InlineData("Warframe.x64.exe")]
    [InlineData("DuneSandbox-Win64-Shipping.exe")]
    [InlineData("3DMark.exe")]
    public void Spiele_und_Benchmarks_werden_gemessen(string app) =>
        Assert.False(GameFilter.IsNonGame(app));

    private static Snapshot Snap(bool frameSourceRunning, string? ignoredApp) =>
        new(new DateTime(2026, 10, 23, 20, 0, 0),
            new CpuData(30, [30, 30], 4500, 5000),
            new MemoryData(12, 32, 40, 18, 55, 0),
            new DiskData(1, 0.1, 1),
            new GpuData { Name = "Test-GPU", Source = "NVML", Load = 99, TempC = 65 }, [],
            null, frameSourceRunning ? "aktiv" : "FPS-Messung braucht Admin-Rechte", frameSourceRunning, null, null)
        { IgnoredApp = ignoredApp };

    [Fact]
    public void Ohne_Spiel_im_Vordergrund_gibt_es_kein_Engpass_Urteil()
    {
        // Die KI-App im Vordergrund lastet die GPU aus – trotzdem kein "GPU-LIMITIERT"
        var v = new BottleneckAnalyzer().Analyze(Snap(frameSourceRunning: true, ignoredApp: "claude"));
        Assert.Equal("KEIN SPIEL", v.Title);
        Assert.Contains("claude", v.Detail);
        Assert.Null(v.Tip);
    }

    [Fact]
    public void Ohne_Frame_Messung_bleibt_die_Schaetzung_aus_der_Auslastung()
    {
        // Ohne Admin-Rechte läuft PresentMon nicht – dann ist die Schätzung das Beste, was es gibt
        var v = new BottleneckAnalyzer().Analyze(Snap(frameSourceRunning: false, ignoredApp: null));
        Assert.Equal("GPU-LIMITIERT", v.Title);
        Assert.Contains("geschätzt", v.Detail);
    }
}
