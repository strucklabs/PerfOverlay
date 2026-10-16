using System.Windows;

namespace PerfOverlay;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Interner Probelauf für die AMD-Schnittstelle, siehe AdlxGpuSensor.TryCreate
        if (e.Args.Contains("--adlx-probe"))
        {
            Environment.Exit(Sensors.AdlxGpuSensor.RunProbe());
            return;
        }

        if (e.Args.Contains("--preview"))
        {
            try { await SelfTest.PreviewAsync(); }
            finally { Shutdown(); }
            return;
        }

        int selfTest = Array.IndexOf(e.Args, "--selftest");
        if (selfTest >= 0)
        {
            int seconds = selfTest + 1 < e.Args.Length && int.TryParse(e.Args[selfTest + 1], out int s) ? s : 5;
            try { await SelfTest.RunAsync(seconds); }
            finally { Shutdown(); }
            return;
        }

        if (!AcquireSingleInstance())
        {
            Shutdown();
            return;
        }

        new OverlayWindow().Show();
    }

    private const string MutexName = "PerfOverlay_SingleInstance";

    /// <summary>Übernimmt die Einzelinstanz-Sperre; false, wenn bereits eine andere Instanz läuft.</summary>
    public static bool AcquireSingleInstance()
    {
        if (_singleInstance != null) return true;
        var mutex = new Mutex(true, MutexName, out bool isNew);
        if (!isNew)
        {
            mutex.Dispose();
            return false;
        }
        _singleInstance = mutex;
        return true;
    }

    /// <summary>Vor einem Neustart (z. B. als Admin) freigeben, damit die neue Instanz starten darf.</summary>
    public static void ReleaseSingleInstance()
    {
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        _singleInstance = null;
    }
}
