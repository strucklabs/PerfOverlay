using System.Diagnostics;
using System.IO;
using System.Text;
using PerfOverlay.Native;

namespace PerfOverlay.Sensors;

/// <summary>
/// AMD-Telemetrie über ADLX (User Mode, kommt mit dem Radeon-Treiber): Temperatur, Hotspot, Speicher-
/// temperatur, Takt, Leistung, Spannung und Lüfter – dieselben Werte, die die AMD Software anzeigt.
/// Ohne HWiNFO sind das bei AMD-Karten sonst nur Last und VRAM aus den Windows-Zählern.
/// </summary>
internal sealed class AdlxGpuSensor : IDisposable
{
    private readonly IntPtr _gpu;
    private readonly IntPtr _perf;
    private readonly string _name;
    private readonly double? _vramTotalGB;

    /// <summary>Ergebnis des letzten Erstellversuchs – für den Selbsttest.</summary>
    public static string Status { get; private set; } = "nicht versucht";

    private AdlxGpuSensor(IntPtr gpu, IntPtr perf)
    {
        _gpu = gpu;
        _perf = perf;
        _name = Adlx.GetString(gpu, Adlx.Gpu_Name)?.Trim() is { Length: > 0 } n ? n : "AMD GPU";
        _vramTotalGB = Adlx.GetInt(gpu, Adlx.Gpu_TotalVRAM) is int mb and > 0 ? mb / 1024.0 : null;
    }

    /// <param name="probeFirst">
    /// Erst in einem eigenen Prozess ausprobieren ("PerfOverlay.exe --adlx-probe"): Ein Fehler in einer
    /// nativen Schnittstelle beendet den Prozess hart und lässt sich nicht abfangen – so trifft er nur den Probelauf.
    /// </param>
    public static AdlxGpuSensor? TryCreate(bool probeFirst = false)
    {
        if (probeFirst && File.Exists(Path.Combine(Environment.SystemDirectory, "amdadlx64.dll")) && !ProbeSucceeded()) return null;
        try
        {
            IntPtr system = Adlx.Initialize(out string status);
            Status = status;
            if (system == IntPtr.Zero) return null;

            IntPtr gpu = PickGpu(system);
            if (gpu == IntPtr.Zero) { Status = "keine AMD-Grafikkarte gefunden"; Adlx.Terminate(); return null; }
            if (!Adlx.Succeeded(Adlx.GetObject(system, Adlx.System_GetPerformanceMonitoringServices, out IntPtr perf)) || perf == IntPtr.Zero)
            {
                Status = "Leistungsüberwachung von ADLX nicht verfügbar";
                Adlx.ReleaseObject(gpu);
                Adlx.Terminate();
                return null;
            }
            return new AdlxGpuSensor(gpu, perf);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Status = ex.Message;
            return null;
        }
    }

    private static bool ProbeSucceeded()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--adlx-probe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            if (!probe.WaitForExit(15_000))
            {
                probe.Kill();
                Status = "Probelauf hängt – ADLX abgeschaltet";
            }
            else if (probe.ExitCode == 0) return true;
            else if (probe.ExitCode == 2) { Status = "AMD-Treiber vorhanden, aber keine nutzbare AMD-Grafikkarte"; return false; }
            else Status = $"Probelauf fehlgeschlagen (Code 0x{probe.ExitCode:X}) – ADLX abgeschaltet";
        }
        catch (Exception ex) { Status = $"Probelauf nicht startbar: {ex.Message}"; }
        ErrorLog.Write("AMD-Grafikkarte: " + Status);
        return false;
    }

    /// <summary>Läuft im Probe-Prozess: einmal komplett initialisieren, zweimal lesen, aufräumen. Exit-Code 0 = alles gut.</summary>
    public static int RunProbe()
    {
        using var sensor = TryCreate();
        if (sensor == null) return 2;
        sensor.Read();
        Thread.Sleep(200);
        return sensor.Read() != null ? 0 : 3;
    }

    /// <summary>Bevorzugt eine dedizierte Karte – in Notebooks steckt oft zusätzlich eine integrierte GPU.</summary>
    private static IntPtr PickGpu(IntPtr system)
    {
        if (!Adlx.Succeeded(Adlx.GetObject(system, Adlx.System_GetGPUs, out IntPtr list)) || list == IntPtr.Zero) return IntPtr.Zero;
        IntPtr chosen = IntPtr.Zero;
        try
        {
            uint count = Adlx.GetUInt(list, Adlx.List_Size);
            for (uint i = 0; i < count; i++)
            {
                if (!Adlx.Succeeded(Adlx.GetAt(list, Adlx.List_At_GPUList, i, out IntPtr gpu)) || gpu == IntPtr.Zero) continue;
                bool discrete = Adlx.GetInt(gpu, Adlx.Gpu_Type) == Adlx.GPUTYPE_DISCRETE;
                if (chosen == IntPtr.Zero || discrete)
                {
                    Adlx.ReleaseObject(chosen);
                    chosen = gpu;
                    if (discrete) break;
                }
                else Adlx.ReleaseObject(gpu);
            }
        }
        finally { Adlx.ReleaseObject(list); }
        return chosen;
    }

    /// <summary>Aktuelle Werte; Hotspot, Speichertemperatur, Spannung und Lüfter gehen in GpuThermalData wie bei HWiNFO.</summary>
    public (GpuData Gpu, GpuThermalData Thermal)? Read()
    {
        if (!Adlx.Succeeded(Adlx.GetObject(_perf, Adlx.Perf_GetCurrentGPUMetrics, _gpu, out IntPtr m)) || m == IntPtr.Zero) return null;
        try
        {
            double? memTemp = null;
            IntPtr m1 = Adlx.Query(m, "IADLXGPUMetrics1");   // ab Treiber 2024: Speichertemperatur
            if (m1 != IntPtr.Zero)
            {
                memTemp = Positive(Adlx.GetDouble(m1, Adlx.M1_MemoryTemp));
                Adlx.ReleaseObject(m1);
            }

            // Gesamte Board-Leistung entspricht dem, was NVML bei NVIDIA meldet; ältere Karten kennen nur die Chip-Leistung
            double? power = Positive(Adlx.GetDouble(m, Adlx.M_BoardPower)) ?? Positive(Adlx.GetDouble(m, Adlx.M_Power));
            var gpu = new GpuData
            {
                Name = _name,
                Source = "ADLX",
                Load = Adlx.GetDouble(m, Adlx.M_Usage),
                CoreMhz = Positive(Adlx.GetInt(m, Adlx.M_Clock)),
                MemMhz = Positive(Adlx.GetInt(m, Adlx.M_VramClock)),
                TempC = Positive(Adlx.GetDouble(m, Adlx.M_Temp)),
                PowerW = power,
                VramUsedGB = Adlx.GetInt(m, Adlx.M_Vram) is int vram and >= 0 ? vram / 1024.0 : null,
                VramTotalGB = _vramTotalGB,
            };
            var thermal = new GpuThermalData(
                Adlx.GetInt(m, Adlx.M_VoltageMv) is int mv and > 0 ? mv / 1000.0 : null,
                Positive(Adlx.GetDouble(m, Adlx.M_Hotspot)),
                memTemp,
                Adlx.GetInt(m, Adlx.M_FanRpm) is int rpm and >= 0 ? rpm : null);
            return (gpu, thermal);
        }
        finally { Adlx.ReleaseObject(m); }
    }

    /// <summary>Nicht unterstützte Sensoren melden teils 0 oder −1 statt eines Fehlers.</summary>
    private static double? Positive(double? v) => v is > 0 ? v : null;

    /// <summary>Rohwerte für den Selbsttest, damit Tester ohne Entwicklungsumgebung prüfen können, was ankommt.</summary>
    public static string Dump(AdlxGpuSensor? sensor)
    {
        var sb = new StringBuilder($"ADLX: {Status}");
        if (sensor?.Read() is { } r)
        {
            var (g, t) = r;
            sb.AppendLine().Append($"  {g.Name}: Last {g.Load:0.#} %, {g.CoreMhz} MHz, Speichertakt {g.MemMhz} MHz, {g.TempC:0.#} °C, ")
              .Append($"Hotspot {t.HotspotC:0.#} °C, Speicher {t.MemoryJunctionC:0.#} °C, {g.PowerW:0.#} W, {t.CoreVoltage:0.000} V, ")
              .Append($"Lüfter {t.FanRpm} U/min, VRAM {g.VramUsedGB:0.00}/{g.VramTotalGB:0.00} GB");
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        Adlx.ReleaseObject(_perf);
        Adlx.ReleaseObject(_gpu);
        Adlx.Terminate();
    }
}
