using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace PerfOverlay.Sensors;

/// <summary>
/// Liest CPU-Temperatur, Package-Leistung und Drosselungsgründe aus dem Shared Memory von HWiNFO
/// ("Shared Memory Support" muss in HWiNFO aktiv sein). HWiNFO bringt einen signierten Treiber mit,
/// das Overlay selbst bleibt damit im User Mode.
///
/// Layout laut HWiNFO-SDK (packed, Version 2):
///   Header:  0 Signatur "HWiS" · 12 poll_time (Unix-Sekunden, int64) · 20/24/28 Sensor-Offset/-Größe/-Anzahl
///            · 32/36/40 Messwert-Offset/-Größe/-Anzahl
///   Sensor:  8 szSensorNameOrig[128]
///   Messwert: 0 Typ · 4 Sensor-Index · 12 szLabelOrig[128] · 268 szUnit[16] · 284 double Value
/// Ausgewertet werden die englischen Originalnamen – unabhängig von der in HWiNFO eingestellten Sprache.
/// </summary>
internal sealed class HwInfoSensor : IDisposable
{
    private const string MapName = @"Global\HWiNFO_SENS_SM2";
    private const uint SignatureActive = 0x53695748;   // "HWiS"; "DEAD" = HWiNFO hat Shared Memory beendet
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static readonly Encoding Ansi = Encoding.Latin1;

    private static readonly string[] TempLabels = ["CPU Package", "CPU (Tctl/Tdie)", "CPU Die (average)"];

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private DateTime _nextAttempt;

    // Index der relevanten Messwerte (Byte-Offsets), neu aufgebaut wenn sich die Anzahl ändert
    private uint _indexedCount;
    private long _temp = -1, _coreMax = -1, _power = -1, _powerLimit = -1;
    private long _tjTemp = -1, _tjDistance = -1;   // Kerntemperatur + "Distance to TjMAX" desselben Kerns
    private bool _isAmd;
    private readonly List<(long Offset, string Reason)> _flags = new();

    public CpuThermalData? Read()
    {
        if (_view == null && !TryOpen()) return null;
        var v = _view!;
        try
        {
            if (v.ReadUInt32(0) != SignatureActive) return Close();

            // Die Mapping-Datei bleibt bestehen, solange wir ein Handle halten – auch wenn HWiNFO beendet
            // wurde oder die 12-Stunden-Grenze der Gratisversion greift. Dann bleiben die Werte stehen.
            long age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - v.ReadInt64(12);
            if (age > 10) return Close();

            uint offR = v.ReadUInt32(32), sizeR = v.ReadUInt32(36), numR = v.ReadUInt32(40);
            if (offR + (long)sizeR * numR > v.Capacity) return Close();
            if (numR != _indexedCount) BuildIndex(v);

            double? Value(long offset) => offset >= 0 ? v.ReadDouble(offset + 284) : null;

            var reasons = _flags
                .Where(f => v.ReadDouble(f.Offset + 284) > 0.5)
                .Select(f => f.Reason)
                .Distinct()
                .ToList();
            if (reasons.Contains("Temperatur (PROCHOT)")) reasons.Remove("Temperatur");

            // TjMax steht nicht direkt im Shared Memory, ergibt sich aber aus Temperatur + Abstand zu TjMax.
            // Bei AMD fehlt der Abstand; Ryzen 3000/7000/9000 haben 95 °C (Ryzen 5000: 90 °C).
            double? tjMax = Value(_tjTemp) + Value(_tjDistance) ?? (_isAmd ? 95 : null);

            return new CpuThermalData("HWiNFO", Value(_temp), Value(_coreMax), Value(_power), Value(_powerLimit),
                reasons, tjMax, _isAmd);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Close();
        }
    }

    private bool TryOpen()
    {
        if (DateTime.UtcNow < _nextAttempt) return false;
        _nextAttempt = DateTime.UtcNow + RetryInterval;
        try
        {
            _mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _indexedCount = 0;
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            Close();
            return false;   // HWiNFO läuft nicht oder Shared Memory ist aus
        }
    }

    private void BuildIndex(MemoryMappedViewAccessor v)
    {
        uint offS = v.ReadUInt32(20), sizeS = v.ReadUInt32(24), numS = v.ReadUInt32(28);
        uint offR = v.ReadUInt32(32), sizeR = v.ReadUInt32(36), numR = v.ReadUInt32(40);

        var sensors = new string[numS];
        for (int i = 0; i < numS; i++)
            sensors[i] = ReadString(v, offS + (long)i * sizeS + 8, 128);

        _temp = _coreMax = _power = _powerLimit = _tjTemp = _tjDistance = -1;
        _flags.Clear();
        _isAmd = sensors.Any(s => s.StartsWith("CPU [#", StringComparison.Ordinal) && s.Contains("AMD"));
        int tempRank = int.MaxValue;
        var coreTemps = new Dictionary<(uint Sensor, string Label), long>();
        var distances = new List<(uint Sensor, string BaseLabel, long Offset)>();
        const string DistanceSuffix = " Distance to TjMAX";

        for (long i = 0; i < numR; i++)
        {
            long o = offR + i * sizeR;
            uint sensorIndex = v.ReadUInt32(o + 4);
            if (sensorIndex >= numS || !sensors[sensorIndex].StartsWith("CPU [#", StringComparison.Ordinal)) continue;

            string label = ReadString(v, o + 12, 128);
            string unit = ReadString(v, o + 268, 16);

            int rank = Array.IndexOf(TempLabels, label);
            if (rank >= 0 && rank < tempRank && unit.EndsWith('C')) { _temp = o; tempRank = rank; }
            else if (label == "Core Max" && _coreMax < 0) _coreMax = o;
            else if (label == "CPU Package Power" && _power < 0) _power = o;
            else if (label.StartsWith("PL1 Power Limit", StringComparison.Ordinal) && _powerLimit < 0) _powerLimit = o;
            else if (unit == "Yes/No" && ThrottleReason(label) is string reason) _flags.Add((o, reason));

            if (label.EndsWith(DistanceSuffix, StringComparison.Ordinal))
                distances.Add((sensorIndex, label[..^DistanceSuffix.Length], o));
            else if (unit.EndsWith('C'))
                coreTemps.TryAdd((sensorIndex, label), o);
        }

        foreach (var (sensor, baseLabel, offset) in distances)
        {
            if (coreTemps.TryGetValue((sensor, baseLabel), out long tempOffset))
            {
                _tjTemp = tempOffset;
                _tjDistance = offset;
                break;
            }
        }
        _indexedCount = numR;
    }

    /// <summary>
    /// Übersetzt HWiNFO-Flags in einen Grund. "Max Turbo Limit", "Turbo Attenuation", "Thermal Velocity Boost"
    /// u. ä. sind normales Boost-Verhalten und werden ignoriert, ebenso alles der iGPU ("GT:").
    /// </summary>
    private static string? ThrottleReason(string label)
    {
        if (label.StartsWith("GT:", StringComparison.Ordinal)) return null;
        if (label.Contains("PROCHOT")) return "Temperatur (PROCHOT)";
        if (label.Contains("VR Thermal")) return "Spannungswandler zu heiß";
        if (label.Contains("Thermal Throttling") || label.Contains("Thermal Event") ||
            label.Contains("Critical Temperature") || label.Contains("Running Average Thermal")) return "Temperatur";
        if (label.Contains("PL1")) return "Power-Limit PL1";
        if (label.Contains("PL2")) return "Power-Limit PL2";
        if (label.Contains("Power Limit Exceeded")) return "Power-Limit";
        if (label.Contains("VR TDC")) return "Stromlimit Spannungswandler (TDC)";
        if (label.Contains("Electrical Design Point") || label.Contains("ICCmax")) return "Stromlimit (ICCmax/EDP)";
        return null;
    }

    private static string ReadString(MemoryMappedViewAccessor v, long offset, int length)
    {
        var buffer = new byte[length];
        v.ReadArray(offset, buffer, 0, length);
        int end = Array.IndexOf(buffer, (byte)0);
        return Ansi.GetString(buffer, 0, end < 0 ? length : end);
    }

    private CpuThermalData? Close()
    {
        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
        _indexedCount = 0;
        return null;
    }

    public void Dispose() => Close();
}
