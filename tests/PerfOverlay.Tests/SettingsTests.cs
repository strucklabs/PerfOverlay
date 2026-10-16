using System.IO;

namespace PerfOverlay.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "PerfOverlayTests_" + Guid.NewGuid().ToString("N"), "einstellungen.json");

    public void Dispose()
    {
        string dir = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    [Fact]
    public void Gespeicherte_Werte_werden_wieder_geladen()
    {
        new Settings { Corner = 3, OffsetX = 40, OffsetY = 120, Monitor = @"\\.\DISPLAY2", Details = false, RecordSessions = false }.Save(_path);

        var loaded = Settings.Load(_path);

        Assert.Equal(3, loaded.Corner);
        Assert.Equal(40, loaded.OffsetX);
        Assert.Equal(120, loaded.OffsetY);
        Assert.Equal(@"\\.\DISPLAY2", loaded.Monitor);
        Assert.False(loaded.Details);
        Assert.False(loaded.RecordSessions);
    }

    [Fact]
    public void Fehlende_Datei_ergibt_Standardwerte()
    {
        var loaded = Settings.Load(_path);
        Assert.Equal(1, loaded.Corner);
        Assert.Null(loaded.OffsetX);
        Assert.True(loaded.Details);
        Assert.True(loaded.RecordSessions);
    }

    [Theory]
    [InlineData("{ kaputt")]
    [InlineData("")]
    [InlineData("{ \"Corner\": 17 }")]
    public void Beschaedigte_oder_unsinnige_Datei_ergibt_Standardwerte(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, content);
        Assert.Equal(1, Settings.Load(_path).Corner);
    }
}
