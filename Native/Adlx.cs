using System.Runtime.InteropServices;

namespace PerfOverlay.Native;

/// <summary>
/// Minimale Anbindung an AMD ADLX (amdadlx64.dll, kommt mit jedem aktuellen Radeon-Treiber in System32).
/// ADLX ist eine C++-Schnittstelle: Jedes Objekt beginnt mit einem Zeiger auf seine Funktionstabelle (vtable).
/// Die Indizes unten stammen aus den C-Headern des offiziellen SDK (github.com/GPUOpen-LibrariesAndSDKs/ADLX,
/// SDK/Include: ISystem.h, IPerformanceMonitoring.h, IPerformanceMonitoring2.h) und müssen exakt stimmen.
/// </summary>
internal static unsafe class Adlx
{
    public const int ADLX_OK = 0, ADLX_ALREADY_ENABLED = 1, ADLX_ALREADY_INITIALIZED = 2;
    public static bool Succeeded(int r) => r is ADLX_OK or ADLX_ALREADY_ENABLED or ADLX_ALREADY_INITIALIZED;

    public const int GPUTYPE_DISCRETE = 2;

    // IADLXSystem (ohne Referenzzählung)
    public const int System_GetGPUs = 1, System_GetPerformanceMonitoringServices = 9;
    // IADLXInterface – gemeinsamer Anfang aller übrigen Objekte
    public const int Release = 1, QueryInterface = 2;
    // IADLXGPUList
    public const int List_Size = 3, List_At_GPUList = 11;
    // IADLXGPU
    public const int Gpu_Type = 5, Gpu_Name = 7, Gpu_TotalVRAM = 11;
    // IADLXPerformanceMonitoringServices
    public const int Perf_GetCurrentGPUMetrics = 18;
    // IADLXGPUMetrics (+ IADLXGPUMetrics1)
    public const int M_Usage = 4, M_Clock = 5, M_VramClock = 6, M_Temp = 7, M_Hotspot = 8, M_Power = 9,
        M_BoardPower = 10, M_FanRpm = 11, M_Vram = 12, M_VoltageMv = 13, M1_MemoryTemp = 15;

    private static IntPtr _lib;
    private static delegate* unmanaged<ulong*, int> _queryFullVersion;
    private static delegate* unmanaged<ulong, IntPtr*, int> _initialize;
    private static delegate* unmanaged<int> _terminate;

    /// <summary>Lädt amdadlx64.dll und initialisiert ADLX. Gibt das IADLXSystem zurück oder null mit Grund.</summary>
    public static IntPtr Initialize(out string status)
    {
        if (!NativeLibrary.TryLoad("amdadlx64.dll", typeof(Adlx).Assembly, DllImportSearchPath.System32, out _lib))
        {
            status = "amdadlx64.dll nicht gefunden (kein AMD-Treiber)";
            return IntPtr.Zero;
        }
        if (!NativeLibrary.TryGetExport(_lib, "ADLXQueryFullVersion", out var q) ||
            !NativeLibrary.TryGetExport(_lib, "ADLXInitialize", out var i) ||
            !NativeLibrary.TryGetExport(_lib, "ADLXTerminate", out var t))
        {
            status = "amdadlx64.dll ohne erwartete Funktionen (Treiber zu alt?)";
            return IntPtr.Zero;
        }
        _queryFullVersion = (delegate* unmanaged<ulong*, int>)q;
        _initialize = (delegate* unmanaged<ulong, IntPtr*, int>)i;
        _terminate = (delegate* unmanaged<int>)t;

        // Die Version, die der installierte Treiber selbst meldet – damit klappt es auch mit älteren Treibern
        ulong version;
        int r = _queryFullVersion(&version);
        if (!Succeeded(r)) { status = $"ADLXQueryFullVersion fehlgeschlagen ({r})"; return IntPtr.Zero; }
        IntPtr system;
        r = _initialize(version, &system);
        if (!Succeeded(r) || system == IntPtr.Zero) { status = $"ADLXInitialize fehlgeschlagen ({r})"; return IntPtr.Zero; }
        status = $"aktiv (ADLX {version >> 48}.{(version >> 32) & 0xFFFF}.{(version >> 16) & 0xFFFF}.{version & 0xFFFF})";
        return system;
    }

    public static void Terminate()
    {
        if (_terminate != null) _terminate();
        _terminate = null;
    }

    private static IntPtr Fn(IntPtr obj, int index) => (*(IntPtr**)obj)[index];

    public static int GetObject(IntPtr obj, int index, out IntPtr result)
    {
        IntPtr r;
        int code = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(obj, index))(obj, &r);
        result = r;
        return code;
    }

    public static int GetObject(IntPtr obj, int index, IntPtr arg, out IntPtr result)
    {
        IntPtr r;
        int code = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)Fn(obj, index))(obj, arg, &r);
        result = r;
        return code;
    }

    public static int GetAt(IntPtr list, int index, uint position, out IntPtr result)
    {
        IntPtr r;
        int code = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(list, index))(list, position, &r);
        result = r;
        return code;
    }

    public static uint GetUInt(IntPtr obj, int index) => ((delegate* unmanaged<IntPtr, uint>)Fn(obj, index))(obj);

    public static int? GetInt(IntPtr obj, int index)
    {
        int v;
        return Succeeded(((delegate* unmanaged<IntPtr, int*, int>)Fn(obj, index))(obj, &v)) ? v : null;
    }

    public static double? GetDouble(IntPtr obj, int index)
    {
        double v;
        return Succeeded(((delegate* unmanaged<IntPtr, double*, int>)Fn(obj, index))(obj, &v)) ? v : null;
    }

    public static string? GetString(IntPtr obj, int index)
    {
        IntPtr s;
        return Succeeded(((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(obj, index))(obj, &s)) ? Marshal.PtrToStringUTF8(s) : null;
    }

    public static IntPtr Query(IntPtr obj, string interfaceId)
    {
        IntPtr r;
        fixed (char* id = interfaceId)
            return Succeeded(((delegate* unmanaged<IntPtr, char*, IntPtr*, int>)Fn(obj, QueryInterface))(obj, id, &r)) ? r : IntPtr.Zero;
    }

    public static void ReleaseObject(IntPtr obj)
    {
        if (obj != IntPtr.Zero) ((delegate* unmanaged<IntPtr, int>)Fn(obj, Release))(obj);
    }
}
