using PerfOverlay.Native;

namespace PerfOverlay.Tests;

/// <summary>Echte Windows-Aufrufe: Monitor über seinen Gerätenamen wiederfinden (für gespeicherte Einstellungen).</summary>
public class MonitorTests
{
    [Fact]
    public void Hauptmonitor_wird_ueber_seinen_Geraetenamen_wiedergefunden()
    {
        IntPtr primary = Win32.PrimaryMonitor;
        string? name = Win32.MonitorDeviceName(primary);

        Assert.StartsWith(@"\\.\DISPLAY", name);
        Assert.Equal(primary, Win32.FindMonitor(name));
    }

    [Fact]
    public void Nicht_angeschlossener_Monitor_wird_nicht_gefunden()
    {
        Assert.Equal(IntPtr.Zero, Win32.FindMonitor(@"\\.\DISPLAY99"));
        Assert.Equal(IntPtr.Zero, Win32.FindMonitor(null));
    }
}
