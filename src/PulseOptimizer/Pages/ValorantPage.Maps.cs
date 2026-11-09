using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Pulse.Core.Games;
using Pulse.Core.Games.Valorant;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// Heatmaps and your rounds. Heatmap: pick a map (pictures), pick what to show (three plain choices), and narrow it
/// with a few drop-downs that start on "everything". Your rounds: every round of a match in one line each, what you
/// did and how you died, with the round replay on the minimap next to it.
/// </summary>
public partial class ValorantPage
{
    string _mapName, _mapLayer = "deaths", _mapSide = "all", _mapPhase = "all", _mapBuy = "all", _mapResult = "all";
    string _mapMode = "heat";
    // Your rounds: the match and round open, or (from the coach) only rounds where you died a certain way.
    string _storyMatch;
    int _storyRound = -1;
    DeathTag? _storyTag;

    sealed record Dot(double X, double Y, string Callout, string Tip);

    static readonly Color HmAccent = Color.FromRgb(0x22, 0xD3, 0xEE), HmCard = Color.FromRgb(0x1B, 0x1D, 0x23), HmLine = Color.FromRgb(0x2A, 0x2D, 0x35);

    /// <summary>The coach's "Watch these deaths": your rounds, only the ones where you died that way.</summary>
    void OpenDeaths(DeathTag tag)
    {
        _mapMode = "rounds";
        _storyTag = tag;
        _storyMatch = null;
        _storyRound = -1;
        ShowTab("maps");
    }

    void BuildMaps()
    {
        var matches = CoachMatches();
        if (matches.Count == 0)
        {
            Root.Children.Add(CoachHeader(matches));
            return;
        }
        Root.Children.Add(ModeBar());
        if (_mapMode == "rounds") { BuildStory(matches); return; }

        var maps = matches.GroupBy(m => m.Map).OrderByDescending(g => g.Count()).Select(g => g.Key).Where(m => m.Length > 0).ToList();
        if (_mapName == null || !maps.Contains(_mapName)) _mapName = maps.FirstOrDefault();

        var pick = new StackPanel();
        // 1. The map, as pictures.
        var mapRow = new WrapPanel();
        foreach (var m in maps) mapRow.Children.Add(MapTile(m, matches.Count(x => x.Map == m)));
        pick.Children.Add(mapRow);

        // 2. What to show: three choices that say what they mean.
        var what = new UniformGrid { Columns = 3, Margin = new Thickness(0, 12, -10, 0) };
        what.Children.Add(Choice("deaths", "Where I die", "Every spot you were killed", Color.FromRgb(0xFF, 0x5A, 0x4E)));
        what.Children.Add(Choice("kills", "Where I get kills", "Where you stood for each kill", Color.FromRgb(0x3D, 0xDC, 0x84)));
        what.Children.Add(Choice("enemies", "Where enemies shoot from", "Where enemies stood when they killed your team", Color.FromRgb(0xFA, 0xD6, 0x63)));
        pick.Children.Add(what);

        // 3. Narrow it down (everything until you change something).
        var filters = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        filters.Children.Add(Faint(Loc.T("Show"), 12, 0).Also(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 10, 6); }));
        filters.Children.Add(Dropdown(_mapSide, new[] { ("all", "Both sides"), ("attack", "Attack only"), ("defense", "Defense only") }, v => _mapSide = v));
        filters.Children.Add(Dropdown(_mapBuy, new[] { ("all", "Every round type"), ("pistol", "Pistol rounds"), ("eco", "Eco rounds"), ("half", "Half-buy rounds"), ("full", "Full-buy rounds") }, v => _mapBuy = v));
        filters.Children.Add(Dropdown(_mapResult, new[] { ("all", "Won and lost rounds"), ("won", "Rounds won"), ("lost", "Rounds lost") }, v => _mapResult = v));
        if (_mapLayer == "enemies")
            filters.Children.Add(Dropdown(_mapPhase, new[] { ("all", "The whole round"), ("Early", "First 25 seconds"), ("Mid", "25 to 60 seconds"), ("Late", "After 60 seconds"), ("Post-plant", "After the plant") }, v => _mapPhase = v));
        if (_mapSide != "all" || _mapBuy != "all" || _mapResult != "all" || _mapPhase != "all")
        {
            var reset = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Reset"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 6) };
            reset.Click += (_, _) => { _mapSide = _mapBuy = _mapResult = _mapPhase = "all"; Build(); };
            filters.Children.Add(reset);
        }
        pick.Children.Add(filters);
        Root.Children.Add(Card(pick));

        var onMap = matches.Where(m => m.Map == _mapName).ToList();
        var dots = Dots(onMap);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        var mapCol = new StackPanel();
        mapCol.Children.Add(Heatmap(_mapName, dots, 480));
        mapCol.Children.Add(HeatLegend());
        body.Children.Add(mapCol);
        var side = new StackPanel { Margin = new Thickness(24, 0, 0, 0) };
        Grid.SetColumn(side, 1);
        string noun = _mapLayer switch { "kills" => "kills", "enemies" => "enemy kills", _ => "deaths" };
        var big = new StackPanel { Orientation = Orientation.Horizontal };
        big.Children.Add(new TextBlock { Text = dots.Count.ToString(CultureInfo.CurrentCulture), FontSize = 34, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont") });
        big.Children.Add(new TextBlock { Text = " " + Loc.T(noun), FontSize = 15, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 7) });
        side.Children.Add(big);
        side.Children.Add(Faint(Loc.F("on {0}, in {1} matches", _mapName, onMap.Count), 12, 0));
        var hot = dots.Where(d => d.Callout.Length > 0).GroupBy(d => d.Callout).OrderByDescending(g => g.Count()).Take(6).ToList();
        if (dots.Count == 0)
            side.Children.Add(Dim(Loc.T("Nothing here with these settings. Try \"Both sides\" or \"Every round type\"."), 12.5, 12));
        if (hot.Count > 0)
        {
            side.Children.Add(Label("Where it happens most").Also(l => l.Margin = new Thickness(0, 18, 0, 8)));
            var hc = _mapLayer == "kills" ? Color.FromRgb(0x3D, 0xDC, 0x84) : _mapLayer == "enemies" ? Color.FromRgb(0xFA, 0xD6, 0x63) : Color.FromRgb(0xFF, 0x5A, 0x4E);
            foreach (var h in hot)
            {
                double pct = 100.0 * h.Count() / dots.Count;
                var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                g.Children.Add(new TextBlock { Text = h.Key, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
                var track = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Center };
                track.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = B(HmLine) });
                var fill = new Border { CornerRadius = new CornerRadius(4), Background = B(hc), HorizontalAlignment = HorizontalAlignment.Left };
                track.Children.Add(fill);
                track.SizeChanged += (_, e) => fill.Width = Math.Max(6, e.NewSize.Width * pct / 100);
                Grid.SetColumn(track, 1); g.Children.Add(track);
                var n = new TextBlock { Text = $"{h.Count()} · {pct:0}%", FontSize = 12, Foreground = Res("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(n, 2); g.Children.Add(n);
                side.Children.Add(g);
            }
            string advice = _mapLayer switch
            {
                "deaths" => Loc.F("You die most at {0}. Next time you're there, go in with a teammate or use utility first, or try a different spot.", hot[0].Key),
                "enemies" => Loc.F("Enemies get the most kills on your team from {0}. Check it first, or block it with a smoke or flash.", hot[0].Key),
                _ => Loc.F("You get the most kills from {0}: a spot that works for you.", hot[0].Key),
            };
            side.Children.Add(new Border
            {
                BorderBrush = Res("AccentBrush"), BorderThickness = new Thickness(2, 0, 0, 0), Background = Res("Surface2Brush"), CornerRadius = new CornerRadius(0, 6, 6, 0),
                Padding = new Thickness(12, 8, 12, 9), Margin = new Thickness(0, 14, 0, 0), Child = Dim(advice, 12.5, 0),
            });
        }
        var me = Analysis.Mine(onMap);
        side.Children.Add(Label("You on this map").Also(l => l.Margin = new Thickness(0, 18, 0, 6)));
        var stats = new WrapPanel();
        void Stat(string label, string value)
        {
            var s = new StackPanel { Margin = new Thickness(0, 0, 24, 6) };
            s.Children.Add(new TextBlock { Text = value, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
            s.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 11, Foreground = Res("TextFaintBrush") });
            stats.Children.Add(s);
        }
        Stat("Matches", me.Matches.ToString(CultureInfo.CurrentCulture));
        Stat("Won", me.WinPct.ToString("0", CultureInfo.CurrentCulture) + "%");
        Stat("Kills per round", me.KillsPerRound.ToString("0.00", CultureInfo.CurrentCulture));
        Stat("Damage per round", me.Adr.ToString("0", CultureInfo.CurrentCulture));
        side.Children.Add(stats);
        body.Children.Add(side);
        Root.Children.Add(Card(body));
        if (!MapGeo.HasMap(_mapName))
            Root.Children.Add(Card(Faint(Loc.T("This map's layout isn't in this version of Pulse yet, so the dots can't be placed. They will be after the next update."), 12, 0)));
    }

    /// <summary>"Heatmap" or "Your rounds", as two big tabs.</summary>
    FrameworkElement ModeBar()
    {
        var g = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 12), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        void Tab(string key, string title, string sub)
        {
            bool on = _mapMode == key;
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Res(on ? "TextBrush" : "TextDimBrush") });
            sp.Children.Add(new TextBlock { Text = Loc.T(sub), FontSize = 11.5, Foreground = Res("TextFaintBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            var b = new Border
            {
                Child = sp, Padding = new Thickness(14, 9, 14, 10), Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1), BorderBrush = B(on ? HmAccent : HmLine), Background = B(on ? A(HmAccent, 0x16) : HmCard),
            };
            b.MouseLeftButtonUp += (_, _) => { if (_mapMode != key) { _mapMode = key; StopRoundPlay(); Build(); } };
            g.Children.Add(b);
        }
        Tab("heat", "Heatmap", "Where things happen on each map");
        Tab("rounds", "Your rounds", "What you did and how you died, round by round");
        return g;
    }

    FrameworkElement MapTile(string map, int count)
    {
        bool on = map == _mapName;
        var g = new Grid { Width = 132, Height = 70 };
        if (MatchArt.MapPicture(map) is { } pic) g.Children.Add(new Border { Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill } });
        g.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(on ? (byte)0x30 : (byte)0x70, 0, 0, 0), Color.FromArgb(0xE0, 0, 0, 0), 90) });
        g.Children.Add(new TextBlock { Text = map, FontSize = 13.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(9, 0, 0, 7) });
        g.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 6, 0), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1, 5, 2),
            Background = B(Color.FromArgb(0xB0, 0, 0, 0)), Child = new TextBlock { Text = count.ToString(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
            ToolTip = Loc.F("{0} matches", count),
        });
        var b = new Border
        {
            Child = g, CornerRadius = new CornerRadius(8), ClipToBounds = true, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(2), BorderBrush = on ? B(HmAccent) : Brushes.Transparent, Opacity = on ? 1 : 0.8,
        };
        b.MouseEnter += (_, _) => b.Opacity = 1;
        b.MouseLeave += (_, _) => b.Opacity = on ? 1 : 0.8;
        b.MouseLeftButtonUp += (_, _) => { _mapName = map; Build(); };
        return b;
    }

    FrameworkElement Choice(string key, string title, string sub, Color c)
    {
        bool on = _mapLayer == key;
        var sp = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = B(c), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res(on ? "TextBrush" : "TextDimBrush") });
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock { Text = Loc.T(sub), FontSize = 11.5, Foreground = Res("TextFaintBrush"), Margin = new Thickness(18, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        var b = new Border
        {
            Child = sp, Padding = new Thickness(12, 9, 12, 10), Margin = new Thickness(0, 0, 10, 0), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1), BorderBrush = B(on ? c : HmLine), Background = B(on ? A(c, 0x14) : HmCard),
        };
        b.MouseLeftButtonUp += (_, _) => { if (_mapLayer != key) { _mapLayer = key; if (key != "enemies") _mapPhase = "all"; Build(); } };
        return b;
    }

    /// <summary>A pill showing the current choice; click it for the list.</summary>
    FrameworkElement Dropdown(string current, (string Key, string Label)[] items, Action<string> set)
    {
        var label = items.FirstOrDefault(i => i.Key == current).Label ?? items[0].Label;
        bool changed = current != items[0].Key;
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res(changed ? "TextBrush" : "TextDimBrush") });
        sp.Children.Add(new TextBlock { Text = "  ▾", FontSize = 11, Foreground = Res("TextFaintBrush"), VerticalAlignment = VerticalAlignment.Center });
        var b = new Border
        {
            Child = sp, Padding = new Thickness(12, 6, 10, 7), Margin = new Thickness(0, 0, 8, 6), CornerRadius = new CornerRadius(999), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1), BorderBrush = B(changed ? HmAccent : HmLine), Background = B(changed ? A(HmAccent, 0x14) : HmCard),
        };
        var menu = new ContextMenu();
        foreach (var (k, l) in items)
        {
            var mi = new MenuItem { Header = Loc.T(l), FontWeight = k == current ? FontWeights.Bold : FontWeights.Normal };
            var key = k;
            mi.Click += (_, _) => { set(key); Build(); };
            menu.Items.Add(mi);
        }
        b.MouseLeftButtonUp += (_, _) => { menu.PlacementTarget = b; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };
        return b;
    }

    FrameworkElement HeatLegend()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var c = _mapLayer == "kills" ? Color.FromRgb(0x3D, 0xDC, 0x84) : _mapLayer == "enemies" ? Color.FromRgb(0xFA, 0xD6, 0x63) : Color.FromRgb(0xFF, 0x5A, 0x4E);
        sp.Children.Add(new Border { Width = 90, Height = 8, CornerRadius = new CornerRadius(4), Background = new LinearGradientBrush(A(c, 0x20), c, 0), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        sp.Children.Add(Faint(Loc.T("brighter = happened more often there · hover a dot for the round"), 11, 0));
        return sp;
    }

    List<Dot> Dots(List<VMatch> onMap)
    {
        var list = new List<Dot>();
        foreach (var m in onMap)
        {
            var team = m.MyTeam;
            bool SideOk(VKill k) => (_mapSide == "all" || (m.Attacking(team, k.Round) == (_mapSide == "attack")))
                && (_mapBuy == "all" || BuyType(m, k.Round) == _mapBuy)
                && (_mapResult == "all" || (RoundWon(m, k.Round) == (_mapResult == "won")));
            foreach (var k in m.Kills.Where(SideOk))
            {
                switch (_mapLayer)
                {
                    case "deaths" when k.Victim == m.Me:
                        list.Add(new Dot(k.VX, k.VY, MapGeo.CalloutAt(m.Map, k.VX, k.VY), Loc.F("Round {0}", k.Round + 1)));
                        break;
                    case "kills" when k.Killer == m.Me && m.TeamOf(k.Victim) != team && k.At(m.Me) is Pt p:
                        list.Add(new Dot(p.X, p.Y, MapGeo.CalloutAt(m.Map, p.X, p.Y), Loc.F("Round {0}", k.Round + 1)));
                        break;
                    case "enemies" when m.TeamOf(k.Victim) == team && m.TeamOf(k.Killer) is { Length: > 0 } kt && kt != team
                                        && k.At(k.Killer) is Pt e && (_mapPhase == "all" || Analysis.Phase(m, k) == _mapPhase):
                        list.Add(new Dot(e.X, e.Y, MapGeo.CalloutAt(m.Map, e.X, e.Y), Loc.F("Round {0}", k.Round + 1)));
                        break;
                }
            }
        }
        return list;
    }

    /// <summary>The minimap with soft dots that add up where many land in one place.</summary>
    FrameworkElement Heatmap(string map, List<Dot> dots, double size, IEnumerable<(double X, double Y, Brush Fill, string Tip)> marks = null)
    {
        var grid = new Grid { Width = size, Height = size, ClipToBounds = true };
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = Res("Surface2Brush") });
        if (MapGeo.Minimap(map) is { } img)
            grid.Children.Add(new Image { Source = img, Stretch = Stretch.Uniform, Opacity = 0.9 }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
        var canvas = new Canvas();
        grid.Children.Add(canvas);
        var hot = _mapLayer == "kills" ? Color.FromRgb(0x3D, 0xDC, 0x84) : _mapLayer == "enemies" ? Color.FromRgb(0xFA, 0xD6, 0x63) : Color.FromRgb(0xFF, 0x5A, 0x4E);
        var glow = new RadialGradientBrush(Color.FromArgb(150, hot.R, hot.G, hot.B), Color.FromArgb(0, hot.R, hot.G, hot.B));
        glow.Freeze();
        var core = new SolidColorBrush(Color.FromArgb(230, hot.R, hot.G, hot.B));
        core.Freeze();
        foreach (var d in dots)
        {
            if (MapGeo.ToMinimap(map, d.X, d.Y) is not Point p || p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1) continue;
            double r = size * 0.045;
            var g = new Ellipse { Width = r * 2, Height = r * 2, Fill = glow, IsHitTestVisible = false };
            Canvas.SetLeft(g, p.X * size - r); Canvas.SetTop(g, p.Y * size - r);
            canvas.Children.Add(g);
            var c = new Ellipse { Width = 6, Height = 6, Fill = core, ToolTip = d.Callout.Length > 0 ? $"{d.Callout} · {d.Tip}" : d.Tip };
            Canvas.SetLeft(c, p.X * size - 3); Canvas.SetTop(c, p.Y * size - 3);
            canvas.Children.Add(c);
        }
        if (marks != null)
            foreach (var mk in marks)
            {
                if (MapGeo.ToMinimap(map, mk.X, mk.Y) is not Point p) continue;
                var c = new Ellipse { Width = 11, Height = 11, Fill = mk.Fill, Stroke = Brushes.White, StrokeThickness = 1.5, ToolTip = mk.Tip };
                Canvas.SetLeft(c, p.X * size - 5.5); Canvas.SetTop(c, p.Y * size - 5.5);
                canvas.Children.Add(c);
            }
        return grid;
    }

    // ───────── your rounds ─────────

    void BuildStory(List<VMatch> matches)
    {
        StopRoundPlay();
        var recent = matches.Take(12).ToList();
        // Every round to list: one match's, or (from the coach) the rounds you died a certain way in recent matches.
        List<(VMatch M, VRound R)> rounds;
        var top = new StackPanel();
        if (_storyTag is DeathTag tag)
        {
            rounds = new List<(VMatch M, VRound R)>();
            foreach (var m in matches.Take(15))
                foreach (var d in Analysis.Deaths(m, m.Me).Where(d => d.Tags.Contains(tag)))
                    if (m.Rounds.FirstOrDefault(r => r.N == d.Kill.Round) is { } rr) rounds.Add((m, rr));
            var bar = new Grid();
            bar.Children.Add(new TextBlock { Text = Loc.F("Rounds where you died: {0} ({1})", Loc.T(Coach.CauseName(tag)), rounds.Count), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
            var all = Btn("Show every round");
            all.HorizontalAlignment = HorizontalAlignment.Right;
            all.Margin = new Thickness(0);
            all.Click += (_, _) => { _storyTag = null; _storyRound = -1; Build(); };
            bar.Children.Add(all);
            top.Children.Add(bar);
            top.Children.Add(Faint(Loc.T("From your last 15 matches. Click a round to replay it on the map."), 11.5, 4));
        }
        else
        {
            var m = recent.FirstOrDefault(x => x.Id == _storyMatch) ?? recent[0];
            if (_storyMatch != m.Id) { _storyMatch = m.Id; _storyRound = -1; }
            var strip = new WrapPanel();
            foreach (var x in recent) strip.Children.Add(MatchTile(x));
            top.Children.Add(strip);
            rounds = m.Rounds.OrderBy(r => r.N).Select(r => (m, r)).ToList();
        }
        Root.Children.Add(Card(top));
        if (rounds.Count == 0) { Root.Children.Add(Card(Dim(Loc.T("No rounds to show yet."), 12.5, 0))); return; }

        // The open round: the one clicked, else the first one you died in, else the first.
        var open = rounds.FirstOrDefault(x => x.M.Id == _storyMatch && x.R.N == _storyRound);
        if (open.M == null) open = rounds.FirstOrDefault(x => x.M.Kills.Any(k => k.Round == x.R.N && k.Victim == x.M.Me));
        if (open.M == null) open = rounds[0];
        _storyMatch = open.M.Id;
        _storyRound = open.R.N;

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 520 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var list = new StackPanel();
        foreach (var (m, r) in rounds) list.Children.Add(StoryRow(m, r, _storyTag != null, m.Id == open.M.Id && r.N == open.R.N));
        // The list scrolls on its own so the replay stays in view next to it.
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 660, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 18, 0) });
        var replay = Replay(open.M, open.R);
        Grid.SetColumn(replay, 1);
        body.Children.Add(replay);
        Root.Children.Add(Card(body));
        Root.Children.Add(Card(Faint(Loc.T("Positions are where everyone stood at the moment of each kill: Riot records them only then, and only hands them out after the match is over. Round times are counted from when the barriers drop."), 11.5, 0)));
    }

    FrameworkElement MatchTile(VMatch m)
    {
        bool on = m.Id == _storyMatch;
        int us = m.Rounds.Count(r => r.Winner == m.MyTeam), them = m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != m.MyTeam);
        int res = m.Outcome(m.MyTeam);
        var g = new Grid { Width = 150, Height = 64 };
        if (MatchArt.MapPicture(m.Map) is { } pic) g.Children.Add(new Border { Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill } });
        g.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(0x60, 0, 0, 0), Color.FromArgb(0xE6, 0, 0, 0), 0) });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (MatchArt.AgentIcon(m.MyAgent) is { } ai) row.Children.Add(new Image { Source = ai, Width = 34, Height = 34, Margin = new Thickness(0, 0, 8, 0) });
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(new TextBlock { Text = m.Map, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        var sc = new StackPanel { Orientation = Orientation.Horizontal };
        sc.Children.Add(new TextBlock { Text = $"{us}–{them}", FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = B(res > 0 ? YouColor : res < 0 ? EnemyColor : Color.FromRgb(0xB0, 0xB3, 0xBB)) });
        sc.Children.Add(new TextBlock { Text = "  " + Ago(m.StartUtc), FontSize = 10.5, Foreground = B(Color.FromRgb(0xB0, 0xB3, 0xBB)), VerticalAlignment = VerticalAlignment.Center });
        t.Children.Add(sc);
        row.Children.Add(t);
        g.Children.Add(row);
        var b = new Border
        {
            Child = g, CornerRadius = new CornerRadius(8), ClipToBounds = true, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(2), BorderBrush = on ? B(HmAccent) : Brushes.Transparent, Opacity = on ? 1 : 0.8,
        };
        b.MouseEnter += (_, _) => b.Opacity = 1;
        b.MouseLeave += (_, _) => b.Opacity = on ? 1 : 0.8;
        var id = m.Id;
        b.MouseLeftButtonUp += (_, _) => { _storyMatch = id; _storyRound = -1; Build(); };
        return b;
    }

    /// <summary>One round in a line: result and buy, what you did, and how it ended for you.</summary>
    FrameworkElement StoryRow(VMatch m, VRound r, bool showMap, bool open)
    {
        var me = m.Me;
        bool won = r.Winner == m.MyTeam, atk = m.Attacking(m.MyTeam, r.N);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });

        var num = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Center, Background = B(A(won ? YouColor : EnemyColor, 0x40)),
            BorderBrush = B(won ? YouColor : EnemyColor), BorderThickness = new Thickness(0, 0, 0, 2), ToolTip = Loc.T(won ? "Won" : "Lost"),
            Child = new TextBlock { Text = (r.N + 1).ToString(CultureInfo.CurrentCulture), FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        g.Children.Add(num);

        var situ = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        situ.Children.Add(new TextBlock { Text = showMap ? m.Map + " · " + Loc.T(atk ? "Attack" : "Defense") : Loc.T(atk ? "Attack" : "Defense"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        var buy = BuyType(m, r.N);
        string load = r.Eco.TryGetValue(me, out var e) ? e.Loadout.ToString("N0", CultureInfo.CurrentCulture) : "";
        situ.Children.Add(new TextBlock { Text = Loc.T(buy switch { "pistol" => "Pistol", "eco" => "Eco", "half" => "Half buy", "full" => "Full buy", _ => "" }) + (load.Length > 0 ? " · " + load : ""), FontSize = 11, Foreground = Res("TextFaintBrush") });
        Grid.SetColumn(situ, 1); g.Children.Add(situ);

        // What you did.
        var kills = m.Kills.Where(k => k.Round == r.N && k.Killer == me && k.Victim != me && m.TeamOf(k.Victim) != m.MyTeam).OrderBy(k => k.RoundMs).ToList();
        int assists = m.Kills.Count(k => k.Round == r.N && k.Assists.Contains(me));
        int dmg = r.Damage.Where(d => d.From == me && m.TeamOf(d.To) != m.MyTeam).Sum(d => d.Amount);
        var did = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var faces = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var k in kills)
            if (MatchArt.AgentIcon(Agent(m, k.Victim)) is { } vi)
                faces.Children.Add(new Image { Source = vi, Width = 22, Height = 22, Margin = new Thickness(0, 0, 3, 0), ToolTip = Loc.F("You killed {0}", Agent(m, k.Victim)) + (Analysis.WeaponName(k) is { Length: > 0 } wn ? " · " + Loc.T(wn) : "") });
        if (kills.Count == 0) faces.Children.Add(new TextBlock { Text = Loc.T("No kills"), FontSize = 12, Foreground = Res("TextFaintBrush") });
        did.Children.Add(faces);
        var parts = new List<string>();
        if (kills.Count > 0) parts.Add(kills.Count == 1 ? Loc.T("1 kill") : Loc.F("{0} kills", kills.Count));
        if (assists > 0) parts.Add(assists == 1 ? Loc.T("1 assist") : Loc.F("{0} assists", assists));
        parts.Add(Loc.F("{0} damage", dmg));
        if (r.Planter == me) parts.Add(Loc.T("planted"));
        if (r.Defuser == me) parts.Add(Loc.T("defused"));
        did.Children.Add(new TextBlock { Text = string.Join(" · ", parts), FontSize = 11, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(did, 2); g.Children.Add(did);

        // How it ended for you.
        var death = Analysis.Deaths(m, me).FirstOrDefault(d => d.Kill.Round == r.N);
        var end = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (death == null)
        {
            end.Children.Add(new TextBlock { Text = "✓ " + Loc.T("Survived"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = B(YouColor) });
        }
        else
        {
            var k = death.Kill;
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new TextBlock { Text = Loc.F("Died at {0}", Clock(Analysis.RoundClockMs(m, k))), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B(EnemyColor), VerticalAlignment = VerticalAlignment.Center });
            if (MatchArt.AgentIcon(death.KillerAgent) is { } ka) line.Children.Add(new Image { Source = ka, Width = 20, Height = 20, Margin = new Thickness(7, 0, 4, 0), ToolTip = death.KillerAgent });
            var w = Analysis.WeaponName(k);
            var call = MapGeo.CalloutAt(m.Map, k.VX, k.VY);
            line.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { death.KillerAgent, w.Length > 0 ? Loc.T(w) : "", call }.Where(x => x.Length > 0)), FontSize = 11.5, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            end.Children.Add(line);
            var tags = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
            foreach (var t in death.Tags.Where(t => t != DeathTag.NoAbility || death.Tags.Count <= 2).Take(3))
            {
                bool bad = Coach.IsMistake(t);
                var tc = t == DeathTag.Traded ? YouColor : bad ? EnemyColor : Color.FromRgb(0x8A, 0x8F, 0x98);
                tags.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 0, 4, 0), Background = B(A(tc, 0x1C)), BorderBrush = B(A(tc, 0x70)), BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = Loc.T(Coach.CauseName(t)), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = B(tc) },
                });
            }
            end.Children.Add(tags);
        }
        Grid.SetColumn(end, 3); g.Children.Add(end);

        var row = new Border
        {
            Child = g, Padding = new Thickness(10, 7, 12, 7), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1), BorderBrush = B(open ? HmAccent : Colors.Transparent), Background = B(open ? A(HmAccent, 0x12) : HmCard),
            ToolTip = Loc.T("Replay this round on the map"),
        };
        if (!open)
        {
            row.MouseEnter += (_, _) => row.Background = B(Color.FromRgb(0x22, 0x25, 0x2C));
            row.MouseLeave += (_, _) => row.Background = B(HmCard);
        }
        var id = m.Id;
        int n = r.N;
        row.MouseLeftButtonUp += (_, _) => { _storyMatch = id; _storyRound = n; _rmKill = 0; Build(); };
        return row;
    }

    /// <summary>The round on the minimap, step by step, with the kill list and what to try if you died.</summary>
    FrameworkElement Replay(VMatch m, VRound round)
    {
        _rmMatch = m.Id;
        _rmRound = round.N;
        var allKills = m.Kills.Where(k => k.Round == round.N).OrderBy(k => k.GameMs).ToList();
        var kills = _rmAll ? allKills : allKills.Where(k => k.Killer == m.Me || k.Victim == m.Me).ToList();
        if (kills.Count == 0) kills = allKills;
        _rmKill = Math.Clamp(_rmKill, 0, Math.Max(0, kills.Count - 1));
        var sp = new StackPanel { Width = 500 };
        sp.Children.Add(RoundSummary(m, round, allKills));
        _rmStage = new ContentControl { Margin = new Thickness(0, 12, 0, 0) };
        sp.Children.Add(_rmStage);
        var which = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        which.Children.Add(Chip(Loc.F("My kills and deaths ({0})", allKills.Count(k => k.Killer == m.Me || k.Victim == m.Me)), "rmall", !_rmAll, () => { _rmAll = false; _rmKill = 0; Build(); }));
        which.Children.Add(Chip(Loc.F("Every kill ({0})", allKills.Count), "rmall", _rmAll, () => { _rmAll = true; _rmKill = 0; Build(); }));
        sp.Children.Add(which);
        sp.Children.Add(KillList(m, kills));
        if (Analysis.Deaths(m, m.Me).FirstOrDefault(d => d.Kill.Round == round.N) is { } death) sp.Children.Add(WhatToDo(death));
        ShowKill(m, round, kills);
        return sp;
    }

    /// <summary>One line on what to try next time, from the death's main cause.</summary>
    FrameworkElement WhatToDo(DeathInfo d)
    {
        string tip = d.Main switch
        {
            DeathTag.FirstDeath => "First death of the round. Next time: use utility before you peek, or let a teammate swing with you so you can be traded.",
            DeathTag.Outnumbered => "Two or more enemies were close. Next time: fight one angle at a time and use cover or a smoke to cut the others.",
            DeathTag.Alone => "No teammate within 15 m, so nobody could trade. Next time: move with someone, or fall back once you've seen them.",
            DeathTag.NoAbility => "You died with your abilities unused. Next time: use one before this fight.",
            DeathTag.NotTraded => "A teammate was alive but didn't trade you. Next time: fight where they can see your killer, and call where you died.",
            DeathTag.Outgunned => "They had a full buy against your eco. Nothing much to fix: on ecos, play for a close-range pick or to save.",
            DeathTag.LastAlive => "You were the last one alive. Clutches are hard; save the gun when the round is lost.",
            DeathTag.Traded => "You were traded: this death didn't cost the round anything extra.",
            _ => "",
        };
        if (tip.Length == 0) return new Border();
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = Loc.T("What to try").ToUpper(CultureInfo.CurrentCulture), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Res("AccentBrush") });
        sp.Children.Add(Dim(Loc.T(tip), 12, 3));
        return new Border
        {
            BorderBrush = Res("AccentBrush"), BorderThickness = new Thickness(2, 0, 0, 0), Background = Res("Surface2Brush"),
            CornerRadius = new CornerRadius(0, 6, 6, 0), Padding = new Thickness(12, 8, 12, 9), Margin = new Thickness(0, 12, 0, 0), Child = sp,
        };
    }
}
