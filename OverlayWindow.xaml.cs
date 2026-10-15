using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
    private SensorHub? _hub;
    private System.Windows.Forms.NotifyIcon? _tray;
    private IntPtr _hwnd;
    private int _corner = 1;   // 0 = oben links, 1 = oben rechts, 2 = unten rechts, 3 = unten links
    private bool _details = true;
    private bool _exiting;

    /// <summary>Feste Anzahl Meldungszeilen, damit das Overlay seine Größe nicht ändert.</summary>
    public const int NoteSlots = 4;
    private readonly NoteLog _noteLog = new();
    private readonly List<(Grid Row, TextBlock Text, TextBlock Age)> _noteRows = new();

    public OverlayWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Reposition();
        CreateNoteRows();
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
        VerdictDetail.Text = v.Detail;
        var tint = ((SolidColorBrush)sevBrush).Color;
        VerdictBar.Background = new SolidColorBrush(Color.FromArgb(0x30, tint.R, tint.G, tint.B));

        RenderNotes(notes);

        // FPS
        var f = s.Frames;
        if (f != null)
            Line(FpsLine, L("FPS   "), V($"{f.Fps,4:0}"), L("  1%↓ "), V($"{f.Low1Fps:0}"),
                L("  "), V($"{f.AvgMs:0.0}"), L(" ms  "), (Shorten(f.App, 22), MutedBrush));
        else
            Line(FpsLine, L("FPS   "), V("   –  "),
                (s.FrameSourceRunning ? "keine 3D-Anwendung" : s.FrameSourceStatus, s.FrameSourceRunning ? MutedBrush : WarnBrush));

        // CPU
        var c = s.Cpu;
        Line(CpuLine, L("CPU   "), V($"{c.TotalLoad,4:0} %"),
            L("  "), V($"{c.AvgMhz / 1000:0.00}"), L(" GHz (max "), V($"{c.MaxMhz / 1000:0.00}"), L(")"),
            L("  Kern max "), (Pct(c.MaxCoreLoad), LoadBrush(c.MaxCoreLoad)));
        RenderCoreBars(c.CoreLoads);

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
            if (g.PcieGen is int gen && g.PcieWidth is int width) { p2.Add(L("  PCIe ")); p2.Add(V($"{gen}.0 x{width}")); }
            if (g.FanPercent is double fan) { p2.Add(L("  Lüfter ")); p2.Add(V($"{fan:0} %")); }
            if (g.PState is int ps) { p2.Add(L("  ")); p2.Add(V($"P{ps}")); p2.Add(L(" " + PStateLabel(ps))); }
            Line(GpuLine2, p2.ToArray());
            GpuLine2.Visibility = Visibility.Visible;
        }

        // Frame-Aufteilung
        if (f != null && !double.IsNaN(f.GpuBusyMs))
            Line(FrameLine, L("Frame "), L("CPU "), V($"{f.CpuBusyMs:0.0}"), L("  GPU "), V($"{f.GpuBusyMs:0.0}"),
                L("  GPU wartet "), V($"{f.GpuWaitMs:0.0}"), L(" ms"));
        else
            Line(FrameLine, L("Frame "), ("–", MutedBrush));

        // RAM & Datenträger
        var m = s.Memory;
        Line(RamLine, L("RAM   "), ($"{m.UsedGB:0.0}/{m.TotalGB:0.0}", LoadBrush(m.LoadPercent)), L(" GB"),
            L("  Commit "), V($"{m.CommitGB:0}/{m.CommitLimitGB:0}"), L(" GB"),
            L("  Nachl. "), (($"{m.PagesInPerSec:0}/s"), m.PagesInPerSec > 500 ? WarnBrush : null));
        var d = s.Disk;
        Line(DiskLine, L("Disk  "), V($"{d.ActivePercent,4:0} %"), L(" aktiv  "),
            ($"{d.LatencyMs:0.0}", d.LatencyMs > 20 ? WarnBrush : null), L(" ms  "), V($"{d.MBps:0}"), L(" MB/s"));

        string source = g?.Source == "NVML" ? "NVML" : g != null ? "Win-Zähler" : "–";
        FooterLine.Text = $"Strg+Umschalt+F10 aus/an · F9 Position · F8 Details   |   GPU-Quelle: {source}";
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

    private void RenderCoreBars(double[] loads)
    {
        if (CoreBars.Children.Count != loads.Length)
        {
            CoreBars.Children.Clear();
            for (int i = 0; i < loads.Length; i++)
            {
                var cell = new Grid { Width = 6, Height = BarHeight, Margin = new Thickness(0, 0, 2, 0) };
                cell.Children.Add(new Rectangle { Fill = BarBackBrush });
                cell.Children.Add(new Rectangle { VerticalAlignment = VerticalAlignment.Bottom });
                CoreBars.Children.Add(cell);
            }
        }
        for (int i = 0; i < loads.Length; i++)
        {
            var bar = (Rectangle)((Grid)CoreBars.Children[i]).Children[1];
            bar.Height = Math.Max(1, loads[i] / 100 * BarHeight);
            bar.Fill = LoadBrush(loads[i]) ?? GoodBrush;
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

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        const double margin = 12;
        Left = _corner is 0 or 3 ? area.Left + margin : area.Right - ActualWidth - margin;
        Top = _corner is 0 or 1 ? area.Top + margin : area.Bottom - ActualHeight - margin;
    }

    private void NextCorner()
    {
        _corner = (_corner + 1) % 4;
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
        if (!Win32.IsElevated())
        {
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Als Administrator neu starten (für FPS)", null, (_, _) => RestartElevated());
        }
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => ExitApp());

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "PerfOverlay",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ToggleVisible();
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
