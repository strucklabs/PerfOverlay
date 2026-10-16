using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PerfOverlay.Analysis;
using PerfOverlay.Native;
using PerfOverlay.Sensors;

namespace PerfOverlay;

public partial class OverlayWindow : Window
{
    private const int HotkeyCorner = 1, HotkeyToggle = 2, HotkeyDetails = 3;
    private const uint VK_F8 = 0x77, VK_F9 = 0x78, VK_F10 = 0x79;
    private const double BarHeight = 18;

    private static readonly Brush LabelBrush = Frozen("#9CA3AF");
    private static readonly Brush MutedBrush = Frozen("#6B7280");
    private static readonly Brush GoodBrush = Frozen("#4ADE80");
    private static readonly Brush InfoBrush = Frozen("#60A5FA");
    private static readonly Brush WarnBrush = Frozen("#FBBF24");
    private static readonly Brush BadBrush = Frozen("#F87171");
    private static readonly Brush BarBackBrush = Frozen("#26FFFFFF");

    private readonly CancellationTokenSource _cts = new();
    private readonly BottleneckAnalyzer _analyzer = new();
    private readonly SessionRecorder _recorder = new();
    private SensorHub? _hub;
    private System.Windows.Forms.NotifyIcon? _tray;
    private IntPtr _hwnd;
    private int _corner = 1;   // 0 = oben links, 1 = oben rechts, 2 = unten rechts, 3 = unten links
    private IntPtr _monitor = Win32.PrimaryMonitor;
    private bool _details = true;
    private bool _exiting;

    // Verschieben mit der Maus: solange Strg+Umschalt gedrückt ist, nimmt das Overlay Klicks an.
    // Eigene Position = Abstand zur linken oberen Ecke des Arbeitsbereichs; null = feste Ecke (_corner).
    private readonly System.Windows.Threading.DispatcherTimer _modifierTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private bool _dragMode;
    private (int X, int Y)? _customOffset;
    private static readonly Brush DragBorderBrush = Frozen("#4ADE80");
    private static readonly Brush NormalBorderBrush = Frozen("#33FFFFFF");

    /// <summary>Feste Anzahl Meldungszeilen, damit das Overlay seine Größe nicht ändert.</summary>
    public const int NoteSlots = 4;
    private readonly NoteLog _noteLog = new();
    private readonly List<(Grid Row, TextBlock Text, TextBlock Age)> _noteRows = new();

    public OverlayWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Reposition();
        CreateNoteRows();
        _modifierTimer.Tick += (_, _) => SetDragMode(Win32.IsKeyDown(Win32.VK_CONTROL) && Win32.IsKeyDown(Win32.VK_SHIFT));
        MouseLeftButtonDown += OnDragStart;
    }

    /// <summary>
    /// Verschiebemodus an/aus: Ohne WS_EX_TRANSPARENT nimmt das Fenster Mausklicks an, mit lässt es sie
    /// zum Spiel durch. Der grüne Rahmen zeigt, dass das Overlay gerade greifbar ist.
    /// </summary>
    private void SetDragMode(bool on)
    {
        if (on == _dragMode || _hwnd == IntPtr.Zero) return;
        _dragMode = on;
        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex = on ? ex & ~(long)Win32.WS_EX_TRANSPARENT : ex | Win32.WS_EX_TRANSPARENT;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        Root.BorderBrush = on ? DragBorderBrush : NormalBorderBrush;
        Cursor = on ? Cursors.SizeAll : null;
        FooterLine.Text = on ? "Mit der Maus ziehen zum Verschieben · Strg+Umschalt+F9: zurück in die Ecke" : _footerText;
    }

    private string _footerText = "";

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (!_dragMode) return;
        DragMove();   // kehrt zurück, sobald die Maustaste losgelassen wird
        RememberPosition();
    }

    /// <summary>Merkt sich die gezogene Position relativ zum Monitor, auf dem das Overlay jetzt liegt.</summary>
    private void RememberPosition()
    {
        _monitor = Win32.MonitorFromWindow(_hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        if (Win32.GetWorkArea(_monitor) is not { } area) return;
        Win32.GetWindowRect(_hwnd, out var rect);
        _customOffset = (rect.Left - area.Left, rect.Top - area.Top);
        Reposition();   // ggf. vollständig in den sichtbaren Bereich ziehen
    }

    private void CreateNoteRows()
    {
        var font = new FontFamily("Segoe UI");
        for (int i = 0; i < NoteSlots; i++)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new TextBlock { FontFamily = font, FontSize = 11.5, Text = " " };
            var age = new TextBlock { FontFamily = font, FontSize = 11.5, Foreground = MutedBrush, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(age, 1);
            row.Children.Add(text);
            row.Children.Add(age);
            NotesPanel.Children.Add(row);
            _noteRows.Add((row, text, age));
        }
    }

    public IReadOnlyList<LoggedNote> LogNotes(Verdict v, DateTime now) => _noteLog.Update(v.Notes, now, NoteSlots);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        // Klicks gehen durch das Fenster hindurch, kein Fokus, nicht in Alt+Tab
        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex |= Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));

        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        const uint mods = Win32.MOD_CONTROL | Win32.MOD_SHIFT | Win32.MOD_NOREPEAT;
        Win32.RegisterHotKey(_hwnd, HotkeyCorner, mods, VK_F9);
        Win32.RegisterHotKey(_hwnd, HotkeyToggle, mods, VK_F10);
        Win32.RegisterHotKey(_hwnd, HotkeyDetails, mods, VK_F8);
        Reposition();
        _modifierTimer.Start();

        CreateTray();
        _hub = new SensorHub();
        _hub.StartFrameCapture();
        _ = UpdateLoopAsync();
    }

    private async Task UpdateLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                var hub = _hub!;
                var snapshot = await Task.Run(hub.Sample);
                if (_exiting) return;
                var verdict = _analyzer.Analyze(snapshot);
                Render(snapshot, verdict, LogNotes(verdict, snapshot.Time));
                try { _recorder.Record(snapshot, verdict); }
                catch (IOException) { _recorder.Stop(); }   // z. B. Datei gesperrt – Overlay läuft weiter
                UpdateTrayText(snapshot);
                FollowGameMonitor(snapshot);
                // Manche Spiele schieben sich nach vorn – Topmost regelmäßig erneuern
                Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ Darstellung

    public void Render(Snapshot s, Verdict v, IReadOnlyList<LoggedNote> notes)
    {
        Brush sevBrush = SeverityBrush(v.Severity);
        VerdictTitle.Text = v.Title;
        VerdictTitle.Foreground = sevBrush;
        VerdictDetail.Inlines.Clear();
        VerdictDetail.Inlines.Add(new Run(v.Detail));
        if (v.Tip != null)
        {
            VerdictDetail.Inlines.Add(new LineBreak());
            VerdictDetail.Inlines.Add(new Run("→ " + v.Tip) { Foreground = sevBrush });
        }
        var tint = ((SolidColorBrush)sevBrush).Color;
        VerdictBar.Background = new SolidColorBrush(Color.FromArgb(0x30, tint.R, tint.G, tint.B));
        RenderShares(v);

        RenderNotes(notes);

        // FPS (mit Frame Generation zusätzlich die vom Spiel gerenderten)
        var f = s.Frames;
        if (f != null)
        {
            var fps = new List<(string, Brush?)> { L("FPS   "), V($"{f.Fps,4:0}") };
            if (f.GeneratedShare > 0.3) fps.Add(($" ({f.AppFps:0} echt)", MutedBrush));
            fps.AddRange([L("  1%↓ "), V($"{f.Low1Fps:0}"), L("  "), V($"{f.AvgMs:0.0}"), L(" ms  "), (Shorten(f.App, 20), MutedBrush)]);
            Line(FpsLine, fps.ToArray());
        }
        else
            Line(FpsLine, L("FPS   "), V("   –  "),
                (s.FrameSourceRunning ? "keine 3D-Anwendung" : s.FrameSourceStatus, s.FrameSourceRunning ? MutedBrush : WarnBrush));
        RenderFrameGraph(f);

        // CPU
        var c = s.Cpu;
        var cpuParts = new List<(string, Brush?)>
        {
            L("CPU   "), V($"{c.TotalLoad,4:0} %"),
            L("  "), V($"{c.AvgMhz / 1000:0.00}/{c.MaxMhz / 1000:0.00}"), L(" GHz"),
        };
        if (s.CpuThermal is { } th)
        {
            if (th.TempC is double ct) { cpuParts.Add(L("  ")); cpuParts.Add(($"{ct:0} °C", CpuTempBrush(th))); }
            if (th.PowerW is double cw)
            {
                cpuParts.Add(L("  ")); cpuParts.Add(V($"{cw:0}"));
                cpuParts.Add(L(th.PowerLimitW is double pl ? $"/{pl:0} W" : " W"));
            }
        }
        Line(CpuLine, cpuParts.ToArray());
        RenderCoreBars(c);
        if (c.IsHybrid)
            Line(CoreMaxLine, L("P max "), (Pct(c.MaxPerfCoreLoad), LoadBrush(c.MaxPerfCoreLoad)),
                L("  E max "), (Pct(c.MaxEffCoreLoad), MutedBrush));
        else
            Line(CoreMaxLine, L("max "), (Pct(c.MaxCoreLoad), LoadBrush(c.MaxCoreLoad)));

        // GPU
        var g = s.Gpu;
        if (g == null)
        {
            Line(GpuLine, L("GPU   "), ("nicht gefunden", MutedBrush));
            GpuLine2.Visibility = Visibility.Collapsed;
        }
        else
        {
            var parts = new List<(string, Brush?)> { L("GPU   "), V($"{g.Load ?? 0,4:0} %") };
            if (g.CoreMhz is double mhz) { parts.Add(L("  ")); parts.Add(V($"{mhz:0}")); parts.Add(L(" MHz")); }
            // Spannung (HWiNFO): zeigt direkt, ob ein Undervolting greift
            if (s.GpuThermal?.CoreVoltage is double volt && volt > 0) { parts.Add(L(" ")); parts.Add(V($"{volt:0.000}")); parts.Add(L(" V")); }
            if (g.TempC is double t) { parts.Add(L("  ")); parts.Add(($"{t:0} °C", TempBrush(t))); }
            if (g.PowerW is double w)
            {
                parts.Add(L("  ")); parts.Add(V($"{w:0}"));
                parts.Add(L(g.PowerLimitW is double lim ? $"/{lim:0} W" : " W"));
            }
            Line(GpuLine, parts.ToArray());

            var p2 = new List<(string, Brush?)> { L("VRAM  ") };
            p2.Add(V(g.VramTotalGB is double tot ? $"{g.VramUsedGB:0.0}/{tot:0.0}" : $"{g.VramUsedGB:0.0}"));
            p2.Add(L(" GB"));
            var gt = s.GpuThermal;
            bool hasTemps = gt?.MemoryJunctionC != null || gt?.HotspotC != null;
            if (hasTemps)
            {
                // Mit HWiNFO: Speicher- und Hotspot-Temperatur statt PCIe (ein reduzierter Link wird ohnehin gemeldet)
                if (gt!.MemoryJunctionC is double mem) { p2.Add(L("  ")); p2.Add(($"{mem:0} °C", MemTempBrush(mem))); }
                if (gt.HotspotC is double hot) { p2.Add(L("  Hotspot ")); p2.Add(($"{hot:0} °C", HotspotBrush(hot))); }
            }
            else if (g.PcieGen is int gen && g.PcieWidth is int width) { p2.Add(L("  PCIe ")); p2.Add(V($"{gen}.0 x{width}")); }
            if (g.FanPercent is double fan) { p2.Add(L("  Lüfter ")); p2.Add(V($"{fan:0} %")); }
            if (g.PState is int ps)
            {
                p2.Add(L("  ")); p2.Add(V($"P{ps}"));
                if (!hasTemps) p2.Add(L(" " + PStateLabel(ps)));
            }
            Line(GpuLine2, p2.ToArray());
            GpuLine2.Visibility = Visibility.Visible;
        }

        RenderBusyBars(s, v);

        // RAM & Datenträger
        var m = s.Memory;
        var ramParts = new List<(string, Brush?)>
        {
            L("RAM   "), ($"{m.UsedGB:0.0}/{m.TotalGB:0.0}", LoadBrush(m.LoadPercent)), L(" GB  "),
        };
        if (s.MemoryConfig is { } rc)
        {
            // Bestückung statt Commit-Wert; gelb, wenn Takt oder Kanäle nicht optimal sind
            bool suboptimal = rc.RatedMTs > rc.ConfiguredMTs + 100 || rc.Channels == ChannelMode.Single && rc.SlotCount > 1 || rc.SameChannelPair;
            ramParts.Add((rc.Summary, suboptimal ? WarnBrush : null));
        }
        else
        {
            ramParts.Add(L("Commit ")); ramParts.Add(V($"{m.CommitGB:0}/{m.CommitLimitGB:0}")); ramParts.Add(L(" GB"));
        }
        ramParts.Add(L("  Nachl. "));
        ramParts.Add(($"{m.PagesInPerSec:0}/s", m.PagesInPerSec > 500 ? WarnBrush : null));
        Line(RamLine, ramParts.ToArray());
        var d = s.Disk;
        Line(DiskLine, L("Disk  "), V($"{d.ActivePercent,4:0} %"), L(" aktiv  "),
            ($"{d.LatencyMs:0.0}", d.LatencyMs > 20 ? WarnBrush : null), L(" ms  "), V($"{d.MBps:0}"), L(" MB/s"));

        string source = g?.Source == "NVML" ? "NVML" : g != null ? "Win-Zähler" : "–";
        _footerText = $"Strg+Umschalt: F10 an/aus · F9 Ecke · F8 Details  |  GPU: {source} · CPU: {s.CpuThermal?.Source ?? "–"}";
        if (!_dragMode) FooterLine.Text = _footerText;
    }

    private static readonly (BottleneckKind Kind, string Label, Brush Brush)[] ShareKinds =
    [
        (BottleneckKind.Gpu, "GPU", GoodBrush),
        (BottleneckKind.Cpu, "CPU", WarnBrush),
        (BottleneckKind.Limit, "Limit", InfoBrush),
        (BottleneckKind.Mixed, "gemischt", MutedBrush),
    ];

    /// <summary>Gestapelter Balken der Engpass-Anteile der letzten 30 s.</summary>
    private void RenderShares(Verdict v)
    {
        ShareBar.Children.Clear();
        ShareBar.ColumnDefinitions.Clear();
        var parts = ShareKinds.Where(k => v.Shares.GetValueOrDefault(k.Kind) > 0).ToList();
        ShareText.Text = parts.Count == 0
            ? ""
            : "30 s: " + string.Join(" · ", parts
                .OrderByDescending(k => v.Shares[k.Kind])
                .Select(k => $"{k.Label} {v.Shares[k.Kind] * 100:0} %"));

        for (int i = 0; i < parts.Count; i++)
        {
            ShareBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(v.Shares[parts[i].Kind], GridUnitType.Star) });
            var segment = new Rectangle { Fill = parts[i].Brush };
            Grid.SetColumn(segment, i);
            ShareBar.Children.Add(segment);
        }
    }

    private const double GraphWidth = 438, GraphHeight = 40, GraphSeconds = 5;

    /// <summary>
    /// Frametime-Verlauf: links alt, rechts neu. Eine ruhige Linie heißt gleichmäßige Bildausgabe,
    /// Zacken sind Ruckler (rot markiert). Skala passt sich dem Spiel an, Ausreißer werden oben abgeschnitten.
    /// </summary>
    private void RenderFrameGraph(FrameStats? f)
    {
        FrameGraph.Children.Clear();
        bool has = f != null && f.Frametimes.Length > 1;
        FrameGraphEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        FrameGraphScale.Text = "";
        FrameGraphPacing.Text = "";
        if (!has) return;

        double scale = Math.Clamp(Math.Max(f!.MedianMs * 2.5, f.P99Ms * 1.2), 16.7, 100);
        FrameGraphScale.Text = $"{scale:0} ms";
        string rating = FrameMetrics.PacingRating(f.PacingMs, f.MedianMs);
        if (rating != "")
        {
            FrameGraphPacing.Text = $"Pacing {rating} · ±{f.PacingMs:0.0} ms";
            FrameGraphPacing.Foreground = rating == "unruhig" ? WarnBrush : MutedBrush;
        }
        double X(double age) => GraphWidth - age / GraphSeconds * GraphWidth;
        double Y(double ms) => GraphHeight - Math.Min(ms, scale) / scale * (GraphHeight - 2) - 1;

        // Median als gestrichelte Referenz
        FrameGraph.Children.Add(new Line
        {
            X1 = 0, X2 = GraphWidth, Y1 = Y(f.MedianMs), Y2 = Y(f.MedianMs),
            Stroke = MutedBrush, StrokeThickness = 1, StrokeDashArray = [3, 3],
        });

        var points = new PointCollection(f.Frametimes.Length);
        foreach (var (age, ms) in f.Frametimes)
            points.Add(new Point(X(age), Y(ms)));
        FrameGraph.Children.Add(new Polyline { Points = points, Stroke = GoodBrush, StrokeThickness = 1.2 });

        foreach (double age in f.SpikeAges.Where(a => a <= GraphSeconds))
            FrameGraph.Children.Add(new Line
            {
                X1 = X(age), X2 = X(age), Y1 = 0, Y2 = GraphHeight,
                Stroke = BadBrush, StrokeThickness = 1, Opacity = 0.7,
            });
    }

    private const double BusyBarWidth = 140;

    /// <summary>CPU- und GPU-Arbeitszeit pro Frame relativ zur Frametime – der längere Balken ist der Engpass.</summary>
    private void RenderBusyBars(Snapshot s, Verdict v)
    {
        var f = s.Frames;
        if (f == null || double.IsNaN(f.GpuBusyMs) || f.AvgMs <= 0)
        {
            CpuBusyBar.Width = GpuBusyBar.Width = 0;
            Line(CpuBusyText, ("–", MutedBrush));
            Line(GpuBusyText, ("–", MutedBrush));
            return;
        }
        CpuBusyBar.Width = Math.Clamp(f.CpuBusyMs / f.AvgMs, 0, 1) * BusyBarWidth;
        GpuBusyBar.Width = Math.Clamp(f.GpuBusyMs / f.AvgMs, 0, 1) * BusyBarWidth;
        CpuBusyBar.Fill = v.Kind == BottleneckKind.Cpu ? WarnBrush : InfoBrush;
        GpuBusyBar.Fill = v.Kind == BottleneckKind.Gpu ? GoodBrush : InfoBrush;

        var cpu = new List<(string, Brush?)> { V($"{f.CpuBusyMs,4:0.0}"), L(" ms") };
        if (s.MainThread is { } mt)
        {
            cpu.Add(L("  Thread "));
            cpu.Add(($"{mt.Percent:0} %", LoadBrush(mt.Percent)));
            if (mt.Name != null) cpu.Add(($" {Shorten(mt.Name, 16)}", MutedBrush));
        }
        Line(CpuBusyText, cpu.ToArray());
        Line(GpuBusyText, V($"{f.GpuBusyMs,4:0.0}"), L(" ms  wartet "), V($"{f.GpuWaitMs:0.0}"), L(" ms"));
    }

    private void RenderNotes(IReadOnlyList<LoggedNote> notes)
    {
        for (int i = 0; i < _noteRows.Count; i++)
        {
            var (row, text, age) = _noteRows[i];
            if (i < notes.Count)
            {
                var n = notes[i];
                text.Text = "▲ " + n.Text;
                text.Foreground = SeverityBrush(n.Severity);
                age.Text = (n.Active ? "jetzt" : "vor " + FormatAge(n.SinceLast)) + (n.Count > 1 ? $" · {n.Count}×" : "");
                row.Opacity = n.Active ? 1.0 : 0.55;
            }
            else
            {
                // Leere Zeilen behalten ihre Höhe
                text.Text = i == 0 ? "keine Meldungen" : " ";
                text.Foreground = MutedBrush;
                age.Text = "";
                row.Opacity = 1.0;
            }
        }
    }

    private static string FormatAge(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0} s"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:00}"
        : $"{(int)t.TotalHours}:{t.Minutes:00} h";

    /// <summary>Platz für die Balken neben dem Max-Label (Overlay-Breite 460 minus Einzug und Label).</summary>
    private const double CoreBarsWidth = 230;

    private int[] _coreOrder = [];

    /// <summary>
    /// Ein Balken pro logischem Kern. Bei Hybrid-CPUs zuerst die P-Kerne, dann mit Abstand die E-Kerne
    /// (gedimmt, da sie für Spiele selten entscheidend sind). Die Breite passt sich der Kernzahl an,
    /// damit auch CPUs mit 64 Threads in die feste Overlay-Breite passen.
    /// </summary>
    private void RenderCoreBars(CpuData cpu)
    {
        var loads = cpu.CoreLoads;
        if (CoreBars.Children.Count != loads.Length)
        {
            _coreOrder = Enumerable.Range(0, loads.Length).OrderByDescending(i => cpu.IsPerfCore(i)).ToArray();
            int groupGaps = cpu.IsHybrid ? 2 : 0;
            double slot = Math.Clamp(Math.Floor(CoreBarsWidth / (loads.Length + groupGaps)), 3, 8);
            double gap = slot >= 6 ? 2 : 1;

            CoreBars.Children.Clear();
            for (int n = 0; n < _coreOrder.Length; n++)
            {
                bool perf = cpu.IsPerfCore(_coreOrder[n]);
                bool firstEfficiency = cpu.IsHybrid && !perf && (n == 0 || cpu.IsPerfCore(_coreOrder[n - 1]));
                var cell = new Grid
                {
                    Width = slot - gap,
                    Height = BarHeight,
                    Margin = new Thickness(firstEfficiency ? slot * groupGaps : 0, 0, gap, 0),
                    Opacity = perf ? 1.0 : 0.6,
                };
                cell.Children.Add(new Rectangle { Fill = BarBackBrush });
                cell.Children.Add(new Rectangle { VerticalAlignment = VerticalAlignment.Bottom });
                CoreBars.Children.Add(cell);
            }
        }
        for (int n = 0; n < _coreOrder.Length; n++)
        {
            double load = loads[_coreOrder[n]];
            var bar = (Rectangle)((Grid)CoreBars.Children[n]).Children[1];
            bar.Height = Math.Max(1, load / 100 * BarHeight);
            bar.Fill = LoadBrush(load) ?? GoodBrush;
        }
    }

    private static (string, Brush?) L(string text) => (text, LabelBrush);
    private static (string, Brush?) V(string text) => (text, null);

    private static void Line(TextBlock target, params (string Text, Brush? Brush)[] parts)
    {
        target.Inlines.Clear();
        foreach (var (text, brush) in parts)
        {
            var run = new Run(text);
            if (brush != null) run.Foreground = brush;
            target.Inlines.Add(run);
        }
    }

    private static string Pct(double v) => $"{v:0} %";

    /// <summary>NVIDIA Performance State: je kleiner die Zahl, desto höher die Leistungsstufe.</summary>
    private static string PStateLabel(int p) => p switch
    {
        0 => "Volllast",
        <= 4 => "hoch",
        <= 7 => "mittel",
        _ => "Idle",
    };
    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static Brush? LoadBrush(double load) => load >= 90 ? BadBrush : load >= 70 ? WarnBrush : null;
    private static Brush? TempBrush(double t) => t >= 87 ? BadBrush : t >= 80 ? WarnBrush : null;
    /// <summary>GDDR6X drosselt um 110 °C; ab 100 °C lohnt ein Blick auf die Wärmeleitpads.</summary>
    private static Brush? MemTempBrush(double t) => t >= 106 ? BadBrush : t >= 100 ? WarnBrush : null;
    private static Brush? HotspotBrush(double t) => t >= 105 ? BadBrush : t >= 95 ? WarnBrush : null;
    /// <summary>
    /// Farbe relativ zur Maximaltemperatur (TjMax) statt fester Grenzen: Ryzen 7000 läuft regulär bei 95 °C,
    /// ein Intel mit TjMax 100 °C wäre dort schon kurz vor der Drosselung.
    /// </summary>
    private static Brush? CpuTempBrush(CpuThermalData th)
    {
        if (th.TempC is not double t) return null;
        double headroom = th.HeadroomC ?? 100 - t;
        if (th.IsAmd) return headroom <= 0 ? WarnBrush : null;
        return headroom <= 5 ? BadBrush : headroom <= 15 ? WarnBrush : null;
    }

    private static Brush SeverityBrush(Severity s) => s switch
    {
        Severity.Good => GoodBrush,
        Severity.Info => InfoBrush,
        Severity.Warn => WarnBrush,
        _ => BadBrush,
    };

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    // ------------------------------------------------------------------ Position, Hotkeys, Tray

    /// <summary>
    /// Wechselt auf den Monitor des Spiels, sobald es im Vordergrund ist und Frames liefert.
    /// Auf dem Desktop (Alt+Tab) bleibt das Overlay, wo es ist, statt hin und her zu springen.
    /// </summary>
    private void FollowGameMonitor(Snapshot s)
    {
        if (s.Frames == null) return;
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return;
        Win32.GetWindowThreadProcessId(fg, out uint pid);
        if (pid != s.Frames.Pid) return;

        IntPtr monitor = Win32.MonitorFromWindow(fg, Win32.MONITOR_DEFAULTTONEAREST);
        if (monitor != _monitor)
        {
            _monitor = monitor;
            Reposition();
        }
    }

    /// <summary>
    /// Positioniert in physischen Pixeln über Win32, weil WPF-Koordinaten bei Monitoren mit
    /// unterschiedlicher Skalierung nicht eindeutig sind. Wechselt dabei die DPI, skaliert WPF das
    /// Fenster neu, SizeChanged ruft diese Methode erneut auf und die Ecke stimmt wieder.
    /// </summary>
    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (Win32.GetWorkArea(_monitor) is not { } area)
        {
            _monitor = Win32.PrimaryMonitor;   // Monitor wurde abgesteckt
            if (Win32.GetWorkArea(_monitor) is not { } primary) return;
            area = primary;
        }
        Win32.GetWindowRect(_hwnd, out var rect);
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        int margin = (int)Math.Round(12 * VisualTreeHelper.GetDpi(this).DpiScaleX);

        int x, y;
        if (_customOffset is { } off)
        {
            // Gezogene Position: gleicher Abstand zur Ecke – auch auf einem anderen Monitor –,
            // aber immer vollständig im sichtbaren Bereich
            x = Math.Clamp(area.Left + off.X, area.Left, Math.Max(area.Left, area.Right - w));
            y = Math.Clamp(area.Top + off.Y, area.Top, Math.Max(area.Top, area.Bottom - h));
        }
        else
        {
            x = _corner is 0 or 3 ? area.Left + margin : area.Right - w - margin;
            y = _corner is 0 or 1 ? area.Top + margin : area.Bottom - h - margin;
        }
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    /// <summary>Feste Ecken durchschalten; eine gezogene Position wird dabei verworfen.</summary>
    private void NextCorner()
    {
        if (_customOffset != null) _customOffset = null;
        else _corner = (_corner + 1) % 4;
        Reposition();
    }

    private void ToggleVisible()
    {
        if (IsVisible) Hide();
        else Show();
    }

    private void ToggleDetails()
    {
        _details = !_details;
        DetailPanel.Visibility = _details ? Visibility.Visible : Visibility.Collapsed;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case HotkeyCorner: NextCorner(); break;
                case HotkeyToggle: ToggleVisible(); break;
                case HotkeyDetails: ToggleDetails(); break;
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void CreateTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Ein-/Ausblenden\tStrg+Umschalt+F10", null, (_, _) => ToggleVisible());
        menu.Items.Add("Position wechseln\tStrg+Umschalt+F9", null, (_, _) => NextCorner());
        menu.Items.Add("Details an/aus\tStrg+Umschalt+F8", null, (_, _) => ToggleDetails());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        var record = new System.Windows.Forms.ToolStripMenuItem("Sitzungen aufzeichnen") { Checked = _recorder.Enabled, CheckOnClick = true };
        record.CheckedChanged += (_, _) => _recorder.Enabled = record.Checked;
        menu.Items.Add(record);
        menu.Items.Add("Sitzungsordner öffnen", null, (_, _) =>
        {
            Directory.CreateDirectory(SessionRecorder.Folder);
            Process.Start(new ProcessStartInfo(SessionRecorder.Folder) { UseShellExecute = true });
        });
        string viewer = System.IO.Path.Combine(AppContext.BaseDirectory, "Auswertung", "PerfOverlay-Auswertung.html");
        if (File.Exists(viewer))
            menu.Items.Add("Auswertung öffnen (Browser)", null, (_, _) =>
                Process.Start(new ProcessStartInfo(viewer) { UseShellExecute = true }));
        if (!Win32.IsElevated())
        {
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Als Administrator neu starten (für FPS)", null, (_, _) => RestartElevated());
        }
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => ExitApp());

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "PerfOverlay",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ToggleVisible();
    }

    /// <summary>Eigenes Icon aus den eingebetteten Ressourcen, in der Größe passend zur Tray-Skalierung.</summary>
    internal static System.Drawing.Icon LoadTrayIcon()
    {
        using var stream = typeof(OverlayWindow).Assembly.GetManifestResourceStream("PerfOverlay.ico");
        if (stream == null) return System.Drawing.SystemIcons.Application;
        return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
    }

    /// <summary>Tooltip am Tray-Icon (max. 63 Zeichen): zeigt, ob gerade aufgezeichnet wird.</summary>
    private void UpdateTrayText(Snapshot s)
    {
        if (_tray == null) return;
        string state = _recorder.Recording ? "● Aufzeichnung läuft"
                     : !_recorder.Enabled ? "Aufzeichnung aus"
                     : s.Frames != null ? "Spiel erkannt" : "wartet auf Spiel";
        string text = $"PerfOverlay – {state}";
        if (_tray.Text != text) _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private void RestartElevated()
    {
        App.ReleaseSingleInstance();
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return;   // UAC-Abfrage abgelehnt – weiterlaufen
        }
        ExitApp();
    }

    private void ExitApp()
    {
        _exiting = true;
        _cts.Cancel();
        if (_hwnd != IntPtr.Zero)
            foreach (int id in new[] { HotkeyCorner, HotkeyToggle, HotkeyDetails })
                Win32.UnregisterHotKey(_hwnd, id);
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _recorder.Dispose();
        _hub?.Dispose();
        Application.Current.Shutdown();
    }

    // ------------------------------------------------------------------ Selbsttest

    /// <summary>Rendert das Overlay ohne Fenster in eine PNG (über dunklem Hintergrund).</summary>
    public void SaveImage(string path)
    {
        Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Root.Arrange(new Rect(Root.DesiredSize));
        Root.UpdateLayout();
        int w = (int)Math.Ceiling(Root.ActualWidth) + 24, h = (int)Math.Ceiling(Root.ActualHeight) + 24;

        var overlay = new RenderTargetBitmap(w - 24, h - 24, 96, 96, PixelFormats.Pbgra32);
        overlay.Render(Root);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x3A, 0x55, 0x70), Color.FromRgb(0x6B, 0x5B, 0x45), 45), null, new Rect(0, 0, w, h));
            dc.DrawImage(overlay, new Rect(12, 12, w - 24, h - 24));
        }
        var final = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        final.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(final));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
