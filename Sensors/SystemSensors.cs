using PerfOverlay.Native;

namespace PerfOverlay.Sensors;

internal sealed class MemorySensor
{
    private readonly PdhQuery _pdh;
    private readonly IntPtr _pagesInput;

    public MemorySensor(PdhQuery pdh)
    {
        _pdh = pdh;
        _pagesInput = pdh.Add(@"\Memory\Pages Input/sec");
    }

    public MemoryData Read()
    {
        var status = new Win32.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
        Win32.GlobalMemoryStatusEx(ref status);
        const double GB = 1024.0 * 1024 * 1024;
        double total = status.ullTotalPhys / GB;
        double used = (status.ullTotalPhys - status.ullAvailPhys) / GB;
        double commitLimit = status.ullTotalPageFile / GB;
        double commit = (status.ullTotalPageFile - status.ullAvailPageFile) / GB;
        var (pageFile, pageFileUsed) = PageFiles();
        return new MemoryData(used, total, status.dwMemoryLoad, commit, commitLimit, _pdh.Get(_pagesInput) ?? 0)
        {
            PageFileGB = pageFile,
            PageFileUsedGB = pageFileUsed,
        };
    }

    /// <summary>
    /// Größe und Belegung aller Auslagerungsdateien (SystemPageFileInformation, ohne Admin-Rechte). Eine von Windows
    /// verwaltete Datei wächst bei Bedarf – die Größe ist der aktuelle Stand.
    /// </summary>
    internal static (double? SizeGB, double? UsedGB) PageFiles()
    {
        const int SystemPageFileInformation = 18;
        const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
        int size = 1024;
        for (int attempt = 0; attempt < 4; attempt++, size *= 4)
        {
            IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemPageFileInformation, buffer, size, out int needed);
                if (status == StatusInfoLengthMismatch) { size = Math.Max(size, needed); continue; }
                if (status != 0) return (null, null);
                if (needed == 0) return (0, 0);   // keine Auslagerungsdatei
                long pages = 0, inUse = 0;
                int offset = 0;
                while (true)
                {
                    // SYSTEM_PAGEFILE_INFORMATION: NextEntryOffset, TotalSize, TotalInUse, PeakUsage (je ULONG, in Seiten)
                    int next = System.Runtime.InteropServices.Marshal.ReadInt32(buffer, offset);
                    pages += (uint)System.Runtime.InteropServices.Marshal.ReadInt32(buffer, offset + 4);
                    inUse += (uint)System.Runtime.InteropServices.Marshal.ReadInt32(buffer, offset + 8);
                    if (next == 0) break;
                    offset += next;
                }
                double pageGB = Environment.SystemPageSize / (1024.0 * 1024 * 1024);
                return (pages * pageGB, inUse * pageGB);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            }
        }
        return (null, null);
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);
}

internal sealed class DiskSensor
{
    private readonly PdhQuery _pdh;
    private readonly IntPtr _idle;
    private readonly IntPtr _latency;
    private readonly IntPtr _bytes;

    public DiskSensor(PdhQuery pdh)
    {
        _pdh = pdh;
        _idle = pdh.Add(@"\PhysicalDisk(*)\% Idle Time");
        _latency = pdh.Add(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer");
        _bytes = pdh.Add(@"\PhysicalDisk(_Total)\Disk Bytes/sec");
    }

    public DiskData Read()
    {
        // Pro Laufwerk betrachten: Der Durchschnitt über alle Platten würde eine volle Spiele-SSD verstecken.
        var idle = _pdh.GetArray(_idle).Where(x => x.Key != "_Total").Select(x => x.Value).ToList();
        var lat = _pdh.GetArray(_latency).Where(x => x.Key != "_Total").Select(x => x.Value).ToList();
        double active = idle.Count > 0 ? 100 - Math.Clamp(idle.Min(), 0, 100) : 0;
        double latencyMs = lat.Count > 0 ? lat.Max() * 1000 : 0;
        return new DiskData(active, latencyMs, (_pdh.Get(_bytes) ?? 0) / (1024 * 1024));
    }
}
