using System.Runtime.InteropServices;

namespace PerfOverlay.Native;

/// <summary>
/// Schlanker Wrapper um die Windows Performance Data Helper (PDH) API.
/// Nutzt PdhAddEnglishCounter, damit die Zählerpfade auch auf deutschem Windows funktionieren.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;

    private IntPtr _query;

    public PdhQuery()
    {
        uint status = PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (status != 0)
            throw new InvalidOperationException($"PdhOpenQuery fehlgeschlagen: 0x{status:X8}");
    }

    /// <summary>Fügt einen Zähler hinzu. Gibt IntPtr.Zero zurück, wenn es ihn auf diesem System nicht gibt.</summary>
    public IntPtr Add(string englishPath)
    {
        return PdhAddEnglishCounterW(_query, englishPath, IntPtr.Zero, out IntPtr counter) == 0 ? counter : IntPtr.Zero;
    }

    public void Collect() => PdhCollectQueryData(_query);

    public double? Get(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        if (PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out PDH_FMT_COUNTERVALUE value) != 0)
            return null;
        return value.CStatus <= 1 ? value.DoubleValue : null;
    }

    /// <summary>Liest alle Instanzen eines Wildcard-Zählers (z. B. "\Processor Information(*)\% Processor Time").</summary>
    public List<KeyValuePair<string, double>> GetArray(IntPtr counter)
    {
        var result = new List<KeyValuePair<string, double>>();
        if (counter == IntPtr.Zero) return result;

        uint size = 0;
        uint status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, IntPtr.Zero);
        if (status != PDH_MORE_DATA || size == 0) return result;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buffer);
            if (status != 0) return result;

            // PDH_FMT_COUNTERVALUE_ITEM_W (x64): [0] wchar* szName, [8] uint CStatus, [16] double
            int itemSize = IntPtr.Size + 16;
            for (int i = 0; i < count; i++)
            {
                IntPtr item = buffer + i * itemSize;
                string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                uint cstatus = (uint)Marshal.ReadInt32(item, IntPtr.Size);
                if (cstatus > 1) continue;
                double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));
                result.Add(new(name, v));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
