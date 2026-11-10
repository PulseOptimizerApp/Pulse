using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pulse.Core;
using Pulse.Core.Guide;
using Loc = Pulse.Ui.Loc;

namespace Pulse.Pages;

/// <summary>
/// The guide's Hardware tab: look up any CPU, graphics card or memory and see how it ranks and what it would do in
/// your PC; and a builder that starts as your PC, where any part can be swapped, with expected frame rates in popular
/// games at 720p, 1080p and 1440p (from <see cref="HardwareDb"/>: estimates, not benchmarks).
/// </summary>
public partial class GuidePage
{
    static HwCpu _myCpu, _bCpu;
    static HwGpu _myGpu, _bGpu;
    static HwRam _myRam, _bRam;
    static bool _hwDetected, _ramAsked;
    static object _hwPicked;
    static string _hwQuery = "";

    void EnsureDetected()
    {
        if (!_hwDetected)
        {
            _hwDetected = true;
            _myCpu = HardwareDb.MatchCpu(SystemInfo.Cpu);
            // The strongest card Windows lists (a laptop's graphics card over its built-in chip).
            _myGpu = SystemInfo.Gpus.Select(g => HardwareDb.MatchGpu(g.Name, g.MemoryBytes)).Where(g => g != null).OrderByDescending(g => g.Score).FirstOrDefault();
            int gb = (int)Math.Max(4, Math.Round(SystemInfo.TotalRam / 1073741824.0 / 4) * 4);
            _myRam = new HwRam(gb <= 0 ? 16 : gb, "DDR4", 3200, true);
            _bCpu ??= _myCpu; _bGpu ??= _myGpu; _bRam ??= _myRam;
        }
        if (_ramAsked) return;
        _ramAsked = true;
        // Memory type and speed come from WMI, which takes a moment: fill them in when they arrive.
        _ = Task.Run(HardwareDb.DetectRam).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion || t.Result is not { Gb: > 0 } ram) return;
            bool wasMine = _bRam == _myRam;
            _myRam = ram;
            if (wasMine) _bRam = ram;
            if (_tab == "hardware") ShowTab("hardware", animate: false);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    FrameworkElement BuildHardware()
    {
        EnsureDetected();
        var sp = new StackPanel();
        sp.Children.Add(Intro("Hardware", "Look up any CPU, graphics card or memory: how it ranks, and what it would do in your PC. Below, build a PC (it starts as yours) and see the frame rates to expect."));
        sp.Children.Add(HwSearchCard());
        if (_hwPicked != null) sp.Children.Add(HwDetail(_hwPicked));
        sp.Children.Add(HwBuilder());
        sp.Children.Add(HwFpsCard());
        return sp;
    }

    // ───────── search ─────────

    FrameworkElement HwSearchCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading(Loc.T("Look up a part")));
        var box = new TextBox
        {
            Style = (Style)FindResource("SearchBox"), Tag = Loc.T("Search a CPU, graphics card or memory (e.g. 5600X, RTX 4060, DDR5 6000)"),
            FontSize = 14, Margin = new Thickness(0, 12, 0, 0), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left, Width = 560, Text = _hwQuery,
        };
        var results = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        void Fill()
        {
            results.Children.Clear();
            _hwQuery = box.Text;
            if (box.Text.Trim().Length < 2) return;
            var hits = HardwareDb.Search(box.Text, 8);
            if (hits.Count == 0) { results.Children.Add(Faint(Loc.T("Nothing by that name in Pulse's list. Try just the model number."), 12, 6)); return; }
            foreach (var h in hits) results.Children.Add(HwResultRow(h));
        }
        box.TextChanged += (_, _) => Fill();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var first = HardwareDb.Search(box.Text, 1).FirstOrDefault();
            if (first != null) { _hwPicked = first; ShowTab("hardware", animate: false); }
        };
        sp.Children.Add(box);
        sp.Children.Add(results);
        Fill();
        sp.Children.Add(Faint(Loc.T("Scores are averages from public reviews (graphics: RTX 4070 = 100; CPUs: Ryzen 7 7800X3D = 100)."), 11.5, 10));
        return Card(sp);
    }

    FrameworkElement HwResultRow(object part)
    {
        var (kind, name, detail, score, max, tier) = Describe(part);
        var g = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        g.Children.Add(Pill(Loc.T(kind), KindColor(kind)));
        var mid = new StackPanel { Margin = new Thickness(8, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(new TextBlock { Text = name, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
        mid.Children.Add(Faint(detail, 11.5));
        Grid.SetColumn(mid, 1);
        g.Children.Add(mid);
        if (score > 0)
        {
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(Bar(score / max, TierColor(tier), 200));
            right.Children.Add(Faint(Loc.T(tier), 11, 3));
            Grid.SetColumn(right, 2);
            g.Children.Add(right);
        }
        var row = new Border { Child = g, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        row.MouseEnter += (_, _) => row.Background = Res("SurfaceHoverBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => { _hwPicked = part; ShowTab("hardware", animate: false); };
        return row;
    }

    static (string Kind, string Name, string Detail, double Score, double Max, string Tier) Describe(object part) => part switch
    {
        HwCpu c => ("CPU", c.Name, $"{c.Brand} · {c.Year} · " + Loc.F("{0} cores, {1} threads", c.Cores, c.Threads) + $" · {c.Ghz:0.0} GHz" + (c.Kind == "laptop" ? " · " + Loc.T("laptop") : ""),
            c.Score, HardwareDb.MaxCpu, HardwareDb.Tier(c)),
        HwGpu g => ("Graphics", g.Name, $"{g.Brand} · {g.Year}" + (g.Vram > 0 ? $" · {g.Vram} GB" : " · " + Loc.T("uses system memory")) + (g.Kind == "laptop" ? " · " + Loc.T("laptop") : g.Kind == "integrated" ? " · " + Loc.T("built into the processor") : ""),
            g.Score, HardwareDb.MaxGpu, HardwareDb.Tier(g)),
        HwRam r => ("Memory", r.Label, r.Type == "DDR5" ? Loc.T("For AMD AM5 and Intel 12th gen and newer boards that take DDR5") : Loc.T("For DDR4 boards (AMD AM4, Intel up to 14th gen)"),
            HardwareDb.RamFactor(r), 1.02, RamTier(r)),
        _ => ("", "", "", 0, 1, ""),
    };

    static string RamTier(HwRam r) => HardwareDb.RamFactor(r) is var f && f >= 0.99 ? "Top gaming" : f >= 0.95 ? "High end" : f >= 0.88 ? "Mid range" : "Entry level";

    static Color KindColor(string kind) => kind switch { "CPU" => Cyan, "Graphics" => Green, _ => Violet };

    static Color TierColor(string tier) => tier switch
    {
        "Top gaming" => Violet, "High end" => Green, "Mid range" => Cyan, "Entry level" => Amber, _ => Grey,
    };

    Border Bar(double fraction, Color c, double width)
    {
        var fill = new Border { Width = Math.Clamp(fraction, 0, 1) * width, Height = 6, CornerRadius = new CornerRadius(3), Background = B(c), HorizontalAlignment = HorizontalAlignment.Left };
        return new Border { Width = width, Height = 6, CornerRadius = new CornerRadius(3), Background = B(c, 0x26), Child = fill, HorizontalAlignment = HorizontalAlignment.Left };
    }

    // ───────── one part ─────────

    FrameworkElement HwDetail(object part)
    {
        var (kind, name, detail, score, max, tier) = Describe(part);
        var sp = new StackPanel();
        var head = new Grid();
        var left = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(Heading(name, 20));
        title.Children.Add(Pill(Loc.T(tier), TierColor(tier)).Also(p => p.Margin = new Thickness(12, 0, 0, 0)));
        left.Children.Add(title);
        left.Children.Add(Dim(detail, 12.5, 4));
        head.Children.Add(left);
        var close = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Close"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(12, 4, 12, 5) };
        close.Click += (_, _) => { _hwPicked = null; ShowTab("hardware", animate: false); };
        head.Children.Add(close);
        sp.Children.Add(head);

        // How good: against the best, and against yours.
        var how = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        string best = part switch { HwCpu => HardwareDb.Cpus.OrderByDescending(c => c.Score).First().Name, HwGpu => HardwareDb.Gpus.OrderByDescending(g => g.Score).First().Name, _ => "DDR5-7200" };
        how.Children.Add(Gauge(Loc.F("{0:0}% of the best", 100 * score / max), Loc.F("against {0}", best), score / max, TierColor(tier)));
        object mine = part switch { HwCpu => _myCpu, HwGpu => _myGpu, HwRam => _myRam, _ => null };
        if (mine != null && !Equals(mine, part))
        {
            double myScore = Describe(mine).Score;
            double diff = myScore > 0 ? score / myScore - 1 : 0;
            var c = diff > 0.03 ? Green : diff < -0.03 ? Red : Grey;
            string text = Math.Abs(diff) < 0.03 ? Loc.T("About the same") : diff >= 1 ? Loc.F("{0:0.0}× as fast", 1 + diff) : diff > 0 ? Loc.F("{0:0}% faster", diff * 100) : Loc.F("{0:0}% slower", -diff * 100);
            how.Children.Add(Gauge(text, Loc.F("than yours ({0})", Describe(mine).Name), Math.Clamp(0.5 + diff / 2, 0, 1), c));
        }
        sp.Children.Add(how);

        // In your PC: the same games with this part instead of yours.
        var swapCpu = part is HwCpu ? (HwCpu)part : _myCpu; var swapGpu = part is HwGpu ? (HwGpu)part : _myGpu; var swapRam = part is HwRam ? (HwRam)part : _myRam;
        if (_myCpu != null && _myGpu != null && mine != null && !Equals(mine, part))
        {
            sp.Children.Add(Faint(Loc.T("IN YOUR PC AT 1080p"), 10.5, 18).Also(t => t.FontWeight = FontWeights.Bold));
            var table = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var game in HardwareDb.Games.Where(g => g.Name is "Valorant" or "Fortnite" or "Counter-Strike 2" or "Cyberpunk 2077"))
            {
                var now = HardwareDb.Estimate(_myCpu, _myGpu, _myRam, game, 1080);
                var then = HardwareDb.Estimate(swapCpu, swapGpu, swapRam, game, 1080);
                if (now == null || then == null) continue;
                var row = new Grid { Margin = new Thickness(0, 3, 0, 0), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                row.Children.Add(new TextBlock { Text = game.Name, FontSize = 13, Foreground = Res("TextBrush") });
                row.Children.Add(new TextBlock { Text = Loc.F("{0} → {1} fps", Math.Round(now.Fps), Math.Round(then.Fps)), FontSize = 13, Foreground = Res("TextDimBrush") }.Also(t => Grid.SetColumn(t, 1)));
                double ch = then.Fps / now.Fps - 1;
                row.Children.Add(new TextBlock
                {
                    Text = Math.Abs(ch) < 0.02 ? Loc.T("no change") : (ch > 0 ? "+" : "") + (ch * 100).ToString("0", CultureInfo.CurrentCulture) + "%",
                    FontSize = 13, FontWeight = FontWeights.Bold, Foreground = B(Math.Abs(ch) < 0.02 ? Grey : ch > 0 ? Green : Red),
                }.Also(t => Grid.SetColumn(t, 2)));
                table.Children.Add(row);
            }
            sp.Children.Add(table);
            if (part is HwGpu && _myCpu != null && HardwareDb.Games.Count(g => HardwareDb.Estimate(_myCpu, (HwGpu)part, _myRam, g, 1080)?.Limit == "cpu") >= HardwareDb.Games.Count / 2)
                sp.Children.Add(Dim(Loc.F("Your {0} would hold this card back at 1080p in most games: it shines more at 1440p, or with a faster CPU.", _myCpu.Name), 12, 8).Also(t => t.Foreground = B(Amber)));
        }
        if (part is HwRam)
            sp.Children.Add(Dim(Loc.T("16 GB is the least for today's games and 32 GB is comfortable. Two sticks (dual channel) matter more than speed: one stick can cost 15–20% in CPU-heavy games."), 12.5, 14));

        var use = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T("Put it in the builder"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(16, 6, 16, 7) };
        use.Click += (_, _) =>
        {
            switch (part) { case HwCpu c: _bCpu = c; break; case HwGpu g: _bGpu = g; break; case HwRam r: _bRam = r; break; }
            ShowTab("hardware", animate: false);
        };
        sp.Children.Add(use);
        return Card(sp);
    }

    FrameworkElement Gauge(string big, string small, double fraction, Color c)
    {
        var sp = new StackPanel { Width = 260, Margin = new Thickness(0, 0, 24, 8) };
        sp.Children.Add(new TextBlock { Text = big, FontSize = 20, FontWeight = FontWeights.Bold, Foreground = B(c) });
        sp.Children.Add(Faint(small, 11.5, 1));
        sp.Children.Add(Bar(fraction, c, 240).Also(b => b.Margin = new Thickness(0, 6, 0, 0)));
        return sp;
    }

    // ───────── the builder ─────────

    FrameworkElement HwBuilder()
    {
        var sp = new StackPanel();
        var head = new Grid();
        head.Children.Add(Heading(Loc.T("Build a PC")));
        bool isMine = _bCpu == _myCpu && _bGpu == _myGpu && _bRam == _myRam;
        if (!isMine)
        {
            var reset = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Back to my PC"), HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 4, 12, 5) };
            reset.Click += (_, _) => { _bCpu = _myCpu; _bGpu = _myGpu; _bRam = _myRam; ShowTab("hardware", animate: false); };
            head.Children.Add(reset);
        }
        sp.Children.Add(head);
        sp.Children.Add(Dim(isMine ? Loc.T("This is your PC as Windows reports it. Change any part to see what it would do.") : Loc.T("Your own parts are marked; the frame rates below are for this build."), 12.5, 4));

        var grid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var cpuBox = Combo(HardwareDb.Cpus.OrderByDescending(c => c.Score).Select(c => (object)c), _bCpu, o => ((HwCpu)o).Name + (o == _myCpu ? "  ·  " + Loc.T("yours") : ""), 380);
        cpuBox.SelectionChanged += (_, _) => { if (cpuBox.SelectedItem is ComboBoxItem { Tag: HwCpu c } && c != _bCpu) { _bCpu = c; ShowTab("hardware", animate: false); } };
        var gpuBox = Combo(HardwareDb.Gpus.OrderByDescending(g => g.Score).Select(g => (object)g), _bGpu, o => ((HwGpu)o).Name + (o == _myGpu ? "  ·  " + Loc.T("yours") : ""), 380);
        gpuBox.SelectionChanged += (_, _) => { if (gpuBox.SelectedItem is ComboBoxItem { Tag: HwGpu g } && g != _bGpu) { _bGpu = g; ShowTab("hardware", animate: false); } };

        var ram = new WrapPanel();
        var size = Combo(new object[] { 8, 16, 32, 64 }, _bRam?.Gb, o => $"{o} GB", 90);
        var speeds = new[] { ("DDR4", 2400), ("DDR4", 2666), ("DDR4", 3200), ("DDR4", 3600), ("DDR5", 4800), ("DDR5", 5600), ("DDR5", 6000), ("DDR5", 6400), ("DDR5", 7200) }.Select(t => (object)$"{t.Item1}-{t.Item2}").ToList();
        string curSpeed = _bRam == null ? "DDR4-3200" : $"{_bRam.Type}-{_bRam.Mhz}";
        if (!speeds.Contains(curSpeed)) speeds.Add(curSpeed);
        var speed = Combo(speeds, curSpeed, o => (string)o, 130);
        var chan = Combo(new object[] { true, false }, _bRam?.Dual ?? true, o => (bool)o ? Loc.T("Two sticks or more (dual channel)") : Loc.T("One stick (single channel)"), 260);
        void RamChanged()
        {
            if (size.SelectedItem is not ComboBoxItem { Tag: int gb } || speed.SelectedItem is not ComboBoxItem { Tag: string sp2 } || chan.SelectedItem is not ComboBoxItem { Tag: bool dual }) return;
            var parts = sp2.Split('-');
            var r = new HwRam(gb, parts[0], int.Parse(parts[1], CultureInfo.InvariantCulture), dual);
            if (r == _bRam) return;
            _bRam = r == _myRam ? _myRam : r;
            ShowTab("hardware", animate: false);
        }
        size.SelectionChanged += (_, _) => RamChanged();
        speed.SelectionChanged += (_, _) => RamChanged();
        chan.SelectionChanged += (_, _) => RamChanged();
        ram.Children.Add(size.Also(c => c.Margin = new Thickness(0, 0, 8, 0)));
        ram.Children.Add(speed.Also(c => c.Margin = new Thickness(0, 0, 8, 0)));
        ram.Children.Add(chan);

        void Row(int r, string label, FrameworkElement pick, string tier)
        {
            var l = new TextBlock { Text = Loc.T(label), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
            Grid.SetRow(l, r);
            grid.Children.Add(l);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            right.Children.Add(pick);
            if (tier != null) right.Children.Add(Pill(Loc.T(tier), TierColor(tier)).Also(p => p.Margin = new Thickness(12, 0, 0, 0)));
            Grid.SetRow(right, r); Grid.SetColumn(right, 1);
            grid.Children.Add(right);
        }
        Row(0, "CPU", cpuBox, _bCpu != null ? HardwareDb.Tier(_bCpu) : null);
        Row(1, "Graphics", gpuBox, _bGpu != null ? HardwareDb.Tier(_bGpu) : null);
        Row(2, "Memory", ram, _bRam != null ? RamTier(_bRam) : null);
        sp.Children.Add(grid);
        if (_myCpu == null || _myGpu == null)
            sp.Children.Add(Faint(Loc.F("Pulse couldn't find {0} in its list ({1}). Pick the closest one above.", _myCpu == null ? Loc.T("your CPU") : Loc.T("your graphics card"), _myCpu == null ? SystemInfo.Cpu : SystemInfo.GpuText), 11.5, 2));
        return Card(sp);
    }

    ComboBox Combo(IEnumerable<object> items, object selected, Func<object, string> label, double width)
    {
        var c = new ComboBox { Width = width, MaxDropDownHeight = 420 };
        foreach (var i in items)
        {
            var item = new ComboBoxItem { Content = label(i), Tag = i };
            c.Items.Add(item);
            if (Equals(i, selected)) c.SelectedItem = item;
        }
        return c;
    }

    // ───────── frame rates ─────────

    FrameworkElement HwFpsCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading(Loc.T("Frame rates to expect")));
        if (_bCpu == null || _bGpu == null) { sp.Children.Add(Dim(Loc.T("Pick a CPU and a graphics card above to see frame rates."), 12.5, 6)); return Card(sp); }
        sp.Children.Add(Dim(Loc.F("{0} + {1} + {2}. Estimates from review averages, at the settings shown, without upscaling or frame generation; real numbers vary by 10–20% with drivers, coolers and patches.",
            _bCpu.Name, _bGpu.Name, _bRam?.Label ?? "16 GB"), 12, 4));

        var cols = new[] { 720, 1080, 1440 };
        var grid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 180 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        foreach (var _ in cols) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        void Cell(int row, int col, UIElement e) { Grid.SetRow(e, row); Grid.SetColumn(e, col); grid.Children.Add(e); }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(0, 0, Faint(Loc.T("GAME"), 10.5).Also(t => t.FontWeight = FontWeights.Bold));
        Cell(0, 1, Faint(Loc.T("SETTINGS"), 10.5).Also(t => t.FontWeight = FontWeights.Bold));
        for (int i = 0; i < cols.Length; i++) Cell(0, 2 + i, Faint($"{cols[i]}p", 10.5).Also(t => { t.FontWeight = FontWeights.Bold; t.HorizontalAlignment = HorizontalAlignment.Right; }));

        var limits = cols.ToDictionary(c => c, _ => (Cpu: 0, Gpu: 0));
        int r = 1;
        foreach (var game in HardwareDb.Games)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var line = new Border { BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 6, 0, 0) };
            Grid.SetColumnSpan(line, 2 + cols.Length);
            Cell(r, 0, line);
            Cell(r, 0, new TextBlock { Text = game.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 12, 0, 6) });
            Cell(r, 1, Faint(Loc.T(game.Settings), 11.5).Also(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 12, 8, 6); }));
            for (int i = 0; i < cols.Length; i++)
            {
                var e = HardwareDb.Estimate(_bCpu, _bGpu, _bRam, game, cols[i]);
                if (e == null) continue;
                if (e.Limit == "cpu") limits[cols[i]] = (limits[cols[i]].Cpu + 1, limits[cols[i]].Gpu);
                else if (e.Limit == "gpu") limits[cols[i]] = (limits[cols[i]].Cpu, limits[cols[i]].Gpu + 1);
                var c = e.Fps >= 144 ? Green : e.Fps >= 60 ? Amber : Red;
                var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 4) };
                cell.Children.Add(new TextBlock { Text = Math.Round(e.Fps).ToString("N0", CultureInfo.CurrentCulture) + (e.VramShort ? " !" : ""), FontSize = 17, FontWeight = FontWeights.Bold, Foreground = B(c), HorizontalAlignment = HorizontalAlignment.Right });
                string why = e.Limit switch { "cpu" => Loc.T("CPU-limited"), "gpu" => Loc.T("graphics-limited"), "cap" => Loc.T("game's cap"), _ => Loc.T("balanced") };
                cell.Children.Add(Faint(why, 10.5).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right));
                if (e.VramShort) cell.ToolTip = Loc.F("Needs about {0} GB of video memory at these settings: expect stutter, or lower textures.", cols[i] >= 1440 ? game.V1440 : game.V1080);
                Cell(r, 2 + i, cell);
            }
            r++;
        }
        sp.Children.Add(grid);

        // What holds this PC back, and the one upgrade that would help most.
        var (c1080, g1080) = limits[1080];
        var (c1440, g1440) = limits[1440];
        string verdict = c1080 > g1080 && c1440 > g1440 ? Loc.F("The {0} sets the pace at every resolution: a faster CPU is the upgrade that would help most.", _bCpu.Name)
            : c1080 > g1080 ? Loc.F("At 1080p the {0} holds most games back; at 1440p the {1} does. Play at 1440p, or upgrade the CPU for more frames at 1080p.", _bCpu.Name, _bGpu.Name)
            : Loc.F("The {0} sets the pace in most games: a faster graphics card is the upgrade that would help most.", _bGpu.Name);
        sp.Children.Add(new Border
        {
            Background = B(Cyan, 0x14), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 16, 0, 0),
            Child = new TextBlock { Text = verdict, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextBrush") },
        });
        if (_bRam is { Dual: false } || _bRam is { Gb: <= 8 })
            sp.Children.Add(Dim(_bRam.Dual ? Loc.T("8 GB of memory is tight for today's games: expect stutter in big ones. 16 GB or more fixes it.") : Loc.T("One memory stick costs frames in CPU-heavy games: a second matching stick (dual channel) is a cheap win."), 12, 10).Also(t => t.Foreground = B(Amber)));
        sp.Children.Add(Faint(Loc.T("Green: 144 fps or more. Yellow: 60 to 143. Red: under 60. ! = short on video memory at these settings."), 11, 12));
        return Card(sp);
    }
}
