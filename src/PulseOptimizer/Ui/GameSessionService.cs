using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Pulse.Core;
using Pulse.Core.Games;
using Pulse.Core.Measurement;

namespace Pulse.Ui;

/// <summary>
/// Watches for a game (every few seconds) and, if the user turned them on, switches on game mode and shows the FPS
/// overlay while it runs. Runs in the tray too.
/// </summary>
public static class GameSessionService
{
    static GameSessionSettings S => TweakEngine.State.GameSession ??= new GameSessionSettings();
    static DispatcherTimer _timer, _overlayTimer;
    static int _pid;
    static LiveFrames _frames;
    static FpsOverlay _overlay;
    static GpuSensors _gpuSensors;
    static GpuMeter _gpuLoad;
    static readonly CpuMeter _cpu = new();
    static bool _hiddenByKey, _sampling;
    static HwndSource _hotkey;
    const int HotkeyId = 0x5E01;

    public static void Init()
    {
        if (_timer != null) return;
        GamePowerPlan.End(); // left over from a Pulse that was closed mid-game
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await Tick();
        _timer.Start();
        _overlayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _overlayTimer.Tick += (_, _) => UpdateOverlay();
    }

    public static void Shutdown()
    {
        _timer?.Stop();
        StopOverlay();
        GamePowerPlan.End();
    }

    /// <summary>Settings changed on the Games page.</summary>
    public static void Refresh() { _pid = -1; GameDetector.Forget(); _ = Tick(); }

    static async Task Tick()
    {
        bool want = S.GameMode || S.Overlay;
        string game = want ? await Task.Run(GameDetector.RunningGame) : null;
        int pid = game != null ? GameDetector.GamePid : 0;

        if (pid == 0)
        {
            if (_pid != 0) { StopOverlay(); GamePowerPlan.End(); }
            _pid = 0;
            return;
        }
        if (pid == _pid) return;
        _pid = pid;

        if (S.GameMode) GamePowerPlan.Start(); else GamePowerPlan.End();
        StopOverlay();
        if (S.Overlay)
        {
            string name = null;
            try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch { }
            if (name != null && (_frames = LiveFrames.Start(name)) != null)
            {
                _overlay = new FpsOverlay(S);
                if (!_hiddenByKey) _overlay.Show();
                _cpu.Sample();
                _overlayTimer.Start();
                RegisterHotkey();
            }
        }
    }

    static async void UpdateOverlay()
    {
        if (_frames == null || _overlay == null || _sampling) return;
        if (!_frames.Running) { StopOverlay(); _pid = -1; return; }
        var (fps, low, ms) = _frames.Now();
        var reading = new OverlayReading { Fps = fps, Low1 = low, FrameMs = ms, Graph = S.OverlayGraph ? _frames.Recent(3) : null, Clock = DateTime.Now };
        // Counters and the GPU kernel query run off the UI thread, and only the ones that are shown.
        _sampling = true;
        try
        {
            await Task.Run(() =>
            {
                if (S.OverlayCpu) reading.Cpu = _cpu.Sample();
                if (S.OverlayGpu) reading.Gpu = (_gpuLoad ??= new GpuMeter()).Sample();
                if (S.OverlayGpuTemp || S.OverlayVram) _gpuSensors ??= new GpuSensors();
                if (S.OverlayGpuTemp) reading.GpuTempC = _gpuSensors.TemperatureC();
                if (S.OverlayVram) reading.VramBytes = _gpuSensors.VramBytes();
                if (S.OverlayRam) { var m = Native.Memory(); reading.RamUsed = m.ullTotalPhys - m.ullAvailPhys; reading.RamTotal = m.ullTotalPhys; }
            });
        }
        catch { }
        finally { _sampling = false; }
        _overlay?.Set(reading);
    }

    static void StopOverlay()
    {
        _overlayTimer?.Stop();
        _frames?.Dispose(); _frames = null;
        _overlay?.Close(); _overlay = null;
        _gpuSensors?.Dispose(); _gpuSensors = null;
        _gpuLoad?.Dispose(); _gpuLoad = null;
        UnregisterHotkey();
    }

    // ───────── Ctrl+Shift+O: show / hide while playing ─────────

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    static void RegisterHotkey()
    {
        if (_hotkey != null || !S.OverlayHotkey) return;
        _hotkey = new HwndSource(new HwndSourceParameters("Pulse overlay hotkey") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) });
        _hotkey.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x0312 && w.ToInt32() == HotkeyId)
            {
                _hiddenByKey = !_hiddenByKey;
                if (_overlay != null) { if (_hiddenByKey) _overlay.Hide(); else _overlay.Show(); }
                handled = true;
            }
            return IntPtr.Zero;
        });
        RegisterHotKey(_hotkey.Handle, HotkeyId, 0x2 | 0x4 | 0x4000, 0x4F /* O */);
    }

    static void UnregisterHotkey()
    {
        if (_hotkey == null) return;
        UnregisterHotKey(_hotkey.Handle, HotkeyId);
        _hotkey.Dispose();
        _hotkey = null;
    }
}

/// <summary>One update for the overlay; null readings aren't shown.</summary>
sealed class OverlayReading
{
    public double Fps, Low1, FrameMs;
    public double[] Graph;
    public double? Cpu, Gpu, GpuTempC, VramBytes;
    public double RamUsed, RamTotal;
    public DateTime Clock;
}

/// <summary>
/// The performance overlay (like MSI Afterburner's, without its driver): FPS and 1% low, a frame time graph, CPU and
/// GPU load, GPU temperature, video and system memory, and the time. Drawn as one picture, clicks pass through it, it
/// never takes focus, and it sits on the game's screen.
/// </summary>
sealed class FpsOverlay : Window
{
    readonly OverlayView _view = new();
    readonly GameSessionSettings _s;

    public FpsOverlay(GameSessionSettings s)
    {
        _s = s;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight; Focusable = false; IsHitTestVisible = false;
        _view.Settings = s;
        Content = _view;
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        Loaded += (_, _) => Place();
        SizeChanged += (_, _) => Place();
    }

    /// <summary>In the chosen corner of the screen the game is on.</summary>
    void Place()
    {
        double m = 16;
        var area = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        var g = GameDetector.GameWindow;
        if (g != IntPtr.Zero)
        {
            var mon = MonitorFromWindow(g, 2);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(mon, ref mi))
            {
                var src = PresentationSource.FromVisual(this);
                double sx = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1, sy = src?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
                area = new Rect(mi.rcMonitor.Left * sx, mi.rcMonitor.Top * sy, (mi.rcMonitor.Right - mi.rcMonitor.Left) * sx, (mi.rcMonitor.Bottom - mi.rcMonitor.Top) * sy);
            }
        }
        var corner = _s.OverlayCorner ?? "tl";
        Left = corner.EndsWith('r') ? area.Right - ActualWidth - m : area.Left + m;
        Top = corner.StartsWith('b') ? area.Bottom - ActualHeight - m : area.Top + m;
    }

    public void Set(OverlayReading r) => _view.Set(r);

    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
}

/// <summary>Draws the overlay's readings. Also used as the live preview on the Games page.</summary>
public sealed class OverlayView : FrameworkElement
{
    public GameSessionSettings Settings;
    OverlayReading _r = new() { Clock = DateTime.Now };
    static readonly Typeface Mono = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Typeface Label = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Brush Text = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xEF, 0xE8)));
    static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromRgb(0x9C, 0x98, 0x8F)));
    static readonly Brush Warm = Frozen(new SolidColorBrush(Color.FromRgb(0xE0, 0x77, 0x3F)));
    static readonly Brush Hot = Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0x67, 0x4F)));
    static Brush Frozen(Brush b) { b.Freeze(); return b; }

    internal void Set(OverlayReading r) { _r = r; InvalidateMeasure(); InvalidateVisual(); }

    /// <summary>Sample readings for the preview on the Games page.</summary>
    public void ShowSample()
    {
        var rnd = new Random(3);
        Set(new OverlayReading
        {
            Fps = 238, Low1 = 171, FrameMs = 4.2, Cpu = 0.41, Gpu = 0.93, GpuTempC = 67, VramBytes = 6.1e9, RamUsed = 11.4e9, RamTotal = 32e9, Clock = DateTime.Now,
            Graph = Enumerable.Range(0, 300).Select(i => 4.2 + rnd.NextDouble() * 0.6 + (i % 97 == 0 ? 6 : 0)).ToArray(),
        });
    }

    double K => Math.Clamp(Settings?.OverlayScale ?? 1, 0.8, 1.6);

    List<(string Label, string Value, Brush Color)> Rows()
    {
        var s = Settings ?? new GameSessionSettings();
        var ci = System.Globalization.CultureInfo.CurrentCulture;
        var rows = new List<(string, string, Brush)>();
        if (s.OverlayCpu && _r.Cpu is double c) rows.Add(("CPU", Math.Round(c * 100) + "%", c > 0.9 ? Warm : Text));
        if (s.OverlayGpu && _r.Gpu is double g) rows.Add(("GPU", Math.Round(g * 100) + "%", Text));
        if (s.OverlayGpuTemp && _r.GpuTempC is double t) rows.Add(("GPU", Math.Round(t) + "°C", t >= 85 ? Hot : t >= 78 ? Warm : Text));
        if (s.OverlayVram && _r.VramBytes is double v) rows.Add(("VRAM", (v / 1073741824).ToString("0.0", ci) + " GB", Text));
        if (s.OverlayRam && _r.RamTotal > 0) rows.Add(("RAM", (_r.RamUsed / 1073741824).ToString("0.0", ci) + " GB", _r.RamUsed / _r.RamTotal > 0.9 ? Warm : Text));
        if (s.OverlayClock) rows.Add(("", _r.Clock.ToString("t", ci), Dim));
        return rows;
    }

    FormattedText Fmt(string text, Typeface tf, double size, Brush b) =>
        new(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, size * K, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    bool Row => Settings?.OverlayLayout == "row";
    bool GraphOn => Settings?.OverlayGraph != false && _r.Graph is { Length: > 2 };

    protected override Size MeasureOverride(Size available)
    {
        double pad = 10 * K;
        var fps = Fmt(FpsText(), Mono, 20, Text);
        var low = Fmt(LowText(), Mono, 11, Dim);
        var rows = Rows();
        if (Row)
        {
            double w = pad + fps.Width + 10 * K;
            foreach (var r in rows) w += Fmt(r.Label, Label, 10, Dim).Width + 4 * K + Fmt(r.Value, Mono, 13, Text).Width + 12 * K;
            if (GraphOn) w += 90 * K + 8 * K;
            return new Size(w + pad, Math.Max(fps.Height, 24 * K) + pad * 1.4);
        }
        double width = Math.Max(fps.Width, low.Width), height = fps.Height + low.Height;
        foreach (var r in rows) { width = Math.Max(width, 44 * K + Fmt(r.Value, Mono, 13, Text).Width); height += 17 * K; }
        if (GraphOn) { width = Math.Max(width, 150 * K); height += 34 * K; }
        return new Size(Math.Max(width, 120 * K) + pad * 2, height + pad * 1.6);
    }

    string FpsText() => _r.Fps > 0 ? Math.Round(_r.Fps) + " FPS" : "– FPS";
    string LowText() => _r.Fps > 0 ? $"1% {Math.Round(_r.Low1)} · {_r.FrameMs.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)} ms" : Loc.T("Waiting for frames…");

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, pad = 10 * K;
        byte a = (byte)(255 * Math.Clamp(Settings?.OverlayBackground ?? 0.7, 0, 1));
        var bg = new SolidColorBrush(Color.FromArgb(a, 0x0C, 0x0C, 0x0B)); bg.Freeze();
        var edge = new Pen(new SolidColorBrush(Color.FromArgb((byte)(a / 4), 0xF2, 0xEF, 0xE8)), 1); edge.Freeze();
        dc.DrawRoundedRectangle(bg, edge, new Rect(0.5, 0.5, w - 1, h - 1), 8 * K, 8 * K);
        var fps = Fmt(FpsText(), Mono, 20, Text);
        var rows = Rows();
        if (Row)
        {
            double x = pad, cy = h / 2;
            dc.DrawText(fps, new Point(x, cy - fps.Height / 2)); x += fps.Width + 10 * K;
            foreach (var r in rows)
            {
                var l = Fmt(r.Label, Label, 10, Dim); var v = Fmt(r.Value, Mono, 13, r.Color);
                if (r.Label.Length > 0) { dc.DrawText(l, new Point(x, cy - l.Height / 2)); x += l.Width + 4 * K; }
                dc.DrawText(v, new Point(x, cy - v.Height / 2)); x += v.Width + 12 * K;
            }
            if (GraphOn) DrawGraph(dc, new Rect(x, cy - 10 * K, 90 * K, 20 * K));
            return;
        }
        double y = pad * 0.8;
        dc.DrawText(fps, new Point(pad, y)); y += fps.Height;
        var low = Fmt(LowText(), Mono, 11, Dim);
        dc.DrawText(low, new Point(pad, y)); y += low.Height + 4 * K;
        if (GraphOn) { DrawGraph(dc, new Rect(pad, y, w - pad * 2, 26 * K)); y += 34 * K; }
        foreach (var r in rows)
        {
            var l = Fmt(r.Label, Label, 10, Dim); var v = Fmt(r.Value, Mono, 13, r.Color);
            dc.DrawText(l, new Point(pad, y + (v.Height - l.Height) / 2));
            dc.DrawText(v, new Point(w - pad - v.Width, y));
            y += 17 * K;
        }
    }

    /// <summary>Frame times as a line (lower is smoother); spikes are stutters you felt.</summary>
    void DrawGraph(DrawingContext dc, Rect r)
    {
        var f = _r.Graph;
        double avg = f.Average(), top = Math.Max(avg * 2.5, f.Max());
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            for (int i = 0; i < f.Length; i++)
            {
                var p = new Point(r.Left + r.Width * i / (f.Length - 1), r.Bottom - r.Height * Math.Min(f[i], top) / top);
                if (i == 0) ctx.BeginFigure(p, false, false); else ctx.LineTo(p, true, false);
            }
        }
        g.Freeze();
        var mid = new Pen(Dim, 0.5) { DashStyle = DashStyles.Dot }; mid.Freeze();
        double ay = r.Bottom - r.Height * avg / top;
        dc.DrawLine(mid, new Point(r.Left, ay), new Point(r.Right, ay));
        var pen = new Pen(f.Max() > avg * 2 ? Warm : Text, 1.2 * K) { LineJoin = PenLineJoin.Round }; pen.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}
