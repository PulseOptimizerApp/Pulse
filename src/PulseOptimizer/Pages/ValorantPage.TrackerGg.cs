using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Pulse.Core;
using Pulse.Core.Games;
using Pulse.Core.Games.Live;
using Pulse.Core.Games.Valorant;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// The tracker laid out like tracker.gg: a tab bar under the profile (Overview, Matches, Performance, Agents, Maps,
/// Weapons, Encounters), playlist and act buttons, a side rail (weapons, maps, roles, accuracy) next to the act
/// overview, top agents and the last matches grouped by day. Each tab is short, so nothing needs a long scroll, and a
/// match opens in a panel over the page instead of replacing it.
/// </summary>
public partial class ValorantPage
{
    // tracker.gg's slate palette.
    static readonly Color TgPanel = Color.FromRgb(0x19, 0x1E, 0x25), TgCard = Color.FromRgb(0x20, 0x26, 0x2F), TgHead = Color.FromRgb(0x26, 0x2D, 0x38),
        TgLine = Color.FromRgb(0x2B, 0x32, 0x3D), TgText = Color.FromRgb(0xEE, 0xF1, 0xF5), TgSub = Color.FromRgb(0xBF, 0xC6, 0xD0), TgDim = Color.FromRgb(0x87, 0x92, 0xA1),
        TgWin = Color.FromRgb(0x2D, 0xD4, 0xA7), TgLoss = Color.FromRgb(0xFF, 0x54, 0x68), TgRed = Color.FromRgb(0xFF, 0x46, 0x55), TgYellow = Color.FromRgb(0xF7, 0xC1, 0x4C), TgBlue = Color.FromRgb(0x4D, 0xA3, 0xFF);

    static string _trView = "overview";
    static bool _trShowMore;
    static int _trShown = 20; // matches on the Matches tab; more on request (fewer rows draw faster)

    // ───────── building blocks ─────────

    Border TgBox(UIElement child, double pad = 0, double bottom = 12) => new()
    {
        Background = B(TgPanel), CornerRadius = new CornerRadius(6), Padding = new Thickness(pad), Margin = new Thickness(0, 0, 0, bottom), Child = child, ClipToBounds = true,
    };

    /// <summary>A section title in tracker.gg's style: big caps on the left, a note next to it, a red link on the right.</summary>
    FrameworkElement TgHeading(string title, string note = null, string link = null, Action go = null)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new TextBlock { Text = title.ToUpper(CultureInfo.CurrentCulture), FontSize = 16, FontWeight = FontWeights.Bold, Foreground = B(TgText), FontFamily = (FontFamily)FindResource("DisplayFont"), VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(note)) left.Children.Add(new TextBlock { Text = note, FontSize = 11.5, Foreground = B(TgDim), Margin = new Thickness(14, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        g.Children.Add(left);
        if (link != null && go != null) g.Children.Add(TgLink(link, go).Also(l => l.HorizontalAlignment = HorizontalAlignment.Right));
        return g;
    }

    FrameworkElement TgLink(string text, Action go)
    {
        var t = new TextBlock { Text = text + "  →", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B(TgRed), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
        t.MouseEnter += (_, _) => t.TextDecorations = TextDecorations.Underline;
        t.MouseLeave += (_, _) => t.TextDecorations = null;
        t.MouseLeftButtonUp += (_, e) => { e.Handled = true; go(); };
        return t;
    }

    /// <summary>A label over a value, the way tracker.gg prints every stat.</summary>
    static StackPanel LabelValue(string label, string value, Color? c = null, double size = 15, string tip = null, string sub = null, Color? subColor = null)
    {
        var sp = new StackPanel { ToolTip = tip == null ? null : Loc.T(tip) };
        sp.Children.Add(Txt(Loc.T(label), 11.5, TgDim));
        sp.Children.Add(Txt(value, size, c ?? TgText, true).Also(t => t.Margin = new Thickness(0, 1, 0, 0)));
        if (!string.IsNullOrEmpty(sub)) sp.Children.Add(Txt(sub, 10.5, subColor ?? TgDim));
        return sp;
    }

    /// <summary>A button that looks like tracker.gg's grey "View All …" bars.</summary>
    Border TgWide(string text, Action go)
    {
        var b = new Border
        {
            Background = B(TgHead), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 7, 10, 8), Margin = new Thickness(10, 6, 10, 10), Cursor = Cursors.Hand,
            Child = Txt(Loc.T(text), 12, TgSub, true, HorizontalAlignment.Center),
        };
        b.MouseEnter += (_, _) => b.Background = B(TgLine);
        b.MouseLeave += (_, _) => b.Background = B(TgHead);
        b.MouseLeftButtonUp += (_, _) => go();
        return b;
    }

    static void Hover(Border b, Color normal, Color over)
    {
        b.Background = B(normal);
        b.MouseEnter += (_, _) => b.Background = B(over);
        b.MouseLeave += (_, _) => b.Background = B(normal);
    }

    /// <summary>An arc of a full circle (degrees, 0 = top, clockwise).</summary>
    static System.Windows.Shapes.Path Arc(double size, double stroke, double startDeg, double sweepDeg, Color c)
    {
        double r = (size - stroke) / 2, cx = size / 2, cy = size / 2;
        sweepDeg = Math.Clamp(sweepDeg, 0.01, 359.99);
        Point P(double deg) => new(cx + r * Math.Sin(deg * Math.PI / 180), cy - r * Math.Cos(deg * Math.PI / 180));
        var fig = new PathFigure { StartPoint = P(startDeg), IsClosed = false };
        fig.Segments.Add(new ArcSegment(P(startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true));
        return new System.Windows.Shapes.Path { Data = new PathGeometry(new[] { fig }), Stroke = B(c), StrokeThickness = stroke, Width = size, Height = size };
    }

    /// <summary>tracker.gg's win/loss ring: green for wins, red for losses, the record in the middle.</summary>
    static FrameworkElement WinRing(int wins, int losses, double size = 58)
    {
        var g = new Grid { Width = size, Height = size };
        int n = wins + losses;
        if (n == 0) g.Children.Add(Arc(size, 4, 0, 359.99, TgLine));
        else
        {
            double w = 360.0 * wins / n;
            if (wins > 0) g.Children.Add(Arc(size, 4, 0, w - (losses > 0 ? 3 : 0), TgWin));
            if (losses > 0) g.Children.Add(Arc(size, 4, w, 360 - w - (wins > 0 ? 3 : 0), TgLoss));
        }
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        t.Children.Add(Txt($"{wins} W", 11.5, TgWin, true, HorizontalAlignment.Center));
        t.Children.Add(Txt($"{losses} L", 11.5, TgLoss, true, HorizontalAlignment.Center));
        g.Children.Add(t);
        return g;
    }

    const string RoleDuelist = "M4 20 L12 3 L20 20 L12 15.5 Z";
    const string RoleInitiator = "M2.5 12 C6.5 5.5 17.5 5.5 21.5 12 C17.5 18.5 6.5 18.5 2.5 12 Z M9 12 A3 3 0 1 0 15 12 A3 3 0 1 0 9 12";
    const string RoleController = "M12 3 A9 9 0 1 0 12.01 3 Z M12 8 A4 4 0 1 0 12.01 8 Z";

    static FrameworkElement RoleIcon(string role, double size = 30)
    {
        // VALORANT's own role icon in a dark ring, like the trackers; a drawn one if the build had no art.
        if (MatchArt.RoleIcon(role) is { } art)
            return new Border
            {
                Width = size + 6, Height = size + 6, CornerRadius = new CornerRadius((size + 6) / 2), Background = B(Color.FromRgb(0x10, 0x13, 0x18)),
                BorderBrush = B(Color.FromRgb(0x3A, 0x42, 0x4F)), BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new Image { Source = art, Width = size * 0.62, Height = size * 0.62, Stretch = Stretch.Uniform }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)),
                ToolTip = Loc.T(role),
            };
        var (path, c) = role switch
        {
            "Duelist" => (RoleDuelist, Color.FromRgb(0xFF, 0x7A, 0x59)),
            "Initiator" => (RoleInitiator, Color.FromRgb(0x4D, 0xA3, 0xFF)),
            "Controller" => (RoleController, Color.FromRgb(0xC9, 0xA2, 0xFF)),
            _ => (IconShield, TgWin),
        };
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = B(A(c, 0x22)), BorderBrush = B(A(c, 0x88)), BorderThickness = new Thickness(1.5),
            Child = Icon(path, c, size * 0.5), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
        };
    }

    static string Short(double v) => v >= 10000 ? (v / 1000).ToString("0.0", CultureInfo.CurrentCulture) + "K" : v.ToString("N0", CultureInfo.CurrentCulture);

    static string QueueTitle(string q) => q switch { "all" => "All", "deathmatch" => "Deathmatch", _ => QueueName(q) };

    /// <summary>"3w ago", "2d ago", "5h ago": tracker.gg's short ages.</summary>
    static string AgoShort(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalDays >= 60) return Loc.F("{0:0}mo ago", d.TotalDays / 30.4);
        if (d.TotalDays >= 14) return Loc.F("{0:0}w ago", d.TotalDays / 7);
        return Ago(utc);
    }

    // Per-match numbers, worked out once (until a match is saved) instead of on every redraw.
    static PlayerTotals TotalsOf(VMatch m, string id) => Memo("tot:" + m.Id + ":" + id, () => Analysis.Totals(m, id));
    static double ScoreOf(VMatch m, string id) => Memo("score:" + m.Id + ":" + id, () => (object)Tracker.MatchScore(m, id)) is double d ? d : double.NaN;
    static List<(string Text, Color C)> BadgesOf(VMatch m, string id) => Memo("badges:" + m.Id + ":" + id, () => Badges(m, id));
    static Overview OverviewOf(List<VMatch> ms, string id) => Memo("ovr:" + id + ":" + ms.Count + ":" + string.Join(",", ms.Select(m => m.Id)).GetHashCode(), () => Stats.Of(ms, id));

    // ───────── which matches ─────────

    /// <summary>The profile's matches plus its Deathmatch games (kept for the Deathmatch playlist only).</summary>
    static List<VMatch> ProfileMatches(Profile p) => Memo("pm:" + p.Puuid + ":" + p.Matches.Count + ":" + p.FromYourMatches, () =>
    {
        if (!p.FromYourMatches) return p.Matches;
        var dm = VStore.All.Where(m => m.Queue == "deathmatch" && m.Console == _trConsole && m.Players.Any(x => x.Id == p.Puuid)).ToList();
        return p.Matches.Concat(dm).OrderByDescending(m => m.StartUtc).ToList();
    });

    // ───────── profile tab bar ─────────

    FrameworkElement ProfileNav(Profile p)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        void Item(string key, string label, Action go = null, bool isNew = false)
        {
            bool on = _trView == key;
            var t = new TextBlock { Text = Loc.T(label), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = B(on ? TgText : TgDim), VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 13, 0, 13) };
            row.Children.Add(t);
            if (isNew) row.Children.Add(new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1, 5, 2), Margin = new Thickness(6, 0, 0, 0), BorderBrush = B(TgRed), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center, Child = Txt(Loc.T("NEW"), 9, TgRed, true) });
            var g = new Grid { Margin = new Thickness(0, 0, 28, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            g.Children.Add(row);
            g.Children.Add(new Border { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Background = on ? B(TgRed) : Brushes.Transparent });
            g.MouseEnter += (_, _) => { if (!on) t.Foreground = B(TgText); };
            g.MouseLeave += (_, _) => { if (!on) t.Foreground = B(TgDim); };
            g.MouseLeftButtonUp += (_, _) => { if (go != null) go(); else { _trView = key; _trShown = 20; Build(); } };
            bar.Children.Add(g);
        }
        Item("overview", "Overview");
        Item("matches", "Matches");
        Item("performance", "Performance");
        Item("agents", "Agents");
        Item("maps", "Maps");
        Item("weapons", "Weapons");
        Item("encounters", "Encounters", isNew: true);
        if (p.IsYou) Item("insights", "Pulse insights");
        Item("crosshairs", "Crosshairs", () => MainWindow.Instance.Navigate("crosshair"));
        Item("lineups", "Lineups", () => ShowTab("lineups"));
        return new Border
        {
            Background = B(TgPanel), CornerRadius = new CornerRadius(6), Padding = new Thickness(18, 0, 18, 0), Margin = new Thickness(0, -4, 0, 12),
            Child = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = bar },
        };
    }

    // ───────── playlist and act buttons ─────────

    FrameworkElement Segments(IEnumerable<(string Label, bool On, Action Go)> items, double minWidth = 104)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, on, go) in items)
        {
            var b = new Border { MinWidth = minWidth, Height = 34, Padding = new Thickness(14, 0, 14, 0), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 1, 0), Child = Txt(label, 12.5, on ? Colors.White : TgSub, true, HorizontalAlignment.Center) };
            if (on) b.Background = B(TgRed); else Hover(b, TgCard, TgHead);
            b.MouseLeftButtonUp += (_, _) => go();
            sp.Children.Add(b);
        }
        return new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Margin = new Thickness(0, 0, 10, 8), Child = sp, HorizontalAlignment = HorizontalAlignment.Left };
    }

    /// <summary>A small "⋮" button that opens a menu.</summary>
    Border MenuButton(IEnumerable<(string Label, bool On, Action Go)> items, string tip)
    {
        var b = new Border { Width = 34, Height = 34, Cursor = Cursors.Hand, ToolTip = Loc.T(tip), Child = Txt("⋮", 16, TgSub, true, HorizontalAlignment.Center) };
        Hover(b, TgCard, TgHead);
        var menu = new ContextMenu();
        foreach (var (label, on, go) in items)
        {
            var mi = new MenuItem { Header = label, IsCheckable = false, FontWeight = on ? FontWeights.Bold : FontWeights.Normal };
            mi.Click += (_, _) => go();
            menu.Items.Add(mi);
        }
        b.MouseLeftButtonUp += (_, _) => { if (menu.Items.Count == 0) return; menu.PlacementTarget = b; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };
        return b;
    }

    FrameworkElement FilterBarGg(Profile p)
    {
        var all = ProfileMatches(p);
        var q = _trQueue ?? DefaultQueue(p.Matches);
        var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        // PC or console first, as tracker.gg puts it: each has its own matches and rank.
        wrap.Children.Add(Segments(new (string, bool, Action)[]
        {
            (Loc.T("PC"), !_trConsole, () => SetPlatform(false)),
            (Loc.T("Console"), _trConsole, () => SetPlatform(true)),
        }, 80));
        var queues = new List<(string Label, bool On, Action Go)>();
        foreach (var key in new[] { "all", "competitive", "unrated", "deathmatch", "swiftplay", "premier" })
        {
            int n = key == "all" ? all.Count(m => VMatch.Counts(m.Queue)) : all.Count(m => m.Queue == key);
            if (n == 0 && key is "premier" or "deathmatch" && key != q) continue;
            var k = key;
            queues.Add((Loc.T(QueueTitle(key)), q == key, () => { _trQueue = k; Build(); }));
        }
        wrap.Children.Add(Segments(queues));

        // Acts: All, this act (or the one picked), and the rest in a menu.
        var acts = p.Acts.Where(a => a.Games > 0 || a.Tier >= 3 || a.Current).ToList();
        var cur = acts.FirstOrDefault(a => a.Current);
        var picked = _trAct is "all" or "current" ? null : acts.FirstOrDefault(a => a.Id == _trAct);
        string middle = picked != null ? (picked.Name.Length > 0 ? picked.Name : Loc.T("Earlier act")) : cur?.Name is { Length: > 0 } cn ? cn : Loc.T("This act");
        var actRow = new StackPanel { Orientation = Orientation.Horizontal };
        var seg = (Border)Segments(new (string, bool, Action)[]
        {
            (Loc.T("All Acts"), _trAct == "all", () => { _trAct = "all"; Build(); }),
            (middle, _trAct != "all", () => { if (picked == null) { _trAct = "current"; Build(); } }),
        }, 96);
        seg.Margin = new Thickness(0);
        actRow.Children.Add(seg);
        var earlier = acts.Where(a => !a.Current).Select(a => (a.Name.Length > 0 ? a.Name : Loc.T("Earlier act"), _trAct == a.Id, (Action)(() => { _trAct = a.Id; Build(); }))).ToList();
        if (earlier.Count > 0) actRow.Children.Add(MenuButton(earlier, "Earlier acts").Also(b => b.Margin = new Thickness(1, 0, 0, 0)));
        wrap.Children.Add(new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = actRow, Margin = new Thickness(0, 0, 0, 8) });
        return wrap;
    }

    // ───────── the profile, by tab ─────────

    /// <summary>The tracker part of a profile (under the header, gauges, acts and skills on Overview).</summary>
    IEnumerable<FrameworkElement> TrackerBody(Profile p, Tracker.MmrEstimate mmr)
    {
        var filter = CurrentFilter(p);
        var ms = filter.Apply(ProfileMatches(p)).OrderByDescending(m => m.StartUtc).ToList();
        string id = p.Puuid;

        if (_trView is "overview" or "matches" or "performance" or "agents" or "maps" or "weapons") yield return FilterBarGg(p);

        if (ms.Count == 0 && _trConsole && _trView is not ("encounters" or "insights"))
        {
            yield return Card(ConsoleProbe());
            yield break;
        }
        if (ms.Count == 0 && _trView is not ("encounters" or "insights"))
        {
            var none = new StackPanel { Margin = new Thickness(18, 22, 18, 24) };
            none.Children.Add(Txt(Loc.T("NO DATA FOUND"), 18, TgText, true, HorizontalAlignment.Center));
            none.Children.Add(Txt(Loc.T(_trAct != "all"
                ? "Riot shares only the rank, record and peak for earlier acts (in the acts row above), not the matches. Pulse keeps every match you play from now on."
                : (_trQueue ?? "") == "deathmatch" ? "No Deathmatch games saved yet. Pulse saves them from now on, with your other matches." : "No matches in this playlist yet."), 12, TgDim, false, HorizontalAlignment.Center)
                .Also(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; t.TextAlignment = TextAlignment.Center; t.Margin = new Thickness(0, 6, 0, 0); }));
            if (_trAct != "all" || _trQueue != null)
            {
                var all = Btn("Show all matches");
                all.HorizontalAlignment = HorizontalAlignment.Center;
                all.Margin = new Thickness(0, 14, 0, 0);
                all.Click += (_, _) => { _trAct = "all"; _trQueue = "all"; Build(); };
                none.Children.Add(all);
            }
            yield return TgBox(none);
            yield break;
        }

        switch (_trView)
        {
            case "matches":
                if (p.IsYou) yield return OlderMatchesCard(p.Matches.Count);
                yield return LastMatchesGg(p, ms, _trShown, false);
                break;
            case "performance":
                yield return PerformanceTrends(p, ms);
                break;
            case "agents":
                yield return TgHeading(Loc.T("Agents"), Loc.F("{0} matches", ms.Count));
                yield return AgentsGg(ms, id, int.MaxValue, true);
                break;
            case "maps":
                yield return TgHeading(Loc.T("Maps"), Loc.F("{0} matches", ms.Count));
                yield return MapsFullGg(ms, id);
                break;
            case "weapons":
                yield return TgHeading(Loc.T("Weapons"), Loc.F("{0} matches", ms.Count));
                yield return PanelRow((WeaponsFullGg(ms, id), 0), (AccuracyGg(ms, id), 300));
                break;
            case "encounters":
                yield return EncountersGg(p);
                break;
            case "insights":
                foreach (var e in InsightCards(p, mmr)) yield return e;
                break;
            default:
            {
                var rail = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
                rail.Children.Add(RolesGg(ms, id));
                rail.Children.Add(TopWeaponsGg(ms, id));
                rail.Children.Add(TopMapsGg(ms, id));
                rail.Children.Add(AccuracyGg(ms, id));
                var main = new StackPanel();
                main.Children.Add(ActOverviewGg(p, ms));
                main.Children.Add(TgHeading(Loc.T("Top agents"), Loc.T("by playtime"), Loc.T("All Agents"), () => { _trView = "agents"; Build(); }));
                main.Children.Add(AgentsGg(ms, id, 3, false));
                main.Children.Add(LastMatchesGg(p, ms, 10, true)); // the rest on the Matches tab: a shorter page draws faster
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(rail);
                Grid.SetColumn(main, 1);
                row.Children.Add(main);
                yield return row;
                break;
            }
        }
        if (!p.FromYourMatches) yield return Faint(Loc.T("Searched profiles start with the player's last 10 matches; load more on the Matches tab. Only anonymous numbers from them are kept (each player's rank and stats, no names), to make skill comparisons more accurate."), 11.5, 6);
        if (!p.IsYou && _trView == "overview") yield return Faint(Loc.T("Cheating signals and smurf chance add up warning signs; they're hints, not proof. Some players are just good, or improving fast."), 11.5, 4);
    }

    // ───────── overview (right column) ─────────

    string ActLabel(Profile p)
    {
        if (_trAct == "all") return Loc.T("All acts");
        var acts = p.Acts;
        var a = _trAct == "current" ? acts.FirstOrDefault(x => x.Current) : acts.FirstOrDefault(x => x.Id == _trAct);
        return a?.Name is { Length: > 0 } n ? n : Loc.T("This act");
    }

    FrameworkElement ActOverviewGg(Profile p, List<VMatch> ms)
    {
        var o = Memo("ov:" + p.Puuid + ":" + string.Join(",", ms.Take(3).Select(m => m.Id)) + ms.Count, () => Stats.Of(ms, p.Puuid));
        var q = _trQueue ?? DefaultQueue(p.Matches);
        var sp = new StackPanel();
        sp.Children.Add(TgHeading(Loc.F("{0} {1} overview", ActLabel(p), Loc.T(QueueTitle(q))), Loc.F("{0} playtime // {1} matches", Hours(o.PlayMs), o.Matches),
            Loc.T(_trShowMore ? "Show less" : "Show more"), () => { _trShowMore = !_trShowMore; Build(); }));

        var body = new StackPanel();
        // Level and record.
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        if (p.Level > 0) top.Children.Add(LabelValue("Level", p.Level.ToString(CultureInfo.CurrentCulture), TgText, 22).Also(s => { s.Margin = new Thickness(0, 0, 18, 0); s.VerticalAlignment = VerticalAlignment.Center; }));
        top.Children.Add(WinRing(o.Wins, o.Losses));
        if (o.Draws > 0) top.Children.Add(Txt(Loc.F("{0} draws", o.Draws), 11.5, TgDim).Also(t => t.Margin = new Thickness(12, 0, 0, 0)));
        body.Children.Add(top);

        // The four big ones, each with how it ranks.
        var big = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -8, 0) };
        void Big(string label, string value, string topId, double raw, Color c)
        {
            var g = new Grid();
            var top2 = topId != null ? Tracker.TopPercentOf(topId, raw, p.Console) : double.NaN;
            g.Children.Add(LabelValue(label, value, c, 17, sub: double.IsNaN(top2) ? null : Loc.F("Top {0:0}%", top2), subColor: !double.IsNaN(top2) && top2 <= 25 ? TgWin : TgDim));
            g.Children.Add(Txt("⋮", 15, TgRed, true, HorizontalAlignment.Right).Also(t => { t.VerticalAlignment = VerticalAlignment.Top; t.ToolTip = double.IsNaN(top2) ? Loc.T("Not ranked: needs more players at this rank.") : Loc.F("Better than {0:0}% of the players in the matches Pulse has seen.", 100 - top2); }));
            big.Children.Add(new Border { Background = B(TgCard), CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 9, 10, 9), Margin = new Thickness(0, 0, 8, 0), Child = g });
        }
        Big("Damage/Round", N(o.DamagePerRound, "0.0"), "adr", o.DamagePerRound, TgText);
        Big("K/D Ratio", N(o.Kd, "0.00"), "kd", o.Kd, KdColor(o.Kd));
        Big("Headshot %", double.IsNaN(o.HeadshotPct) ? "–" : N(o.HeadshotPct, "0.0") + "%", "hs", o.HeadshotPct, TgText);
        Big("Win %", double.IsNaN(o.WinPct) ? "–" : N(o.WinPct, "0.0") + "%", null, 0, double.IsNaN(o.WinPct) ? TgDim : o.WinPct >= 50 ? TgWin : TgLoss);
        body.Children.Add(big);

        var grid = new UniformGrid { Columns = 6, Margin = new Thickness(4, 14, 0, 0) };
        void Small(string label, string value, string topId = null, double raw = double.NaN, Color? c = null, string tip = null)
        {
            var t = topId != null ? Tracker.TopPercentOf(topId, raw, p.Console) : double.NaN;
            grid.Children.Add(LabelValue(label, value, c, 15, tip, double.IsNaN(t) ? null : Loc.F("Top {0:0}%", t), !double.IsNaN(t) && t <= 25 ? TgWin : TgDim).Also(s => s.Margin = new Thickness(0, 0, 8, 12)));
        }
        Small("Wins", o.Wins.ToString(CultureInfo.CurrentCulture));
        Small("KAST", double.IsNaN(o.KastPct) ? "–" : N(o.KastPct, "0.0") + "%", "kast", o.KastPct, null, "Rounds with a kill, assist, survival or trade.");
        Small("DDΔ/Round", Signed(o.DdPerRound), "dd", o.DdPerRound, SignColor(o.DdPerRound), "Damage dealt minus damage taken, per round.");
        Small("Kills", o.Kills.ToString("N0", CultureInfo.CurrentCulture));
        Small("Deaths", o.Deaths.ToString("N0", CultureInfo.CurrentCulture));
        Small("Assists", o.Assists.ToString("N0", CultureInfo.CurrentCulture));
        Small("ACS", N(o.Acs, "0.0"), "acs", o.Acs, null, "Average combat score per round, as on VALORANT's scoreboard.");
        Small("KAD Ratio", N(o.Kad, "0.00"), "kad", o.Kad);
        Small("Kills/Round", N(o.KillsPerRound, "0.00"), "kpr", o.KillsPerRound);
        Small("First Bloods", o.FirstBloods.ToString(CultureInfo.CurrentCulture));
        Small("Flawless Rounds", o.Flawless.ToString(CultureInfo.CurrentCulture), tip: "Rounds your team won without losing anyone.");
        Small("Aces", o.Aces.ToString(CultureInfo.CurrentCulture));
        if (_trShowMore)
        {
            Small("Clutches", o.ClutchChances > 0 ? $"{o.ClutchWins}/{o.ClutchChances}" : "0", tip: "Rounds won as the last one alive, of the times you were.");
            Small("Multi-kills", o.MultiKills.ToString(CultureInfo.CurrentCulture), tip: "Rounds with 2 or more kills.");
            Small("First Deaths", o.FirstDeaths.ToString(CultureInfo.CurrentCulture));
            Small("Plants", o.Plants.ToString(CultureInfo.CurrentCulture));
            Small("Defuses", o.Defuses.ToString(CultureInfo.CurrentCulture));
            Small("Econ Rating", N(o.Econ), tip: "Damage for every 1,000 credits spent.");
            Small("2K", o.TwoK.ToString(CultureInfo.CurrentCulture));
            Small("3K", o.ThreeK.ToString(CultureInfo.CurrentCulture));
            Small("4K", o.FourK.ToString(CultureInfo.CurrentCulture));
            Small("Rounds", o.Rounds.ToString("N0", CultureInfo.CurrentCulture));
            Small("Damage", Short(o.Damage));
            Small("Pulse score", N(ms.Select(m => ScoreOf(m, p.Puuid)).Where(x => !double.IsNaN(x)).DefaultIfEmpty(double.NaN).Average()), tip: "One match's overall impact (0–1000), averaged.");
        }
        body.Children.Add(grid);
        sp.Children.Add(TgBox(body, 14));
        return sp;
    }

    // ───────── agents ─────────

    FrameworkElement AgentsGg(List<VMatch> ms, string id, int take, bool full)
    {
        var list = Memo("ag:" + id + ":" + ms.Count + ":" + (ms.Count > 0 ? ms[0].Id : ""), () => Panels.Agents(ms, id)).Take(take).ToList();
        var heads = full
            ? new[] { "Agent", "Role", "Matches", "Win %", "K/D", "KDA", "ADR", "ACS", "DDΔ", "HS%", "KAST", "Best Map" }
            : new[] { "Agent", "Matches", "Win %", "K/D", "ADR", "ACS", "DDΔ", "Best Map" };
        var widths = full ? new double[] { 0, 90, 66, 66, 56, 56, 58, 58, 56, 56, 58, 190 } : new double[] { 0, 70, 70, 60, 64, 64, 60, 210 };
        var g = new Grid();
        foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star), MinWidth = w > 0 ? 0 : 150 });
        int r = 0;
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < heads.Length; c++)
        {
            var h = new Border { Padding = new Thickness(c == 0 ? 14 : 4, 9, c == heads.Length - 1 ? 14 : 4, 7), Child = Txt(Loc.T(heads[c]), 11, TgDim, false, c == 0 ? HorizontalAlignment.Left : c == heads.Length - 1 ? HorizontalAlignment.Right : HorizontalAlignment.Center) };
            Grid.SetColumn(h, c); g.Children.Add(h);
        }
        r++;
        foreach (var a in list)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });
            var line = new Border { BorderBrush = B(r == 1 ? TgYellow : Colors.Transparent), BorderThickness = new Thickness(r == 1 ? 3 : 0, 0, 0, 0) };
            Hover(line, r % 2 == 1 ? TgCard : TgPanel, TgHead);
            Grid.SetRow(line, r); Grid.SetColumnSpan(line, widths.Length); g.Children.Add(line);
            var t = a.Totals;
            var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (MatchArt.AgentIcon(a.Agent) is { } ai) name.Children.Add(new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(4), ClipToBounds = true, Margin = new Thickness(0, 0, 10, 0), Background = new ImageBrush(ai) { Stretch = Stretch.UniformToFill } });
            var nm = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nm.Children.Add(Txt(a.Agent, 14, TgText, true));
            nm.Children.Add(Txt(Hours(a.PlayMs), 11, TgDim));
            name.Children.Add(nm);
            var best = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            if (a.BestMap.Length > 0)
            {
                best.Children.Add(Txt(a.BestMap, 13, TgText, true));
                best.Children.Add(Txt("  " + N(a.BestMapWin) + "% " + Loc.T("WR"), 13, a.BestMapWin >= 50 ? TgBlue : TgLoss, true));
                if (MatchArt.MapPicture(a.BestMap) is { } mp) best.Children.Add(new Border { Width = 30, Height = 22, CornerRadius = new CornerRadius(3), Margin = new Thickness(8, 0, 0, 0), ClipToBounds = true, Background = new ImageBrush(mp) { Stretch = Stretch.UniformToFill } });
            }
            else best.Children.Add(Txt("–", 13, TgDim));
            var cells = new List<UIElement> { name };
            if (full) cells.Add(Txt(Loc.T(Analysis.RoleOf(a.Agent)), 12.5, TgSub));
            cells.Add(Txt(a.Matches.ToString(CultureInfo.CurrentCulture), 14, TgText, true));
            cells.Add(Txt(N(a.WinPct, "0.0") + "%", 14, a.WinPct >= 50 ? TgWin : TgLoss, true));
            cells.Add(Txt(N(t.KdRatio, "0.00"), 14, KdColor(t.KdRatio), true));
            if (full) cells.Add(Txt(N(t.Deaths > 0 ? (double)(t.Kills + t.Assists) / t.Deaths : t.Kills + t.Assists, "0.00"), 14, TgText, true));
            cells.Add(Txt(N(t.Adr, "0.0"), 14, TgText, true));
            cells.Add(Txt(N(t.Acs, "0.0"), 14, TgText, true));
            cells.Add(Txt(Signed(t.DamageDelta), 14, SignColor(t.DamageDelta), true));
            if (full)
            {
                cells.Add(Txt(double.IsNaN(t.HeadshotPct) ? "–" : N(t.HeadshotPct) + "%", 14, TgText, true));
                cells.Add(Txt(double.IsNaN(t.KastPct) ? "–" : N(t.KastPct) + "%", 14, TgText, true));
            }
            cells.Add(best);
            for (int c = 0; c < cells.Count; c++)
            {
                if (cells[c] is FrameworkElement fe && c > 0 && c < cells.Count - 1) fe.HorizontalAlignment = HorizontalAlignment.Center;
                var host = new Border { Padding = new Thickness(c == 0 ? 14 : 4, 0, c == cells.Count - 1 ? 14 : 4, 0), Child = cells[c], IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(host, r); Grid.SetColumn(host, c); g.Children.Add(host);
            }
            r++;
        }
        if (list.Count == 0) { g.RowDefinitions.Add(new RowDefinition()); var e = Txt(Loc.T("No agents played here yet."), 12, TgDim).Also(t => t.Margin = new Thickness(14)); Grid.SetRow(e, r); Grid.SetColumnSpan(e, widths.Length); g.Children.Add(e); }
        return TgBox(full ? new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = g.Also(x => x.MinWidth = 1040) } : g);
    }

    // ───────── the side rail ─────────

    FrameworkElement RolesGg(List<VMatch> ms, string id)
    {
        var sp = new StackPanel();
        sp.Children.Add(TgHeading(Loc.T("Roles")));
        var box = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var lines = Panels.Roles(ms, id);
        foreach (var r in lines)
        {
            var g = new Grid { Margin = new Thickness(12, 7, 12, 7) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(RoleIcon(r.Role));
            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(Txt(Loc.T(r.Role), 11, TgDim));
            mid.Children.Add(Txt(Loc.T("WR") + " " + N(r.WinPct, "0.0") + "%", 14, TgText, true));
            mid.Children.Add(Txt($"{r.Wins}W - {r.Losses}L", 10.5, TgDim));
            Grid.SetColumn(mid, 1); g.Children.Add(mid);
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(Txt("KDA " + N(r.Kda, "0.00"), 14, TgText, true, HorizontalAlignment.Right));
            right.Children.Add(Txt($"{Short(r.Kills)} / {Short(r.Deaths)} / {Short(r.Assists)}", 10.5, TgDim, false, HorizontalAlignment.Right));
            Grid.SetColumn(right, 2); g.Children.Add(right);
            box.Children.Add(g);
        }
        if (lines.Count == 0) box.Children.Add(Txt(Loc.T("No matches yet."), 12, TgDim).Also(t => t.Margin = new Thickness(12)));
        sp.Children.Add(TgBox(box));
        return sp;
    }

    FrameworkElement TopWeaponsGg(List<VMatch> ms, string id)
    {
        var list = Panels.Weapons(ms, id).Take(3).ToList();
        var sp = new StackPanel();
        sp.Children.Add(TgHeading(Loc.T("Top weapons")));
        var box = new StackPanel();
        foreach (var w in list) box.Children.Add(WeaponRow(w, true));
        if (list.Count == 0) box.Children.Add(Txt(Loc.T("No weapon kills yet."), 12, TgDim).Also(t => t.Margin = new Thickness(12)));
        box.Children.Add(TgWide("View All Weapons", () => { _trView = "weapons"; Build(); }));
        sp.Children.Add(TgBox(box));
        return sp;
    }

    FrameworkElement WeaponRow(Panels.WeaponLine w, bool compact)
    {
        var g = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        if (MatchArt.WeaponIcon(w.Id) is { } icon) left.Children.Add(Pic(icon, compact ? 96 : 120, compact ? 28 : 34, 0).Also(im => im.HorizontalAlignment = HorizontalAlignment.Left));
        left.Children.Add(Txt(w.Name, 13, TgText, true).Also(t => t.Margin = new Thickness(0, 4, 0, 0)));
        left.Children.Add(Txt(w.Category.Length > 0 ? Loc.T(w.Category) : "", 10.5, TgDim));
        g.Children.Add(left);
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(Txt(Loc.T("Kills"), 11, TgDim, false, HorizontalAlignment.Right));
        right.Children.Add(Txt(w.Kills.ToString("N0", CultureInfo.CurrentCulture), 17, TgText, true, HorizontalAlignment.Right));
        right.Children.Add(Txt(Loc.F("{0:0}% of kills", w.Share) + (double.IsNaN(w.AvgMeters) ? "" : " · " + Loc.F("avg {0:0} m", w.AvgMeters)), 10.5, TgDim, false, HorizontalAlignment.Right));
        Grid.SetColumn(right, 1); g.Children.Add(right);
        return new Border { BorderBrush = B(TgLine), BorderThickness = new Thickness(0, 0, 0, 1), Child = g };
    }

    FrameworkElement TopMapsGg(List<VMatch> ms, string id)
    {
        var list = Panels.Maps(ms, id).OrderByDescending(m => m.WinPct).ThenByDescending(m => m.Games).Take(8).ToList();
        var sp = new StackPanel();
        sp.Children.Add(TgHeading(Loc.T("Top maps")));
        var box = new StackPanel();
        var head = new Grid { Margin = new Thickness(12, 8, 12, 6) };
        head.Children.Add(Txt(Loc.T("Map"), 11, TgDim));
        head.Children.Add(Txt(Loc.T("Win %"), 11, TgDim, false, HorizontalAlignment.Right));
        box.Children.Add(head);
        foreach (var m in list) box.Children.Add(MapBanner(m.Map, 40, Txt(m.Map, 14, TgText, true), N(m.WinPct, "0.0") + "%", $"{m.Wins}W - {m.Losses}L", m.WinPct >= 50 ? TgYellow : TgLoss));
        if (list.Count == 0) box.Children.Add(Txt(Loc.T("No matches yet."), 12, TgDim).Also(t => t.Margin = new Thickness(12)));
        box.Children.Add(TgWide("View All Maps", () => { _trView = "maps"; Build(); }));
        sp.Children.Add(TgBox(box));
        return sp;
    }

    /// <summary>A row with the map's picture faded in behind it, like tracker.gg's map lists.</summary>
    static FrameworkElement MapBanner(string map, double height, UIElement left, string big, string small, Color bigColor)
    {
        var g = new Grid { Height = height, ClipToBounds = true };
        if (MatchArt.MapPicture(map) is { } pic) g.Children.Add(new Border { Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill, Opacity = 0.32 } });
        g.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(0xE6, TgPanel.R, TgPanel.G, TgPanel.B), Color.FromArgb(0x55, TgPanel.R, TgPanel.G, TgPanel.B), 0) });
        var l = new Border { Padding = new Thickness(12, 0, 12, 0), Child = left, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(l);
        var r = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        r.Children.Add(Txt(big, 14, bigColor, true, HorizontalAlignment.Right));
        if (!string.IsNullOrEmpty(small)) r.Children.Add(Txt(small, 10, TgDim, false, HorizontalAlignment.Right));
        g.Children.Add(r);
        return new Border { BorderBrush = B(TgLine), BorderThickness = new Thickness(0, 1, 0, 0), Child = g };
    }

    FrameworkElement AccuracyGg(List<VMatch> ms, string id)
    {
        var a = Panels.AccuracyOf(ms, id);
        var sp = new StackPanel();
        sp.Children.Add(TgHeading(Loc.T("Accuracy"), Loc.F("last {0} matches", Math.Min(20, ms.Count))));
        var g = new Grid { Margin = new Thickness(14, 12, 14, 14) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        Color Shade(double pct, Color hot) => double.IsNaN(pct) ? TgLine : Color.FromArgb((byte)Math.Clamp(70 + pct * 3, 70, 255), hot.R, hot.G, hot.B);
        var fig = new Canvas { Width = 40, Height = 92, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        fig.Children.Add(new Ellipse { Width = 15, Height = 15, Fill = B(Shade(a.Pct(a.Head), TgWin)) }.Also(e => { Canvas.SetLeft(e, 12.5); Canvas.SetTop(e, 1); }));
        fig.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M10 19 H30 Q34 19 34 24 V46 H29 V41 H11 V46 H6 V24 Q6 19 10 19Z M11 41 H29 V50 H11Z"), Fill = B(Shade(a.Pct(a.Body) / 3, TgSub)) });
        fig.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M11 50 H19 V90 H13Z M21 50 H29 L27 90 H21Z"), Fill = B(Shade(a.Pct(a.Legs) * 3, TgSub)) });
        g.Children.Add(fig);
        var rows = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        void Line3(string name, int n, Color c)
        {
            var r = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            r.ColumnDefinitions.Add(new ColumnDefinition());
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            r.Children.Add(Txt(Loc.T(name), 12.5, TgDim));
            var pct = Txt(a.Hits > 0 ? N(a.Pct(n), "0.0") + "%" : "–", 13, c, true, HorizontalAlignment.Right); Grid.SetColumn(pct, 1); r.Children.Add(pct);
            var hits = Txt(Loc.F("{0:N0} hits", n), 11, TgDim, false, HorizontalAlignment.Right); Grid.SetColumn(hits, 2); r.Children.Add(hits);
            rows.Children.Add(r);
        }
        Line3("Head", a.Head, TgWin);
        Line3("Body", a.Body, TgText);
        Line3("Legs", a.Legs, TgText);
        Grid.SetColumn(rows, 1);
        g.Children.Add(rows);
        sp.Children.Add(TgBox(g));
        return sp;
    }

    // ───────── last matches ─────────

    static string _hfWin = "";

    FrameworkElement HistoryPills(Profile p, List<VMatch> all)
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        void Pill(string label, bool active, IEnumerable<(string Label, bool On, Action Go)> items)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(3), Padding = new Thickness(10, 5, 8, 6), Margin = new Thickness(0, 0, 8, 6), Cursor = Cursors.Hand,
                Background = B(active ? TgRed : A(TgRed, 0xD0)),
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Txt(label, 12, Colors.White, true));
            sp.Children.Add(Txt("  ⋮", 12, Colors.White, true));
            b.Child = sp;
            var menu = new ContextMenu();
            foreach (var (l, on, go) in items) { var mi = new MenuItem { Header = l, FontWeight = on ? FontWeights.Bold : FontWeights.Normal }; mi.Click += (_, _) => go(); menu.Items.Add(mi); }
            b.MouseLeftButtonUp += (_, _) => { menu.PlacementTarget = b; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };
            bar.Children.Add(b);
        }
        var agents = all.Select(m => m.Players.FirstOrDefault(x => x.Id == p.Puuid)?.Agent).Where(x => !string.IsNullOrEmpty(x)).Distinct().OrderBy(x => x).ToList();
        Pill(_hfAgent.Length > 0 ? _hfAgent : Loc.T("All Agents"), _hfAgent.Length > 0,
            new[] { (Loc.T("All Agents"), _hfAgent.Length == 0, (Action)(() => { _hfAgent = ""; Build(); })) }.Concat(agents.Select(a => (a, _hfAgent == a, (Action)(() => { _hfAgent = a; Build(); })))));
        var maps = all.Select(m => m.Map).Where(x => !string.IsNullOrEmpty(x)).Distinct().OrderBy(x => x).ToList();
        Pill(_hfMap.Length > 0 ? _hfMap : Loc.T("All Maps"), _hfMap.Length > 0,
            new[] { (Loc.T("All Maps"), _hfMap.Length == 0, (Action)(() => { _hfMap = ""; Build(); })) }.Concat(maps.Select(m => (m, _hfMap == m, (Action)(() => { _hfMap = m; Build(); })))));
        Pill(_hfResult == "win" ? Loc.T("Wins") : _hfResult == "loss" ? Loc.T("Losses") : Loc.T("All Results"), _hfResult.Length > 0, new[]
        {
            (Loc.T("All Results"), _hfResult.Length == 0, (Action)(() => { _hfResult = ""; Build(); })),
            (Loc.T("Wins"), _hfResult == "win", (Action)(() => { _hfResult = "win"; Build(); })),
            (Loc.T("Losses"), _hfResult == "loss", (Action)(() => { _hfResult = "loss"; Build(); })),
        });
        Pill(_hfDays switch { 1 => Loc.T("Today"), 7 => Loc.T("Last 7 days"), 30 => Loc.T("Last 30 days"), 90 => Loc.T("Last 90 days"), _ => Loc.T("All Dates") }, _hfDays > 0,
            new[] { (0, "All Dates"), (1, "Today"), (7, "Last 7 days"), (30, "Last 30 days"), (90, "Last 90 days") }.Select(d => (Loc.T(d.Item2), _hfDays == d.Item1, (Action)(() => { _hfDays = d.Item1; Build(); }))));
        if (_hfMap.Length + _hfAgent.Length + _hfResult.Length > 0 || _hfDays > 0)
        {
            var clear = TgLink(Loc.T("Clear filters"), () => { _hfMap = _hfAgent = _hfResult = ""; _hfDays = 0; Build(); });
            clear.Margin = new Thickness(6, 0, 0, 6);
            bar.Children.Add(clear);
        }
        return bar;
    }

    FrameworkElement LastMatchesGg(Profile p, List<VMatch> all, int take, bool overview)
    {
        var id = p.Puuid;
        var filtered = HistoryFiltered(p, all);
        var ms = filtered.Take(take).ToList();
        var sp = new StackPanel();
        sp.Children.Add(TgHeading(overview ? Loc.F("Last {0} matches", ms.Count) : Loc.T("Match history"), overview ? null : Loc.F("{0} matches", filtered.Count),
            overview ? Loc.T("All Matches") : null, overview ? () => { _trView = "matches"; Build(); } : null));
        sp.Children.Add(HistoryPills(p, all));
        if (ms.Count == 0)
        {
            sp.Children.Add(TgBox(Txt(Loc.T("No matches here. Try another playlist, act or filter."), 12.5, TgDim).Also(t => t.Margin = new Thickness(14))));
            return sp;
        }

        // Summary: record, K/D and ADR, and the top three agents.
        var o = OverviewOf(ms, id);
        var sum = new Grid { Margin = new Thickness(12) };
        sum.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sum.ColumnDefinitions.Add(new ColumnDefinition());
        var rec = new StackPanel();
        var recTop = new StackPanel { Orientation = Orientation.Horizontal };
        recTop.Children.Add(Txt($"{o.Wins}W", 15, TgWin, true));
        recTop.Children.Add(Txt(" - ", 15, TgDim, true));
        recTop.Children.Add(Txt($"{o.Losses}L", 15, TgLoss, true));
        recTop.Children.Add(Txt($" ({N(o.WinPct)}%)", 13, TgSub));
        rec.Children.Add(recTop);
        rec.Children.Add(Txt($"{N(o.Kd, "0.00")} K/D | {N(o.DamagePerRound)} ADR", 11.5, TgWin));
        sum.Children.Add(new Border { Background = B(A(TgWin, 0x18)), BorderBrush = B(A(TgWin, 0x55)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 6, 12, 7), Child = rec, VerticalAlignment = VerticalAlignment.Center });
        var tops = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var a in Panels.Agents(ms, id).Take(3))
        {
            var cell = new Grid { Margin = new Thickness(18, 0, 0, 0), Width = 150 };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            if (MatchArt.AgentIcon(a.Agent) is { } ai) line.Children.Add(Pic(ai, 30, 30, 8));
            var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            t.Children.Add(Txt($"{a.Wins}W - {a.Matches - a.Wins}L ({N(a.WinPct)}%)", 12, TgText, true));
            t.Children.Add(Txt("K/D " + N(a.Totals.KdRatio, "0.00"), 10.5, TgDim));
            line.Children.Add(t);
            cell.Children.Add(line);
            var c = a.WinPct >= 50 ? TgRed : TgYellow;
            cell.Children.Add(new Border { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(38, 0, 0, -6), Background = new LinearGradientBrush(c, A(c, 0x30), 0) });
            tops.Children.Add(cell);
        }
        Grid.SetColumn(tops, 1); sum.Children.Add(tops);
        sp.Children.Add(TgBox(sum, 0, 8));

        // The last 8 as score tiles.
        var strip = new UniformGrid { Rows = 1, Columns = 8 };
        foreach (var m in ms.Take(8))
        {
            var team = m.TeamOf(id);
            int us = m.Rounds.Count(r => r.Winner == team), them = m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != team);
            int res = m.Outcome(team);
            var c = res > 0 ? TgWin : res < 0 ? TgLoss : TgDim;
            var t = TotalsOf(m, id);
            var tile = new StackPanel { Margin = new Thickness(4, 8, 4, 8) };
            tile.Children.Add(Txt(AgoShort(m.StartUtc), 10, TgDim, false, HorizontalAlignment.Center));
            var score = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 4) };
            score.Children.Add(Txt(us.ToString(CultureInfo.CurrentCulture), 16, TgText, true));
            score.Children.Add(Txt(" : ", 16, TgDim, true));
            score.Children.Add(Txt(them.ToString(CultureInfo.CurrentCulture), 16, c, true));
            tile.Children.Add(score);
            tile.Children.Add(Txt("K/D " + N(t.KdRatio, "0.0"), 11.5, TgSub, true, HorizontalAlignment.Center));
            var b = new Border { Child = tile, Margin = new Thickness(0, 0, 4, 0), BorderBrush = B(c), BorderThickness = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand, ToolTip = m.Map };
            Hover(b, res > 0 ? Color.FromRgb(0x1C, 0x33, 0x33) : TgCard, TgHead);
            var mid = m.Id;
            b.MouseLeftButtonUp += (_, _) => OpenMatch(mid);
            strip.Children.Add(b);
        }
        sp.Children.Add(TgBox(strip, 6, 10));

        // Day by day.
        var days = new StackPanel { Margin = new Thickness(10, 4, 10, 10) };
        foreach (var day in Memo("days:" + id + ":" + ms.Count + ":" + string.Join(",", ms.Select(m => m.Id)).GetHashCode(), () => Stats.Days(ms, id)))
        {
            days.Children.Add(DayHeader(day));
            foreach (var m in day.Matches) days.Children.Add(MatchRowGg(m, id));
        }
        if (!overview && filtered.Count > ms.Count)
        {
            var next = Btn(Loc.F("Show {0} more", Math.Min(20, filtered.Count - ms.Count)));
            next.Margin = new Thickness(0, 8, 0, 4);
            next.HorizontalAlignment = HorizontalAlignment.Center;
            next.Click += (_, _) => { _trShown += 20; Build(); };
            days.Children.Add(next);
        }
        else if (!overview && !p.FromYourMatches && ValorantSearch.HasMore(p))
        {
            var more = Btn(_trMoreBusy ? "Loading…" : "Load 10 more matches");
            more.IsEnabled = !_trMoreBusy;
            more.Margin = new Thickness(0, 8, 0, 4);
            more.HorizontalAlignment = HorizontalAlignment.Center;
            more.Click += async (_, _) => await LoadMore(p);
            days.Children.Add(more);
        }
        else if (!overview) days.Children.Add(Txt(Loc.T("End of Results"), 11.5, TgDim, false, HorizontalAlignment.Center).Also(t => t.Margin = new Thickness(0, 10, 0, 4)));
        sp.Children.Add(TgBox(days));
        return sp;
    }

    // Columns on the right of a match row and a day header, so their numbers line up.
    static readonly double[] RowStatWidths = { 56, 110, 52, 50, 56, 26 };

    static Grid StatColumns(params UIElement[] cells)
    {
        var g = new Grid { HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < RowStatWidths.Length; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RowStatWidths[i]) });
        for (int i = 0; i < cells.Length && i < RowStatWidths.Length; i++)
        {
            if (cells[i] == null) continue;
            if (cells[i] is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(cells[i], i); g.Children.Add(cells[i]);
        }
        return g;
    }

    static StackPanel Stacked(string label, string value, Color c, double size = 14)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 0) };
        sp.Children.Add(Txt(Loc.T(label), 10, TgDim, false, HorizontalAlignment.Right));
        sp.Children.Add(Txt(value, size, c, true, HorizontalAlignment.Right));
        return sp;
    }

    FrameworkElement DayHeader(Stats.Day day)
    {
        var d = day.Totals;
        var g = new Grid { Margin = new Thickness(4, 12, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var date = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        date.Children.Add(Txt(day.Date == DateTime.Today ? Loc.T("Today") : day.Date == DateTime.Today.AddDays(-1) ? Loc.T("Yesterday") : day.Date.ToString("MMM d", CultureInfo.CurrentCulture), 14, TgSub, true));
        date.Children.Add(new Border { CornerRadius = new CornerRadius(3), BorderBrush = B(TgDim), BorderThickness = new Thickness(1), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(8, 0, 0, 0), Child = Txt(day.Matches.Count.ToString(CultureInfo.CurrentCulture), 10.5, TgSub, true) });
        g.Children.Add(date);
        var rec = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        rec.Children.Add(Txt($"{d.Wins} W", 13, TgWin, true));
        rec.Children.Add(Txt(" // ", 13, TgDim, true));
        rec.Children.Add(Txt($"{d.Losses} L", 13, TgLoss, true));
        Grid.SetColumn(rec, 1); g.Children.Add(rec);
        double kda = d.Deaths > 0 ? (double)(d.Kills + d.Assists) / d.Deaths : d.Kills + d.Assists;
        var cols = StatColumns(
            Stacked("K/D", N(d.Kd, "0.0"), KdColor(d.Kd)),
            Stacked(Loc.F("{0}K // {1}D // {2}A", d.Kills, d.Deaths, d.Assists), N(kda, "0.00") + " K/D/A", TgText),
            Stacked("DDΔ", Signed(d.DdPerRound), SignColor(d.DdPerRound)),
            Stacked("HS%", double.IsNaN(d.HeadshotPct) ? "–" : N(d.HeadshotPct), TgText),
            Stacked("ACS", N(d.Acs), TgText),
            null);
        Grid.SetColumn(cols, 2); g.Children.Add(cols);
        return g;
    }

    /// <summary>The little tags on a match row: clutches won and lost, multi-kills, high KAST.</summary>
    static List<(string Text, Color C)> Badges(VMatch m, string id)
    {
        var list = new List<(string, Color)>();
        foreach (var g in Stats.Clutches(m, id).GroupBy(c => (c.Vs, c.Won)).OrderByDescending(g => g.Key.Won).ThenByDescending(g => g.Key.Vs))
            list.Add(($"1v{g.Key.Vs} {(g.Key.Won ? Loc.T("Clutch") : Loc.T("Lost"))}" + (g.Count() > 1 ? $" x{g.Count()}" : ""), g.Key.Won ? TgYellow : TgLoss));
        var mk = Stats.MultiKills(m, id);
        if (mk[5] > 0) list.Insert(0, (Loc.T("Ace") + (mk[5] > 1 ? $" x{mk[5]}" : ""), TgYellow));
        if (mk[4] > 0) list.Add(("4k" + (mk[4] > 1 ? $" x{mk[4]}" : ""), TgYellow));
        if (mk[3] > 0) list.Add(("3k" + (mk[3] > 1 ? $" x{mk[3]}" : ""), TgYellow));
        var t = TotalsOf(m, id);
        if (t.Rounds >= 10 && t.KastPct >= 90) list.Add((Loc.T("High KAST"), TgYellow));
        return list;
    }

    static string Ordinal(int n) => n switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => n + "th" };

    FrameworkElement MatchRowGg(VMatch m, string id)
    {
        var me = m.Players.FirstOrDefault(x => x.Id == id);
        var team = m.TeamOf(id);
        int us = m.Rounds.Count(r => r.Winner == team), them = m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != team);
        int res = m.Outcome(team);
        bool won = res > 0, draw = res == 0;
        bool dm = m.Queue == "deathmatch";
        var c = draw || dm ? TgDim : won ? TgWin : TgLoss;
        var t = TotalsOf(m, id);
        var place = Stats.PlaceOf(m, id);
        double score = ScoreOf(m, id);

        var g = new Grid { Margin = new Thickness(12, 6, 6, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 130 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Put(int col, UIElement el) { Grid.SetColumn(el, col); g.Children.Add(el); }
        if (MatchArt.AgentIcon(me?.Agent) is { } ai) Put(0, new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = new ImageBrush(ai) { Stretch = Stretch.UniformToFill }, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = me?.Agent });
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(Txt(AgoShort(m.StartUtc) + " // " + Loc.T(QueueTitle(m.Queue)), 10.5, TgDim));
        var mapLine = new StackPanel { Orientation = Orientation.Horizontal };
        mapLine.Children.Add(Txt(m.Map, 15, TgText, true));
        if (place.Place > 0)
            mapLine.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                BorderBrush = B(place.MatchMvp ? TgYellow : place.TeamMvp ? TgSub : TgDim),
                Child = Txt(place.MatchMvp ? Loc.T("MVP") : place.TeamMvp ? Loc.T("Team MVP") : Ordinal(place.Place), 9.5, place.MatchMvp ? TgYellow : place.TeamMvp ? TgText : TgDim, true),
            });
        info.Children.Add(mapLine);
        Put(1, info);
        if (MatchArt.RankIcon(me?.Tier ?? 0) is { } ri) Put(2, Pic(ri, 28, 28, 0).Also(im => im.ToolTip = ValorantMatches.RankName(me.Tier)));
        var sc = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        sc.Children.Add(Txt(Loc.T(dm ? "Place" : "Score"), 10, TgDim, false, HorizontalAlignment.Center));
        var scLine = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        if (dm) scLine.Children.Add(Txt(place.Place > 0 ? Ordinal(place.Place) : "–", 15, TgText, true));
        else
        {
            scLine.Children.Add(Txt(us.ToString(CultureInfo.CurrentCulture), 15, c, true));
            scLine.Children.Add(Txt(" : ", 15, TgDim, true));
            scLine.Children.Add(Txt(them.ToString(CultureInfo.CurrentCulture), 15, TgSub, true));
        }
        sc.Children.Add(scLine);
        Put(3, sc);
        var trs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, ToolTip = Loc.T("Pulse Score: one match's overall impact (0–1000)") };
        trs.Children.Add(ScoreBadge(score, 24).Also(i => i.Margin = new Thickness(0, 0, 7, 0)));
        var trsT = new StackPanel();
        trsT.Children.Add(Txt(Loc.T("Pulse"), 10, TgDim));
        trsT.Children.Add(Txt(N(score), 15, TgText, true));
        trs.Children.Add(trsT);
        Put(4, trs);
        var badges = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
        foreach (var (text, bc) in BadgesOf(m, id).Take(4))
            badges.Children.Add(new Border { CornerRadius = new CornerRadius(3), BorderBrush = B(A(bc, 0xA0)), BorderThickness = new Thickness(1), Background = B(A(bc, 0x14)), Padding = new Thickness(5, 1, 5, 2), Margin = new Thickness(0, 2, 5, 2), Child = Txt(text, 10, bc, true) });
        Put(5, badges);
        var kda = me != null && me.Deaths > 0 ? (double)(me.Kills + me.Assists) / me.Deaths : (me?.Kills ?? 0) + (me?.Assists ?? 0);
        var dots = Txt("⋮", 16, TgDim, true, HorizontalAlignment.Center);
        Put(6, StatColumns(
            Stacked("K/D", N(t.KdRatio, "0.0"), KdColor(t.KdRatio), 15),
            Stacked(N(kda, "0.00") + " " + "K/D/A", $"{t.Kills} / {t.Deaths} / {t.Assists}", TgText, 15),
            Stacked("DDΔ", Signed(t.DamageDelta), SignColor(t.DamageDelta), 15),
            Stacked("HS%", double.IsNaN(t.HeadshotPct) ? "–" : N(t.HeadshotPct), TgText, 15),
            Stacked("ACS", N(t.Acs), TgText, 15),
            dots).Also(x => x.VerticalAlignment = VerticalAlignment.Center));

        var row = new Border { Child = g, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand, ToolTip = Loc.T("Open the match") };
        var normal = won && !dm ? Color.FromRgb(0x1C, 0x33, 0x33) : TgCard;
        var hover = won && !dm ? Color.FromRgb(0x22, 0x3E, 0x3E) : TgHead;
        Hover(row, normal, hover);
        var host = new Grid();
        host.Children.Add(row);
        host.Children.Add(new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Background = B(c), CornerRadius = new CornerRadius(4, 0, 0, 4), Margin = new Thickness(0, 0, 0, 6), IsHitTestVisible = false });
        row.MouseLeftButtonUp += (_, _) => OpenMatch(m.Id);
        return host;
    }

    // ───────── performance over time ─────────

    FrameworkElement PerformanceTrends(Profile p, List<VMatch> ms)
    {
        var id = p.Puuid;
        var list = ms.Where(m => m.Queue != "deathmatch").Take(40).Reverse().ToList();
        var sp = new StackPanel();
        sp.Children.Add(TgHeading(Loc.T("Performance"), Loc.F("your last {0} matches, oldest to newest", list.Count)));
        if (list.Count < 2)
        {
            sp.Children.Add(TgBox(Txt(Loc.T("Needs two or more matches."), 12.5, TgDim).Also(t => t.Margin = new Thickness(14))));
            return sp;
        }
        var totals = list.Select(m => TotalsOf(m, id)).ToList();
        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, -12, 0) };
        void Chart(string title, IEnumerable<double> values, string fmt, Color c, string tip)
        {
            var v = values.ToList();
            grid.Children.Add(TrendChart(title, v, fmt, c, tip).Also(x => x.Margin = new Thickness(0, 0, 12, 12)));
        }
        Chart("Pulse score", list.Select(m => ScoreOf(m, id)), "0", TgYellow, "One match's overall impact (0–1000). 500 is average.");
        Chart("K/D Ratio", totals.Select(t => t.KdRatio), "0.00", TgWin, "Kills per death in each match.");
        Chart("ACS", totals.Select(t => t.Acs), "0", Color.FromRgb(0x4D, 0xA3, 0xFF), "Average combat score in each match.");
        Chart("Damage/Round", totals.Select(t => t.Adr), "0", Color.FromRgb(0xC9, 0xA2, 0xFF), "Damage per round in each match.");
        Chart("Headshot %", totals.Select(t => t.HeadshotPct), "0", TgRed, "Share of your hits that landed on the head.");
        Chart("KAST", totals.Select(t => t.KastPct), "0", Color.FromRgb(0x59, 0xD4, 0xE8), "Rounds with a kill, assist, survival or trade.");
        var won = list.Select(m => WonFor(m, id) ? 100.0 : 0).ToList();
        Chart("Win % (last 5)", won.Select((_, i) => won.Skip(Math.Max(0, i - 4)).Take(Math.Min(5, i + 1)).Average()), "0", TgWin, "Share of the last five matches won, at each point.");
        if (p.IsYou && _trRr is { Count: >= 2 })
        {
            double sum = 0;
            var rr = _trRr.AsEnumerable().Reverse().Select(x => sum += x).ToList();
            Chart("RR gained", rr, "0", TgYellow, "Your RR gains and losses added up, from Riot's competitive record.");
        }
        sp.Children.Add(grid);
        return sp;
    }

    FrameworkElement TrendChart(string title, List<double> values, string fmt, Color c, string tip)
    {
        var known = values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToList();
        var sp = new StackPanel { Margin = new Thickness(14, 10, 14, 12), ToolTip = Loc.T(tip) };
        var head = new Grid();
        head.Children.Add(Txt(Loc.T(title).ToUpper(CultureInfo.CurrentCulture), 11.5, TgDim, true));
        if (known.Count > 0)
        {
            var avg = known.Average();
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(Txt(Loc.T("avg") + " ", 11, TgDim));
            right.Children.Add(Txt(avg.ToString(fmt, CultureInfo.CurrentCulture), 13, TgText, true));
            right.Children.Add(Txt("   " + Loc.T("last") + " ", 11, TgDim));
            right.Children.Add(Txt(known[^1].ToString(fmt, CultureInfo.CurrentCulture), 13, known[^1] >= avg ? TgWin : TgLoss, true));
            head.Children.Add(right);
        }
        sp.Children.Add(head);
        const double h = 90;
        var canvas = new Grid { Height = h, Margin = new Thickness(0, 10, 0, 0) };
        if (known.Count >= 2)
        {
            double mn = known.Min(), mx = known.Max();
            if (mx - mn < 1e-6) { mx += 1; mn -= 1; }
            double avg = known.Average();
            var pts = new PointCollection();
            int n = values.Count;
            for (int i = 0; i < n; i++)
                if (!double.IsNaN(values[i]) && !double.IsInfinity(values[i])) pts.Add(new Point(i * 1000.0 / (n - 1), h - (values[i] - mn) / (mx - mn) * h));
            var fill = new PointCollection(pts) { new Point(pts[^1].X, h), new Point(pts[0].X, h) };
            canvas.Children.Add(new Polygon { Points = fill, Stretch = Stretch.Fill, Fill = new LinearGradientBrush(A(c, 0x40), A(c, 0x02), 90) });
            canvas.Children.Add(new Polyline { Points = pts, Stretch = Stretch.Fill, Stroke = B(c), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round });
            var y = h - (avg - mn) / (mx - mn) * h;
            canvas.Children.Add(new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, y, 0, 0), Background = B(A(TgSub, 0x55)) });
            canvas.Children.Add(Txt(mx.ToString(fmt, CultureInfo.CurrentCulture), 9.5, TgDim).Also(t => { t.VerticalAlignment = VerticalAlignment.Top; t.HorizontalAlignment = HorizontalAlignment.Right; t.Margin = new Thickness(0, -12, 0, 0); }));
            canvas.Children.Add(Txt(mn.ToString(fmt, CultureInfo.CurrentCulture), 9.5, TgDim).Also(t => { t.VerticalAlignment = VerticalAlignment.Bottom; t.HorizontalAlignment = HorizontalAlignment.Right; t.Margin = new Thickness(0, 0, 0, -12); }));
        }
        else canvas.Children.Add(Txt(Loc.T("Not enough data yet"), 12, TgDim));
        sp.Children.Add(canvas);
        return TgBox(sp, 0, 0);
    }

    // ───────── maps, weapons, encounters ─────────

    FrameworkElement MapsFullGg(List<VMatch> ms, string id)
    {
        var list = Memo("maps:" + id + ":" + ms.Count + ":" + (ms.Count > 0 ? ms[0].Id : ""), () => Stats.MapDetails(ms, id));
        var box = new StackPanel();
        var head = new Grid { Margin = new Thickness(14, 9, 14, 7) };
        head.Children.Add(Txt(Loc.T("Map"), 11, TgDim));
        head.Children.Add(StatColumnsMaps(Txt(Loc.T("Matches"), 11, TgDim), Txt(Loc.T("Win %"), 11, TgDim), Txt(Loc.T("W - L"), 11, TgDim), Txt(Loc.T("K/D"), 11, TgDim),
            Txt(Loc.T("ADR"), 11, TgDim), Txt(Loc.T("ACS"), 11, TgDim), Txt(Loc.T("Attack"), 11, TgDim), Txt(Loc.T("Defense"), 11, TgDim)));
        box.Children.Add(head);
        foreach (var m in list)
        {
            var t = m.Totals;
            var row = new Grid { Height = 56, ClipToBounds = true };
            if (MatchArt.MapPicture(m.Map) is { } pic) row.Children.Add(new Border { Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill, Opacity = 0.35 } });
            row.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(0xF0, TgPanel.R, TgPanel.G, TgPanel.B), Color.FromArgb(0x90, TgPanel.R, TgPanel.G, TgPanel.B), 0) });
            row.Children.Add(Txt(m.Map, 16, TgText, true).Also(x => x.Margin = new Thickness(14, 0, 0, 0)));
            row.Children.Add(StatColumnsMaps(
                Txt(m.Matches.ToString(CultureInfo.CurrentCulture), 14, TgText, true),
                Txt(N(m.WinPct, "0.0") + "%", 14, m.WinPct >= 50 ? TgYellow : TgLoss, true),
                Txt($"{m.Wins} - {m.Losses}", 13, TgSub),
                Txt(N(t.KdRatio, "0.00"), 14, KdColor(t.KdRatio), true),
                Txt(N(t.Adr, "0"), 14, TgText, true),
                Txt(N(t.Acs, "0"), 14, TgText, true),
                Txt(double.IsNaN(m.AtkPct) ? "–" : N(m.AtkPct) + "%", 13, m.AtkPct >= 50 ? TgWin : TgSub, true).Also(x => x.ToolTip = Loc.F("{0} of {1} attack rounds won", m.AtkWon, m.AtkPlayed)),
                Txt(double.IsNaN(m.DefPct) ? "–" : N(m.DefPct) + "%", 13, m.DefPct >= 50 ? TgWin : TgSub, true).Also(x => x.ToolTip = Loc.F("{0} of {1} defense rounds won", m.DefWon, m.DefPlayed))).Also(x => x.Margin = new Thickness(0, 0, 14, 0)));
            box.Children.Add(new Border { BorderBrush = B(TgLine), BorderThickness = new Thickness(0, 1, 0, 0), Child = row });
        }
        box.Children.Add(Txt(Loc.T("Attack and Defense: rounds won on each side."), 11, TgDim).Also(t => t.Margin = new Thickness(14, 8, 14, 10)));
        return TgBox(box);
    }

    static Grid StatColumnsMaps(params UIElement[] cells)
    {
        var g = new Grid { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var w in new double[] { 70, 70, 70, 60, 60, 60, 70, 70 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        for (int i = 0; i < cells.Length; i++) { if (cells[i] is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(cells[i], i); g.Children.Add(cells[i]); }
        return g;
    }

    FrameworkElement WeaponsFullGg(List<VMatch> ms, string id)
    {
        var list = Panels.Weapons(ms, id);
        var box = new StackPanel();
        foreach (var w in list) box.Children.Add(WeaponRow(w, false));
        if (list.Count == 0) box.Children.Add(Txt(Loc.T("No weapon kills yet."), 12, TgDim).Also(t => t.Margin = new Thickness(14)));
        box.Children.Add(Txt(Loc.T("Riot's match data says which weapon made each kill but not where each shot landed, so accuracy is for all weapons together."), 11, TgDim)
            .Also(t => { t.Margin = new Thickness(14, 8, 14, 10); t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; }));
        return TgBox(box).Also(b => b.Margin = new Thickness(0, 0, 14, 12));
    }

    FrameworkElement EncountersGg(Profile p)
    {
        var pool = ProfileMatches(p).Where(m => VMatch.Counts(m.Queue)).ToList();
        var list = Memo("enc:" + p.Puuid + ":" + pool.Count, () => Stats.Encounters(pool, p.Puuid));
        var sp = new StackPanel();
        sp.Children.Add(TgHeading(Loc.T("Encounters"), Loc.F("players met in {0} matches", pool.Count)));
        FrameworkElement EncTable(string title, bool mates)
        {
            var rows = list.Where(e => mates ? e.With > 0 : e.Against > 0).OrderByDescending(e => mates ? e.With : e.Against).ThenByDescending(e => e.Last).Take(25).ToList();
            var box = new StackPanel();
            var head = new Grid { Margin = new Thickness(14, 10, 14, 6) };
            head.Children.Add(Txt(Loc.T(title).ToUpper(CultureInfo.CurrentCulture), 12, TgSub, true));
            head.Children.Add(Txt(Loc.T(mates ? "Games together · won" : "Games against · you won"), 11, TgDim, false, HorizontalAlignment.Right));
            box.Children.Add(head);
            foreach (var e in rows)
            {
                var g = new Grid { Margin = new Thickness(14, 6, 14, 6) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var face = new StackPanel { Orientation = Orientation.Horizontal };
                if (MatchArt.AgentIcon(e.Agent) is { } ai) face.Children.Add(new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(4), ClipToBounds = true, Margin = new Thickness(0, 0, 8, 0), Background = new ImageBrush(ai) { Stretch = Stretch.UniformToFill } });
                if (MatchArt.RankIcon(e.Tier) is { } ri) face.Children.Add(Pic(ri, 22, 22, 8).Also(i => i.ToolTip = ValorantMatches.RankName(e.Tier)));
                g.Children.Add(face);
                var name = e.Name ?? NameBook.Get(e.Id);
                var nm = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                nm.Children.Add(Txt(name ?? (e.Agent.Length > 0 ? Loc.F("{0} player", e.Agent) : Loc.T("Unknown player")), 13, TgText, true));
                nm.Children.Add(Txt(Loc.F("last met {0}", AgoShort(e.Last)) + (e.Tier >= 3 ? " · " + ValorantMatches.RankName(e.Tier) : ""), 10.5, TgDim));
                Grid.SetColumn(nm, 1); g.Children.Add(nm);
                int n = mates ? e.With : e.Against, w = mates ? e.WithWins : e.AgainstWins;
                var rec = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                rec.Children.Add(Txt(Loc.F("{0} games", n), 13, TgText, true, HorizontalAlignment.Right));
                rec.Children.Add(Txt($"{w}W - {n - w}L ({100.0 * w / n:0}%)", 10.5, w * 2 >= n ? TgWin : TgLoss, false, HorizontalAlignment.Right));
                Grid.SetColumn(rec, 2); g.Children.Add(rec);
                var row = new Border { BorderBrush = B(TgLine), BorderThickness = new Thickness(0, 1, 0, 0), Child = g, Cursor = Cursors.Hand, ToolTip = Loc.T("Open their profile") };
                Hover(row, TgPanel, TgCard);
                var pid = e.Id;
                row.MouseLeftButtonUp += (_, _) => OpenPlayer(pid);
                box.Children.Add(row);
            }
            if (rows.Count == 0) box.Children.Add(Txt(Loc.T("Nobody yet."), 12, TgDim).Also(t => t.Margin = new Thickness(14, 4, 14, 12)));
            return TgBox(box).Also(b => b.Margin = new Thickness(0, 0, 12, 12));
        }
        sp.Children.Add(PanelRow((EncTable("Teammates", true), 0), (EncTable("Opponents", false), 0)));
        sp.Children.Add(Faint(Loc.T("From the matches saved on this PC. Names show when Riot shared them with the match."), 11.5, 0));
        return sp;
    }

    /// <summary>Pulse's own cards for you: who you're up against, MMR, how you play each agent, the tier list.</summary>
    IEnumerable<FrameworkElement> InsightCards(Profile p, Tracker.MmrEstimate mmr)
    {
        var mine = CoachMatches();
        var opp = Memo("against:" + mine.Count, () => Tracker.Against(mine));
        yield return opp != null ? OppositionCard(opp) : Card2(Waiting("Who you're up against", "Shows the ranks of your enemies and teammates once you have a match saved where most players are ranked."), Red);
        yield return MmrCard(mmr, p);
        var all = VStore.All;
        var yours = Memo("youragents:" + mine.Count, () => Tracker.YourAgents(mine, all));
        yield return yours.Count > 0 ? YourAgentsCard(yours, all, p.Puuid) : Card2(Waiting("How you play your agents", "Play an agent in two matches to see how you compare with everyone else on it."), Blue);
        var tiers = Memo("tiers", () => Tracker.TierList(all));
        int counted = all.Count(m => VMatch.Counts(m.Queue));
        yield return tiers.Count >= 4 ? TierListCard(tiers, counted) : Card2(Waiting("Agent tier list in your lobbies", Loc.F("Needs about 8 games per agent across your saved matches. You have {0} matches so far.", counted)), Yellow);
    }
}
