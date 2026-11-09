using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PerfOverlay.Native;

namespace PerfOverlay.Sensors;

/// <summary>Monitor, auf dem das Fenster im Vordergrund liegt: Auflösung, aktuelle und höchstmögliche Bildwiederholrate.</summary>
public sealed record DisplayInfo(int Width, int Height, int CurrentHz, int MaxHz)
{
    /// <summary>
    /// Läuft deutlich unter seiner Möglichkeit (z. B. 60 statt 144 Hz). Kleine Abstände wie 59/60 oder 165/170
    /// (übertaktete Modi) zählen nicht.
    /// </summary>
    public bool BelowMax => MaxHz - CurrentHz >= 15;

    public string Summary => $"{Width}×{Height}, {CurrentHz} Hz" + (MaxHz > CurrentHz ? $" (möglich {MaxHz} Hz)" : "");
}

public enum PowerPlanKind { Balanced, HighPerformance, Ultimate, PowerSaver, Other }

/// <param name="Plan">Energiesparplan laut Systemsteuerung.</param>
/// <param name="OverlayBestPerformance">Windows-Energiemodus "Beste Leistung" (Regler, v. a. bei Notebooks); null = nicht ermittelbar.</param>
/// <param name="OnBattery">Notebook läuft gerade mit Akku; null = kein Akku oder unbekannt.</param>
/// <param name="BatterySaver">Windows-Stromsparmodus ("Energiesparen") ist eingeschaltet.</param>
public sealed record PowerInfo(PowerPlanKind Plan, string PlanName, bool? OverlayBestPerformance, bool? OnBattery, bool BatterySaver)
{
    /// <summary>Drosselt Prozessor und Grafikkarte spürbar.</summary>
    public bool Saving => Plan == PowerPlanKind.PowerSaver || BatterySaver;

    public string Summary
    {
        get
        {
            var parts = new List<string> { PlanName };
            if (OverlayBestPerformance == true) parts.Add("Energiemodus beste Leistung");
            if (BatterySaver) parts.Add("Stromsparmodus an");
            if (OnBattery is bool b) parts.Add(b ? "Akkubetrieb" : "Netzbetrieb");
            return string.Join(", ", parts);
        }
    }
}

/// <summary>
/// Systemangaben, die die Leistung beeinflussen können, aber selten wechseln. PerfOverlay liest sie nur –
/// es ändert keine davon selbst.
/// </summary>
public sealed record SystemInfo
{
    public DisplayInfo? Display { get; init; }
    public PowerInfo? Power { get; init; }
    /// <summary>Speicher-Integrität (HVCI, Teil von VBS) läuft; null = nicht lesbar.</summary>
    public bool? MemoryIntegrity { get; init; }
    /// <summary>Hardwarebeschleunigte GPU-Planung; null = nicht gesetzt (Windows-Standard).</summary>
    public bool? Hags { get; init; }
    /// <summary>Resizable BAR aktiv (nur NVIDIA, aus der BAR1-Größe); null = unbekannt.</summary>
    public bool? ReBar { get; init; }

    public static string OnOff(bool? value, string unknown = "unbekannt") => value switch { true => "an", false => "aus", _ => unknown };

    /// <summary>Zeilen für Selbsttest und Sitzungskopf ("# Monitor: …"); die Auswertung liest dieselben Namen.</summary>
    public IEnumerable<(string Label, string Value)> Entries()
    {
        if (Display != null) yield return ("Monitor", Display.Summary);
        if (Power != null) yield return ("Energieplan", Power.Summary);
        yield return ("Speicher-Integrität", OnOff(MemoryIntegrity));
        yield return ("HAGS", OnOff(Hags, "Windows-Standard"));
        if (ReBar != null) yield return ("Resizable BAR", OnOff(ReBar));
    }
}

/// <summary>
/// Liest die Systemangaben: Monitor und Energie regelmäßig (können sich jederzeit ändern), Speicher-Integrität,
/// HAGS und Resizable BAR einmal beim Start (ändern sich nur mit einem Neustart).
/// </summary>
internal sealed class SystemInfoSensor
{
    private static readonly TimeSpan DisplayInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PowerInterval = TimeSpan.FromSeconds(30);

    // WMI braucht einige 100 ms – im Hintergrund
    private readonly Task<bool?> _memoryIntegrity = Task.Run(ReadMemoryIntegrity);
    private readonly bool? _hags = ReadHags();
    private readonly bool? _reBar;
    private readonly Dictionary<(string Device, int W, int H), int> _maxHz = new();

    private DisplayInfo? _display;
    private PowerInfo? _power;
    private DateTime _lastDisplay = DateTime.MinValue, _lastPower = DateTime.MinValue;

    /// <param name="bar1TotalMB">BAR1-Größe aus NVML (null ohne NVIDIA).</param>
    /// <param name="vramTotalMB">Grafikspeicher (für den Vergleich mit BAR1).</param>
    public SystemInfoSensor(double? bar1TotalMB, double? vramTotalMB) => _reBar = ReBarFromBar1(bar1TotalMB, vramTotalMB);

    public SystemInfo Read(DateTime now)
    {
        if (now - _lastDisplay >= DisplayInterval)
        {
            _display = Safe(ReadDisplay) ?? _display;
            _lastDisplay = now;
        }
        if (now - _lastPower >= PowerInterval)
        {
            _power = Safe(ReadPower) ?? _power;
            _lastPower = now;
        }
        return new SystemInfo
        {
            Display = _display,
            Power = _power,
            MemoryIntegrity = _memoryIntegrity.IsCompletedSuccessfully ? _memoryIntegrity.Result : null,
            Hags = _hags,
            ReBar = _reBar,
        };
    }

    private static T? Safe<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            return null;
        }
    }

    // ------------------------------------------------------------------ Einordnung (rein rechnerisch, testbar)

    /// <summary>
    /// Ohne Resizable BAR ist das BAR1-Fenster nur 256 MB groß, mit ReBAR so groß wie der Grafikspeicher
    /// (oder zumindest mehrere GB).
    /// </summary>
    public static bool? ReBarFromBar1(double? bar1TotalMB, double? vramTotalMB)
    {
        if (bar1TotalMB is not double bar1 || bar1 <= 0) return null;
        if (vramTotalMB is double vram && vram <= 512) return null;   // Karten mit kleinem Speicher: nicht aussagekräftig
        return bar1 > 1024;
    }

    /// <summary>HwSchMode: 2 = an, 1 = aus, fehlt = Windows-Standard (unbekannt).</summary>
    public static bool? HagsFromRegistry(object? value) => value is int mode ? mode == 2 ? true : mode == 1 ? false : null : null;

    /// <summary>Win32_DeviceGuard.SecurityServicesRunning: 2 = Speicher-Integrität (HVCI).</summary>
    public static bool MemoryIntegrityFromServices(uint[]? running) => running?.Contains(2u) == true;

    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid OverlayBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static PowerPlanKind PlanKind(Guid scheme) =>
        scheme == Balanced ? PowerPlanKind.Balanced
        : scheme == HighPerformance ? PowerPlanKind.HighPerformance
        : scheme == Ultimate ? PowerPlanKind.Ultimate
        : scheme == PowerSaver ? PowerPlanKind.PowerSaver
        : PowerPlanKind.Other;   // eigene Pläne oder solche von Herstellern (z. B. "AMD Ryzen Balanced")

    // ------------------------------------------------------------------ Monitor

    private DisplayInfo? ReadDisplay()
    {
        var monitor = Win32.MonitorFromWindow(Win32.GetForegroundWindow(), Win32.MONITOR_DEFAULTTOPRIMARY);
        if (Win32.MonitorDeviceName(monitor) is not { } device) return null;
        var current = NewDevMode();
        if (!EnumDisplaySettings(device, EnumCurrentSettings, ref current)) return null;
        int w = (int)current.dmPelsWidth, h = (int)current.dmPelsHeight, hz = (int)current.dmDisplayFrequency;

        // Alle Modi durchzugehen dauert einige ms – je Monitor und Auflösung nur einmal
        if (!_maxHz.TryGetValue((device, w, h), out int max))
        {
            max = hz;
            var mode = NewDevMode();
            for (int i = 0; EnumDisplaySettings(device, i, ref mode); i++)
                if (mode.dmPelsWidth == w && mode.dmPelsHeight == h) max = Math.Max(max, (int)mode.dmDisplayFrequency);
            _maxHz[(device, w, h)] = max;
        }
        return new DisplayInfo(w, h, hz, max);
    }

    private const int EnumCurrentSettings = -1;

    private static DEVMODE NewDevMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE mode);

    // ------------------------------------------------------------------ Energie

    private static PowerInfo? ReadPower()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr) != 0) return null;
        Guid scheme;
        try { scheme = Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }

        bool? overlay = null;
        try
        {
            // Energiemodus-Regler (Windows 10 ab 1709 bei Notebooks, Windows 11 überall); leer = "ausbalanciert"
            if (PowerGetEffectiveOverlayScheme(out Guid o) == 0) overlay = o == OverlayBestPerformance;
        }
        catch (EntryPointNotFoundException) { }

        bool? onBattery = null;
        bool saver = false;
        if (GetSystemPowerStatus(out var status))
        {
            bool hasBattery = status.BatteryFlag != 128 && status.BatteryFlag != 255;
            if (hasBattery && status.ACLineStatus != 255) onBattery = status.ACLineStatus == 0;
            saver = status.SystemStatusFlag == 1;
        }
        return new PowerInfo(PlanKind(scheme), PlanName(scheme), overlay, onBattery, saver);
    }

    /// <summary>Anzeigename in der Sprache von Windows, wie in der Systemsteuerung.</summary>
    private static string PlanName(Guid scheme)
    {
        uint size = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size) != 0 || size == 0)
            return PlanKind(scheme).ToString();
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            return PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
                ? Marshal.PtrToStringUni(buffer) ?? ""
                : PlanKind(scheme).ToString();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);
    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid scheme, IntPtr subGroup, IntPtr setting, IntPtr buffer, ref uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    // ------------------------------------------------------------------ einmalig

    private static bool? ReadHags()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            return HagsFromRegistry(key?.GetValue("HwSchMode"));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static bool? ReadMemoryIntegrity()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\DeviceGuard",
                "SELECT SecurityServicesRunning FROM Win32_DeviceGuard");
            foreach (ManagementObject o in searcher.Get())
                using (o) return MemoryIntegrityFromServices(o["SecurityServicesRunning"] as uint[]);
            return null;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            return null;   // Klasse fehlt (ältere Windows-Editionen) oder kein Zugriff
        }
    }
}
