using System.Management;
using System.Text.RegularExpressions;

namespace PerfOverlay.Sensors;

public enum ChannelMode { Unknown, Single, Dual, Quad }

public sealed record MemoryModule(
    string Slot,            // DeviceLocator, z. B. "DDR4-A2", "DIMM_B1", "ChannelA-DIMM0"
    string Bank,            // BankLabel, z. B. "BANK 0", "P0 CHANNEL A"
    double SizeGB,
    int ConfiguredMTs,      // aktueller Takt (MT/s)
    string PartNumber,
    string Manufacturer,
    int InterleaveDepth,
    string? Channel);       // erkannter Kanal (z. B. "A"), null wenn aus dem Slot-Namen nicht ableitbar

/// <summary>
/// Bestückung und Betriebsart des Arbeitsspeichers aus SMBIOS (über WMI, kein Treiber nötig).
/// Wird einmal beim Start gelesen – das ändert sich zur Laufzeit nicht.
/// </summary>
public sealed record MemoryConfig(
    IReadOnlyList<MemoryModule> Modules,
    int SlotCount,
    string Type,            // "DDR4", "DDR5", ...
    bool IsSoDimm,          // Laptop-Riegel
    int ConfiguredMTs,
    int? RatedMTs,          // Nenntakt laut Teilenummer, null wenn nicht ableitbar
    ChannelMode Channels,
    bool SameChannelPair)   // mehrere Riegel, aber alle im selben Kanal (falsch gesteckt)
{
    public string Summary
    {
        get
        {
            var sizes = Modules.Select(m => m.SizeGB).Distinct().ToList();
            string size = sizes.Count == 1 ? $"{Modules.Count}×{sizes[0]:0}" : $"{Modules.Sum(m => m.SizeGB):0}";
            string channels = Channels switch
            {
                ChannelMode.Single => " Single",
                ChannelMode.Dual => " Dual",
                ChannelMode.Quad => " Quad",
                _ => "",
            };
            // Manche Systeme (VMs, einzelne Boards) melden keinen Takt – dann nur den Typ zeigen
            string speed = ConfiguredMTs > 0 ? $"-{ConfiguredMTs}" : "";
            return $"{size} GB {Type}{speed}{channels}";
        }
    }

    /// <summary>JEDEC-Standardtakt ohne XMP/EXPO – Gaming-Kits sind praktisch immer schneller ausgelegt.</summary>
    public bool RunsAtJedecBase => ConfiguredMTs > 0 && Type switch
    {
        "DDR4" => ConfiguredMTs <= 2400,
        "DDR5" => ConfiguredMTs <= 4800,
        "DDR3" => ConfiguredMTs <= 1333,
        _ => false,
    };

    public static MemoryConfig? Read()
    {
        try
        {
            var modules = new List<MemoryModule>();
            int smbiosType = 0, formFactor = 0;
            using (var searcher = new ManagementObjectSearcher(
                "SELECT DeviceLocator, BankLabel, Capacity, ConfiguredClockSpeed, Speed, PartNumber, Manufacturer, " +
                "InterleaveDataDepth, SMBIOSMemoryType, FormFactor FROM Win32_PhysicalMemory"))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    string slot = (mo["DeviceLocator"] as string ?? "").Trim();
                    string bank = (mo["BankLabel"] as string ?? "").Trim();
                    int configured = ToInt(mo["ConfiguredClockSpeed"]);
                    if (configured == 0) configured = ToInt(mo["Speed"]);
                    smbiosType = ToInt(mo["SMBIOSMemoryType"]);
                    formFactor = ToInt(mo["FormFactor"]);
                    modules.Add(new MemoryModule(
                        slot, bank,
                        Convert.ToDouble(mo["Capacity"] ?? 0UL) / (1024.0 * 1024 * 1024),
                        configured,
                        (mo["PartNumber"] as string ?? "").Trim(),
                        (mo["Manufacturer"] as string ?? "").Trim(),
                        ToInt(mo["InterleaveDataDepth"]),
                        DetectChannel(slot, bank)));
                }
            }
            if (modules.Count == 0) return null;

            int slots = 0;
            using (var searcher = new ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray"))
                foreach (ManagementObject mo in searcher.Get())
                    slots += ToInt(mo["MemoryDevices"]);

            string type = smbiosType switch
            {
                24 => "DDR3",
                26 => "DDR4",
                30 => "LPDDR4",
                34 => "DDR5",
                35 => "LPDDR5",
                _ => "RAM",
            };
            int configuredMts = modules.Min(m => m.ConfiguredMTs);
            int? rated = modules.Select(m => RatedFromPartNumber(m.PartNumber)).FirstOrDefault(r => r.HasValue);
            // Nur relevant, wenn der Riegel schneller könnte – und nur bei bekanntem aktuellen Takt
            if (rated <= configuredMts || configuredMts <= 0) rated = null;

            var (channels, sameChannel) = DetectMode(modules);
            return new MemoryConfig(modules, Math.Max(slots, modules.Count), type, formFactor == 12,
                configuredMts, rated, channels, sameChannel);
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int ToInt(object? value) => value == null ? 0 : Convert.ToInt32(value);

    /// <summary>
    /// Kanal aus den herstellerabhängigen Slot-Namen, z. B.
    /// "ChannelA-DIMM1" / "P0 CHANNEL B" (Intel-/AMD-Referenz, viele Laptops),
    /// "DDR4-A2" (Gigabyte), "DIMM_B1" (ASUS), "DDR4_A1" (ASRock), "DIMMA2" (MSI).
    /// Reine Nummern wie "DIMM 1" / "BANK 0" lassen keinen Schluss zu.
    /// </summary>
    private static string? DetectChannel(string slot, string bank)
    {
        foreach (var text in new[] { slot, bank })
        {
            var m = Regex.Match(text, @"CHANNEL\s*([A-H])", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var controller = Regex.Match(text, @"Controller\s*(\d)", RegexOptions.IgnoreCase);
                return (controller.Success ? controller.Groups[1].Value : "") + m.Groups[1].Value.ToUpperInvariant();
            }
        }
        var slotMatch = Regex.Match(slot, @"(?:^|[^A-Za-z])(?:DIMM)?([A-H])[\s_-]?\d$", RegexOptions.IgnoreCase);
        return slotMatch.Success ? slotMatch.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static (ChannelMode Mode, bool SameChannel) DetectMode(List<MemoryModule> modules)
    {
        if (modules.All(m => m.Channel != null))
        {
            int distinct = modules.Select(m => m.Channel).Distinct().Count();
            bool same = modules.Count > 1 && distinct == 1;
            return (ToMode(distinct), same);
        }
        // Fallback: SMBIOS-Interleave-Tiefe (2 = über zwei Kanäle verschränkt)
        int depth = modules.Max(m => m.InterleaveDepth);
        if (depth >= 1) return (ToMode(depth), false);
        return (modules.Count == 1 ? ChannelMode.Single : ChannelMode.Unknown, false);
    }

    private static ChannelMode ToMode(int channels) => channels switch
    {
        1 => ChannelMode.Single,
        2 or 3 => ChannelMode.Dual,
        >= 4 => ChannelMode.Quad,
        _ => ChannelMode.Unknown,
    };

    /// <summary>
    /// Nenntakt aus gängigen Teilenummern:
    ///   Corsair "CMK32GX4M2E3200C16", G.Skill "F4-3600C16D-32GVKC", TeamGroup "…3200HC16…" → 4 Ziffern vor "C"
    ///   G.Skill DDR5 "F5-6000J3038F16GX2-TZ5N" → 4 Ziffern nach "F5-"
    ///   Kingston Fury/HyperX "KF436C17BBK2/32", "HX432C16PB3K2/16" → 2 Ziffern × 100
    ///   Crucial Ballistix "BL2K16G36C16U4B" → 2 Ziffern × 100
    /// </summary>
    public static int? RatedFromPartNumber(string partNumber)
    {
        if (string.IsNullOrWhiteSpace(partNumber)) return null;
        int? value = null;

        var m = Regex.Match(partNumber, @"(\d{4})H?C\d{2}");
        if (m.Success) value = int.Parse(m.Groups[1].Value);
        else if ((m = Regex.Match(partNumber, @"^F[3-5]-(\d{4})[A-Z]")).Success) value = int.Parse(m.Groups[1].Value);   // G.Skill DDR5 "F5-6000J3038…"
        else if ((m = Regex.Match(partNumber, @"^(?:KF|HX)\d(\d{2})C\d{2}")).Success) value = int.Parse(m.Groups[1].Value) * 100;
        else if ((m = Regex.Match(partNumber, @"^BL\w*?G(\d{2})C\d{2}")).Success) value = int.Parse(m.Groups[1].Value) * 100;

        return value is >= 1600 and <= 10000 ? value : null;
    }
}
