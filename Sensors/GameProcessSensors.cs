using System.Runtime.InteropServices;

namespace PerfOverlay.Sensors;

/// <summary>
/// Speicher des Spiels wächst stetig – mögliches Speicherleck.
/// </summary>
/// <param name="RateMBPerMin">Wachstum in den ruhigen Abschnitten (MB pro Minute).</param>
/// <param name="SinceMinutes">Seit wann es wächst (Beginn des ersten steigenden Abschnitts im Fenster).</param>
/// <param name="HoursToFull">Bei diesem Tempo ist die Speicherzusage in so vielen Stunden ausgeschöpft (nur unter 4 h).</param>
public sealed record MemoryLeak(double RateMBPerMin, double SinceMinutes, double? HoursToFull);

/// <summary>
/// Erkennt ein Speicherleck statistisch (Plan Probelauf Windrose, W1). Spiele laden und verwerfen Gebiete – der Speicher
/// springt dabei um Gigabytes und schwankt zudem um ±0,5 GB. Ein Leck zeigt sich dagegen als stetiges Wachstum
/// zwischen den Sprüngen. Verfahren:
/// 1. Minuten-Mediane (glätten das Rauschen),
/// 2. Sprünge (Minutenänderung über max(300 MB, 6 × Median der Minutenänderungen)) und Lücken trennen Abschnitte,
/// 3. in ruhigen Abschnitten ab 5 Minuten die Theil-Sen-Steigung (Median aller Paarsteigungen, robust gegen Ausreißer),
///    nach Dauer gewichtet,
/// 4. Leck, wenn in den letzten 40 Minuten mindestens 20 Minuten ruhig sind, die Steigung darin mindestens 25 MB/min
///    beträgt und mindestens 85 % der ruhigen Zeit mit 15 MB/min oder mehr steigt – und das schon 10 Minuten vorher galt.
/// An einer echten Windrose-Sitzung (56 min, viel Laden und Freigeben) kein Fehlalarm; künstliche Lecks ab 20 MB/min
/// werden etwa 25 Minuten nach Beginn erkannt. Rein rechnerisch – damit testbar; dasselbe Verfahren steckt in der Auswertung.
/// </summary>
public sealed class MemoryLeakTracker
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(55);
    public const int WindowMinutes = 40, PersistMinutes = 10, MinSegmentPoints = 5, MinQuietMinutes = 20;
    public const double MinRateMB = 25, RisingRateMB = 15, RisingShare = 0.85, MinJumpGB = 0.3, JumpFactor = 6;

    private readonly List<(DateTime Time, double GB)> _samples = new();
    private int _pid;
    private DateTime _start;
    private int _evaluatedMinute = -1;
    private MemoryLeak? _result;

    public void Add(int pid, DateTime time, double gb)
    {
        if (pid != _pid)
        {
            _samples.Clear();
            _pid = pid;
            _start = time;
            _evaluatedMinute = -1;
            _result = null;
        }
        if (_samples.Count > 0 && time - _samples[^1].Time < SampleInterval) return;
        _samples.Add((time, gb));
        _samples.RemoveAll(s => time - s.Time > Keep);
    }

    /// <param name="freeCommitGB">Noch freie Speicherzusage (für die Restzeit); null = unbekannt.</param>
    public MemoryLeak? Detect(DateTime now, double? freeCommitGB = null)
    {
        if (_samples.Count == 0) return null;
        int minute = (int)Math.Floor((now - _start).TotalMinutes);
        if (minute != _evaluatedMinute)   // höchstens einmal pro Minute rechnen
        {
            _evaluatedMinute = minute;
            var medians = MinuteMedians(_samples.Select(s => ((s.Time - _start).TotalMinutes, s.GB)));
            var current = Evaluate(medians, minute);
            var earlier = Evaluate(medians, minute - PersistMinutes);
            _result = current is { IsLeak: true } c && earlier is { IsLeak: true }
                ? new MemoryLeak(c.RateMB, minute - c.RisingSince, null)
                : null;
        }
        if (_result == null) return null;
        double? hours = freeCommitGB is double free && _result.RateMBPerMin > 0 ? free * 1024 / _result.RateMBPerMin / 60 : null;
        return _result with { HoursToFull = hours is < 4 ? hours : null };
    }

    /// <summary>Median je ganzer Minute (nur Minuten mit mindestens 6 Werten).</summary>
    public static List<(double Minute, double GB)> MinuteMedians(IEnumerable<(double Minute, double GB)> samples) =>
        samples.GroupBy(s => Math.Floor(s.Minute))
            .Where(g => g.Count() >= 6)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, Median(g.Select(s => s.GB).ToList())))
            .ToList();

    public readonly record struct Evaluation(bool IsLeak, double RateMB, double RisingShare, double QuietMinutes, double RisingSince);

    /// <summary>Urteil über das Fenster [end − 40 min, end) – null, wenn zu wenig ruhige Zeit für eine Aussage vorliegt.</summary>
    public static Evaluation? Evaluate(IReadOnlyList<(double Minute, double GB)> medians, int end)
    {
        var m = medians.Where(x => x.Minute >= end - WindowMinutes && x.Minute < end).ToList();
        if (m.Count < 2) return null;
        var diffs = m.Zip(m.Skip(1), (a, b) => Math.Abs(b.GB - a.GB)).ToList();
        double jump = Math.Max(MinJumpGB, JumpFactor * Median(diffs));

        var segments = new List<List<(double Minute, double GB)>> { new() { m[0] } };
        for (int i = 1; i < m.Count; i++)
        {
            bool split = Math.Abs(m[i].GB - m[i - 1].GB) > jump || m[i].Minute - m[i - 1].Minute > 2;
            if (split) segments.Add(new() { m[i] });
            else segments[^1].Add(m[i]);
        }
        var quiet = segments.Where(s => s.Count >= MinSegmentPoints).ToList();
        double total = quiet.Sum(s => s[^1].Minute - s[0].Minute);
        if (total < MinQuietMinutes) return null;

        var weighted = quiet.Select(s => (Rate: TheilSen(s) * 1024, Duration: s[^1].Minute - s[0].Minute, Start: s[0].Minute)).ToList();
        double rate = weighted.Sum(w => w.Rate * w.Duration) / total;
        double rising = weighted.Where(w => w.Rate >= RisingRateMB).Sum(w => w.Duration) / total;
        double since = weighted.Where(w => w.Rate >= RisingRateMB).Select(w => w.Start).DefaultIfEmpty(end).Min();
        return new Evaluation(rate >= MinRateMB && rising >= RisingShare, rate, rising, total, since);
    }

    /// <summary>Theil-Sen-Steigung: Median aller Paarsteigungen (GB pro Minute).</summary>
    private static double TheilSen(List<(double Minute, double GB)> s)
    {
        var slopes = new List<double>();
        for (int i = 0; i < s.Count; i++)
            for (int j = i + 1; j < s.Count; j++)
                if (s[j].Minute > s[i].Minute) slopes.Add((s[j].GB - s[i].GB) / (s[j].Minute - s[i].Minute));
        return slopes.Count > 0 ? Median(slopes) : 0;
    }

    private static double Median(List<double> v)
    {
        if (v.Count == 0) return 0;
        v.Sort();
        int mid = v.Count / 2;
        return v.Count % 2 == 1 ? v[mid] : (v[mid - 1] + v[mid]) / 2;
    }
}

/// <summary>Privater Speicher (Commit) des Spielprozesses – ohne Admin-Rechte, nur mit eingeschränktem Lesezugriff.</summary>
internal sealed class GameMemorySensor
{
    private readonly MemoryLeakTracker _tracker = new();

    public (double? GB, MemoryLeak? Leak) Read(int pid, DateTime now, double? freeCommitGB = null)
    {
        if (pid <= 0) return (null, null);
        double? gb = PrivateGB(pid);
        if (gb is double v) _tracker.Add(pid, now, v);
        return (gb, _tracker.Detect(now, freeCommitGB));
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
