using System.Net;
using System.Net.Sockets;
using PerfOverlay.Analysis;
using PerfOverlay.Sensors;

namespace PerfOverlay.Tests;

public class ConnectionTests
{
    private static readonly DateTime T0 = new(2026, 11, 8, 20, 0, 0);

    [Fact]
    public void Tote_Verbindung_zaehlt_erst_nach_30_Sekunden()
    {
        var tracker = new ConnectionTracker();
        (string, bool)[] rows = [("a", true), ("b", true), ("c", true), ("d", false)];

        var first = tracker.Update(100, rows, T0);
        Assert.Equal(new ConnectionData(4, 3, 0), first);

        var later = tracker.Update(100, rows, T0.AddSeconds(31));
        Assert.Equal(new ConnectionData(4, 3, 3), later);
    }

    [Fact]
    public void Geschlossene_Verbindung_wird_vergessen_und_neu_gezaehlt()
    {
        var tracker = new ConnectionTracker();
        tracker.Update(100, [("a", true)], T0);
        tracker.Update(100, [], T0.AddSeconds(10));                       // Spiel hat sie geschlossen
        var again = tracker.Update(100, [("a", true)], T0.AddSeconds(35)); // gleicher Port wiederverwendet
        Assert.Equal(0, again.Stale);
    }

    [Fact]
    public void Wechsel_des_Spiels_setzt_den_Verlauf_zurueck()
    {
        var tracker = new ConnectionTracker();
        tracker.Update(100, [("a", true)], T0);
        var other = tracker.Update(200, [("a", true)], T0.AddSeconds(60));
        Assert.Equal(0, other.Stale);
    }

    /// <summary>
    /// Echter Fall im Kleinen: Der "Server" schließt, der eigene Prozess (wie das fehlerhafte Spiel) nicht –
    /// Windows führt die Verbindung dann als CLOSE_WAIT, und genau die muss die Tabelle liefern.
    /// </summary>
    [Fact]
    public void Von_der_Gegenseite_geschlossene_Verbindung_erscheint_als_CLOSE_WAIT()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var clients = new List<TcpClient>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                var client = new TcpClient();
                client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                using (var server = listener.AcceptTcpClient()) { }   // Gegenseite schließt sofort
                clients.Add(client);                                  // eigene Seite bleibt offen
            }

            int pid = Environment.ProcessId;
            List<(int Pid, string Key, bool CloseWait)> rows = [];
            for (int attempt = 0; attempt < 20 && rows.Count(r => r.CloseWait) < 3; attempt++)
            {
                Thread.Sleep(50);
                rows = ConnectionSensor.ReadTable(pid);
            }
            Assert.All(rows, r => Assert.Equal(pid, r.Pid));
            Assert.True(rows.Count(r => r.CloseWait) >= 3, $"CLOSE_WAIT: {rows.Count(r => r.CloseWait)} von {rows.Count}");

            // Der Tracker zählt sie nach 30 s als tot
            var tracker = new ConnectionTracker();
            tracker.Update(pid, rows.Select(r => (r.Key, r.CloseWait)), T0);
            var data = tracker.Update(pid, rows.Select(r => (r.Key, r.CloseWait)), T0.AddSeconds(30));
            Assert.True(data.Stale >= 3);

            Assert.Contains(System.Diagnostics.Process.GetCurrentProcess().ProcessName, ConnectionSensor.CloseWaitSummary());
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
            listener.Stop();
        }
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void Meldung_ab_drei_toten_Verbindungen_und_nur_mit_Spiel(int stale, bool expected)
    {
        Snapshot Snap(FrameStats? f) => new(T0,
            new CpuData(30, [30, 30], 4500, 5000),
            new MemoryData(12, 32, 40, 18, 55, 0),
            new DiskData(1, 0.1, 1),
            new GpuData { Name = "Test-GPU", Source = "NVML", Load = 95, TempC = 65 }, [],
            f, "aktiv", true, null, null)
        { GameConnections = new ConnectionData(20, stale + 1, stale) };

        var game = new FrameStats(1, "Spiel.exe", 120, 100, 8.3, 8.3, 10, 4, 4, 8.2, 0) { AppFps = 120 };
        var notes = new BottleneckAnalyzer().Analyze(Snap(game)).Notes.Where(n => n.Key == "net-closewait").ToList();
        Assert.Equal(expected, notes.Count == 1);
        if (expected)
        {
            Assert.Contains($"{stale + 1} tote Verbindungen", notes[0].Text);
            Assert.Equal(NoteCategory.Hint, NoteCategories.Of("net-closewait"));
        }

        Assert.DoesNotContain(new BottleneckAnalyzer().Analyze(Snap(null)).Notes, n => n.Key == "net-closewait");
    }
}
