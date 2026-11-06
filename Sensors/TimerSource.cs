using System.Diagnostics;
using System.Text.RegularExpressions;
using PerfOverlay.Native;

namespace PerfOverlay.Sensors;

/// <summary>Zeitgeber hinter QueryPerformanceCounter – die Uhr, die Spiele für Bildzeiten, Physik und Netzwerk abfragen.</summary>
public enum TimerKind { Tsc, Hpet, AcpiPm }

/// <param name="UsePlatformClock">
/// Startoption "useplatformclock" (bcdedit): true = gesetzt (zwingt Windows auf HPET), false = nicht gesetzt,
/// null = unbekannt (ohne Admin-Rechte nicht lesbar).
/// </param>
public sealed record TimerInfo(TimerKind Kind, long FrequencyHz, double NsPerCall, bool? UsePlatformClock)
{
    public bool IsSlow => Kind != TimerKind.Tsc;

    public string Name => Kind switch { TimerKind.Hpet => "HPET", TimerKind.AcpiPm => "ACPI-Timer", _ => "TSC" };

    /// <summary>z. B. "TSC (10 MHz, 32 ns pro Abfrage)" – für Selbsttest und Sitzungskopf.</summary>
    public string Summary => $"{Name} ({FrequencyHz / 1e6:0.###} MHz, {NsPerCall:0} ns pro Abfrage)"
        + (UsePlatformClock switch { true => ", useplatformclock gesetzt", false => ", useplatformclock nicht gesetzt", _ => "" });
}

/// <summary>
/// Erkennt, ob Windows den schnellen TSC im Prozessor oder einen langsamen Zeitgeber auf dem Mainboard (HPET,
/// ACPI-Timer) für QueryPerformanceCounter nutzt. Eine HPET-Abfrage kostet etwa das 20- bis 50-Fache – Spiele, die
/// tausende Male pro Bild die Zeit abfragen, verlieren dadurch FPS. Ursache ist entweder die Startoption
/// "useplatformclock" (oft von Tuning-Tools gesetzt) oder Windows hält den TSC für ungeeignet.
/// </summary>
public static class TimerSource
{
    public const long HpetHz = 14_318_180, AcpiPmHz = 3_579_545;

    /// <summary>Ab dieser Dauer pro Abfrage ist der Zeitgeber sicher nicht der TSC (der liegt bei etwa 20–50 ns).</summary>
    public const double SlowNs = 300;

    /// <summary>Einordnung aus Frequenz und gemessenen Kosten – ohne Systemzugriff, damit testbar.</summary>
    public static TimerKind Classify(long frequencyHz, double nsPerCall)
    {
        if (Math.Abs(frequencyHz - HpetHz) < 1000) return TimerKind.Hpet;
        if (Math.Abs(frequencyHz - AcpiPmHz) < 1000) return TimerKind.AcpiPm;
        // Unbekannte Frequenz, aber teure Abfragen: ein Plattform-Zeitgeber, auch wenn Windows die Frequenz umrechnet
        return nsPerCall >= SlowNs ? TimerKind.Hpet : TimerKind.Tsc;
    }

    /// <summary>Misst einmalig (ca. 10–50 ms) und liest mit Admin-Rechten die Startoption aus.</summary>
    public static TimerInfo Detect()
    {
        long freq = Stopwatch.Frequency;
        double ns = MeasureNsPerCall();
        return new TimerInfo(Classify(freq, ns), freq, ns, Win32.IsElevated() ? ReadUsePlatformClock() : null);
    }

    /// <summary>Kleinster Wert aus mehreren Durchläufen – ein Thread-Wechsel mitten in der Messung soll nicht zählen.</summary>
    private static double MeasureNsPerCall()
    {
        const int calls = 20_000;
        double best = double.MaxValue;
        for (int run = 0; run < 5; run++)
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < calls; i++) Stopwatch.GetTimestamp();
            long end = Stopwatch.GetTimestamp();
            best = Math.Min(best, (end - start) * 1e9 / Stopwatch.Frequency / calls);
        }
        return best;
    }

    /// <summary>"bcdedit /enum {current}" – nur mit Admin-Rechten lesbar. null, wenn es nicht klappt.</summary>
    private static bool? ReadUsePlatformClock()
    {
        try
        {
            var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "bcdedit.exe"))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/enum");
            psi.ArgumentList.Add("{current}");
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000) || p.ExitCode != 0) return null;
            return ParseUsePlatformClock(output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Die Elementnamen von bcdedit sind englisch, die Werte je nach Windows-Sprache übersetzt ("Yes"/"Ja").
    /// Fehlt der Eintrag, ist die Option nicht gesetzt.
    /// </summary>
    public static bool ParseUsePlatformClock(string bcdeditOutput)
    {
        var m = Regex.Match(bcdeditOutput, @"^\s*useplatformclock\s+(\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return m.Success && m.Groups[1].Value.ToLowerInvariant() is "yes" or "ja" or "on" or "true" or "1";
    }
}
