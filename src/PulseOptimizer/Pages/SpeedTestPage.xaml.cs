using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Pulse.Core;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// The speed test page: a live ring while it runs, then every number that matters for games, a plain verdict,
/// and for anything that isn't good, why it happens and what to do about it, easiest fix first.
/// </summary>
public partial class SpeedTestPage : UserControl, IPage
{
    static readonly Color Green = Color.FromRgb(0x3D, 0xDC, 0x84), Yellow = Color.FromRgb(0xFA, 0xD6, 0x63), Red = Color.FromRgb(0xFF, 0x5A, 0x4E),
        Blue = Color.FromRgb(0x3B, 0x9B, 0xFF), Purple = Color.FromRgb(0xA7, 0x8B, 0xFA), Cyan = Color.FromRgb(0x22, 0xD3, 0xEE);

    static SpeedResult _last;
    static CancellationTokenSource _cts;
    static bool _running;
    // What the ring shows while running.
    SpeedTest.Phase _phase;
    double _value, _frac;
    double _down = -1, _up = -1, _ping = -1;

    public SpeedTestPage() => InitializeComponent();

    public void OnShown(object parameter)
    {
        _last ??= SpeedTest.History.FirstOrDefault();
        Build();
    }

    static Color A(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    Brush Res(string k) => (Brush)FindResource(k);
    static Color LevelColor(SpeedTest.Level l) => l switch { SpeedTest.Level.Great => Green, SpeedTest.Level.Ok => Yellow, _ => Red };

    Border Card(UIElement child, Color accent, double pad = 16)
    {
        var g = new Grid();
        g.Children.Add(new Border
        {
            Height = 1, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(60, 0, 60, 0), IsHitTestVisible = false,
            Background = new LinearGradientBrush(new GradientStopCollection { new(A(accent, 0), 0), new(A(accent, 0xCC), 0.5), new(A(accent, 0), 1) }, 0),
        });
        g.Children.Add(new Border { Padding = new Thickness(pad), Child = child });
        return new Border
        {
            CornerRadius = new CornerRadius(14), BorderBrush = B(Color.FromRgb(0x24, 0x26, 0x2C)), BorderThickness = new Thickness(1),
            Background = new LinearGradientBrush(Color.FromRgb(0x17, 0x18, 0x1C), Color.FromRgb(0x12, 0x13, 0x16), 90),
            Margin = new Thickness(0, 0, 0, 10), Child = g, ClipToBounds = true,
        };
    }

    TextBlock Caps(string t, double size = 10.5, Brush b = null) => new()
    {
        Text = Loc.T(t).ToUpper(CultureInfo.CurrentCulture), FontSize = size, FontWeight = FontWeights.ExtraBold, Foreground = b ?? B(Color.FromRgb(0x9A, 0x9D, 0xA6)),
    };
    TextBlock Dim(string t, double size = 12.5, double top = 4) => new() { Text = t, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, top, 0, 0), LineHeight = size * 1.45 };
    TextBlock Faint(string t, double size = 11.5, double top = 4) => new() { Text = t, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, top, 0, 0) };

    static System.Windows.Shapes.Path Ring(double size, double stroke, double frac, Brush brush)
    {
        double r = (size - stroke) / 2 - 4, cx = size / 2, cy = size / 2;
        double sweep = 270 * Math.Clamp(frac, 0.002, 0.9995);
        Point P(double deg) => new(cx + r * Math.Cos(deg * Math.PI / 180), cy + r * Math.Sin(deg * Math.PI / 180));
        var fig = new PathFigure { StartPoint = P(135), IsClosed = false };
        fig.Segments.Add(new ArcSegment(P(135 + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));
        return new System.Windows.Shapes.Path
        {
            Data = new PathGeometry(new[] { fig }), Stroke = brush, StrokeThickness = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = size, Height = size,
        };
    }

    /// <summary>Speed on a log scale so 10, 100 and 1000 Mbps all move the ring visibly.</summary>
    static double SpeedFrac(double mbps) => Math.Clamp(Math.Log10(1 + mbps) / Math.Log10(1 + 1000), 0, 1);

    void Build()
    {
        Root.Children.Clear();
        Root.Children.Add(TestCard());
        if (!_running && _last != null)
        {
            Root.Children.Add(ResultTiles(_last));
            Root.Children.Add(FixGuide(_last));
        }
        else if (!_running) Root.Children.Add(WhatItMeasures());
        if (!_running && SpeedTest.History.Count > 1) Root.Children.Add(HistoryCard());
    }

    // ───────── the speedometer (Speedtest-style) ─────────

    static readonly double[] Ticks = { 0, 5, 10, 50, 100, 250, 500, 750, 1000 };
    const double GSize = 360, GStroke = 26;

    // Live parts, updated in place (no rebuilding while it runs).
    System.Windows.Shapes.Path _arc;
    RotateTransform _needleTurn;
    TextBlock _number, _unitText, _phaseText;
    System.Windows.Threading.DispatcherTimer _anim;
    double _shown, _target;

    /// <summary>Where a speed sits on the dial: each tick is an equal step, like Speedtest's 0–5–10–50–100–250–500–750–1000.</summary>
    static double DialFrac(double mbps)
    {
        if (double.IsNaN(mbps) || mbps <= 0) return 0;
        for (int i = 0; i < Ticks.Length - 1; i++)
            if (mbps <= Ticks[i + 1]) return (i + (mbps - Ticks[i]) / (Ticks[i + 1] - Ticks[i])) / (Ticks.Length - 1);
        return 1;
    }

    static Point OnDial(double frac, double radius)
    {
        double deg = 135 + 270 * frac;
        return new Point(GSize / 2 + radius * Math.Cos(deg * Math.PI / 180), GSize / 2 + radius * Math.Sin(deg * Math.PI / 180));
    }

    static Geometry ArcGeometry(double frac)
    {
        double r = (GSize - GStroke) / 2 - 2;
        frac = Math.Clamp(frac, 0.0005, 0.9995);
        var fig = new PathFigure { StartPoint = OnDial(0, r), IsClosed = false };
        fig.Segments.Add(new ArcSegment(OnDial(frac, r), new Size(r, r), 0, 270 * frac > 180, SweepDirection.Clockwise, true));
        var g = new PathGeometry(new[] { fig });
        g.Freeze();
        return g;
    }

    bool Uploading => _running && _phase == SpeedTest.Phase.Upload;

    FrameworkElement TestCard()
    {
        var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Width = 620 };

        // Download and upload, Speedtest-style, across the top.
        var heads = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        heads.ColumnDefinitions.Add(new ColumnDefinition());
        heads.ColumnDefinitions.Add(new ColumnDefinition());
        double down = _running ? _down : _last?.DownMbps ?? -1, up = _running ? _up : _last?.UpMbps ?? -1;
        heads.Children.Add(Head("↓", "Download", down, Cyan, _running && _phase == SpeedTest.Phase.Download));
        var u = Head("↑", "Upload", up, Purple, Uploading); Grid.SetColumn(u, 1); heads.Children.Add(u);
        col.Children.Add(heads);

        // Ping: idle, while downloading, while uploading.
        var pings = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        pings.Children.Add(new TextBlock { Text = Loc.T("Ping") + " ms", FontSize = 13, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) });
        double idle = _running ? _ping : _last?.IdleMs ?? -1;
        pings.Children.Add(PingChip("⇋", Yellow, idle, Loc.T("Idle ping: nothing else using the connection")));
        pings.Children.Add(PingChip("↓", Cyan, _running ? -1 : _last?.LoadedDownMs ?? -1, Loc.T("Ping while downloading")));
        pings.Children.Add(PingChip("↑", Purple, _running ? -1 : _last?.LoadedUpMs ?? -1, Loc.T("Ping while uploading")));
        col.Children.Add(pings);

        col.Children.Add(Dial());

        // Below the dial: start / stop / again.
        if (_running)
        {
            _phaseText = new TextBlock { FontSize = 13, Foreground = Res("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
            col.Children.Add(_phaseText);
            var stop = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Stop"), Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            stop.Click += (_, _) => _cts?.Cancel();
            col.Children.Add(stop);
        }
        else if (_last != null)
        {
            var (lvl, text) = SpeedTest.Verdict(_last);
            col.Children.Add(new TextBlock { Text = Loc.T(text), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = B(LevelColor(lvl)), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            var again = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T("Test again"), Padding = new Thickness(24, 8, 24, 8), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            again.Click += (_, _) => _ = Start();
            col.Children.Add(again);
        }

        // Who's testing: provider and IP, and the server.
        var who = new Grid { Margin = new Thickness(0, 22, 0, 2) };
        who.ColumnDefinitions.Add(new ColumnDefinition());
        who.ColumnDefinitions.Add(new ColumnDefinition());
        string isp = _last?.Isp ?? "", ip = _last?.Ip ?? "", city = _last?.ServerCity ?? "";
        who.Children.Add(Who("👤", isp.Length > 0 ? isp : Loc.T("Your provider"), ip.Length > 0 ? ip : _running ? Loc.T("Looking up…") : Loc.T("Shown after a test"), HorizontalAlignment.Right));
        var srv = Who("🖧", "Cloudflare", city.Length > 0 ? city : _last?.Server is { Length: > 0 } code ? code : Loc.T("Nearest server"), HorizontalAlignment.Left); Grid.SetColumn(srv, 1); who.Children.Add(srv);
        col.Children.Add(who);

        UpdateDial(snap: true);
        return Card(col, _running ? Cyan : Purple, 20);
    }

    FrameworkElement Head(string arrow, string label, double value, Color c, bool live)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5), BorderBrush = B(c), Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock { Text = arrow, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = B(c), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) } });
        row.Children.Add(new TextBlock { Text = Loc.T(label).ToUpper(CultureInfo.CurrentCulture), FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Res(live ? "TextBrush" : "TextDimBrush"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = " Mbps", FontSize = 14, Foreground = Res("TextFaintBrush"), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(row);
        if (value >= 0)
            sp.Children.Add(new TextBlock { Text = Fmt(value), FontSize = 34, FontWeight = FontWeights.Light, Foreground = Res("TextBrush"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
        else
            sp.Children.Add(new Border { Width = 48, Height = 2, Background = live ? B(c) : Res("TextFaintBrush"), Margin = new Thickness(0, 22, 0, 18), HorizontalAlignment = HorizontalAlignment.Center });
        return sp;
    }

    static string Fmt(double v) => v.ToString(v >= 100 ? "0.0" : "0.00", CultureInfo.CurrentCulture);

    FrameworkElement PingChip(string icon, Color c, double ms, string tip)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 22, 0), ToolTip = tip };
        sp.Children.Add(new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1.5), BorderBrush = B(c), Margin = new Thickness(0, 0, 7, 0),
            Child = new TextBlock { Text = icon, FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = B(c), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) } });
        sp.Children.Add(new TextBlock { Text = ms > 0 ? ms.ToString("0", CultureInfo.CurrentCulture) : "—", FontSize = 14, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    FrameworkElement Who(string icon, string title, string sub, HorizontalAlignment align)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = align, Margin = new Thickness(16, 0, 16, 0) };
        sp.Children.Add(new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(19), BorderThickness = new Thickness(1), BorderBrush = Res("BorderBrush"), Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock { Text = icon, FontSize = 15, Foreground = Res("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(new TextBlock { Text = title, FontSize = 16, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 210 });
        t.Children.Add(new TextBlock { Text = sub, FontSize = 12.5, Foreground = Res("TextDimBrush") });
        sp.Children.Add(t);
        return sp;
    }

    FrameworkElement Dial()
    {
        var c = new Canvas { Width = GSize, Height = GSize - 30, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, ClipToBounds = false };
        double r = (GSize - GStroke) / 2 - 2;
        // Track.
        c.Children.Add(new System.Windows.Shapes.Path { Data = ArcGeometry(1), Stroke = B(Color.FromRgb(0x26, 0x28, 0x34)), StrokeThickness = GStroke, StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat });
        // Progress: blue-cyan to green for download, violet for upload.
        var grad = Uploading
            ? new LinearGradientBrush(Color.FromRgb(0x7C, 0x4D, 0xFF), Color.FromRgb(0xC0, 0x84, 0xFC), new Point(0, 1), new Point(1, 0))
            : new LinearGradientBrush(new GradientStopCollection { new(Color.FromRgb(0x00, 0xB4, 0xFF), 0), new(Color.FromRgb(0x5E, 0xF2, 0xD6), 0.55), new(Color.FromRgb(0x7D, 0xF0, 0x7A), 1) }, new Point(0, 1), new Point(1, 0));
        grad.Freeze();
        _arc = new System.Windows.Shapes.Path { Stroke = grad, StrokeThickness = GStroke, StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat };
        c.Children.Add(_arc);
        // Tick labels inside the ring.
        for (int i = 0; i < Ticks.Length; i++)
        {
            var p = OnDial(i / (double)(Ticks.Length - 1), r - GStroke - 8);
            var t = new TextBlock { Text = Ticks[i].ToString("0", CultureInfo.InvariantCulture), FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush") };
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(t, p.X - t.DesiredSize.Width / 2); Canvas.SetTop(t, p.Y - t.DesiredSize.Height / 2);
            c.Children.Add(t);
        }
        // Needle: a tapered blade from the middle, turned by angle.
        double len = r - GStroke - 30;
        var needle = new System.Windows.Shapes.Polygon
        {
            Points = new PointCollection { new(GSize / 2 + 26, GSize / 2 - 4), new(GSize / 2 + len, GSize / 2 - 1.2), new(GSize / 2 + len, GSize / 2 + 1.2), new(GSize / 2 + 26, GSize / 2 + 4) },
            Fill = new LinearGradientBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF), 0),
        };
        _needleTurn = new RotateTransform(135, GSize / 2, GSize / 2);
        needle.RenderTransform = _needleTurn;
        needle.Visibility = _running || _last != null ? Visibility.Visible : Visibility.Collapsed;
        c.Children.Add(needle);

        // Middle: the live number, or a GO button before the first test.
        if (!_running && _last == null)
        {
            var go = new Border
            {
                Width = 120, Height = 120, CornerRadius = new CornerRadius(60), BorderThickness = new Thickness(3), Cursor = Cursors.Hand, ToolTip = Loc.T("Start test"),
                BorderBrush = new LinearGradientBrush(Color.FromRgb(0x00, 0xB4, 0xFF), Color.FromRgb(0x7D, 0xF0, 0x7A), 45),
                Background = B(Color.FromArgb(0x18, 0x22, 0xD3, 0xEE)),
                Child = new TextBlock { Text = Loc.T("GO"), FontSize = 34, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            go.MouseEnter += (_, _) => go.Background = B(Color.FromArgb(0x30, 0x22, 0xD3, 0xEE));
            go.MouseLeave += (_, _) => go.Background = B(Color.FromArgb(0x18, 0x22, 0xD3, 0xEE));
            go.MouseLeftButtonUp += (_, _) => _ = Start();
            Canvas.SetLeft(go, GSize / 2 - 60); Canvas.SetTop(go, GSize / 2 - 60);
            c.Children.Add(go);
        }
        else
        {
            var mid = new StackPanel { Width = 200 };
            _number = new TextBlock { FontSize = 40, FontWeight = FontWeights.Light, Foreground = Res("TextBrush"), HorizontalAlignment = HorizontalAlignment.Center };
            _unitText = new TextBlock { FontSize = 13, Foreground = Res("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Center };
            mid.Children.Add(_number); mid.Children.Add(_unitText);
            Canvas.SetLeft(mid, GSize / 2 - 100); Canvas.SetTop(mid, GSize / 2 + 48);
            c.Children.Add(mid);
        }
        return c;
    }

    /// <summary>Moves the needle, arc and number toward the live value (smoothly while running).</summary>
    void UpdateDial(bool snap = false)
    {
        if (_arc == null) return;
        bool speed = _running && _phase is SpeedTest.Phase.Download or SpeedTest.Phase.Upload;
        _target = _running ? (speed ? _value : 0) : _last?.DownMbps ?? 0;
        if (snap && !_running) _shown = _target;
        else _shown += (_target - _shown) * 0.18;
        double f = DialFrac(_shown);
        _arc.Data = f > 0.001 ? ArcGeometry(f) : null;
        _needleTurn.Angle = 135 + 270 * f;
        if (_number != null)
        {
            if (_running && _phase == SpeedTest.Phase.Ping) { _number.Text = _value > 0 ? _value.ToString("0", CultureInfo.CurrentCulture) : "…"; _unitText.Text = Loc.T("ms ping"); }
            else if (_running && _phase == SpeedTest.Phase.Loss) { _number.Text = "…"; _unitText.Text = Loc.T("checking packet loss"); }
            else { _number.Text = Fmt(_shown); _unitText.Text = (Uploading ? "↑ " : "↓ ") + "Mbps"; }
        }
        if (_phaseText != null) _phaseText.Text = Loc.T(_phase switch
        {
            SpeedTest.Phase.Ping => "Measuring ping…",
            SpeedTest.Phase.Download => "Testing download…",
            SpeedTest.Phase.Upload => "Testing upload…",
            _ => "Checking for packet loss…",
        });
    }

    async Task Start()
    {
        if (_running) return;
        _running = true;
        _phase = SpeedTest.Phase.Ping; _value = 0; _frac = 0; _down = _up = _ping = -1; _shown = 0;
        _cts = new CancellationTokenSource();
        Build();
        _anim ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _anim.Tick -= AnimTick; _anim.Tick += AnimTick;
        _anim.Start();
        var progress = new Progress<SpeedTest.Progress>(p =>
        {
            bool newPhase = p.Phase != _phase;
            if (newPhase)
            {
                if (_phase == SpeedTest.Phase.Ping) _ping = _value;
                if (_phase == SpeedTest.Phase.Download) _down = _value;
                if (_phase == SpeedTest.Phase.Upload) _up = _value;
            }
            _phase = p.Phase; _value = p.Value; _frac = p.Fraction;
            if (newPhase && _running) { Build(); if (p.Phase is SpeedTest.Phase.Download or SpeedTest.Phase.Upload) _shown = 0; }
        });
        try
        {
            var r = await Task.Run(() => SpeedTest.Run(progress, _cts.Token));
            _last = r;
            SpeedTest.Save(r);
            if (r.Problems.Length > 0) TweakEngine.AddLog("network", "Speed test request problems: " + r.Problems);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            TweakEngine.AddLog("network", "Speed test failed: " + ex.GetType().Name + ": " + ex.Message);
            MainWindow.Instance.Toast(Loc.T("The speed test couldn't reach the test server. Check you're online and try again."), ToastKind.Warning);
        }
        finally
        {
            _running = false;
            _anim?.Stop();
            _cts?.Dispose(); _cts = null;
            Build();
        }
    }

    void AnimTick(object s, EventArgs e) => UpdateDial();

    // ───────── results ─────────

    FrameworkElement ResultTiles(SpeedResult r)
    {
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -10, 0) };
        var d = SpeedTest.Diagnose(r).ToDictionary(x => x.Area);
        Color Of(string area) => d.TryGetValue(area, out var f) ? LevelColor(f.Level) : Color.FromRgb(0x7C, 0x80, 0x8A);
        string N(double v) => v.ToString(v >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture);
        grid.Children.Add(Tile("Ping", r.IdleMs.ToString("0", CultureInfo.CurrentCulture), "ms", Of("Ping"), Loc.T("idle")));
        grid.Children.Add(Tile("Jitter", r.JitterMs.ToString("0.0", CultureInfo.CurrentCulture), "ms", Of("Jitter"), Loc.T("ping changes")));
        grid.Children.Add(Tile("Packet loss", r.LossPercent < 0 ? "–" : r.LossPercent.ToString("0.#", CultureInfo.CurrentCulture), r.LossPercent < 0 ? "" : "%", Of("Packet loss"), r.LossPercent < 0 ? Loc.T("couldn't check") : Loc.T("60 packets")));
        grid.Children.Add(Tile("Lag when busy", r.BloatGrade, "", Of("Bufferbloat"),
            Loc.F("+{0:0} ms down · +{1:0} ms up", Math.Max(0, r.LoadedDownMs - r.IdleMs), Math.Max(0, r.LoadedUpMs - r.IdleMs))));
        var sp = new StackPanel();
        sp.Children.Add(grid);
        sp.Children.Add(Faint(Loc.F("Used about {0:0} MB of data. \"Lag when busy\" is your ping while the line is full (bufferbloat): the lag spike you get when someone starts a download. Most speed tests don't grade it.", r.BytesUsed / 1e6), 11, 2));
        return sp;
    }

    FrameworkElement Tile(string label, string value, string unit, Color c, string sub)
    {
        var sp = new StackPanel();
        sp.Children.Add(Caps(label, 10));
        var v = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        v.Children.Add(new TextBlock { Text = value, FontSize = 24, FontWeight = FontWeights.ExtraBold, Foreground = B(c), FontFamily = (FontFamily)FindResource("DisplayFont") });
        if (unit.Length > 0) v.Children.Add(new TextBlock { Text = unit, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = B(A(c, 0xBF)), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 0, 4) });
        sp.Children.Add(v);
        if (sub.Length > 0) sp.Children.Add(new TextBlock { Text = sub, FontSize = 10.5, Foreground = Res("TextFaintBrush"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = sub });
        var g = new Grid();
        g.Children.Add(new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top, Background = new LinearGradientBrush(c, A(c, 0), 0) });
        g.Children.Add(new Border { Padding = new Thickness(13, 10, 13, 10), Child = sp });
        return new Border { CornerRadius = new CornerRadius(12), Background = B(Color.FromRgb(0x17, 0x18, 0x1D)), BorderBrush = B(Color.FromRgb(0x24, 0x26, 0x2D)), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 10), ClipToBounds = true, Child = g };
    }

    // ───────── how to make it better ─────────

    FrameworkElement FixGuide(SpeedResult r)
    {
        var all = SpeedTest.Diagnose(r);
        var bad = all.Where(f => f.Level != SpeedTest.Level.Great).OrderBy(f => f.Level == SpeedTest.Level.Bad ? 0 : 1).ToList();
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = Loc.T(bad.Any(f => f.Level == SpeedTest.Level.Bad) ? "How to make it better" : "Your connection"), FontSize = 15.5, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont") });

        // Everything that's fine, on one line.
        var good = all.Where(f => f.Level == SpeedTest.Level.Great).ToList();
        if (good.Count > 0)
        {
            var wp = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
            foreach (var f in good)
                wp.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(999), BorderThickness = new Thickness(1), BorderBrush = B(A(Green, 0x55)), Background = B(A(Green, 0x12)), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 6, 6),
                    Child = new TextBlock { Text = "✓ " + Loc.T(f.Area), FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = B(Green), ToolTip = Loc.T(f.Summary) },
                });
            sp.Children.Add(wp);
        }

        foreach (var f in bad) sp.Children.Add(FindingView(f));
        if (bad.Count == 0) sp.Children.Add(Dim(Loc.T("Nothing to fix. If games still lag, the problem is between your provider and the game server, or the game server itself: try another server region in the game."), 12.5, 6));
        else sp.Children.Add(Faint(Loc.T("Run the test again after each change to see what helped. Results vary through the day, so test a few times before calling your provider."), 11.5, 8));
        return Card(sp, bad.Any(f => f.Level == SpeedTest.Level.Bad) ? Red : Green);
    }

    FrameworkElement FindingView(SpeedTest.Finding f)
    {
        var c = LevelColor(f.Level);
        var sp = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2, 7, 3), Margin = new Thickness(0, 0, 10, 0), BorderThickness = new Thickness(1), BorderBrush = B(A(c, 0x80)), Background = B(A(c, 0x1A)), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = Loc.T(f.Area).ToUpper(CultureInfo.CurrentCulture), FontSize = 10, FontWeight = FontWeights.ExtraBold, Foreground = B(c) },
        });
        head.Children.Add(new TextBlock { Text = Loc.T(f.Summary), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(head);
        if (f.Why.Length > 0) sp.Children.Add(Dim(Loc.T(f.Why), 12, 6));
        if (f.Fixes.Length > 0)
        {
            sp.Children.Add(Caps("What to do, easiest first", 9.5, B(A(c, 0xCC))).Also(t => t.Margin = new Thickness(0, 10, 0, 2)));
            int i = 1;
            foreach (var fix in f.Fixes)
            {
                var row = new Grid { Margin = new Thickness(0, 5, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new Border
                {
                    Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = B(A(c, 0x26)), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 0),
                    Child = new TextBlock { Text = (i++).ToString(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = B(c), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });
                var t = new TextBlock { Text = Loc.T(fix), FontSize = 12.5, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
                Grid.SetColumn(t, 1);
                row.Children.Add(t);
                sp.Children.Add(row);
            }
        }
        var g = new Grid();
        g.Children.Add(new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Background = B(c) });
        g.Children.Add(new Border { Padding = new Thickness(14, 11, 14, 12), Child = sp });
        return new Border { CornerRadius = new CornerRadius(10), Background = B(Color.FromRgb(0x1A, 0x1B, 0x20)), BorderBrush = B(Color.FromRgb(0x26, 0x28, 0x2F)), BorderThickness = new Thickness(1), Margin = new Thickness(0, 8, 0, 0), ClipToBounds = true, Child = g };
    }

    FrameworkElement WhatItMeasures()
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = Loc.T("What this checks"), FontSize = 15.5, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont") });
        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 8, -10, 0) };
        void Item(string t, string d, Color c)
        {
            var s = new StackPanel();
            s.Children.Add(Caps(t, 10, B(c)));
            s.Children.Add(Dim(Loc.T(d), 12, 3));
            grid.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = B(Color.FromRgb(0x1A, 0x1B, 0x20)), Padding = new Thickness(12, 9, 12, 10), Margin = new Thickness(0, 0, 10, 10), Child = s });
        }
        Item("Download & upload", "How fast data comes in and goes out, measured over several connections at once.", Cyan);
        Item("Ping & jitter", "How long a packet takes to come back, and how steady that is. Jitter is what makes games rubber-band.", Purple);
        Item("Packet loss", "Game updates that never arrive: enemies teleporting and shots that don't count.", Green);
        Item("Lag when busy", "Your ping while the line is full. A fast connection with a bad grade here still lags when anyone downloads.", Yellow);
        Item("A gaming verdict", "One plain answer: is this connection good for competitive games?", Blue);
        Item("How to fix it", "For anything that isn't good: why it happens and what to do, easiest fix first.", Red);
        sp.Children.Add(grid);
        sp.Children.Add(Faint(Loc.T("Runs on Cloudflare's speed-test servers, using the nearest one. Uses up to about 2.4 GB on a very fast connection (less on slower ones); nothing about your PC is sent."), 11, 2));
        return Card(sp, Purple);
    }

    FrameworkElement HistoryCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = Loc.T("Earlier tests"), FontSize = 15.5, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont"), Margin = new Thickness(0, 0, 0, 6) });
        var cols = new[] { 150.0, 110, 110, 80, 80, 80, 80 };
        Grid Row(string[] cells, bool head)
        {
            var g = new Grid { Margin = new Thickness(0, head ? 0 : 3, 0, 0) };
            for (int i = 0; i < cols.Length; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(cols[i]) });
            for (int i = 0; i < cells.Length; i++)
            {
                var t = head ? Caps(cells[i], 9.5, Res("TextFaintBrush")) : new TextBlock { Text = cells[i], FontSize = 12, Foreground = i == 0 ? Res("TextDimBrush") : Res("TextBrush") };
                Grid.SetColumn(t, i);
                g.Children.Add(t);
            }
            return g;
        }
        sp.Children.Add(Row(new[] { "When", "Download", "Upload", "Ping", "Jitter", "Loss", "Busy lag" }, true));
        foreach (var h in SpeedTest.History.Take(10))
            sp.Children.Add(Row(new[]
            {
                h.At.ToString("g", CultureInfo.CurrentCulture) + (h.Wifi ? " · Wi-Fi" : ""),
                h.DownMbps.ToString("0.0", CultureInfo.CurrentCulture) + " Mbps", h.UpMbps.ToString("0.0", CultureInfo.CurrentCulture) + " Mbps",
                h.IdleMs.ToString("0", CultureInfo.CurrentCulture) + " ms", h.JitterMs.ToString("0.0", CultureInfo.CurrentCulture) + " ms",
                h.LossPercent < 0 ? "–" : h.LossPercent.ToString("0.#", CultureInfo.CurrentCulture) + "%", h.BloatGrade,
            }, false));
        return Card(sp, Blue);
    }
}
