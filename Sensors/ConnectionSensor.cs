using System.Runtime.InteropServices;

namespace PerfOverlay.Sensors;

/// <summary>
/// TCP-Verbindungen des Spiels. <see cref="Stale"/> zählt "tote" Verbindungen: Die Gegenseite (z. B. ein
/// Cloud-Dienst) hat sie geschlossen, das Spiel aber nicht (Zustand CLOSE_WAIT), und das seit mindestens
/// <see cref="ConnectionTracker.StaleAfter"/>. Kurz ist CLOSE_WAIT normal, dauerhaft ist es ein Fehler im Spiel.
/// Greift das Spiel weiter auf solche Verbindungen zu, wartet es auf Zeitüberschreitungen – das kann Ruckler machen.
/// </summary>
/// <param name="Total">Alle TCP-Verbindungen des Spiels (IPv4 und IPv6).</param>
/// <param name="CloseWait">Davon im Zustand CLOSE_WAIT.</param>
/// <param name="Stale">Davon schon länger als die Schwelle in CLOSE_WAIT.</param>
public sealed record ConnectionData(int Total, int CloseWait, int Stale);

/// <summary>
/// Merkt sich, seit wann jede Verbindung in CLOSE_WAIT ist. Rein rechnerisch, ohne Systemzugriff – damit testbar.
/// </summary>
public sealed class ConnectionTracker
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, DateTime> _closeWaitSince = new();
    private int _pid;

    /// <param name="pid">Prozess, dessen Verbindungen gezählt werden; ein Wechsel setzt den Verlauf zurück.</param>
    /// <param name="connections">Verbindungen dieses Prozesses: eindeutiger Schlüssel (Adressen/Ports) und ob CLOSE_WAIT.</param>
    public ConnectionData Update(int pid, IEnumerable<(string Key, bool CloseWait)> connections, DateTime now)
    {
        if (pid != _pid)
        {
            _closeWaitSince.Clear();
            _pid = pid;
        }

        int total = 0;
        var current = new HashSet<string>();
        foreach (var (key, closeWait) in connections)
        {
            total++;
            if (!closeWait) continue;
            current.Add(key);
            _closeWaitSince.TryAdd(key, now);
        }
        // Geschlossene (oder wieder verwendete) Verbindungen vergessen
        foreach (var gone in _closeWaitSince.Keys.Where(k => !current.Contains(k)).ToList())
            _closeWaitSince.Remove(gone);

        int stale = _closeWaitSince.Values.Count(since => now - since >= StaleAfter);
        return new ConnectionData(total, current.Count, stale);
    }
}

/// <summary>
/// Liest die TCP-Tabelle von Windows (dieselbe Quelle wie "netstat -ano") – ohne Admin-Rechte und ohne
/// Zugriff auf den Spielprozess selbst.
/// </summary>
internal sealed class ConnectionSensor
{
    private readonly ConnectionTracker _tracker = new();

    /// <summary>Verbindungen des Spiels; null ohne Spiel.</summary>
    public ConnectionData? Read(int gamePid, DateTime now)
    {
        if (gamePid <= 0) return null;
        try
        {
            return _tracker.Update(gamePid, ReadTable(gamePid).Select(c => (c.Key, c.CloseWait)), now);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            return null;
        }
    }

    private const int AfInet = 2, AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const uint StateCloseWait = 8;   // MIB_TCP_STATE_CLOSE_WAIT
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll")]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);

    /// <summary>TCP-Verbindungen eines Prozesses (pid &gt; 0) oder aller Prozesse (pid ≤ 0) mit Schlüssel und CLOSE_WAIT-Kennzeichen.</summary>
    internal static List<(int Pid, string Key, bool CloseWait)> ReadTable(int pid)
    {
        var result = new List<(int, string, bool)>();
        // MIB_TCPROW_OWNER_PID: 6 × DWORD (Zustand, lokale Adresse/Port, ferne Adresse/Port, PID)
        ReadFamily(AfInet, 24, 0, 20, pid, result);
        // MIB_TCP6ROW_OWNER_PID: Adresse 16, Scope 4, Port 4, ferne Adresse 16, Scope 4, Port 4, Zustand 4, PID 4
        ReadFamily(AfInet6, 56, 48, 52, pid, result);
        return result;
    }

    /// <summary>Für den Selbsttest: tote Verbindungen (CLOSE_WAIT) aller Programme, z. B. "2 (steam 2)" oder "0".</summary>
    internal static string CloseWaitSummary()
    {
        try
        {
            var byProcess = ReadTable(0).Where(c => c.CloseWait)
                .GroupBy(c => c.Pid)
                .Select(g => (Name: ProcessName(g.Key), Count: g.Count()))
                .OrderByDescending(x => x.Count)
                .ToList();
            int total = byProcess.Sum(x => x.Count);
            return total == 0 ? "0" : $"{total} ({string.Join(", ", byProcess.Take(5).Select(x => $"{x.Name} {x.Count}"))})";
        }
        catch (Exception ex)
        {
            return "nicht lesbar: " + ex.Message;
        }
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (ArgumentException)
        {
            return $"PID {pid}";   // inzwischen beendet
        }
        catch (InvalidOperationException)
        {
            return $"PID {pid}";
        }
    }

    private static void ReadFamily(int af, int rowSize, int stateOffset, int pidOffset, int pid, List<(int, string, bool)> result)
    {
        int size = 0;
        int rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TcpTableOwnerPidAll, 0);
        // Die Tabelle kann zwischen beiden Aufrufen wachsen – dann mit der neuen Größe noch einmal
        for (int attempt = 0; attempt < 3 && rc == ErrorInsufficientBuffer; attempt++)
        {
            size += 16 * rowSize;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                rc = GetExtendedTcpTable(buffer, ref size, false, af, TcpTableOwnerPidAll, 0);
                if (rc != 0) continue;
                int count = Marshal.ReadInt32(buffer);
                var row = new byte[rowSize];
                for (int i = 0; i < count; i++)
                {
                    IntPtr p = buffer + 4 + i * rowSize;
                    int owner = Marshal.ReadInt32(p, pidOffset);
                    if (pid > 0 && owner != pid) continue;
                    Marshal.Copy(p, row, 0, rowSize);
                    uint state = BitConverter.ToUInt32(row, stateOffset);
                    // Schlüssel: Adressen, Ports und PID – ohne den Zustand, der sich ändern darf
                    Array.Clear(row, stateOffset, 4);
                    result.Add((owner, af + ":" + Convert.ToHexString(row), state == StateCloseWait));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
