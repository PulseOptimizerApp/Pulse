using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Pulse.Core;
using Pulse.Core.Gpu;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// GPU tuning, Pulse's own take on MSI Afterburner for NVIDIA cards: a live monitor (clocks, temperature, power, load,
/// fan, video memory and what's holding the card back), the risks in plain words before anything can be changed,
/// presets, manual power / core / memory / clock-cap controls with undervolting explained, and an optional
/// apply-at-startup with a safety net. Built on NVIDIA's own NVML; nothing is injected into games.
/// </summary>
public partial class GpuPage : UserControl
{
    static GpuTuneSettings S => TweakEngine.State.GpuTune ??= new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly List<(double Clock, double Temp)> _history = new();
    readonly Dictionary<string, TextBlock> _tiles = new();
    Canvas _chart;
    TextBlock _status;
    GpuTuneSettings _draft;

    public GpuPage()
    {
        InitializeComponent();
        Build();
        Loaded += (_, _) => { _timer.Tick += Tick; _timer.Start(); Tick(null, null); };
        Unloaded += (_, _) => { _timer.Stop(); _timer.Tick -= Tick; };
    }

    // ───────── small building blocks ─────────

    Brush Res(string k) => (Brush)FindResource(k);
    Border Card(UIElement child, double bottom = 14) => new() { Style = (Style)FindResource("Card"), Padding = new Thickness(22, 18, 22, 18), Margin = new Thickness(0, 0, 0, bottom), Child = child };
    TextBlock Heading(string t, double size = 16) => new() { Text = Loc.T(t), FontSize = size, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont") };
    TextBlock Dim(string t, double size = 12.5, double top = 4) => new() { Text = t, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, top, 0, 0), LineHeight = size * 1.45 };
    TextBlock Faint(string t, double size = 11.5, double top = 0) => new() { Text = t, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, top, 0, 0) };

    FrameworkElement Bullets(params string[] lines)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var l in lines)
        {
            var g = new Grid { Margin = new Thickness(0, 5, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = "•", Foreground = Res("TextFaintBrush"), FontSize = 13 });
            var t = Dim(Loc.T(l), 12.5, 0);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            sp.Children.Add(g);
        }
        return sp;
    }

    // ───────── the page ─────────

    void Build()
    {
        Root.Children.Clear();
        _tiles.Clear();
        if (!Nvml.Available)
        {
            SubText.Text = Loc.T("Watch your graphics card live, and tune its power, clocks and voltage, like MSI Afterburner, built into Pulse.");
            var sp = new StackPanel();
            sp.Children.Add(Heading("GPU tuning needs an NVIDIA graphics card"));
            sp.Children.Add(Dim(SystemInfo.HasAmd
                ? Loc.T("Pulse tunes cards through NVIDIA's own driver interface. On an AMD Radeon card, AMD Software: Adrenalin Edition does the same job (Performance → Tuning).")
                : Loc.T("Pulse tunes cards through NVIDIA's own driver interface, and it didn't find an NVIDIA card (or its driver) on this PC."), 13, 6));
            var b = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Open the guide"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(14, 6, 14, 6) };
            b.Click += (_, _) => MainWindow.Instance.Navigate("guide");
            sp.Children.Add(b);
            Root.Children.Add(Card(sp));
            return;
        }

        if (!string.IsNullOrEmpty(GpuTuning.Notice))
        {
            var n = new StackPanel();
            n.Children.Add(new TextBlock { Text = Loc.T("Safety net").ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Res("WarnBrush") });
            n.Children.Add(Dim(Loc.T(GpuTuning.Notice), 13, 4));
            var ok = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Got it"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            ok.Click += (_, _) => { GpuTuning.Notice = null; Build(); };
            n.Children.Add(ok);
            var card = Card(n);
            card.BorderBrush = Res("WarnBrush");
            Root.Children.Add(card);
        }

        Root.Children.Add(Monitor());
        Root.Children.Add(Risks());
        if (S.RisksAccepted)
        {
            Root.Children.Add(Presets());
            Root.Children.Add(Manual());
        }
    }

    // ───────── live monitor ─────────

    FrameworkElement Monitor()
    {
        var sp = new StackPanel();
        var head = new Grid();
        head.Children.Add(Heading(Nvml.CardName ?? "NVIDIA"));
        var live = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        live.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Res("GoodBrush"), Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        live.Children.Add(Faint(Loc.T(GpuTuning.Active != null ? "Live · tune on" : "Live · stock")).Also(t => _tiles["state"] = t));
        head.Children.Add(live);
        sp.Children.Add(head);

        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var (key, label) in new[] { ("core", "Core clock"), ("mem", "Memory clock"), ("temp", "Temperature"), ("power", "Power"), ("usage", "GPU load"), ("fan", "Fan"), ("vram", "Video memory"), ("held", "Held back by") })
        {
            var tile = new StackPanel { Margin = new Thickness(0, 0, 12, 14) };
            tile.Children.Add(Faint(Loc.T(label).ToUpper(CultureInfo.CurrentCulture), 10.5));
            var value = new TextBlock { Text = "–", FontSize = key == "held" ? 14 : 20, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 5, 0, 0), FontFamily = (FontFamily)FindResource("DisplayFont"), TextWrapping = TextWrapping.Wrap };
            _tiles[key] = value;
            tile.Children.Add(value);
            grid.Children.Add(tile);
        }
        sp.Children.Add(grid);

        _chart = new Canvas { Height = 90, ClipToBounds = true, Background = Brushes.Transparent };
        sp.Children.Add(new Border { BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0), Child = _chart });
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        legend.Children.Add(new Border { Width = 10, Height = 3, Background = Res("Series1Brush"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        legend.Children.Add(Faint(Loc.T("Core clock")));
        legend.Children.Add(new Border { Width = 10, Height = 3, Background = Res("Series2Brush"), Margin = new Thickness(16, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        legend.Children.Add(Faint(Loc.T("Temperature")));
        legend.Children.Add(Faint("   ·   " + Loc.T("last 2 minutes")));
        sp.Children.Add(legend);
        return Card(sp);
    }

    async void Tick(object sender, EventArgs e)
    {
        if (MainWindow.Instance?.Quiet == true) return; // Pulse is behind a game or minimized
        if (!Nvml.Available || _tiles.Count == 0) return;
        GpuReading r = null;
        try { r = await Task.Run(GpuTuning.Read); } catch { }
        if (r == null) return;
        var ci = CultureInfo.CurrentCulture;
        string N(double? v, string unit, string fmt = "0") => v is double d ? d.ToString(fmt, ci) + unit : "–";
        _tiles["core"].Text = N(r.CoreMhz, " MHz");
        _tiles["mem"].Text = N(r.MemMhz, " MHz");
        _tiles["temp"].Text = N(r.TempC, " °C");
        _tiles["temp"].Foreground = Res(r.TempC >= 85 ? "BadBrush" : r.TempC >= 78 ? "WarnBrush" : "TextBrush");
        _tiles["power"].Text = r.PowerW is double p ? N(p, " W") + (r.LimitW is double l ? " / " + N(l, " W") : "") : "–";
        _tiles["usage"].Text = N(r.Usage, "%");
        _tiles["fan"].Text = r.Fan is double f ? N(f, "%") : Loc.T("n/a");
        _tiles["vram"].Text = r.VramUsed is double u && r.VramTotal is double t ? (u / 1073741824).ToString("0.0", ci) + " / " + (t / 1073741824).ToString("0", ci) + " GB" : "–";
        _tiles["held"].Text = r.HeldBackBy != null ? Loc.T(r.HeldBackBy) : "–";
        _tiles["state"].Text = Loc.T(GpuTuning.Active != null ? "Live · tune on" : "Live · stock");

        _history.Add((r.CoreMhz ?? 0, r.TempC ?? 0));
        if (_history.Count > 120) _history.RemoveAt(0);
        DrawChart();
    }

    void DrawChart()
    {
        if (_chart == null || _chart.ActualWidth <= 0) return;
        _chart.Children.Clear();
        double w = _chart.ActualWidth, h = _chart.ActualHeight;
        double maxClock = Math.Max(500, _history.Max(x => x.Clock) * 1.1);
        Polyline Line(Func<(double Clock, double Temp), double> pick, double max, string brush)
        {
            var pl = new Polyline { Stroke = Res(brush), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
            for (int i = 0; i < _history.Count; i++)
                pl.Points.Add(new Point(w * (120 - _history.Count + i) / 119.0, h - 4 - (h - 8) * Math.Clamp(pick(_history[i]) / max, 0, 1)));
            return pl;
        }
        _chart.Children.Add(Line(x => x.Clock, maxClock, "Series1Brush"));
        _chart.Children.Add(Line(x => x.Temp, 100, "Series2Brush"));
    }

    // ───────── the risks ─────────

    FrameworkElement Risks()
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Before you tune: the risks"));
        sp.Children.Add(Dim(Loc.T("Tuning changes how your graphics card runs. Most of it is low-risk, but you should know what can happen."), 13, 6));

        sp.Children.Add(new TextBlock { Text = Loc.T("What can go wrong").ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Res("WarnBrush"), Margin = new Thickness(0, 16, 0, 0) });
        sp.Children.Add(Bullets(
            "A setting that goes too far makes games crash, the screen go black for a moment while the driver restarts, or, more rarely, Windows freeze or restart. Anything unsaved is lost.",
            "Overclocking (a higher power limit or positive clocks without a cap) makes the card hotter, louder and use more power. Running hotter for years can shorten its life.",
            "Some card makers don't cover damage they think came from overclocking. Undervolting (lower power and voltage) is the low-risk direction.",
            "Every chip is different. A setting that works on someone else's card can crash yours, so change one thing at a time and test it."));

        sp.Children.Add(new TextBlock { Text = Loc.T("How Pulse keeps you safe").ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Res("GoodBrush"), Margin = new Thickness(0, 16, 0, 0) });
        sp.Children.Add(Bullets(
            "Pulse only uses the ranges NVIDIA's driver allows, and can't raise voltage past them.",
            "Restarting the PC always puts the card back to stock. Reset to stock does it instantly.",
            "If the graphics driver resets while a tune is on, Pulse puts the card back to stock and stops applying that tune at startup.",
            "If Windows didn't shut down normally while a tune was on, Pulse doesn't apply it when it starts again.",
            "It uses NVIDIA's own driver interface (the one nvidia-smi uses), so nothing is injected into games and anti-cheat isn't involved."));

        if (!S.RisksAccepted)
        {
            var accept = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T("I understand the risks"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 18, 0, 0), Padding = new Thickness(16, 7, 16, 7) };
            accept.Click += (_, _) =>
            {
                S.RisksAccepted = true;
                TweakEngine.Save();
                TweakEngine.AddLog("gpu", "GPU tuning risks accepted");
                Build();
            };
            sp.Children.Add(accept);
            sp.Children.Add(Faint(Loc.T("Tuning stays locked until you accept. Watching the live readings is always safe."), 11.5, 8));
            var card = Card(sp);
            card.BorderBrush = Res("WarnBrush");
            return card;
        }

        // Accepted: keep the risks one click away, folded.
        var body = sp;
        body.Visibility = Visibility.Collapsed;
        var outer = new StackPanel();
        var toggle = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Show the risks again"), HorizontalAlignment = HorizontalAlignment.Left };
        toggle.Click += (_, _) =>
        {
            bool show = body.Visibility != Visibility.Visible;
            body.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = Loc.T(show ? "Hide the risks" : "Show the risks again");
        };
        outer.Children.Add(toggle);
        outer.Children.Add(body.Also(b => b.Margin = new Thickness(0, 12, 0, 0)));
        return new Border { Margin = new Thickness(2, -4, 0, 14), Child = outer };
    }

    // ───────── presets ─────────

    FrameworkElement Presets()
    {
        var lim = GpuTuning.Range();
        var sp = new StackPanel();
        sp.Children.Add(Heading("Quick presets"));
        sp.Children.Add(Dim(Loc.T("One click, applied right away. Power limits are the safest kind of tuning: the card picks its own clocks and voltage inside the limit."), 12.5, 4));
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        void Preset(string title, string note, int pct)
        {
            if (lim.CanPower) pct = Math.Clamp(pct, lim.PowerMin, lim.PowerMax);
            var b = new Button { Style = (Style)FindResource("GhostButton"), Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(14, 8, 14, 9), HorizontalContentAlignment = HorizontalAlignment.Left };
            var c = new StackPanel();
            c.Children.Add(new TextBlock { Text = Loc.T(title), FontWeight = FontWeights.SemiBold, FontSize = 13 });
            c.Children.Add(new TextBlock { Text = Loc.T(note), FontSize = 11.5, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 2, 0, 0) });
            b.Content = c;
            b.Click += (_, _) => ApplyNow(new GpuTuneSettings { RisksAccepted = true, PowerPercent = pct, ApplyAtStart = S.ApplyAtStart });
            row.Children.Add(b);
        }
        Preset("Stock", "How the card came", 100);
        if (lim.CanPower)
        {
            Preset("Efficient", "90% power · small FPS cost", 90);
            Preset("Quiet", "80% power · much cooler", 80);
            if (lim.PowerMax > 100) Preset("Max power", Loc.F("{0}% power · overclock, hotter", lim.PowerMax), lim.PowerMax);
        }
        sp.Children.Add(row);
        return Card(sp);
    }

    // ───────── manual tuning ─────────

    FrameworkElement Manual()
    {
        var lim = GpuTuning.Range();
        _draft = (GpuTuning.Active ?? S).Copy();
        var sp = new StackPanel();
        sp.Children.Add(Heading("Manual tuning"));
        sp.Children.Add(Dim(Loc.T("Change one thing at a time, press Apply, then play a demanding game for half an hour. If it crashes, press Reset to stock and go back one step."), 12.5, 4));

        FrameworkElement Slider(string label, string help, int min, int max, int step, Func<int> get, Action<int> set, Func<int, string> show, bool enabled = true)
        {
            var g = new Grid { Margin = new Thickness(0, 18, 0, 0), IsEnabled = enabled, Opacity = enabled ? 1 : 0.45 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            var left = new StackPanel();
            left.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
            if (help != null) left.Children.Add(Faint(Loc.T(help), 11, 2).Also(t => t.Margin = new Thickness(0, 2, 12, 0)));
            var value = new TextBlock { Text = show(get()), FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextBrush") };
            var sl = new System.Windows.Controls.Slider { Style = (Style)FindResource("Seek"), Minimum = min, Maximum = Math.Max(min + step, max), TickFrequency = step, IsSnapToTickEnabled = true, Value = Math.Clamp(get(), min, max), VerticalAlignment = VerticalAlignment.Center };
            sl.ValueChanged += (_, e) => { set((int)Math.Round(e.NewValue)); value.Text = show((int)Math.Round(e.NewValue)); };
            Grid.SetColumn(sl, 1); Grid.SetColumn(value, 2);
            g.Children.Add(left); g.Children.Add(sl); g.Children.Add(value);
            if (!enabled) g.ToolTip = Loc.T("This card or driver doesn't allow it.");
            return g;
        }

        string Signed(int v) => (v > 0 ? "+" : "") + v.ToString(CultureInfo.CurrentCulture) + " MHz";
        sp.Children.Add(Slider("Power limit", "Below 100% is cooler and quieter; above is an overclock.", lim.CanPower ? lim.PowerMin : 100, lim.CanPower ? lim.PowerMax : 100, 1,
            () => _draft.PowerPercent, v => _draft.PowerPercent = v,
            v => v + "%" + (lim.DefaultW > 0 ? " · " + Math.Round(lim.DefaultW * v / 100.0) + " W" : ""), lim.CanPower));
        sp.Children.Add(Slider("Core clock offset", "Moves the whole clock/voltage curve. Positive with a cap = undervolt.", lim.CanCore ? lim.CoreMin : 0, lim.CanCore ? lim.CoreMax : 0, 15,
            () => _draft.CoreOffset, v => _draft.CoreOffset = v, Signed, lim.CanCore));
        sp.Children.Add(Slider("Memory clock offset", "Same scale as MSI Afterburner. Judge it by FPS, not just crashes.", lim.CanMem ? lim.MemMin : 0, lim.CanMem ? lim.MemMax : 0, 25,
            () => _draft.MemOffset, v => _draft.MemOffset = v, Signed, lim.CanMem));

        // Clock cap, which is what makes a positive offset an undervolt.
        int capMax = lim.ClockMax > 0 ? lim.ClockMax : 3200;
        var capSwitch = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = _draft.MaxClock > 0, Content = Loc.T("Cap the core clock (for undervolting)"), Margin = new Thickness(0, 20, 0, 0), IsEnabled = lim.CanCap };
        sp.Children.Add(capSwitch);
        var capRow = Slider("Clock cap", "The highest clock the card may use. Set it to what it normally boosts to in games.", 1200, capMax, 15,
            () => _draft.MaxClock > 0 ? _draft.MaxClock : Math.Min(capMax, 2700), v => { if (capSwitch.IsChecked == true) _draft.MaxClock = v; }, v => v.ToString(CultureInfo.CurrentCulture) + " MHz", lim.CanCap);
        capRow.Visibility = capSwitch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        capSwitch.Checked += (_, _) => { capRow.Visibility = Visibility.Visible; _draft.MaxClock = Math.Min(capMax, _draft.MaxClock > 0 ? _draft.MaxClock : 2700); };
        capSwitch.Unchecked += (_, _) => { capRow.Visibility = Visibility.Collapsed; _draft.MaxClock = 0; };
        sp.Children.Add(capRow);

        // How to undervolt.
        var how = new StackPanel();
        how.Children.Add(new TextBlock { Text = Loc.T("How to undervolt").ToUpper(CultureInfo.CurrentCulture), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Res("AccentBrush") });
        how.Children.Add(Dim(Loc.T("1. Play a game and note the core clock above while it runs. 2. Turn on the clock cap and set it to that clock. 3. Raise the core clock offset in +15 MHz steps: each step makes the card reach the same clock at a lower voltage, so it runs cooler and quieter at the same speed. 4. Test each step; when a game crashes, go back two steps."), 12.5, 3));
        var start = Pulse.Core.Guide.Afterburner.StartingPoint(Nvml.CardName);
        if (start != null) how.Children.Add(Faint(Loc.T("A common starting point for your card:") + " " + Loc.T(start), 11.5, 6));
        sp.Children.Add(new Border
        {
            BorderBrush = Res("AccentBrush"), BorderThickness = new Thickness(2, 0, 0, 0), Background = Res("Surface2Brush"),
            CornerRadius = new CornerRadius(0, 6, 6, 0), Padding = new Thickness(12, 9, 12, 10), Margin = new Thickness(0, 18, 0, 0), Child = how,
        });

        // Apply, reset, startup.
        var buttons = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) };
        var apply = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T("Apply"), Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(0, 0, 10, 0) };
        apply.Click += (_, _) => ApplyNow(_draft.Copy());
        var reset = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Reset to stock"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 10, 0) };
        reset.Click += (_, _) => ApplyNow(new GpuTuneSettings { RisksAccepted = true });
        buttons.Children.Add(apply);
        buttons.Children.Add(reset);
        sp.Children.Add(buttons);
        _status = Faint("", 12, 10);
        sp.Children.Add(_status);

        var startup = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = S.ApplyAtStart, Content = Loc.T("Apply my tune when Pulse starts"), Margin = new Thickness(0, 18, 0, 0) };
        startup.Checked += async (_, _) =>
        {
            if (!await MainWindow.Instance.Confirm(Loc.T("Apply this tune at startup?"),
                Loc.T("Only turn this on once the tune has been stable for a few hours of gaming. If Windows crashes or the driver resets while it's on, Pulse turns this off by itself."),
                Loc.T("Turn on"), Loc.T("Cancel")))
            { startup.IsChecked = false; return; }
            S.ApplyAtStart = true; TweakEngine.Save();
        };
        startup.Unchecked += (_, _) => { S.ApplyAtStart = false; TweakEngine.Save(); };
        sp.Children.Add(startup);
        sp.Children.Add(Faint(Loc.T("Without this, the card goes back to stock whenever the PC restarts."), 11.5, 4));
        return Card(sp);
    }

    void ApplyNow(GpuTuneSettings t)
    {
        t.RisksAccepted = true;
        t.ApplyAtStart = S.ApplyAtStart && !t.IsStock;
        var problems = GpuTuning.Apply(t);
        // What was asked for is what's saved, so the page and startup use it.
        TweakEngine.State.GpuTune = t;
        TweakEngine.Save();
        Build();
        if (_status != null)
        {
            _status.Text = problems.Count == 0
                ? Loc.T(t.IsStock ? "Back to stock." : "Applied. Now play something demanding for a while to test it.")
                : Loc.T("Some settings didn't apply:") + " " + string.Join(" ", problems.Select(Loc.T));
            _status.Foreground = Res(problems.Count == 0 ? "GoodBrush" : "WarnBrush");
        }
        MainWindow.Instance.Toast(Loc.T(problems.Count > 0 ? "Some settings didn't apply" : t.IsStock ? "GPU back to stock" : "GPU tune applied"), problems.Count > 0 ? ToastKind.Warning : ToastKind.Success);
    }
}
