using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pulse.Core.Games;
using Pulse.Core.Games.Valorant;
using Pulse.Ui;
using Ellipse = System.Windows.Shapes.Ellipse;
using Line = System.Windows.Shapes.Line;

namespace Pulse.Pages;

/// <summary>
/// Lineups, map first: pick an agent and a map, and every lineup shows on the real minimap as a line from where you
/// stand to where it lands. Click one for its steps, picture and video. Pulse's library (written up by guide sites,
/// credited on each) and your own, with favorites and ratings.
/// </summary>
public partial class ValorantPage
{
    static string _luMap = "", _luAgent = "", _luSide = "", _luSel = "";
    static bool _luFav;
    static Lineup _luEdit;
    static bool _luMine; // false: Pulse's library, true: your own

    static readonly Color LuAttack = Color.FromRgb(0xFF, 0x54, 0x68), LuDefense = Color.FromRgb(0x2D, 0xD4, 0xA7);
    static Color SideColor(Lineup l) => l.Side == "Defense" ? LuDefense : LuAttack;

    void BuildLineups()
    {
        if (_luEdit != null) { Root.Children.Add(LineupEditor(_luEdit)); return; }
        IReadOnlyList<Lineup> source = _luMine ? Lineups.All : Lineups.Library;

        var head = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(Heading("Lineups"));
        var add = Btn("Add a lineup", true);
        add.Margin = new Thickness(0);
        add.Click += (_, _) => { _luEdit = new Lineup { Map = _luMap, Agent = _luAgent, Side = _luSide.Length > 0 ? _luSide : "Attack" }; Build(); };
        Grid.SetColumn(add, 1); top.Children.Add(add);
        head.Children.Add(top);
        head.Children.Add(Dim(Loc.T("Pick an agent and a map. Each line runs from where you stand to where it lands: click one for the steps."), 12.5, 4));
        var scope = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        scope.Children.Add(Chip(Loc.F("Lineup library ({0})", Lineups.Library.Count), "luscope", !_luMine, () => { _luMine = false; _luSel = ""; Build(); }));
        scope.Children.Add(Chip(Loc.F("My lineups ({0})", Lineups.All.Count), "luscope", _luMine, () => { _luMine = true; _luSel = ""; Build(); }));
        head.Children.Add(scope);

        if (_luMine && source.Count == 0)
        {
            Root.Children.Add(Card(head));
            Root.Children.Add(Card(Dim(Loc.T("No lineups of your own yet. Save one from the library, or add your own: pick the map, click where you stand and where it lands, and write the steps."))));
            return;
        }

        // Agents: the ones with lineups first; in the library, the other lineup agents too (they open videos).
        var counts = source.GroupBy(l => l.Agent).ToDictionary(g => g.Key, g => g.Count());
        var agents = counts.Keys.OrderByDescending(a => counts[a]).ThenBy(a => a).ToList();
        if (!_luMine) agents.AddRange(Lineups.LineupAgents.Where(a => !counts.ContainsKey(a)).OrderBy(a => a));
        if (!agents.Contains(_luAgent)) { _luAgent = agents.FirstOrDefault() ?? "Sova"; _luMap = ""; }
        var agentRow = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var a in agents)
        {
            var name = a;
            counts.TryGetValue(a, out var n);
            agentRow.Children.Add(LuPill(MatchArt.AgentIcon(a), a, n, a == _luAgent, () => { _luAgent = name; _luMap = ""; _luSel = ""; Build(); }));
        }
        head.Children.Add(agentRow);

        // Maps for this agent, busiest first; maps without any still show (they open videos).
        var mine = source.Where(l => l.Agent == _luAgent).ToList();
        var perMap = mine.GroupBy(l => l.Map).ToDictionary(g => g.Key, g => g.Count());
        var maps = Lineups.Maps.OrderByDescending(m => perMap.GetValueOrDefault(m)).ThenBy(m => m).ToList();
        if (_luMap.Length == 0 || !maps.Contains(_luMap)) _luMap = maps.FirstOrDefault(m => perMap.ContainsKey(m)) ?? maps[0];
        var mapRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var m in maps)
        {
            var name = m;
            mapRow.Children.Add(LuPill(null, m, perMap.GetValueOrDefault(m), m == _luMap, () => { _luMap = name; _luSel = ""; Build(); }));
        }
        head.Children.Add(mapRow);

        var sideRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        sideRow.Children.Add(Chip(Loc.T("Both sides"), "luside", _luSide == "", () => { _luSide = ""; Build(); }));
        sideRow.Children.Add(Chip(Loc.T("Attack"), "luside", _luSide == "Attack", () => { _luSide = "Attack"; Build(); }));
        sideRow.Children.Add(Chip(Loc.T("Defense"), "luside", _luSide == "Defense", () => { _luSide = "Defense"; Build(); }));
        if (_luMine)
        {
            var fav = new CheckBox { Content = Loc.T("Favorites only"), IsChecked = _luFav, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 6), Foreground = Res("TextBrush") };
            fav.Click += (_, _) => { _luFav = fav.IsChecked == true; Build(); };
            sideRow.Children.Add(fav);
        }
        head.Children.Add(sideRow);
        Root.Children.Add(Card(head));

        var list = Lineups.Find(_luMap, _luAgent, "", _luSide, 0, _luFav && _luMine, "", !_luMine)
            .OrderBy(l => Lineups.Sites.ToList().IndexOf(l.Site)).ThenBy(l => l.Side).ThenBy(l => l.Title).ToList();
        if (list.Count == 0)
        {
            var none = new StackPanel();
            none.Children.Add(Dim(_luSide.Length > 0 && mine.Any(l => l.Map == _luMap)
                ? Loc.F("No {0} lineups for {1} on {2} here. Try both sides.", Loc.T(_luSide).ToLowerInvariant(), _luAgent, _luMap)
                : Loc.F("No written lineups for {0} on {1} in Pulse yet.", _luAgent, _luMap), 12.5, 0));
            var watch = Btn(Loc.F("Watch {0} lineups on {1}", _luAgent, _luMap), true);
            watch.HorizontalAlignment = HorizontalAlignment.Left;
            watch.Margin = new Thickness(0, 12, 0, 0);
            var url = Lineups.VideoSearch(_luAgent, _luMap);
            watch.Click += (_, _) => Open(url);
            none.Children.Add(watch);
            Root.Children.Add(Card(none));
            return;
        }
        var sel = list.FirstOrDefault(l => l.Id == _luSel) ?? list[0];
        _luSel = sel.Id;

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        var mapCard = Card(LineupMap(_luMap, list, sel, 540, l => { _luSel = l.Id; Build(); }));
        mapCard.Margin = new Thickness(0, 0, 14, 14);
        mapCard.VerticalAlignment = VerticalAlignment.Top;
        body.Children.Add(mapCard);
        var right = new StackPanel();
        right.Children.Add(Card(LineupDetail(sel, list.IndexOf(sel) + 1)));
        right.Children.Add(Card(LineupList(list, sel)));
        Grid.SetColumn(right, 1);
        body.Children.Add(right);
        Root.Children.Add(body);
    }

    static void Open(string url) { try { Process.Start(new ProcessStartInfo(url.Trim()) { UseShellExecute = true }); } catch { } }

    /// <summary>A pill to pick an agent or map, with its count (dimmed when there are none).</summary>
    FrameworkElement LuPill(ImageSource icon, string text, int count, bool on, Action pick)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null) sp.Children.Add(new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), ClipToBounds = true, Margin = new Thickness(-4, 0, 7, 0), Background = new ImageBrush(icon) { Stretch = Stretch.UniformToFill } });
        sp.Children.Add(new TextBlock { Text = text, FontSize = 12.5, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, Foreground = on ? Res("TextBrush") : count > 0 ? Res("TextBrush") : Res("TextFaintBrush") });
        if (count > 0) sp.Children.Add(new TextBlock { Text = count.ToString(CultureInfo.InvariantCulture), FontSize = 11, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextDimBrush") });
        var b = new Border
        {
            Child = sp, CornerRadius = new CornerRadius(999), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand,
            Background = on ? Res("Surface2Brush") : Brushes.Transparent, BorderBrush = on ? Res("AccentBrush") : Res("BorderBrush"), BorderThickness = new Thickness(1),
            ToolTip = count > 0 ? null : Loc.T("No written lineups yet: opens videos"),
        };
        b.MouseEnter += (_, _) => { if (!on) b.Background = Res("SurfaceHoverBrush"); };
        b.MouseLeave += (_, _) => { if (!on) b.Background = Brushes.Transparent; };
        b.MouseLeftButtonUp += (_, _) => pick();
        return b;
    }

    /// <summary>
    /// The minimap with every lineup as a line from where you stand (ring) to where it lands (numbered dot): red for
    /// attack, teal for defense; the picked one bright, the rest faint. Lineups sharing a spot fan out around it.
    /// </summary>
    FrameworkElement LineupMap(string map, List<Lineup> list, Lineup sel, double size, Action<Lineup> pick)
    {
        var grid = new Grid { Width = size, Height = size, ClipToBounds = true };
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = Res("Surface2Brush") });
        var mini = MapGeo.Minimap(map);
        if (mini != null)
            grid.Children.Add(new Image { Source = mini, Stretch = Stretch.Uniform, Opacity = 0.85 }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
        var canvas = new Canvas();
        grid.Children.Add(canvas);
        if (mini == null)
        {
            grid.Children.Add(new TextBlock { Text = Loc.T("This map's picture isn't in this build yet: the list has every lineup."), Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center });
            return grid;
        }
        foreach (var (letter, at) in MapGeo.Sites(map))
        {
            var t = new TextBlock { Text = letter, FontSize = 30, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), FontFamily = (FontFamily)FindResource("DisplayFont"), IsHitTestVisible = false };
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(t, at.X * size - t.DesiredSize.Width / 2); Canvas.SetTop(t, at.Y * size - t.DesiredSize.Height / 2);
            canvas.Children.Add(t);
        }

        // Fan out lineups that share a spot, so each stays clickable.
        var seen = new Dictionary<string, int>();
        Point Fan(Point p)
        {
            var key = $"{Math.Round(p.X, 3)}:{Math.Round(p.Y, 3)}";
            int k = seen.GetValueOrDefault(key);
            seen[key] = k + 1;
            if (k == 0) return p;
            double ang = k * 2.39996, r = 0.024 * Math.Sqrt(k);
            return new Point(p.X + Math.Cos(ang) * r, p.Y + Math.Sin(ang) * r);
        }
        var placed = new List<(Lineup L, int N, Point? S, Point? D)>();
        for (int i = 0; i < list.Count; i++)
        {
            var (s, d) = Lineups.Spots(list[i]);
            placed.Add((list[i], i + 1, s is Point sp ? Fan(sp) : null, d is Point dp ? Fan(dp) : null));
        }
        // The picked one last, so it's drawn on top.
        foreach (var (l, n, s, d) in placed.OrderBy(x => x.L == sel))
        {
            bool on = l == sel;
            var col = SideColor(l);
            var brush = new SolidColorBrush(col);
            var g = new Canvas { Opacity = on ? 1 : 0.55, Cursor = Cursors.Hand, ToolTip = $"{n}. {l.Title}" };
            if (s is Point a && d is Point b)
            {
                var A = new Point(a.X * size, a.Y * size); var B = new Point(b.X * size, b.Y * size);
                if (on) g.Children.Add(new Line { X1 = A.X, Y1 = A.Y, X2 = B.X, Y2 = B.Y, Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), StrokeThickness = 7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                g.Children.Add(new Line { X1 = A.X, Y1 = A.Y, X2 = B.X, Y2 = B.Y, Stroke = brush, StrokeThickness = on ? 3 : 2, StrokeDashArray = on ? null : new DoubleCollection { 3, 2 }, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                // Where you stand: a ring.
                var ring = new Ellipse { Width = on ? 16 : 12, Height = on ? 16 : 12, Stroke = brush, StrokeThickness = 2.5, Fill = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x18)) };
                Canvas.SetLeft(ring, A.X - ring.Width / 2); Canvas.SetTop(ring, A.Y - ring.Height / 2);
                g.Children.Add(ring);
            }
            if (d is Point land)
            {
                double r = on ? 13 : 10;
                var dot = new Grid { Width = r * 2, Height = r * 2 };
                dot.Children.Add(new Ellipse { Fill = brush, Stroke = on ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x18)), StrokeThickness = on ? 2 : 1.5 });
                dot.Children.Add(new TextBlock { Text = n.ToString(CultureInfo.InvariantCulture), FontSize = on ? 12.5 : 10.5, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x16)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                Canvas.SetLeft(dot, land.X * size - r); Canvas.SetTop(dot, land.Y * size - r);
                g.Children.Add(dot);
            }
            var pickMe = l;
            g.MouseLeftButtonUp += (_, e) => { e.Handled = true; pick(pickMe); };
            canvas.Children.Add(g);
        }

        var legend = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 0, 10) };
        void AddKey(Color c, string t, bool ring)
        {
            legend.Children.Add(new Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, Stroke = new SolidColorBrush(c), StrokeThickness = ring ? 2 : 0, Fill = ring ? Brushes.Transparent : new SolidColorBrush(c) });
            legend.Children.Add(new TextBlock { Text = Loc.T(t), FontSize = 11, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center });
        }
        AddKey(Color.FromRgb(0xC8, 0xCC, 0xD4), "Stand here", true);
        AddKey(Color.FromRgb(0xC8, 0xCC, 0xD4), "Lands here", false);
        AddKey(LuAttack, "Attack", false);
        AddKey(LuDefense, "Defense", false);
        grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x12, 0x16)), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 0, 6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10), Child = legend.Also(k => k.Margin = new Thickness(0)) });
        return grid;
    }

    /// <summary>The picked lineup: what it is, where to stand and aim, its picture, and what you can do with it.</summary>
    FrameworkElement LineupDetail(Lineup l, int n)
    {
        var sp = new StackPanel();
        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.ColumnDefinitions.Add(new ColumnDefinition());
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(SideColor(l)), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = n.ToString(CultureInfo.InvariantCulture), FontWeight = FontWeights.Bold, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x16)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        title.Children.Add(badge);
        var name = new TextBlock { Text = l.Title.Length > 0 ? l.Title : Loc.T("Untitled lineup"), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), TextWrapping = TextWrapping.Wrap, FontFamily = (FontFamily)FindResource("DisplayFont"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1); title.Children.Add(name);
        if (!l.BuiltIn)
        {
            var heart = new TextBlock { Text = l.Favorite ? "♥" : "♡", FontSize = 18, Cursor = Cursors.Hand, Margin = new Thickness(10, 0, 0, 0), Foreground = l.Favorite ? new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x6E)) : Res("TextDimBrush"), ToolTip = Loc.T(l.Favorite ? "Remove from favorites" : "Add to favorites") };
            heart.MouseLeftButtonUp += (_, _) => { l.Favorite = !l.Favorite; Lineups.Save(l); Build(); };
            Grid.SetColumn(heart, 2); title.Children.Add(heart);
        }
        sp.Children.Add(title);

        var tags = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        void Tag(string t, Brush fg = null) { if (!string.IsNullOrEmpty(t)) tags.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = Res("Surface2Brush"), Padding = new Thickness(8, 3, 8, 4), Margin = new Thickness(0, 0, 6, 6), Child = new TextBlock { Text = t, FontSize = 11.5, Foreground = fg ?? Res("TextBrush") } }); }
        Tag(l.Ability);
        Tag(Loc.T(l.Side), new SolidColorBrush(SideColor(l)));
        Tag(l.Site == "Mid" ? Loc.T("Mid") : Loc.F("{0} site", l.Site));
        Tag(DiffName(l.Difficulty.ToString(CultureInfo.InvariantCulture)));
        sp.Children.Add(tags);
        var from = l.Stand.Length > 0 ? l.Stand : l.StandX >= 0 ? MapGeo.CalloutNear(l.Map, new Point(l.StandX, l.StandY)) : "";
        var to = l.Land.Length > 0 ? l.Land : l.LandX >= 0 ? MapGeo.CalloutNear(l.Map, new Point(l.LandX, l.LandY)) : "";
        if (from.Length > 0 && to.Length > 0)
            sp.Children.Add(new TextBlock { Text = Loc.F("From {0} to {1}", from, to), FontSize = 12.5, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 2, 0, 0) });

        if (LineupImage(l.Image, 760) is { } pic)
        {
            var img = new Border { Height = 200, CornerRadius = new CornerRadius(8), ClipToBounds = true, Margin = new Thickness(0, 12, 0, 0), Cursor = Cursors.Hand, ToolTip = Loc.T("Open the picture"), Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill } };
            img.MouseLeftButtonUp += (_, _) => Open(Path.Combine(Lineups.ImageDir, l.Image));
            sp.Children.Add(img);
        }

        // Steps: "Stand:", "Aim:" and the like as a label column, so they're quick to read mid-round.
        var steps = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        steps.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        steps.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < l.Steps.Count; i++)
        {
            steps.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = l.Steps[i];
            int colon = text.IndexOf(':');
            string label = colon > 0 && colon <= 12 ? text[..colon] : (i + 1).ToString(CultureInfo.InvariantCulture);
            string rest = colon > 0 && colon <= 12 ? text[(colon + 1)..].Trim() : text;
            var lt = new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(SideColor(l)), Margin = new Thickness(0, 4, 12, 4) };
            var rt = new TextBlock { Text = rest, FontSize = 13, Foreground = Res("TextBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4), LineHeight = 19 };
            Grid.SetRow(lt, i); Grid.SetRow(rt, i); Grid.SetColumn(rt, 1);
            steps.Children.Add(lt); steps.Children.Add(rt);
        }
        sp.Children.Add(steps);

        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        if (Lineups.SafeLink(l.Video))
        {
            var vid = Btn("Watch it", true);
            vid.ToolTip = l.Video;
            vid.Click += (_, _) => Open(l.Video);
            actions.Children.Add(vid);
        }
        if (l.BuiltIn)
        {
            var save = Btn("Save to mine");
            save.Click += (_, _) =>
            {
                var copy = Clone(l);
                copy.Id = Guid.NewGuid().ToString("N");
                copy.AddedUtc = DateTime.UtcNow;
                copy.Favorite = true;
                Lineups.Save(copy);
                MainWindow.Instance.Toast(Loc.T("Saved to My lineups"));
            };
            actions.Children.Add(save);
            if (Lineups.SafeLink(l.Source))
            {
                var src = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.F("Written up by {0}", l.SourceName.Length > 0 ? l.SourceName : Loc.T("a guide site")), VerticalAlignment = VerticalAlignment.Center, ToolTip = l.Source };
                src.Click += (_, _) => Open(l.Source);
                actions.Children.Add(src);
            }
        }
        else
        {
            var edit = Btn("Edit");
            edit.Click += (_, _) => { _luEdit = Clone(l); Build(); };
            actions.Children.Add(edit);
            var del = Btn("Delete");
            bool armed = false;
            del.Click += (_, _) =>
            {
                if (!armed) { armed = true; del.Content = Loc.T("Click again to delete"); return; }
                Lineups.Delete(l.Id); _luSel = ""; Build();
            };
            actions.Children.Add(del);
            var stars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            for (int i = 1; i <= 5; i++)
            {
                int v = i;
                var st = new TextBlock { Text = v <= l.Rating ? "★" : "☆", FontSize = 15, Padding = new Thickness(1, 0, 1, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent,
                    Foreground = v <= l.Rating ? new SolidColorBrush(Color.FromRgb(0xF2, 0xC2, 0x4B)) : Res("TextFaintBrush"), ToolTip = Loc.F("Rate {0} of 5", v) };
                st.MouseLeftButtonUp += (_, _) => { l.Rating = v == l.Rating ? 0 : v; Lineups.Save(l); Build(); };
                stars.Children.Add(st);
            }
            actions.Children.Add(stars);
        }
        sp.Children.Add(actions);
        return sp;
    }

    /// <summary>Every lineup on this map, numbered like the map; click one to pick it.</summary>
    FrameworkElement LineupList(List<Lineup> list, Lineup sel)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = Loc.F("{0} lineups on {1}", list.Count, _luMap), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 0, 0, 8) });
        for (int i = 0; i < list.Count; i++)
        {
            var l = list[i];
            bool on = l == sel;
            var row = new Grid { Background = on ? Res("Surface2Brush") : Brushes.Transparent, Cursor = Cursors.Hand };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var num = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(SideColor(l)), Margin = new Thickness(8, 7, 10, 7), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = (i + 1).ToString(CultureInfo.InvariantCulture), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x16)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            row.Children.Add(num);
            var txt = new StackPanel { Margin = new Thickness(0, 6, 8, 6) };
            txt.Children.Add(new TextBlock { Text = l.Title, FontSize = 12.5, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            txt.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { l.Ability, Loc.T(l.Side), DiffName(l.Difficulty.ToString(CultureInfo.InvariantCulture)) }.Where(x => !string.IsNullOrEmpty(x))) + (l.Favorite && !l.BuiltIn ? "  ♥" : ""), FontSize = 11, Foreground = Res("TextDimBrush") });
            Grid.SetColumn(txt, 1); row.Children.Add(txt);
            var border = new Border { CornerRadius = new CornerRadius(8), Child = row, ClipToBounds = true };
            var pick = l;
            border.MouseEnter += (_, _) => { if (!on) row.Background = Res("SurfaceHoverBrush"); };
            border.MouseLeave += (_, _) => { if (!on) row.Background = Brushes.Transparent; };
            border.MouseLeftButtonUp += (_, _) => { _luSel = pick.Id; Build(); };
            sp.Children.Add(border);
        }
        var more = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.F("More {0} lineups on {1} on YouTube", _luAgent, _luMap), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 10, 0, 0) };
        var url = Lineups.VideoSearch(_luAgent, _luMap);
        more.Click += (_, _) => Open(url);
        sp.Children.Add(more);
        return sp;
    }

    static string DiffName(string d) => d switch { "1" => Loc.T("Easy"), "2" => Loc.T("Medium"), "3" => Loc.T("Hard"), _ => d };

    static ImageSource LineupImage(string file, int width)
    {
        if (string.IsNullOrEmpty(file)) return null;
        var path = Path.Combine(Lineups.ImageDir, file);
        if (!File.Exists(path)) return null;
        try
        {
            var b = new BitmapImage();
            b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.DecodePixelWidth = width; b.UriSource = new Uri(path); b.EndInit(); b.Freeze();
            return b;
        }
        catch { return null; }
    }

    static Lineup Clone(Lineup l) => System.Text.Json.JsonSerializer.Deserialize<Lineup>(System.Text.Json.JsonSerializer.Serialize(l));

    FrameworkElement LineupEditor(Lineup l)
    {
        bool isNew = !Lineups.All.Any(x => x.Id == l.Id);
        var sp = new StackPanel();
        sp.Children.Add(Heading(isNew ? "Add a lineup" : "Edit lineup"));
        var form = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        form.ColumnDefinitions.Add(new ColumnDefinition());
        int row = 0;
        void Row(string label, UIElement el)
        {
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var t = new TextBlock { Text = Loc.T(label), FontSize = 12.5, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 10, 6) };
            Grid.SetRow(t, row); form.Children.Add(t);
            if (el is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetRow(el, row); Grid.SetColumn(el, 1); form.Children.Add(el);
            row++;
        }
        TextBox Box(string value, string hint, bool multi = false) => new()
        {
            Style = (Style)FindResource("SearchBox"), Text = value, Tag = Loc.T(hint),
            AcceptsReturn = multi, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multi ? 110 : 0, VerticalContentAlignment = multi ? VerticalAlignment.Top : VerticalAlignment.Center,
        };
        ComboBox Combo(IEnumerable<string> items, string sel, Func<string, string> label = null)
        {
            var b = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var i in items) b.Items.Add(new ComboBoxItem { Content = label?.Invoke(i) ?? i, Tag = i });
            b.SelectedIndex = Math.Max(0, b.Items.Cast<ComboBoxItem>().ToList().FindIndex(x => (string)x.Tag == sel));
            return b;
        }
        string Sel(ComboBox b) => (b.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var title = Box(l.Title, "e.g. Recon dart for A main"); Row("Title", title);
        var map = Combo(Lineups.Maps, l.Map.Length > 0 ? l.Map : Lineups.Maps[0]); Row("Map", map);
        var agent = Combo(Lineups.Agents.OrderBy(a => a), l.Agent.Length > 0 ? l.Agent : "Sova"); Row("Agent", agent);
        var ability = Box(l.Ability, "e.g. Recon Bolt, Molly, Smoke"); Row("Ability", ability);
        var site = Combo(Lineups.Sites, l.Site, s => s == "Mid" ? Loc.T("Mid") : Loc.F("{0} site", s)); Row("Site", site);
        var side = Combo(new[] { "Attack", "Defense" }, l.Side, Loc.T); Row("Side", side);
        // Where it goes: click where you stand, then where it lands.
        var place = new StackPanel();
        var picker = new Grid { Width = 380, Height = 380, HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Cross, Background = Brushes.Transparent };
        var placeHint = new TextBlock { FontSize = 12, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        void DrawPicker()
        {
            picker.Children.Clear();
            picker.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = Res("Surface2Brush") });
            var mapName = Sel(map);
            if (MapGeo.Minimap(mapName) is { } mini) picker.Children.Add(new Image { Source = mini, Stretch = Stretch.Uniform, Opacity = 0.85 });
            var c = new Canvas();
            picker.Children.Add(c);
            var col = new SolidColorBrush(Sel(side) == "Defense" ? LuDefense : LuAttack);
            Point? st = l.StandX >= 0 ? new Point(l.StandX * 380, l.StandY * 380) : null, ld = l.LandX >= 0 ? new Point(l.LandX * 380, l.LandY * 380) : null;
            if (st is Point a && ld is Point b) c.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = col, StrokeThickness = 3 });
            if (st is Point s0) { var e = new Ellipse { Width = 16, Height = 16, Stroke = col, StrokeThickness = 2.5, Fill = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x18)) }; Canvas.SetLeft(e, s0.X - 8); Canvas.SetTop(e, s0.Y - 8); c.Children.Add(e); }
            if (ld is Point d0) { var e = new Ellipse { Width = 22, Height = 22, Fill = col, Stroke = Brushes.White, StrokeThickness = 2 }; Canvas.SetLeft(e, d0.X - 11); Canvas.SetTop(e, d0.Y - 11); c.Children.Add(e); }
            placeHint.Text = st == null ? Loc.T("Click where you stand to throw it.")
                : ld == null ? Loc.F("Standing at {0}. Now click where it lands.", MapGeo.CalloutNear(mapName, new Point(l.StandX, l.StandY)))
                : Loc.F("From {0} to {1}. Click again to start over.", MapGeo.CalloutNear(mapName, new Point(l.StandX, l.StandY)), MapGeo.CalloutNear(mapName, new Point(l.LandX, l.LandY)));
        }
        picker.MouseLeftButtonUp += (_, e) =>
        {
            var at = e.GetPosition(picker);
            double x = Math.Clamp(at.X / 380, 0, 1), y = Math.Clamp(at.Y / 380, 0, 1);
            if (l.StandX < 0 || l.LandX >= 0) { l.StandX = x; l.StandY = y; l.LandX = -1; l.LandY = -1; }
            else { l.LandX = x; l.LandY = y; }
            l.Stand = ""; l.Land = "";
            DrawPicker();
        };
        // A lineup copied from the library starts at its callouts' spots.
        if (l.StandX < 0 && l.Stand.Length > 0 && Lineups.Spots(l) is var (cs, cl) && cs is Point ps && cl is Point pl)
        { l.StandX = ps.X; l.StandY = ps.Y; l.LandX = pl.X; l.LandY = pl.Y; l.Stand = ""; l.Land = ""; }
        map.SelectionChanged += (_, _) => { l.StandX = l.StandY = l.LandX = l.LandY = -1; DrawPicker(); };
        place.Children.Add(picker);
        place.Children.Add(placeHint);
        Row("On the map", place);
        side.SelectionChanged += (_, _) => DrawPicker();
        DrawPicker();
        var diff = Combo(new[] { "1", "2", "3" }, l.Difficulty.ToString(CultureInfo.InvariantCulture), DiffName); Row("Difficulty", diff);
        var steps = Box(string.Join(Environment.NewLine, l.Steps), "One step per line: where to stand, where to aim, how to throw", true); Row("Steps", steps);
        var video = Box(l.Video, "https://… (optional)"); Row("Video link", video);
        var picRow = new StackPanel { Orientation = Orientation.Horizontal };
        var picName = new TextBlock { Text = l.Image.Length > 0 ? Loc.T("Picture added") : Loc.T("No picture"), FontSize = 12, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var choose = Btn("Choose a picture");
        choose.Margin = new Thickness(0);
        choose.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Pictures") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
            if (dlg.ShowDialog() != true) return;
            var name = Lineups.AddImage(dlg.FileName);
            if (name.Length == 0) { MainWindow.Instance.Toast(Loc.T("That picture couldn't be added (PNG, JPG, WebP or BMP under 15 MB)."), ToastKind.Info); return; }
            l.Image = name; picName.Text = Loc.T("Picture added");
        };
        picRow.Children.Add(choose);
        picRow.Children.Add(picName);
        Row("Picture", picRow);
        sp.Children.Add(form);

        var err = new TextBlock { FontSize = 12, Foreground = Res("WarnBrush"), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        sp.Children.Add(err);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var save = Btn("Save lineup", true);
        save.Click += (_, _) =>
        {
            var lines = steps.Text.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (title.Text.Trim().Length == 0) { err.Text = Loc.T("Give it a title."); err.Visibility = Visibility.Visible; return; }
            if (video.Text.Trim().Length > 0 && !Lineups.SafeLink(video.Text)) { err.Text = Loc.T("The video link needs to start with https://"); err.Visibility = Visibility.Visible; return; }
            l.Title = title.Text.Trim(); l.Map = Sel(map); l.Agent = Sel(agent); l.Ability = ability.Text.Trim(); l.Site = Sel(site); l.Side = Sel(side);
            l.Difficulty = int.TryParse(Sel(diff), out var d) ? d : 1; l.Steps = lines; l.Video = video.Text.Trim();
            Lineups.Save(l);
            _luEdit = null; Build();
        };
        btns.Children.Add(save);
        var cancel = Btn("Cancel");
        cancel.Click += (_, _) => { _luEdit = null; Build(); };
        btns.Children.Add(cancel);
        sp.Children.Add(btns);
        return Card(sp);
    }
}
