using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PerfOverlay.Native;

namespace PerfOverlay.Sensors;

/// <summary>
/// Gleitende Ping-Statistik über die letzten Messungen. Rein rechnerisch, ohne Netzwerkzugriff – damit testbar.
/// Jitter = durchschnittliche Abweichung zwischen aufeinanderfolgenden Antworten (wie bei VoIP/Spielen üblich).
/// </summary>
public sealed class PingSeries
{
    private readonly int _capacity;
    private readonly Queue<double?> _samples = new();   // null = keine Antwort (verloren)

    public PingSeries(int capacity = 30) => _capacity = capacity;

    public void Add(double? roundTripMs)
    {
        _samples.Enqueue(roundTripMs);
        while (_samples.Count > _capacity) _samples.Dequeue();
    }

    public int Count => _samples.Count;

    /// <summary>Ist überhaupt je eine Antwort gekommen? (Manche Router beantworten Ping grundsätzlich nicht.)</summary>
    public bool EverAnswered { get; private set; }

    public PingStats? Stats()
    {
        if (_samples.Count < 3) return null;
        var answered = _samples.Where(s => s.HasValue).Select(s => s!.Value).ToList();
        if (answered.Count > 0) EverAnswered = true;
        double loss = 100.0 * (_samples.Count - answered.Count) / _samples.Count;
        if (answered.Count == 0) return new PingStats(null, null, loss);

        double jitter = 0;
        for (int i = 1; i < answered.Count; i++) jitter += Math.Abs(answered[i] - answered[i - 1]);
        jitter = answered.Count > 1 ? jitter / (answered.Count - 1) : 0;
        return new PingStats(answered.Average(), jitter, loss);
    }
}

public sealed record PingStats(double? AvgMs, double? JitterMs, double LossPercent);

/// <summary>
/// Verbindungsqualität für Online-Spiele: Ping, Jitter und Verlust zum Router (eigenes Netz) und zu einem
/// Internet-Ziel (dahinter), dazu der Datendurchsatz aller Adapter. Die FPS beeinflusst das nicht – aber Lag,
/// Gummiband-Effekte und verzögerte Treffer. Der Vergleich Router ↔ Internet zeigt, wo ein Problem liegt.
/// </summary>
internal sealed class NetworkSensor : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GatewayRefresh = TimeSpan.FromSeconds(30);
    private const int TimeoutMs = 1000;

    private readonly PdhQuery _pdh;
    private readonly IntPtr _received;
    private readonly IntPtr _sent;
    private readonly string? _internetTarget;
    private readonly object _lock = new();
    private readonly PingSeries _router = new();
    private readonly PingSeries _internet = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private IPAddress? _gateway;

    /// <param name="internetTarget">Ping-Ziel im Internet; null oder leer = nicht pingen (nur Datendurchsatz).</param>
    public NetworkSensor(PdhQuery pdh, string? internetTarget)
    {
        _pdh = pdh;
        _received = pdh.Add(@"\Network Interface(*)\Bytes Received/sec");
        _sent = pdh.Add(@"\Network Interface(*)\Bytes Sent/sec");
        _internetTarget = string.IsNullOrWhiteSpace(internetTarget) ? null : internetTarget.Trim();
    }

    public bool PingEnabled => _internetTarget != null;

    public void Start()
    {
        if (_loop != null || !PingEnabled) return;
        _loop = Task.Run(() => PingLoopAsync(_cts.Token));
    }

    public NetworkData Read()
    {
        double down = _pdh.GetArray(_received).Sum(x => x.Value) / (1024 * 1024);
        double up = _pdh.GetArray(_sent).Sum(x => x.Value) / (1024 * 1024);
        lock (_lock)
        {
            return new NetworkData(
                PingEnabled ? _router.Stats() : null,
                PingEnabled ? _internet.Stats() : null,
                _router.EverAnswered,
                down, up);
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        using var routerPing = new Ping();
        using var internetPing = new Ping();
        var lastGatewayCheck = DateTime.MinValue;
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                if (DateTime.UtcNow - lastGatewayCheck > GatewayRefresh)
                {
                    _gateway = FindGateway();   // Netzwechsel (WLAN ↔ LAN, VPN) berücksichtigen
                    lastGatewayCheck = DateTime.UtcNow;
                }
                var routerTask = _gateway != null ? PingOnce(routerPing, _gateway.ToString()) : Task.FromResult<double?>(null);
                var internetTask = PingOnce(internetPing, _internetTarget!);
                await Task.WhenAll(routerTask, internetTask);
                lock (_lock)
                {
                    if (_gateway != null) _router.Add(routerTask.Result);
                    _internet.Add(internetTask.Result);
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }
    }

    private static async Task<double?> PingOnce(Ping ping, string target)
    {
        try
        {
            var reply = await ping.SendPingAsync(target, TimeoutMs);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException)
        {
            return null;   // z. B. Ziel nicht auflösbar oder Netz weg
        }
    }

    /// <summary>Standard-Gateway (Router) des aktiven Netzwerkadapters, IPv4.</summary>
    private static IPAddress? FindGateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
