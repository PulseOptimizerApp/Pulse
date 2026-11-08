using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Pulse.Core.Games;
using Pulse.Core.Games.Valorant;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// Heatmaps on each map's minimap: where you die, where you get your kills, and where enemies stood when they killed
/// your team, by side and part of the round. The hot spots are named with the game's own callouts.
/// </summary>
public partial class ValorantPage
{
    string _mapName, _mapLayer = "deaths", _mapSide = "all", _mapPhase = "all";

    sealed record Dot(double X, double Y, string Callout, string Tip);

    void BuildMaps()
    {
        var matches = CoachMatches();
        if (matches.Count == 0)
        {
            Root.Children.Add(CoachHeader(matches));
            return;
        }
        var maps = matches.GroupBy(m => m.Map).OrderByDescending(g => g.Count()).Select(g => g.Key).Where(m => m.Length > 0).ToList();
        if (_mapName == null || !maps.Contains(_mapName)) _mapName = maps.FirstOrDefault();

        var pick = new StackPanel();
        var mapRow = new WrapPanel();
        foreach (var m in maps)
        {
            var name = m;
            mapRow.Children.Add(Chip($"{m}  {matches.Count(x => x.Map == m)}", "vmap", m == _mapName, () => { _mapName = name; Build(); }));
        }
        pick.Children.Add(Label("Map"));
        pick.Children.Add(mapRow);
        var layerRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (key, title) in new[] { ("deaths", "Where you die"), ("kills", "Where you get kills"), ("enemies", "Where enemies kill your team from") })
        {
            var k = key;
            layerRow.Children.Add(Chip(Loc.T(title), "vlayer", _mapLayer == key, () => { _mapLayer = k; Build(); }));
        }
        pick.Children.Add(layerRow);
        var sideRow = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var (key, title) in new[] { ("all", "Both sides"), ("attack", "Attack"), ("defense", "Defense") })
        {
            var k = key;
            sideRow.Children.Add(Chip(Loc.T(title), "vside", _mapSide == key, () => { _mapSide = k; Build(); }));
        }
        if (_mapLayer == "enemies")
        {
            sideRow.Children.Add(new Border { Width = 14 });
            foreach (var (key, title) in new[] { ("all", "Whole round"), ("Early", "First 25 s"), ("Mid", "25–60 s"), ("Late", "After 60 s"), ("Post-plant", "After the plant") })
            {
                var k = key;
                sideRow.Children.Add(Chip(Loc.T(title), "vphase", _mapPhase == key, () => { _mapPhase = k; Build(); }));
            }
        }
        pick.Children.Add(sideRow);
        Root.Children.Add(Card(pick));

        var onMap = matches.Where(m => m.Map == _mapName).ToList();
        var dots = Dots(onMap);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(Heatmap(_mapName, dots, 460));
        var side = new StackPanel { Margin = new Thickness(24, 0, 0, 0) };
        Grid.SetColumn(side, 1);
        side.Children.Add(Heading(_mapLayer switch { "kills" => "Where you get kills", "enemies" => "Where enemies kill your team from", _ => "Where you die" }));
        side.Children.Add(Dim(_mapLayer switch
        {
            "kills" => Loc.T("Where you were standing for each of your kills."),
            "enemies" => Loc.T("Where the enemy stood each time they killed you or a teammate. These are the spots to clear, smoke or flash before you walk into them."),
            _ => Loc.T("Where you were standing each time you died."),
        }, 12.5, 4));
        side.Children.Add(Faint(Loc.F("{0} on {1} in {2} matches", dots.Count, _mapName, onMap.Count), 11.5, 6));
        var hot = dots.Where(d => d.Callout.Length > 0).GroupBy(d => d.Callout).OrderByDescending(g => g.Count()).Take(6).ToList();
        if (hot.Count > 0)
        {
            side.Children.Add(Label("Hot spots").Also(l => l.Margin = new Thickness(0, 16, 0, 6)));
            int max = hot[0].Count();
            foreach (var h in hot)
            {
                var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                g.Children.Add(new TextBlock { Text = h.Key, FontSize = 12.5, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
                var bar = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = Res(_mapLayer == "kills" ? "GoodBrush" : "WarnBrush"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Width = Math.Max(6, 120.0 * h.Count() / max) };
                Grid.SetColumn(bar, 1);
                g.Children.Add(bar);
                var n = new TextBlock { Text = h.Count().ToString(CultureInfo.CurrentCulture), FontSize = 12, Foreground = Res("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(n, 2);
                g.Children.Add(n);
                side.Children.Add(g);
            }
            if (_mapLayer == "deaths")
                side.Children.Add(Dim(Loc.F("You die most at {0}. Next time you're there, go in with a teammate or use utility first, or try a different spot.", hot[0].Key), 12, 12));
            else if (_mapLayer == "enemies")
                side.Children.Add(Dim(Loc.F("Enemies get the most kills on your team from {0}. Check it first, or block it with a smoke or flash.", hot[0].Key), 12, 12));
        }
        // How you do on this map.
        var me = Analysis.Mine(onMap);
        side.Children.Add(Label("You on this map").Also(l => l.Margin = new Thickness(0, 18, 0, 6)));
        side.Children.Add(Dim(Loc.F("{0} matches · {1:0}% won · {2:0.00} kills per round · {3:0} damage per round", me.Matches, me.WinPct, me.KillsPerRound, me.Adr), 12.5, 0));
        body.Children.Add(side);
        Root.Children.Add(Card(body));
        if (!MapGeo.HasMap(_mapName))
            Root.Children.Add(Card(Faint(Loc.T("This map's layout isn't in this version of Pulse yet, so the dots can't be placed. They will be after the next update."), 12, 0)));
    }

    List<Dot> Dots(List<VMatch> onMap)
    {
        var list = new List<Dot>();
        foreach (var m in onMap)
        {
            var team = m.MyTeam;
            bool SideOk(VKill k) => _mapSide == "all" || (m.Attacking(team, k.Round) == (_mapSide == "attack"));
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
        var hot = _mapLayer == "kills" ? Color.FromRgb(0x3D, 0xDC, 0x84) : Color.FromRgb(0xFF, 0x5A, 0x4E);
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
            var c = new Ellipse { Width = 5, Height = 5, Fill = core, ToolTip = d.Callout.Length > 0 ? $"{d.Callout} · {d.Tip}" : d.Tip };
            Canvas.SetLeft(c, p.X * size - 2.5); Canvas.SetTop(c, p.Y * size - 2.5);
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
}
