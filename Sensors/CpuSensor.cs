using PerfOverlay.Native;

namespace PerfOverlay.Sensors;

/// <summary>
/// CPU-Last pro Kern und effektiver Takt, komplett im User Mode über PDH.
/// Effektiver Takt = "Processor Frequency" (Nenntakt) × "% Processor Performance" / 100,
/// so berechnet ihn auch der Task-Manager. Kein Ring-0-Treiber nötig.
/// </summary>
internal sealed class CpuSensor
{
    private readonly PdhQuery _pdh;
    private readonly IntPtr _coreTime;
    private readonly IntPtr _corePerf;
    private readonly IntPtr _coreFreq;
    private readonly IntPtr _totalUtility;
    private readonly IntPtr _totalTime;
    private readonly IntPtr _perfLimit;

    public CpuSensor(PdhQuery pdh)
    {
        _pdh = pdh;
        _coreTime = pdh.Add(@"\Processor Information(*)\% Processor Time");
        _corePerf = pdh.Add(@"\Processor Information(*)\% Processor Performance");
        _coreFreq = pdh.Add(@"\Processor Information(*)\Processor Frequency");
        _totalUtility = pdh.Add(@"\Processor Information(_Total)\% Processor Utility");
        _totalTime = pdh.Add(@"\Processor Information(_Total)\% Processor Time");
        _perfLimit = pdh.Add(@"\Processor Information(_Total)\% Performance Limit");
    }

    public CpuData Read()
    {
        var loads = PerCore(_pdh.GetArray(_coreTime));
        var perf = PerCore(_pdh.GetArray(_corePerf));
        var freq = PerCore(_pdh.GetArray(_coreFreq));

        var mhz = new List<double>();
        var busyMhz = new List<double>();
        foreach (var (key, f) in freq)
            if (perf.TryGetValue(key, out double p) && f > 0)
            {
                mhz.Add(f * p / 100.0);
                // "% Processor Performance" springt bei fast ruhenden Kernen (kaum Arbeit im Messfenster) auf
                // unrealistische Werte – für den Höchsttakt zählen deshalb nur Kerne, die wirklich arbeiten
                if (loads.TryGetValue(key, out double load) && load >= 20) busyMhz.Add(f * p / 100.0);
            }

        double total = _pdh.Get(_totalUtility) ?? _pdh.Get(_totalTime) ?? loads.Values.DefaultIfEmpty(0).Average();

        return new CpuData(
            Math.Clamp(total, 0, 100),
            loads.Values.Select(v => Math.Clamp(v, 0, 100)).ToArray(),
            mhz.Count > 0 ? mhz.Average() : 0,
            busyMhz.Count > 0 ? busyMhz.Max() : mhz.Count > 0 ? mhz.Average() : 0)
        {
            PerfLimitPercent = _pdh.Get(_perfLimit),
            CoreClasses = loads.Keys.Select(k => Native.CpuTopology.Get(k.Group, k.Index)?.EfficiencyClass ?? 0).ToArray(),
            IsHybrid = Native.CpuTopology.IsHybrid,
        };
    }

    /// <summary>Instanzen heißen "0,0", "0,1", ... (Gruppe,Index); "_Total"-Instanzen werden verworfen.</summary>
    private static SortedDictionary<(int Group, int Index), double> PerCore(List<KeyValuePair<string, double>> items)
    {
        var result = new SortedDictionary<(int, int), double>();
        foreach (var (name, value) in items)
        {
            var parts = name.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out int g) && int.TryParse(parts[1], out int i))
                result[(g, i)] = value;
        }
        return result;
    }
}
