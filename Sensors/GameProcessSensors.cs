using System.Runtime.InteropServices;

namespace PerfOverlay.Sensors;

/// <summary>Speicher des Spiels wächst ohne Plateau – mögliches Speicherleck.</summary>
public sealed record MemoryLeak(double FromGB, double ToGB, double Minutes);

/// <summary>
/// Erkennt ein Speicherleck am privaten Speicher des Spielprozesses: über mindestens 20 Minuten stetig
/// steigend (mindestens 0,5 GB) und auch in den letzten 5 Minuten noch steigend. Laden neuer Gebiete erzeugt
/// dagegen Stufen mit Plateau. Rein rechnerisch – damit testbar.
/// </summary>
public sealed class MemoryLeakTracker
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(60);
    public const double MinGrowthGB = 0.5;

    private readonly List<(DateTime Time, double GB)> _samples = new();
    private int _pid;

    public void Add(int pid, DateTime time, double gb)
    {
        if (pid != _pid)
        {
            _samples.Clear();
            _pid = pid;
        }
        if (_samples.Count > 0 && time - _samples[^1].Time < SampleInterval) return;
        _samples.Add((time, gb));
        _samples.RemoveAll(s => time - s.Time > Keep);
    }

    public MemoryLeak? Detect(DateTime now)
    {
        if (_samples.Count < 10 || now - _samples[0].Time < Window) return null;
        var window = _samples.Where(s => now - s.Time <= Window).ToList();
        var recent = _samples.Where(s => now - s.Time <= RecentWindow).ToList();
        if (window.Count < 10 || recent.Count < 5) return null;

        double slope = Slope(window);                 // GB pro Minute
        double recentSlope = Slope(recent);
        bool growing = slope * Window.TotalMinutes >= MinGrowthGB && window[^1].GB - window[0].GB >= MinGrowthGB;
        bool noPlateau = recentSlope >= slope * 0.5;
        if (!growing || !noPlateau) return null;
        return new MemoryLeak(_samples[0].GB, _samples[^1].GB, (_samples[^1].Time - _samples[0].Time).TotalMinutes);
    }

    /// <summary>Steigung der Ausgleichsgeraden in GB pro Minute.</summary>
    private static double Slope(List<(DateTime Time, double GB)> s)
    {
        var t0 = s[0].Time;
        double mx = s.Average(p => (p.Time - t0).TotalMinutes), my = s.Average(p => p.GB);
        double num = s.Sum(p => ((p.Time - t0).TotalMinutes - mx) * (p.GB - my));
        double den = s.Sum(p => Math.Pow((p.Time - t0).TotalMinutes - mx, 2));
        return den > 0 ? num / den : 0;
    }
}

/// <summary>Privater Speicher (Commit) des Spielprozesses – ohne Admin-Rechte, nur mit eingeschränktem Lesezugriff.</summary>
internal sealed class GameMemorySensor
{
    private readonly MemoryLeakTracker _tracker = new();

    public (double? GB, MemoryLeak? Leak) Read(int pid, DateTime now)
    {
        if (pid <= 0) return (null, null);
        double? gb = PrivateGB(pid);
        if (gb is double v) _tracker.Add(pid, now, v);
        return (gb, _tracker.Detect(now));
    }

    internal static double? PrivateGB(int pid)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var counters = new PROCESS_MEMORY_COUNTERS_EX { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>() };
            return GetProcessMemoryInfo(handle, ref counters, counters.cb) ? counters.PrivateUsage / (1024.0 * 1024 * 1024) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("psapi.dll")] private static extern bool GetProcessMemoryInfo(IntPtr process, ref PROCESS_MEMORY_COUNTERS_EX counters, uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Programm mit dem meisten zugesagten (privaten) Speicher; mehrere Prozesse gleichen Namens zusammengezählt.</summary>
public sealed record MemoryHog(string Name, double GB);

/// <summary>
/// Größter Speicherverbraucher – nur gefragt, wenn die Speicherzusage hoch ist. Meist läuft dann nebenbei ein großes
/// Programm (z. B. eine KI-Anwendung oder ein Browser mit vielen Tabs), und es zu schließen hilft mehr als eine
/// größere Auslagerungsdatei.
/// </summary>
internal static class MemoryHogs
{
    public static MemoryHog? Largest()
    {
        var all = System.Diagnostics.Process.GetProcesses();
        try
        {
            return Largest(all.Select(p => (p.ProcessName, SafePrivate(p))));
        }
        finally
        {
            foreach (var p in all) p.Dispose();
        }
    }

    /// <summary>Rein rechnerisch – damit testbar.</summary>
    public static MemoryHog? Largest(IEnumerable<(string Name, long PrivateBytes)> processes) =>
        processes
            .Where(p => p.Name is not ("Idle" or "System" or "Memory Compression" or "Registry"))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MemoryHog(g.First().Name, g.Sum(p => p.PrivateBytes) / (1024.0 * 1024 * 1024)))
            .OrderByDescending(h => h.GB)
            .FirstOrDefault(h => h.GB >= 0.5);

    private static long SafePrivate(System.Diagnostics.Process p)
    {
        try { return p.PrivateMemorySize64; }
        catch (InvalidOperationException) { return 0; }   // inzwischen beendet
    }
}

/// <summary>Grafikadapter laut DXGI; Luid im Format der PDH-Instanznamen ("0x00000000_0x0000C2B1").</summary>
public sealed record GpuAdapter(string Luid, string Name, bool Integrated);

/// <summary>Spiel rendert auf der Prozessorgrafik, obwohl eine Grafikkarte vorhanden ist.</summary>
public sealed record GpuMismatch(string Used, string Dedicated);

/// <summary>
/// Auf welchem Grafikadapter rendert das Spiel? Notebooks (und PCs mit Monitor am Mainboard) lassen Spiele
/// manchmal auf der Prozessorgrafik laufen. Maßgeblich ist die 3D-Last je Adapter, nicht der Monitoranschluss –
/// bei Notebooks mit Optimus läuft das Bild immer über die interne Grafik, gerendert wird aber auf der Grafikkarte.
/// </summary>
internal static class GpuAdapters
{
    private static readonly Lazy<IReadOnlyList<GpuAdapter>> _all = new(Enumerate);

    public static IReadOnlyList<GpuAdapter> All => _all.Value;

    /// <summary>
    /// Prozessorgrafik statt Grafikkarte: Die meiste 3D-Last des Spiels (mindestens 5 %) liegt auf einem
    /// internen Adapter, während ein dedizierter vorhanden ist.
    /// </summary>
    public static GpuMismatch? Check(IReadOnlyDictionary<string, double> gameLoadByLuid, IReadOnlyList<GpuAdapter> adapters)
    {
        var dedicated = adapters.FirstOrDefault(a => !a.Integrated);
        if (dedicated == null || gameLoadByLuid.Count == 0) return null;
        var (luid, load) = gameLoadByLuid.MaxBy(kv => kv.Value);
        if (load < 5) return null;
        var used = adapters.FirstOrDefault(a => string.Equals(a.Luid, luid, StringComparison.OrdinalIgnoreCase));
        return used is { Integrated: true } ? new GpuMismatch(used.Name, dedicated.Name) : null;
    }

    /// <summary>
    /// Intern = wenig eigener Grafikspeicher (Prozessorgrafik von Intel und AMD reserviert höchstens etwa 512 MB)
    /// oder Intel-Grafik ohne „Arc“ im Namen.
    /// </summary>
    public static bool IsIntegrated(string name, uint vendorId, ulong dedicatedBytes) =>
        dedicatedBytes < 1024UL * 1024 * 1024 || (vendorId == 0x8086 && !name.Contains("Arc", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<GpuAdapter> Enumerate()
    {
        var result = new List<GpuAdapter>();
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var factory) != 0) return result;
            try
            {
                for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0; i++)
                {
                    try
                    {
                        if (adapter.GetDesc1(out var d) != 0) continue;
                        if ((d.Flags & 2) != 0) continue;   // DXGI_ADAPTER_FLAG_SOFTWARE (Microsoft Basic Render Driver)
                        string luid = $"0x{d.LuidHigh:X8}_0x{d.LuidLow:X8}";
                        result.Add(new GpuAdapter(luid, d.Description.Trim(), IsIntegrated(d.Description, d.VendorId, d.DedicatedVideoMemory)));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or InvalidCastException)
        {
            ErrorLog.Write(ex);
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemoryRaw, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;

        public ulong DedicatedVideoMemory => DedicatedVideoMemoryRaw;
    }

    // Nur die benötigten Methoden werden aufgerufen; die übrigen stehen als Platzhalter für die richtige vtable-Reihenfolge.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);
}
