using System.IO;

namespace PerfOverlay.Analysis;

/// <summary>
/// Anwendungen, die Bilder über die Grafikkarte ausgeben, aber keine Spiele sind. PresentMon misst sie trotzdem,
/// wenn sie im Vordergrund sind – das Overlay bewertet dann keinen Engpass und die Aufzeichnung startet keine Sitzung.
/// </summary>
public static class GameFilter
{
    private static readonly HashSet<string> NonGames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Browser
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "iexplore", "msedgewebview2",
        // Launcher und Chat
        "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "EADesktop", "Origin",
        "GalaxyClient", "UbisoftConnect", "upc", "RiotClientUx", "Discord", "Teams", "ms-teams", "slack", "Telegram", "WhatsApp",
        // KI-Assistenten (Desktop-Apps; im Browser sind sie über die Browser abgedeckt)
        "claude", "ChatGPT", "Copilot", "Microsoft.Copilot", "M365Copilot", "Perplexity", "Gemini", "LM Studio", "Ollama app",
        // Terminals und Entwicklungsumgebungen – darin laufen z. B. Claude Code oder Build-Werkzeuge
        "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "Code", "Cursor", "devenv", "rider64",
        // Desktop-Apps, Medien, System
        "Spotify", "explorer", "dwm", "ApplicationFrameHost", "SearchHost", "StartMenuExperienceHost",
        "TextInputHost", "ShellExperienceHost", "vlc", "mpc-hc64", "obs64", "PerfOverlay", "PresentMon",
        "SystemSettings", "Taskmgr", "mmc",
        // Hardware- und Tuning-Tools: zeichnen ihre Oberfläche oft über die GPU und laufen beim Einstellen
        // minutenlang im Vordergrund. Benchmarks (3DMark, FurMark …) bleiben bewusst messbar.
        "EasyTune", "GCC", "GigabyteControlCenter", "AppCenter", "RGBFusion",
        "MSIAfterburner", "RTSS", "MSI Center", "MSI.Center",
        "ArmouryCrate", "AISuite3", "GPUTweakIII", "GPU Tweak III",
        "iCUE", "NZXT CAM", "SignalRgb", "L-Connect 3", "FanControl",
        "HWiNFO64", "HWiNFO32", "aida64", "cpuz", "cpuz_x64", "GPU-Z", "OCCT", "ThrottleStop",
        "RadeonSoftware", "NVIDIA App", "NVIDIA Overlay", "NVIDIA GeForce Experience", "nvcplui",
    };

    /// <param name="app">Prozessname mit oder ohne ".exe".</param>
    public static bool IsNonGame(string app) => NonGames.Contains(Path.GetFileNameWithoutExtension(app));
}
