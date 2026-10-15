using System.Runtime.InteropServices;

namespace PerfOverlay.Native;

/// <summary>
/// Kern-Topologie aus GetSystemCpuSetInformation: pro logischem Prozessor die Effizienzklasse
/// (Hybrid-CPUs: höher = Performance-Kern, z. B. P = 1, E = 0; klassische CPUs: alle 0)
/// und der physische Kern (zwei Threads mit SMT/Hyper-Threading teilen sich einen).
/// </summary>
internal static class CpuTopology
{
    public readonly record struct LogicalCpu(int Group, int Index, int Core, int EfficiencyClass);

    private static readonly Lazy<Dictionary<(int Group, int Index), LogicalCpu>> Map = new(Load);

    public static LogicalCpu? Get(int group, int index) =>
        Map.Value.TryGetValue((group, index), out var cpu) ? cpu : null;

    /// <summary>True bei Intel ab 12. Generation (P- und E-Kerne) – mehr als eine Effizienzklasse.</summary>
    public static bool IsHybrid => Map.Value.Values.Select(c => c.EfficiencyClass).Distinct().Count() > 1;

    private static Dictionary<(int, int), LogicalCpu> Load()
    {
        var result = new Dictionary<(int, int), LogicalCpu>();
        GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint length, IntPtr.Zero, 0);
        if (length == 0) return result;

        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0)) return result;

            // SYSTEM_CPU_SET_INFORMATION: [0] Size · [4] Type · [8] Id · [12] Group (WORD)
            // · [14] LogicalProcessorIndex · [15] CoreIndex · [16] LLC · [17] NUMA · [18] EfficiencyClass
            for (int offset = 0; offset < length;)
            {
                IntPtr item = buffer + offset;
                int size = Marshal.ReadInt32(item);
                if (size <= 0) break;
                if (Marshal.ReadInt32(item, 4) == 0)   // CpuSetInformation
                {
                    int group = (ushort)Marshal.ReadInt16(item, 12);
                    int index = Marshal.ReadByte(item, 14);
                    result[(group, index)] = new LogicalCpu(group, index, Marshal.ReadByte(item, 15), Marshal.ReadByte(item, 18));
                }
                offset += size;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);
}
