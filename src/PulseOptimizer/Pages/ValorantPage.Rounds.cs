using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Pulse.Core.Games;
using Pulse.Core.Games.Valorant;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// Round by round: pick one of your past matches and a round, then step through every kill of it on the minimap
/// with all ten players where they stood at that moment, who killed whom, the buys and the spike. It only uses
/// finished matches; Riot records positions at each kill, and only gives them out after the match.
/// </summary>
public partial class ValorantPage
{
    string _rmMatch;
    int _rmRound, _rmKill;
    bool _rmAll;
    DispatcherTimer _rmTimer;
    ContentControl _rmStage;

    // You green, your teammates yellow, enemies red: the same everywhere in the round view.
    static readonly Color YouColor = Color.FromRgb(0x3D, 0xDC, 0x84), MateColor = Color.FromRgb(0xFA, 0xD6, 0x63), EnemyColor = Color.FromRgb(0xFF, 0x5A, 0x4E);

    static Color ColorFor(VMatch m, string id) => id == m.Me ? YouColor : m.TeamOf(id) == m.MyTeam ? MateColor : EnemyColor;

    /// <summary>Your team's buy in a round: "pistol", "eco", "half" or "full", from the average loadout.</summary>
    internal static string BuyType(VMatch m, int round)
    {
        int h = m.HalfLength;
        if (round == 0 || round == h) return "pistol";
        var r = m.Rounds.FirstOrDefault(x => x.N == round);
        if (r == null) return "";
        var team = m.MyTeam;
        var mine = r.Eco.Where(e => m.TeamOf(e.Key) == team).Select(e => e.Value.Loadout).ToList();
        if (mine.Count == 0) return "";
        double avg = mine.Average();
        return avg < 2000 ? "eco" : avg < 3900 ? "half" : "full";
    }

    static string TeamBuy(VMatch m, VRound r, string team)
    {
        var l = r.Eco.Where(e => m.TeamOf(e.Key) == team).Select(e => e.Value.Loadout).ToList();
        if (l.Count == 0) return "";
        double avg = l.Average();
        return r.N == 0 || r.N == m.HalfLength ? "Pistol" : avg < 2000 ? "Eco" : avg < 3900 ? "Half buy" : "Full buy";
    }

    internal static bool RoundWon(VMatch m, int round) => m.Rounds.FirstOrDefault(r => r.N == round)?.Winner == m.MyTeam;

    void StopRoundPlay() => _rmTimer?.Stop();

    FrameworkElement RoundSummary(VMatch m, VRound r, List<VKill> kills)
    {
        var sp = new StackPanel();
        bool won = r.Winner == m.MyTeam, atk = m.Attacking(m.MyTeam, r.N);
        int us = m.Rounds.Count(x => x.N < r.N && x.Winner == m.MyTeam), them = m.Rounds.Count(x => x.N < r.N && x.Winner.Length > 0 && x.Winner != m.MyTeam);
        sp.Children.Add(Heading(Loc.F("Round {0}", r.N + 1) + " · " + Loc.T(won ? "Won" : "Lost")));
        string how = r.Result switch
        {
            "Elimination" => Loc.T("by elimination"),
            "Detonate" => Loc.T("spike detonated"),
            "Defuse" => Loc.T("spike defused"),
            "Surrendered" => Loc.T("surrender"),
            _ => Loc.T("time ran out"),
        };
        sp.Children.Add(Dim($"{Loc.T(atk ? "Attack" : "Defense")} · {how} · {Loc.F("score before: {0}–{1}", us, them)}", 12.5, 4));
        var enemy = m.Players.FirstOrDefault(p => p.Team != m.MyTeam)?.Team ?? "";
        var buys = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        void Tag(string label, string value, Color c)
        {
            if (string.IsNullOrEmpty(value)) return;
            var s = new StackPanel { Margin = new Thickness(0, 0, 22, 4) };
            s.Children.Add(new TextBlock { Text = Loc.T(value), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(c) });
            s.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 11, Foreground = Res("TextFaintBrush") });
            buys.Children.Add(s);
        }
        Tag("Your team's buy", TeamBuy(m, r, m.MyTeam), YouColor);
        Tag("Their buy", TeamBuy(m, r, enemy), EnemyColor);
        if (r.Planter != null && r.Site != null)
        {
            long plant = r.PlantMs;
            var fake = new VKill { Round = r.N, RoundMs = plant };
            Tag("Spike planted", $"{r.Site} · {Clock(Analysis.RoundClockMs(m, fake))}", Color.FromRgb(0xFA, 0xD6, 0x63));
        }
        sp.Children.Add(buys);
        if (kills.Count > 0)
        {
            var fb = kills[0];
            bool ours = m.TeamOf(fb.Killer) == m.MyTeam;
            sp.Children.Add(Dim(Loc.F("First blood: {0} killed {1}", Agent(m, fb.Killer), Agent(m, fb.Victim)) + " · " + Loc.T(ours ? "your team" : "their team"), 12, 4)
                .Also(t => t.Foreground = new SolidColorBrush(ColorFor(m, fb.Killer))));
        }
        return sp;
    }

    static string Agent(VMatch m, string id) => m.Players.FirstOrDefault(p => p.Id == id)?.Agent is { Length: > 0 } a ? a : "?";

    static string Clock(long ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(@"m\:ss", CultureInfo.InvariantCulture);

    FrameworkElement KillList(VMatch m, List<VKill> kills)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        var prev = Btn("Previous"); prev.Click += (_, _) => { StopRoundPlay(); _rmKill = Math.Max(0, _rmKill - 1); RefreshRound(m, kills); };
        var play = Btn("Play the round", true);
        play.Click += (_, _) =>
        {
            _rmKill = 0;
            RefreshRound(m, kills);
            _rmTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1700) };
            _rmTimer.Tick -= RoundTick;
            _rmTimer.Tick += RoundTick;
            _rmPlay = (m, kills);
            _rmTimer.Start();
        };
        var next = Btn("Next"); next.Click += (_, _) => { StopRoundPlay(); _rmKill = Math.Min(kills.Count - 1, _rmKill + 1); RefreshRound(m, kills); };
        controls.Children.Add(prev); controls.Children.Add(play); controls.Children.Add(next);
        sp.Children.Add(controls);
        if (kills.Count == 0) { sp.Children.Add(Dim(Loc.T("No kills this round."), 12.5, 0)); return sp; }
        _rmRows.Clear();
        for (int i = 0; i < kills.Count; i++)
        {
            var k = kills[i];
            bool ours = m.TeamOf(k.Killer) == m.MyTeam;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = Clock(Analysis.RoundClockMs(m, k)), FontSize = 12, Foreground = Res("TextFaintBrush"), VerticalAlignment = VerticalAlignment.Center });
            var killer = AgentChip(m, k.Killer);
            Grid.SetColumn(killer, 1); g.Children.Add(killer);
            var arrow = new TextBlock { Text = "→", FontSize = 13, Foreground = Res("TextFaintBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(arrow, 2); g.Children.Add(arrow);
            var victim = AgentChip(m, k.Victim);
            Grid.SetColumn(victim, 3); g.Children.Add(victim);
            var w = Analysis.WeaponName(k);
            var weapon = new TextBlock { Text = w.Length > 0 ? Loc.T(w) : "", FontSize = 11.5, Foreground = Res("TextDimBrush"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(weapon, 4); g.Children.Add(weapon);
            var row = new Border
            {
                Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(7), Child = g, Cursor = System.Windows.Input.Cursors.Hand,
                BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = new SolidColorBrush(ColorFor(m, k.Killer)),
            };
            int idx = i;
            row.MouseLeftButtonUp += (_, _) => { StopRoundPlay(); _rmKill = idx; RefreshRound(m, kills); };
            _rmRows.Add(row);
            sp.Children.Add(row);
        }
        PaintRows();
        return sp;
    }

    readonly List<Border> _rmRows = new();
    (VMatch M, List<VKill> K) _rmPlay;

    void PaintRows()
    {
        for (int i = 0; i < _rmRows.Count; i++)
            _rmRows[i].Background = i == _rmKill ? Res("Surface2Brush") : Brushes.Transparent;
    }

    void RoundTick(object sender, EventArgs e)
    {
        var (m, kills) = _rmPlay;
        if (m == null || _tab != "maps" || _rmKill >= kills.Count - 1) { StopRoundPlay(); return; }
        _rmKill++;
        RefreshRound(m, kills);
    }

    void RefreshRound(VMatch m, List<VKill> kills)
    {
        var round = m.Rounds.FirstOrDefault(r => r.N == _rmRound) ?? m.Rounds.First();
        PaintRows();
        ShowKill(m, round, kills);
    }

    FrameworkElement AgentChip(VMatch m, string id)
    {
        bool mate = m.TeamOf(id) == m.MyTeam, me = id == m.Me;
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var name = Agent(m, id);
        if (MatchArt.AgentIcon(name) is { } art)
            sp.Children.Add(new Image { Source = art, Width = 22, Height = 22, Margin = new Thickness(0, 0, 6, 0) }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
        sp.Children.Add(new TextBlock
        {
            Text = me ? Loc.T("You") : name, FontSize = 12.5, FontWeight = me ? FontWeights.Bold : FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ColorFor(m, id)), VerticalAlignment = VerticalAlignment.Center,
        });
        return sp;
    }

    /// <summary>The minimap at one kill: everyone alive, earlier deaths as faded crosses, the killer → victim line.</summary>
    void ShowKill(VMatch m, VRound r, List<VKill> kills)
    {
        if (_rmStage == null) return;
        const double size = 500;
        var grid = new Grid { Width = size, Height = size, ClipToBounds = true };
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = Res("Surface2Brush") });
        if (MapGeo.Minimap(m.Map) is { } img)
            grid.Children.Add(new Image { Source = img, Stretch = Stretch.Uniform, Opacity = 0.85 }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
        var canvas = new Canvas();
        grid.Children.Add(canvas);
        Point? At(double x, double y) => MapGeo.ToMinimap(m.Map, x, y) is Point p ? new Point(p.X * size, p.Y * size) : null;

        var head = new StackPanel();
        if (kills.Count == 0)
        {
            head.Children.Add(grid);
            _rmStage.Content = head;
            return;
        }
        var k = kills[_rmKill];

        // Earlier deaths this round.
        foreach (var d in kills.Take(_rmKill))
            if (At(d.VX, d.VY) is Point p) Cross(canvas, p, ColorFor(m, d.Victim), 0.45, Agent(m, d.Victim));

        // Heat: everywhere each side has been at the kills so far this round, newest brightest.
        for (int j = 0; j <= _rmKill; j++)
        {
            double age = _rmKill == 0 ? 1 : 0.35 + 0.65 * j / _rmKill;
            foreach (var p in kills[j].Pos)
                if (At(p.X, p.Y) is Point hp) Glow(canvas, hp, ColorFor(m, p.Id), age, p.Id == m.Me ? 34 : 28);
        }

        // Killer → victim.
        var from = k.At(k.Killer) is Pt kp ? At(kp.X, kp.Y) : null;
        var to = At(k.VX, k.VY);
        if (from is Point a && to is Point b)
            canvas.Children.Add(new Line
            {
                X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, StrokeThickness = 2.5, StrokeDashArray = new DoubleCollection { 3, 2 },
                Stroke = new SolidColorBrush(ColorFor(m, k.Killer)),
            });

        // Everyone alive at that moment.
        foreach (var p in k.Pos)
        {
            if (p.Id == k.Victim || At(p.X, p.Y) is not Point pt) continue;
            Marker(canvas, pt, Agent(m, p.Id), ColorFor(m, p.Id), p.Id == m.Me, p.Id == k.Killer);
        }
        if (to is Point v) Cross(canvas, v, ColorFor(m, k.Victim), 1, Agent(m, k.Victim));

        head.Children.Add(grid);
        var legend = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        void Key(Color c, string t)
        {
            legend.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(c), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            legend.Children.Add(new TextBlock { Text = Loc.T(t), FontSize = 11.5, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 0, 14, 0) });
        }
        Key(YouColor, "You");
        Key(MateColor, "Teammates");
        Key(EnemyColor, "Enemies");
        legend.Children.Add(new TextBlock { Text = Loc.T("Glow: where each side has been so far this round"), FontSize = 11.5, Foreground = Res("TextFaintBrush") });
        head.Children.Add(legend);
        bool ours = m.TeamOf(k.Killer) == m.MyTeam;
        var w = Analysis.WeaponName(k);
        head.Children.Add(new TextBlock
        {
            Text = $"{Clock(Analysis.RoundClockMs(m, k))}  ·  " + (k.Victim == m.Me ? Loc.F("{0} killed you", Agent(m, k.Killer)) : k.Killer == m.Me ? Loc.F("You killed {0}", Agent(m, k.Victim)) : Loc.F("{0} killed {1}", Agent(m, k.Killer), Agent(m, k.Victim)))
                   + (w.Length > 0 ? " · " + Loc.T(w) : "") + $"  ·  {_rmKill + 1}/{kills.Count}",
            FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0),
            Foreground = new SolidColorBrush(ColorFor(m, k.Killer)),
        });
        var callA = k.At(k.Killer) is Pt kc ? MapGeo.CalloutAt(m.Map, kc.X, kc.Y) : "";
        var callB = MapGeo.CalloutAt(m.Map, k.VX, k.VY);
        if (callA.Length > 0 || callB.Length > 0)
            head.Children.Add(Faint(Loc.F("From {0} to {1}", callA.Length > 0 ? callA : "?", callB.Length > 0 ? callB : "?"), 11.5, 2));
        int alive(string team) => k.Pos.Count(p => p.Id != k.Victim && m.TeamOf(p.Id) == team);
        var enemy = m.Players.FirstOrDefault(p => p.Team != m.MyTeam)?.Team ?? "";
        head.Children.Add(Faint(Loc.F("Alive after this kill: {0} vs {1}", alive(m.MyTeam), alive(enemy)), 11.5, 2));
        _rmStage.Content = head;
    }

    /// <summary>A player on the map: their agent's face in a ring: green for you, yellow for teammates, red for enemies.</summary>
    static void Marker(Canvas c, Point p, string agent, Color team, bool me, bool killer)
    {
        double r = me ? 14 : 12;
        Brush fill = MatchArt.AgentIcon(agent) is { } art ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(team);
        var dot = new Ellipse
        {
            Width = r * 2, Height = r * 2, Fill = fill, StrokeThickness = me ? 3.5 : killer ? 3 : 2,
            Stroke = new SolidColorBrush(team), ToolTip = agent,
        };
        Canvas.SetLeft(dot, p.X - r); Canvas.SetTop(dot, p.Y - r);
        c.Children.Add(dot);
    }

    /// <summary>A soft colored glow; overlapping glows add up, so where a side spent time shows brighter.</summary>
    static void Glow(Canvas c, Point p, Color col, double strength, double radius)
    {
        var brush = new RadialGradientBrush(Color.FromArgb((byte)(110 * strength), col.R, col.G, col.B), Color.FromArgb(0, col.R, col.G, col.B));
        brush.Freeze();
        var e = new Ellipse { Width = radius * 2, Height = radius * 2, Fill = brush, IsHitTestVisible = false };
        Canvas.SetLeft(e, p.X - radius); Canvas.SetTop(e, p.Y - radius);
        c.Children.Add(e);
    }

    static void Cross(Canvas c, Point p, Color team, double opacity, string tip)
    {
        var b = new SolidColorBrush(team);
        const double s = 7;
        var g = new Canvas { Opacity = opacity, ToolTip = tip, Width = 1, Height = 1 };
        g.Children.Add(new Line { X1 = p.X - s, Y1 = p.Y - s, X2 = p.X + s, Y2 = p.Y + s, Stroke = b, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        g.Children.Add(new Line { X1 = p.X - s, Y1 = p.Y + s, X2 = p.X + s, Y2 = p.Y - s, Stroke = b, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        c.Children.Add(g);
    }
}
