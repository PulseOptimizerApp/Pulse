using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    static DropShadowEffect Glow(Color c, double blur = 16, double op = 0.6) => new() { Color = c, BlurRadius = blur, ShadowDepth = 0, Opacity = op };
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

    // ───────── the ring and the start button ─────────

    Grid _ringHost;
    TextBlock _phaseText;

    FrameworkElement TestCard()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());

        _ringHost = new Grid { Width = 190, Height = 190, Margin = new Thickness(4, 0, 26, 0) };
        g.Children.Add(_ringHost);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(right, 1);
        if (_running)
        {
            _phaseText = new TextBlock { FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont") };
            right.Children.Add(_phaseText);
            right.Children.Add(Steps());
            var stop = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Stop"), Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            stop.Click += (_, _) => _cts?.Cancel();
            right.Children.Add(stop);
        }
        else
        {
            var (lvl, text) = _last != null ? SpeedTest.Verdict(_last) : (SpeedTest.Level.Ok, "");
            if (_last != null)
            {
                var c = LevelColor(lvl);
                right.Children.Add(Caps("For games", 10.5, B(A(c, 0xCC))));
                right.Children.Add(new TextBlock { Text = Loc.T(text), FontSize = 21, FontWeight = FontWeights.Bold, Foreground = B(c), FontFamily = (FontFamily)FindResource("DisplayFont"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                var where = new List<string> { _last.At.ToString("g", CultureInfo.CurrentCulture) };
                if (_last.Server.Length > 0) where.Add(Loc.F("server {0}", _last.Server));
                where.Add(Loc.T(_last.Wifi ? "Wi-Fi" : "Ethernet"));
                right.Children.Add(Faint(string.Join(" · ", where), 11.5, 4));
            }
            else
            {
                right.Children.Add(new TextBlock { Text = Loc.T("Test your connection for gaming"), FontSize = 21, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont"), TextWrapping = TextWrapping.Wrap });
                right.Children.Add(Dim(Loc.T("Takes about 30 seconds. Close downloads and streams first for a fair result."), 12.5, 4));
            }
            var go = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T(_last == null ? "Start test" : "Test again"), Padding = new Thickness(22, 8, 22, 8), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            go.Click += (_, _) => _ = Start();
            right.Children.Add(go);
        }
        g.Children.Add(right);
        DrawRing();
        return Card(g, _running ? Cyan : Purple, 18);
    }

    FrameworkElement Steps()
    {
        var wp = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        void Step(SpeedTest.Phase ph, string label, double val, string unit)
        {
            bool done = _phase > ph, now = _phase == ph;
            var c = done ? Green : now ? Cyan : Color.FromRgb(0x5A, 0x5E, 0x68);
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = B(c) });
            if (val >= 0) sp.Children.Add(new TextBlock { Text = " " + val.ToString(val >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + unit, FontSize = 11.5, Foreground = Res("TextDimBrush") });
            wp.Children.Add(new Border { CornerRadius = new CornerRadius(999), BorderThickness = new Thickness(1), BorderBrush = B(A(c, 0x66)), Background = B(A(c, 0x14)), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 6, 6), Child = sp });
        }
        Step(SpeedTest.Phase.Ping, "Ping", _ping, "ms");
        Step(SpeedTest.Phase.Download, "Download", _down, "Mbps");
        Step(SpeedTest.Phase.Upload, "Upload", _up, "Mbps");
        Step(SpeedTest.Phase.Loss, "Packet loss", -1, "");
        return wp;
    }

    void DrawRing()
    {
        if (_ringHost == null) return;
        _ringHost.Children.Clear();
        const double size = 190, stroke = 13;
        Color c; double frac; string value, unit, label;
        if (_running)
        {
            (c, label) = _phase switch
            {
                SpeedTest.Phase.Ping => (Purple, "Ping"),
                SpeedTest.Phase.Download => (Cyan, "Download"),
                SpeedTest.Phase.Upload => (Blue, "Upload"),
                _ => (Green, "Packet loss"),
            };
            bool speed = _phase is SpeedTest.Phase.Download or SpeedTest.Phase.Upload;
            frac = speed ? SpeedFrac(_value) : _phase == SpeedTest.Phase.Ping ? Math.Clamp(_value / 150, 0, 1) : _frac;
            value = _phase == SpeedTest.Phase.Loss ? "…" : _value.ToString(_value >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture);
            unit = speed ? "Mbps" : _phase == SpeedTest.Phase.Ping ? "ms" : "";
            if (_phaseText != null) _phaseText.Text = Loc.T(_phase switch
            {
                SpeedTest.Phase.Ping => "Measuring ping…",
                SpeedTest.Phase.Download => "Testing download…",
                SpeedTest.Phase.Upload => "Testing upload…",
                _ => "Checking for packet loss…",
            });
        }
        else if (_last != null)
        {
            c = Cyan; label = "Download"; frac = SpeedFrac(_last.DownMbps);
            value = _last.DownMbps.ToString(_last.DownMbps >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture); unit = "Mbps";
        }
        else { c = Purple; label = "Ready"; frac = double.NaN; value = "–"; unit = ""; }

        _ringHost.Children.Add(Ring(size, stroke, 1, B(A(c, 0x1F))));
        if (!double.IsNaN(frac) && frac > 0)
        {
            var arc = Ring(size, stroke, frac, new LinearGradientBrush(A(c, 0x8C), c, new Point(0, 1), new Point(1, 0)));
            arc.Effect = Glow(c, 18, 0.7);
            _ringHost.Children.Add(arc);
        }
        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        mid.Children.Add(new TextBlock { Text = value, FontSize = 40, FontWeight = FontWeights.ExtraBold, Foreground = B(c), FontFamily = (FontFamily)FindResource("DisplayFont"), HorizontalAlignment = HorizontalAlignment.Center, Effect = Glow(c, 20, 0.45) });
        if (unit.Length > 0) mid.Children.Add(new TextBlock { Text = unit, FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = B(A(c, 0xCC)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -4, 0, 0) });
        _ringHost.Children.Add(mid);
        _ringHost.Children.Add(new TextBlock { Text = Loc.T(label).ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.ExtraBold, Foreground = B(A(c, 0xB3)), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) });
    }

    async Task Start()
    {
        if (_running) return;
        _running = true;
        _phase = SpeedTest.Phase.Ping; _value = 0; _frac = 0; _down = _up = _ping = -1;
        _cts = new CancellationTokenSource();
        Build();
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
            if (newPhase && _running) Build(); else DrawRing();
        });
        try
        {
            var r = await Task.Run(() => SpeedTest.Run(progress, _cts.Token));
            _last = r;
            SpeedTest.Save(r);
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
            _cts?.Dispose(); _cts = null;
            Build();
        }
    }

    // ───────── results ─────────

    FrameworkElement ResultTiles(SpeedResult r)
    {
        var grid = new UniformGrid { Columns = 6, Margin = new Thickness(0, 0, -10, 0) };
        var d = SpeedTest.Diagnose(r).ToDictionary(x => x.Area);
        Color Of(string area) => d.TryGetValue(area, out var f) ? LevelColor(f.Level) : Color.FromRgb(0x7C, 0x80, 0x8A);
        string N(double v) => v.ToString(v >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture);
        grid.Children.Add(Tile("Download", N(r.DownMbps), "Mbps", Of("Download"), ""));
        grid.Children.Add(Tile("Upload", N(r.UpMbps), "Mbps", Of("Upload"), ""));
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
        sp.Children.Add(Faint(Loc.T("Runs on Cloudflare's speed-test servers, using the nearest one. Uses about 200–800 MB on a fast connection; nothing about your PC is sent."), 11, 2));
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
