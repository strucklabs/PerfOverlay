using System.Windows;

namespace PerfOverlay;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int selfTest = Array.IndexOf(e.Args, "--selftest");
        if (selfTest >= 0)
        {
            int seconds = selfTest + 1 < e.Args.Length && int.TryParse(e.Args[selfTest + 1], out int s) ? s : 5;
            try { await SelfTest.RunAsync(seconds); }
            finally { Shutdown(); }
            return;
        }

        _singleInstance = new Mutex(true, "PerfOverlay_SingleInstance", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        new OverlayWindow().Show();
    }

    /// <summary>Vor einem Neustart (z. B. als Admin) freigeben, damit die neue Instanz starten darf.</summary>
    public static void ReleaseSingleInstance()
    {
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        _singleInstance = null;
    }
}
