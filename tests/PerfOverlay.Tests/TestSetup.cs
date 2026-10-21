using System.IO;
using System.Runtime.CompilerServices;

namespace PerfOverlay.Tests;

internal static class TestSetup
{
    /// <summary>
    /// Läuft vor allen Tests: Fehler, die Tests absichtlich auslösen (kaputte Einstellungsdatei …), landen in einem
    /// temporären Protokoll statt in Dokumente\PerfOverlay\fehler.log des Nutzers.
    /// </summary>
    [ModuleInitializer]
    internal static void RedirectErrorLog() =>
        ErrorLog.FilePath = Path.Combine(Path.GetTempPath(), "PerfOverlayTests_fehler.log");
}
