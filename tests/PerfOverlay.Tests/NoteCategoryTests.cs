using System.IO;
using System.Text.RegularExpressions;
using PerfOverlay.Analysis;

namespace PerfOverlay.Tests;

public class NoteCategoryTests
{
    /// <summary>Sucht die Quelldatei des Analyzers vom Testausgabeordner aus nach oben.</summary>
    private static string AnalyzerSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "Analysis", "BottleneckAnalyzer.cs");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("Analysis/BottleneckAnalyzer.cs nicht gefunden");
    }

    [Fact]
    public void Jede_Meldung_des_Analyzers_ist_einer_Kategorie_zugeordnet()
    {
        // Alle Kennungen aus notes.Add(new("…", …)) – eine neue Meldung ohne Einordnung lässt diesen Test scheitern
        var keys = Regex.Matches(AnalyzerSource(), @"new\(""([a-z0-9-]+)""").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.True(keys.Count >= 25, $"nur {keys.Count} Kennungen gefunden – Suchmuster prüfen");
        var missing = keys.Where(k => !NoteCategories.All.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "ohne Kategorie: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("cpu-throttle", NoteCategory.Bottleneck)]
    [InlineData("gpu-thermal", NoteCategory.Bottleneck)]
    [InlineData("stutter", NoteCategory.Bottleneck)]
    [InlineData("vram", NoteCategory.Bottleneck)]
    [InlineData("hwinfo", NoteCategory.Hint)]
    [InlineData("cpu-foreign", NoteCategory.Hint)]
    [InlineData("net-internet", NoteCategory.Hint)]
    [InlineData("pagein", NoteCategory.Hint)]
    public void Engpass_und_Hinweise_werden_getrennt(string key, NoteCategory expected) =>
        Assert.Equal(expected, NoteCategories.Of(key));

    [Fact]
    public void Unbekannte_Kennung_gilt_als_Hinweis() =>
        Assert.Equal(NoteCategory.Hint, NoteCategories.Of("gibt-es-nicht"));

    [Fact]
    public void NoteLog_reicht_die_Kategorie_an_die_Anzeige_weiter()
    {
        var log = new NoteLog();
        var shown = log.Update([new Note("stutter", "Ruckler", Severity.Warn), new Note("hwinfo", "HWiNFO fehlt", Severity.Info)],
            new DateTime(2026, 10, 23, 20, 0, 0), 10);
        Assert.Equal(NoteCategory.Bottleneck, shown.Single(n => n.Text == "Ruckler").Category);
        Assert.Equal(NoteCategory.Hint, shown.Single(n => n.Text == "HWiNFO fehlt").Category);
    }
}
