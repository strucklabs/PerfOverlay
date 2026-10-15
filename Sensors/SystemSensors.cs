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
        return new MemoryData(used, total, status.dwMemoryLoad, commit, commitLimit, _pdh.Get(_pagesInput) ?? 0);
    }
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
