using System.IO;
using System.Text.Json;

namespace PerfOverlay;

/// <summary>
/// Dauerhafte Einstellungen in %APPDATA%\PerfOverlay\einstellungen.json.
/// Fehlt die Datei oder ist sie beschädigt, gelten die Standardwerte – das Overlay startet immer.
/// </summary>
public sealed class Settings
{
    /// <summary>Feste Ecke: 0 = oben links, 1 = oben rechts, 2 = unten rechts, 3 = unten links.</summary>
    public int Corner { get; set; } = 1;

    /// <summary>Gezogene Position als Abstand zur linken oberen Ecke des Arbeitsbereichs (physische Pixel); null = feste Ecke.</summary>
    public int? OffsetX { get; set; }
    public int? OffsetY { get; set; }

    /// <summary>Gerätename des Monitors, z. B. "\\.\DISPLAY2"; null = Hauptmonitor.</summary>
    public string? Monitor { get; set; }

    public bool Details { get; set; } = true;
    public bool RecordSessions { get; set; } = true;

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerfOverlay", "einstellungen.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new Settings();
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new Settings();
            if (settings.Corner is < 0 or > 3) settings.Corner = 1;
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write($"Einstellungen nicht lesbar, Standardwerte: {ex.Message}");
            return new Settings();
        }
    }

    /// <summary>Schreibt über eine temporäre Datei, damit ein Absturz mittendrin keine halbe Datei hinterlässt.</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write($"Einstellungen nicht gespeichert: {ex.Message}");
        }
    }
}
