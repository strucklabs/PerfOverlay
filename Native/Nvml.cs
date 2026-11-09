using System.Runtime.InteropServices;
using System.Text;

namespace PerfOverlay.Native;

/// <summary>
/// P/Invoke auf die NVIDIA Management Library (nvml.dll), die mit jedem NVIDIA-Treiber
/// in System32 installiert wird. Rückgabewert 0 = NVML_SUCCESS.
/// </summary>
public static class Nvml
{
    private const string Dll = "nvml.dll";

    public const int NVML_TEMPERATURE_GPU = 0;
    public const int NVML_CLOCK_GRAPHICS = 0;
    public const int NVML_CLOCK_MEM = 2;

    // Bitmaske aus nvmlDeviceGetCurrentClocksThrottleReasons
    [Flags]
    public enum ThrottleReasons : ulong
    {
        None = 0,
        GpuIdle = 0x1,
        ApplicationsClocksSetting = 0x2,
        SwPowerCap = 0x4,
        HwSlowdown = 0x8,
        SyncBoost = 0x10,
        SwThermalSlowdown = 0x20,
        HwThermalSlowdown = 0x40,
        HwPowerBrakeSlowdown = 0x80,
        DisplayClockSetting = 0x100,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Memory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    /// <summary>BAR1: Fenster, über das die CPU den Grafikspeicher sieht – 256 MB ohne, bis VRAM-Größe mit Resizable BAR.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Bar1Memory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [DllImport(Dll)] public static extern int nvmlInit_v2();
    [DllImport(Dll)] public static extern int nvmlDeviceGetBAR1MemoryInfo(IntPtr device, out Bar1Memory bar1);
    [DllImport(Dll)] public static extern int nvmlShutdown();
    [DllImport(Dll)] public static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport(Dll)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport(Dll)] private static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
    [DllImport(Dll)] public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
    [DllImport(Dll)] public static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory memory);
    [DllImport(Dll)] public static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
    [DllImport(Dll)] public static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport(Dll)] public static extern int nvmlDeviceGetMaxClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport(Dll)] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport(Dll)] public static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
    [DllImport(Dll)] public static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint percent);
    [DllImport(Dll)] public static extern int nvmlDeviceGetPerformanceState(IntPtr device, out int pstate);
    [DllImport(Dll)] public static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons);
    [DllImport(Dll)] public static extern int nvmlDeviceGetCurrPcieLinkGeneration(IntPtr device, out uint gen);
    [DllImport(Dll)] public static extern int nvmlDeviceGetCurrPcieLinkWidth(IntPtr device, out uint width);
    [DllImport(Dll)] public static extern int nvmlDeviceGetMaxPcieLinkGeneration(IntPtr device, out uint gen);
    [DllImport(Dll)] public static extern int nvmlDeviceGetMaxPcieLinkWidth(IntPtr device, out uint width);

    public static string GetName(IntPtr device)
    {
        var buffer = new byte[96];
        if (nvmlDeviceGetName(device, buffer, (uint)buffer.Length) != 0) return "NVIDIA GPU";
        int len = Array.IndexOf(buffer, (byte)0);
        return Encoding.ASCII.GetString(buffer, 0, len < 0 ? buffer.Length : len);
    }
}
