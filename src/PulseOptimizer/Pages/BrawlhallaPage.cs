using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pulse.Core;
using Pulse.Core.Games.Brawlhalla;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// A Brawlhalla tracker, from Brawlhalla's official public API: find a player by name (ranked players) or Brawlhalla
/// ID; a profile with Overview (stats, smurf factor, playstyle, standout legends, latest matches), Legends (a card for
/// every legend), Weapons, Ranked (1v1, every 2v2 team, 3v3, ranked legends) and Matches (logged by Pulse) tabs; and
/// the ranked leaderboards by region.
/// </summary>
public sealed partial class BrawlhallaPage : UserControl, IPage
{
    static BrawlhallaSettings S => TweakEngine.State.Brawlhalla ??= new BrawlhallaSettings();

    readonly StackPanel _root = new() { Margin = new Thickness(0, 6, 0, 40) };
    BhProfile _profile;
    Dictionary<int, BhLegend> _legends = new();
    List<BhRanking> _results;
    (List<BhRanking> Rows, int Pages) _board;
    string _boardMode = "1v1", _boardRegion = "ALL";
    int _boardPage = 1;
    string _status, _sort = "games", _tab = "overview";
    int _openLegend, _legendLimit = 10;
    string _weaponFilter;
    List<BhMatch> _matches = new();
    bool _busy, _shown;
    CancellationTokenSource _cts;

    // Brawlhalla's gold, and the colours the page uses for damage and KO sources.
    static readonly Color Gold = Color.FromRgb(0xF5, 0xB9, 0x42), Green = Color.FromRgb(0x3D, 0xDC, 0x84), Red = Color.FromRgb(0xFF, 0x5A, 0x4E),
        Blue = Color.FromRgb(0x4D, 0xA3, 0xFF), Purple = Color.FromRgb(0xA7, 0x8B, 0xFA), Teal = Color.FromRgb(0x4F, 0xD1, 0xC5), Grey = Color.FromRgb(0x8A, 0x8F, 0x98);
    static readonly Color PanelBg = Color.FromRgb(0x17, 0x18, 0x1C), Inset = Color.FromRgb(0x1E, 0x20, 0x25), Line = Color.FromRgb(0x2A, 0x2C, 0x33);

    public BrawlhallaPage()
    {
        Content = _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _root };
        Build();
        // A match Pulse just logged for the player on screen: show it.
        Pulse.Ui.BrawlhallaWatch.Logged += id =>
        {
            if (_profile?.Id != id || _busy) return;
            _matches = BrawlhallaMatches.For(id);
            if (_tab is "matches" or "overview") Build();
        };
    }

    readonly ScrollViewer _scroll;

    /// <summary>"matches" (from the after-match popup): your matches.</summary>
    public void OnShown(object parameter)
    {
        bool matches = parameter as string == "matches" && S.MyId > 0;
        if (!_shown)
        {
            _shown = true;
            _ = Start(matches);
            return;
        }
        if (!matches) return;
        if (_profile?.Id == S.MyId) { _matches = BrawlhallaMatches.For(S.MyId); _tab = "matches"; Build(); }
        else _ = Open(S.MyId, "matches");
    }

    async Task Start(bool matches = false)
    {
        try { _legends = await Task.Run(() => Brawlhalla.Legends()); } catch { }
        if (S.MyId > 0) await Open(S.MyId, matches ? "matches" : "overview");
        else await LoadBoard();
    }

    // ───────── the page ─────────

    void Build()
    {
        _root.Children.Clear();
        _root.Children.Add(new TextBlock { Style = (Style)FindResource("H1"), Text = "Brawlhalla" });
        _root.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Sub"), Margin = new Thickness(0, 6, 0, 0), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left,
            Text = Loc.T("Ranked, legends and weapons for any player, and the ranked leaderboards, from Brawlhalla's official stats."),
        });
        _root.Children.Add(Card(SearchPanel()));
        if (_results is { Count: > 0 }) _root.Children.Add(Card(ResultsPanel()));
        if (_profile == null) { _root.Children.Add(Card(BoardPanel())); return; }

        _root.Children.Add(Header(_profile));
        _root.Children.Add(Tabs());
        var parts = _tab switch
        {
            "legends" => LegendCards(_profile),
            "matches" => MatchesTab(_profile),
            "weapons" => WeaponsTab(_profile),
            "ranked" => RankedTab(_profile),
            _ => OverviewTab(_profile),
        };
        foreach (var p in parts) _root.Children.Add(p);
    }

    FrameworkElement SearchPanel()
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Find a player"));
        var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { MaxWidth = 420 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new TextBox { Style = (Style)FindResource("SearchBox"), Tag = Loc.T("Name or Brawlhalla ID"), FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center };
        row.Children.Add(box);
        var go = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T("Search"), Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(16, 6, 16, 6), IsEnabled = !_busy };
        Grid.SetColumn(go, 1);
        row.Children.Add(go);
        sp.Children.Add(row);
        go.Click += (_, _) => _ = Search(box.Text);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = Search(box.Text); } };

        if (_status != null) sp.Children.Add(new TextBlock { Text = _status, FontSize = 12, Foreground = Res(_busy ? "TextDimBrush" : "BadBrush"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock
        {
            Text = Loc.T("Names search the ranked leaderboards. For someone who doesn't play ranked, use their Brawlhalla ID (in game: Inventory, Profile)."),
            FontSize = 11.5, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
        });

        var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        if (S.MyId > 0) chips.Children.Add(Chip(Loc.T("You") + (S.MyName is { Length: > 0 } my ? " · " + my : ""), _profile?.Id == S.MyId, () => _ = Open(S.MyId)));
        foreach (var r in S.Recent.Where(r => r.Id != S.MyId).Take(6))
            chips.Children.Add(Chip(r.Name.Length > 0 ? r.Name : r.Id.ToString(CultureInfo.InvariantCulture), _profile?.Id == r.Id, () => _ = Open(r.Id)));
        if (_profile != null) chips.Children.Add(Chip(Loc.T("Leaderboards"), false, () => { _profile = null; _results = null; Build(); if (_board.Rows == null) _ = LoadBoard(); }));
        if (chips.Children.Count > 0) sp.Children.Add(chips);
        return sp;
    }

    FrameworkElement ResultsPanel()
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Players found"));
        sp.Children.Add(BoardHeader());
        foreach (var r in _results) sp.Children.Add(RankRow(r, showRank: true, oneVsOne: false));
        return sp;
    }

    // ───────── leaderboards ─────────

    FrameworkElement BoardPanel()
    {
        var sp = new StackPanel();
        var head = new Grid();
        head.Children.Add(Heading("Ranked leaderboards"));
        if (_board.Pages > 0)
            head.Children.Add(new TextBlock { Text = Loc.F("Page {0} of {1}", _boardPage, _board.Pages.ToString("N0", CultureInfo.CurrentCulture)), FontSize = 12, Foreground = Res("TextFaintBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(head);
        var modes = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        modes.Children.Add(Segments(new[] { "1v1", "2v2", "3v3" }.Select(m => (m, _boardMode == m, (Action)(() => { _boardMode = m; _boardPage = 1; _ = LoadBoard(); })))));
        sp.Children.Add(modes);
        var regions = new WrapPanel { Margin = new Thickness(0, 2, 0, 10) };
        foreach (var r in Brawlhalla.Regions)
            regions.Children.Add(Chip(r == "ALL" ? Loc.T("Global") : r, _boardRegion == r, () => { _boardRegion = r; _boardPage = 1; _ = LoadBoard(); }));
        sp.Children.Add(regions);
        if (_board.Rows == null) { sp.Children.Add(Dim(_busy ? Loc.T("Loading…") : Loc.T("Couldn't load the leaderboard."), 12.5, 6)); return sp; }
        sp.Children.Add(BoardHeader());
        foreach (var r in _board.Rows) sp.Children.Add(RankRow(r, showRank: true, oneVsOne: _boardMode == "1v1"));
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        if (_boardPage > 1) nav.Children.Add(Ghost("← " + Loc.T("Previous"), () => { _boardPage--; _ = LoadBoard(); }));
        if (_boardPage < _board.Pages) nav.Children.Add(Ghost(Loc.T("Next") + " →", () => { _boardPage++; _ = LoadBoard(); }));
        sp.Children.Add(nav);
        return sp;
    }

    static readonly double[] BoardCols = { 64, double.NaN, 140, 90, 90, 130, 70 };

    FrameworkElement BoardHeader() => GridRow(BoardCols, true, "Rank", "Player", "Tier", "Rating", "Peak", "Record", "Region");

    FrameworkElement RankRow(BhRanking r, bool showRank, bool oneVsOne)
    {
        int games = r.Wins + r.Losses;
        var row = GridRow(BoardCols, false,
            showRank ? "#" + r.Rank.ToString("N0", CultureInfo.CurrentCulture) : "", r.Name, null, r.Rating.ToString("N0", CultureInfo.CurrentCulture),
            r.BestRating.ToString("N0", CultureInfo.CurrentCulture), games > 0 ? $"{r.Wins}–{r.Losses} · {100.0 * r.Wins / games:0}%" : "", r.Region);
        PutIn(row, 2, TierPill(r.Tier.Length > 0 ? r.Tier : Brawlhalla.TierOf(r.Rating)));
        // Teams (2v2) open the first player.
        // From the 1v1 board, its tier (Valhallan) carries over: the profile can't always look it up (names with accents).
        return Clickable(row, r.Id > 0 ? () => _ = Open(r.Id, tier1v1: oneVsOne ? r.Tier : null) : null);
    }

    // ───────── a player: header and tabs ─────────

    FrameworkElement Header(BhProfile p)
    {
        var main = p.Legends.Where(l => l.Games > 0).OrderByDescending(l => l.Games).FirstOrDefault();
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Their main legend's portrait, in a gold rim.
        string mainName = main != null ? LegendName(main.LegendId) : "";
        g.Children.Add(Portrait(mainName, 84, 18, 2).Also(b => { b.Margin = new Thickness(0, 0, 20, 0); b.VerticalAlignment = VerticalAlignment.Top; }));

        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(mid, 1);
        var nameRow = new WrapPanel();
        nameRow.Children.Add(new TextBlock { Text = p.Name.Length > 0 ? p.Name : Loc.T("Unknown player"), FontSize = 24, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        if (S.MyId == p.Id) nameRow.Children.Add(Badge(Loc.T("You"), Gold));
        if (p.OneVsOne?.Tier is { Length: > 0 } t1) nameRow.Children.Add(TierPill(t1).Also(x => x.Margin = new Thickness(0, 0, 6, 0)));
        if (S.MyId != p.Id && BrawlhallaMatches.Smurf(p) is { Score: >= 35 } sm)
            nameRow.Children.Add(Badge(Loc.T(sm.Label), sm.Score >= 60 ? Red : Gold));
        mid.Children.Add(nameRow);
        var facts = new List<string>();
        if (main != null) facts.Add(Loc.F("Mostly {0}", LegendName(main.LegendId)));
        if (p.Guild.Length > 0) facts.Add(Loc.F("Clan {0}", p.Guild) + (p.GuildRank.Length > 0 ? $" ({p.GuildRank})" : ""));
        facts.Add(Loc.F("ID {0}", p.Id));
        mid.Children.Add(Dim(string.Join(" · ", facts), 12.5, 3));
        if (p.Level > 0)
        {
            // Level, with how far into the next one.
            var lv = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            lv.Children.Add(new TextBlock { Text = Loc.F("Level {0}", p.Level), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            lv.Children.Add(Bar(Math.Clamp(p.XpPercent, 0, 1), Gold, 160).Also(b => b.VerticalAlignment = VerticalAlignment.Center));
            mid.Children.Add(lv);
        }
        var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        long time = p.Legends.Sum(l => l.MatchTime);
        chips.Children.Add(Stat("Games", p.Games.ToString("N0", CultureInfo.CurrentCulture)));
        chips.Children.Add(Stat("Wins", p.Wins.ToString("N0", CultureInfo.CurrentCulture)));
        chips.Children.Add(Stat("Win %", p.Games > 0 ? $"{100.0 * p.Wins / p.Games:0.0}%" : "–"));
        chips.Children.Add(Stat("Playtime", Hours(time)));
        chips.Children.Add(Stat("Legends", p.Legends.Count(l => l.Games > 0).ToString(CultureInfo.CurrentCulture)));
        mid.Children.Add(chips);
        g.Children.Add(mid);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 0, 0, 0) };
        Grid.SetColumn(right, 2);
        if (p.OneVsOne is { } one)
        {
            right.Children.Add(Caps("Ranked 1v1", 10, Res("TextFaintBrush")).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right));
            right.Children.Add(new TextBlock { Text = one.Rating.ToString("N0", CultureInfo.CurrentCulture), FontSize = 30, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(TierColor(one.Tier)), HorizontalAlignment = HorizontalAlignment.Right });
            right.Children.Add(new TextBlock { Text = Loc.F("Peak {0}", one.PeakRating.ToString("N0", CultureInfo.CurrentCulture)), FontSize = 12, Foreground = Res("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Right });
        }
        var me = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right,
            Content = S.MyId == p.Id ? Loc.T("This is you") : Loc.T("This is me"), IsEnabled = S.MyId != p.Id };
        me.Click += (_, _) => { S.MyId = p.Id; S.MyName = p.Name; TweakEngine.Save(); Build(); };
        right.Children.Add(me);
        g.Children.Add(right);

        // A banner: their main legend, large and faded, on the right; a gold wash from the top left.
        var layers = new Grid();
        if (BrawlhallaArt.Legend(mainName) is { } art)
            // As a background, so it fills the header without making it taller.
            layers.Children.Add(new Border
            {
                Width = 360, HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0.2,
                Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top },
                OpacityMask = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0xFF, 0, 0, 0), 0),
            });
        layers.Children.Add(new Border { Background = new RadialGradientBrush(A(Gold, 0x1E), A(Gold, 0x00)) { Center = new Point(0, 0), GradientOrigin = new Point(0, 0), RadiusX = 0.6, RadiusY = 1.3 } });
        layers.Children.Add(new Border { Child = g, Padding = new Thickness(24, 22, 24, 22) });
        layers.SizeChanged += (_, e) => layers.Clip = new RectangleGeometry(new Rect(e.NewSize), 13, 13);
        return new Border
        {
            Child = layers, Background = new SolidColorBrush(PanelBg), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 14, 0, 0), ClipToBounds = true,
            BorderBrush = new SolidColorBrush(A(Gold, 0x55)), BorderThickness = new Thickness(1),
        };
    }

    FrameworkElement Tabs()
    {
        var w = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        w.Children.Add(Segments(new[] { ("overview", "Overview"), ("legends", "Legends"), ("weapons", "Weapons"), ("ranked", "Ranked"), ("matches", "Matches") }
            .Select(t => (Loc.T(t.Item2) + (t.Item1 == "matches" && _matches.Count > 0 ? $" {_matches.Sum(m => m.Games)}" : ""), _tab == t.Item1, (Action)(() => { _tab = t.Item1; Build(); _scroll.ScrollToTop(); })))));
        return w;
    }

    // ───────── Overview ─────────

    IEnumerable<FrameworkElement> OverviewTab(BhProfile p)
    {
        var played = p.Legends.Where(l => l.Games > 0).ToList();
        long damage = played.Sum(l => l.DamageDealt), taken = played.Sum(l => l.DamageTaken), time = played.Sum(l => l.MatchTime);
        int kos = played.Sum(l => l.Kos), falls = played.Sum(l => l.Falls), suicides = played.Sum(l => l.Suicides), teamKos = played.Sum(l => l.TeamKos);
        int legendGames = Math.Max(1, played.Sum(l => l.Games));

        // The numbers.
        var tiles = new WrapPanel();
        tiles.Children.Add(Tile("KOs", kos.ToString("N0", CultureInfo.CurrentCulture), Loc.F("{0:0.00} a game", kos / (double)legendGames)));
        tiles.Children.Add(Tile("Falls", falls.ToString("N0", CultureInfo.CurrentCulture), Loc.F("{0:0.00} a game", falls / (double)legendGames)));
        tiles.Children.Add(Tile("KO / fall", falls > 0 ? (kos / (double)falls).ToString("0.00", CultureInfo.CurrentCulture) : "–", Loc.T("KOs for each fall"), falls > 0 ? (kos >= falls ? Green : Red) : null));
        tiles.Children.Add(Tile("Damage per game", (damage / (double)legendGames).ToString("N0", CultureInfo.CurrentCulture), Loc.F("{0} dealt in all", Short(damage))));
        tiles.Children.Add(Tile("Damage ratio", taken > 0 ? (damage / (double)taken).ToString("0.00", CultureInfo.CurrentCulture) : "–", Loc.T("dealt for each 1 taken"), taken > 0 ? (damage >= taken ? Green : Red) : null));
        tiles.Children.Add(Tile("Average game", Clock(time / legendGames), Loc.F("{0} played", Hours(time))));
        tiles.Children.Add(Tile("Self-KOs", suicides.ToString("N0", CultureInfo.CurrentCulture), Loc.F("{0:0.0}% of falls", falls > 0 ? 100.0 * suicides / falls : 0)));
        if (teamKos > 0) tiles.Children.Add(Tile("Team KOs", teamKos.ToString("N0", CultureInfo.CurrentCulture), Loc.T("on teammates")));
        yield return Card(Section("Overall", tiles));

        // Their latest matches Pulse logged.
        if (_matches.Count > 0) yield return Card(RecentMatches(5));

        // Standouts.
        var standouts = new WrapPanel();
        var most = played.OrderByDescending(l => l.Games).FirstOrDefault();
        if (most != null) standouts.Children.Add(Standout("Most played", LegendName(most.LegendId), Loc.F("{0} games · {1:0}% of all", most.Games.ToString("N0", CultureInfo.CurrentCulture), 100.0 * most.Games / legendGames), Gold, most.LegendId));
        int floor = Math.Max(20, legendGames / 100);
        var best = played.Where(l => l.Games >= floor).OrderByDescending(l => l.Wins / (double)l.Games).FirstOrDefault();
        if (best != null) standouts.Children.Add(Standout("Best win rate", LegendName(best.LegendId), Loc.F("{0:0.0}% in {1} games", 100.0 * best.Wins / best.Games, best.Games), Green, best.LegendId));
        var deadly = played.Where(l => l.Games >= floor).OrderByDescending(l => l.Kos / (double)l.Games).FirstOrDefault();
        if (deadly != null) standouts.Children.Add(Standout("Most KOs a game", LegendName(deadly.LegendId), Loc.F("{0:0.00} KOs a game", deadly.Kos / (double)deadly.Games), Red, deadly.LegendId));
        var ranked = played.Where(l => l.Rating > 0).OrderByDescending(l => l.Rating).FirstOrDefault();
        if (ranked != null)
        {
            var rt = ranked.Tier.Length > 0 ? ranked.Tier : Brawlhalla.TierOf(ranked.Rating);
            standouts.Children.Add(Standout("Best ranked legend", LegendName(ranked.LegendId), Loc.F("{0} rating · {1}", ranked.Rating, rt), TierColor(rt), ranked.LegendId));
        }
        if (Weapons(p).OrderByDescending(w => w.Time).FirstOrDefault() is { Time: > 0 } fav)
            standouts.Children.Add(Standout("Favourite weapon", fav.Name, Loc.F("held {0} · {1} KOs", Hours(fav.Time), fav.Kos.ToString("N0", CultureInfo.CurrentCulture)), Blue, 0));
        if (standouts.Children.Count > 0) yield return Card(Section("Standouts", standouts));

        if (S.MyId != p.Id) yield return Card(SmurfPanel(p));

        // Where damage and KOs come from.
        long dWeapons = played.Sum(l => l.DamageWeaponOne + l.DamageWeaponTwo), dUnarmed = played.Sum(l => l.DamageUnarmed), dThrown = played.Sum(l => l.DamageThrown), dGadgets = played.Sum(l => l.DamageGadgets);
        int kWeapons = played.Sum(l => l.KoWeaponOne + l.KoWeaponTwo), kUnarmed = played.Sum(l => l.KoUnarmed), kGadgets = played.Sum(l => l.KoGadgets);
        var style = new StackPanel();
        style.Children.Add(Dim(Loc.T("Where their damage and KOs come from, across every legend."), 12.5, 0));
        style.Children.Add(Split("Damage", new[] { ("Weapons", (double)dWeapons, Gold), ("Unarmed", dUnarmed, Blue), ("Thrown weapons", dThrown, Purple), ("Gadgets", dGadgets, Teal) }));
        style.Children.Add(Split("KOs", new[] { ("Weapons", (double)kWeapons, Gold), ("Unarmed", kUnarmed, Blue), ("Gadgets", kGadgets, Teal), ("Other", Math.Max(0, kos - kWeapons - kUnarmed - kGadgets), Grey) }));
        yield return Card(Section("Playstyle", style));

        // Top legends.
        var top = new StackPanel();
        top.Children.Add(LegendHeader());
        foreach (var l in played.OrderByDescending(l => l.Games).Take(6)) top.Children.Add(LegendRow(l, legendGames));
        top.Children.Add(Ghost(Loc.T("All legends") + " →", () => { _tab = "legends"; Build(); }).Also(b => b.Margin = new Thickness(0, 10, 0, 0)));
        yield return Card(Section("Top legends", top));

        // Gadgets.
        var gadgets = new WrapPanel();
        gadgets.Children.Add(Tile("Bombs", Loc.F("{0} KOs", p.KoBomb), Loc.F("{0} damage", Short(p.DamageBomb))));
        gadgets.Children.Add(Tile("Mines", Loc.F("{0} KOs", p.KoMine), Loc.F("{0} damage", Short(p.DamageMine))));
        gadgets.Children.Add(Tile("Spike balls", Loc.F("{0} KOs", p.KoSpikeball), Loc.F("{0} damage", Short(p.DamageSpikeball))));
        gadgets.Children.Add(Tile("Sidekicks", Loc.F("{0} KOs", p.KoSidekick), Loc.F("{0} damage", Short(p.DamageSidekick))));
        if (p.HitSnowball > 0) gadgets.Children.Add(Tile("Snowballs", Loc.F("{0} KOs", p.KoSnowball), Loc.F("{0} hits", p.HitSnowball)));
        yield return Card(Section("Gadgets", gadgets));
    }

    // ───────── Legends (table rows, for the overview) ─────────

    static readonly double[] LegendCols = { double.NaN, 130, 80, 90, 90, 110, 100, 80 };

    FrameworkElement LegendHeader() => GridRow(LegendCols, true, "Legend", "Games", "Win %", "KOs / game", "Falls / game", "Damage / game", "Ranked", "Time");

    FrameworkElement LegendRow(BhLegendStats l, int allGames)
    {
        double win = 100.0 * l.Wins / l.Games;
        var row = GridRow(LegendCols, false, null, null, null, (l.Kos / (double)l.Games).ToString("0.00", CultureInfo.CurrentCulture),
            (l.Falls / (double)l.Games).ToString("0.00", CultureInfo.CurrentCulture), (l.DamageDealt / (double)l.Games).ToString("N0", CultureInfo.CurrentCulture),
            null, Hours(l.MatchTime));

        PutIn(row, 0, Named(LegendName(l.LegendId), 28));

        // Games, with a bar for its share of all games (a quarter of all games fills it).
        var games = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0) };
        games.Children.Add(new TextBlock { Text = l.Games.ToString("N0", CultureInfo.CurrentCulture), FontSize = 13, Foreground = Res("TextBrush"), HorizontalAlignment = HorizontalAlignment.Right });
        games.Children.Add(Bar(l.Games / (double)allGames * 4, Gold, 90).Also(b => { b.Margin = new Thickness(0, 3, 0, 0); b.HorizontalAlignment = HorizontalAlignment.Right; }));
        PutIn(row, 1, games);

        // Win rate coloured: green above 50%, red below (grey with under 10 games).
        var wc = l.Games < 10 ? Grey : win >= 55 ? Green : win >= 50 ? Color.FromRgb(0xB8, 0xE9, 0xC9) : win >= 45 ? Color.FromRgb(0xF2, 0xB8, 0xB0) : Red;
        PutIn(row, 2, new TextBlock { Text = $"{win:0.0}%", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(wc), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });

        if (l.Rating > 0)
        {
            var rk = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0) };
            rk.Children.Add(Dot(TierColor(l.Tier.Length > 0 ? l.Tier : Brawlhalla.TierOf(l.Rating))).Also(d => d.Margin = new Thickness(0, 0, 6, 0)));
            rk.Children.Add(new TextBlock { Text = l.Rating.ToString(CultureInfo.CurrentCulture), FontSize = 13, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
            PutIn(row, 6, rk);
        }
        else PutIn(row, 6, new TextBlock { Text = "–", FontSize = 13, Foreground = Res("TextFaintBrush"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0) });

        return Clickable(row, () => ShowLegend(l.LegendId));
    }

    // ───────── Weapons ─────────

    sealed record WeaponLine(string Name, long Damage, int Kos, long Time, int Legends);

    List<WeaponLine> Weapons(BhProfile p)
    {
        var by = new Dictionary<string, (long Damage, int Kos, long Time, HashSet<int> Legends)>();
        void Add(string weapon, int legend, long damage, int kos, long time)
        {
            if (string.IsNullOrEmpty(weapon)) return;
            if (!by.TryGetValue(weapon, out var c)) c = (0, 0, 0, new HashSet<int>());
            if (time > 0) c.Legends.Add(legend);
            by[weapon] = (c.Damage + damage, c.Kos + kos, c.Time + time, c.Legends);
        }
        foreach (var l in p.Legends)
        {
            if (!_legends.TryGetValue(l.LegendId, out var info)) continue;
            Add(info.WeaponOne, l.LegendId, l.DamageWeaponOne, l.KoWeaponOne, l.TimeWeaponOne);
            Add(info.WeaponTwo, l.LegendId, l.DamageWeaponTwo, l.KoWeaponTwo, l.TimeWeaponTwo);
        }
        return by.Select(x => new WeaponLine(Spaced(x.Key), x.Value.Damage, x.Value.Kos, x.Value.Time, x.Value.Legends.Count)).ToList();
    }

    static readonly double[] WeaponCols = { 170, double.NaN, 80, 90, 90, 100, 80 };

    IEnumerable<FrameworkElement> WeaponsTab(BhProfile p)
    {
        var lines = Weapons(p).OrderByDescending(w => w.Time).ToList();
        var sp = new StackPanel();
        if (lines.Count == 0) { sp.Children.Add(Dim(Loc.T("Weapon stats show once the legend list has loaded."), 12.5, 0)); yield return Card(Section("Weapons", sp)); yield break; }
        long total = Math.Max(1, lines.Sum(w => w.Time));
        long top = Math.Max(1, lines.Max(w => w.Time));
        sp.Children.Add(Dim(Loc.T("Every legend has two weapons; these add up each weapon across the legends that use it."), 12.5, 0));
        sp.Children.Add(GridRow(WeaponCols, true, "Weapon", "Time held", "Share", "KOs", "KOs / hour", "Damage", "Legends").Also(b => b.Margin = new Thickness(0, 10, 0, 0)));
        foreach (var w in lines)
        {
            var row = GridRow(WeaponCols, false, null, null, $"{100.0 * w.Time / total:0.0}%", w.Kos.ToString("N0", CultureInfo.CurrentCulture),
                w.Time > 0 ? (w.Kos / (w.Time / 3600.0)).ToString("0.0", CultureInfo.CurrentCulture) : "–", Short(w.Damage), w.Legends.ToString(CultureInfo.CurrentCulture));
            var held = new StackPanel { Orientation = Orientation.Horizontal };
            held.Children.Add(Bar(w.Time / (double)top, Gold, 140).Also(b => { b.VerticalAlignment = VerticalAlignment.Center; b.Margin = new Thickness(0, 0, 10, 0); }));
            held.Children.Add(new TextBlock { Text = Hours(w.Time), FontSize = 13, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
            PutIn(row, 1, held);
            PutIn(row, 0, WeaponName(w.Name, 22));
            sp.Children.Add(row);
        }
        yield return Card(Section("Weapons", sp));
    }

    // ───────── Ranked ─────────

    static readonly double[] RankedLegendCols = { double.NaN, 150, 100, 100, 150 };
    static readonly double[] TeamCols = { double.NaN, 140, 90, 90, 130, 70 };

    IEnumerable<FrameworkElement> RankedTab(BhProfile p)
    {
        var boxes = new WrapPanel();
        if (p.OneVsOne is { } one) boxes.Children.Add(RankedBox("Ranked 1v1", one.Tier, one.Rating, one.PeakRating, one.Wins, one.Games, one.Region, one.RegionRank, one.GlobalRank, null));
        if (p.Teams.FirstOrDefault() is { } best)
            boxes.Children.Add(RankedBox("Ranked 2v2", best.Tier.Length > 0 ? best.Tier : Brawlhalla.TierOf(best.Rating), best.Rating, best.PeakRating, best.Wins, best.Games, best.Region, 0, best.GlobalRank, Loc.F("best team, with {0}", best.Partner)));
        if (p.ThreeVsThree is { } three) boxes.Children.Add(RankedBox("Ranked 3v3", three.Tier, three.Rating, three.PeakRating, three.Wins, three.Games, three.Region, three.RegionRank, three.GlobalRank, null));
        if (boxes.Children.Count == 0) boxes.Children.Add(Dim(Loc.T("No ranked games this season."), 12.5, 0));
        yield return Card(Section("This season", boxes));

        var ranked = p.Legends.Where(l => l.Rating > 0).OrderByDescending(l => l.Rating).ToList();
        if (ranked.Count > 0)
        {
            var sp = new StackPanel();
            sp.Children.Add(GridRow(RankedLegendCols, true, "Legend", "Tier", "Rating", "Peak", "Record"));
            foreach (var l in ranked)
            {
                var row = GridRow(RankedLegendCols, false, null, null, l.Rating.ToString("N0", CultureInfo.CurrentCulture), l.PeakRating.ToString("N0", CultureInfo.CurrentCulture),
                    l.RankedGames > 0 ? $"{l.RankedWins}–{l.RankedGames - l.RankedWins} · {100.0 * l.RankedWins / l.RankedGames:0}%" : "");
                PutIn(row, 1, TierPill(l.Tier.Length > 0 ? l.Tier : Brawlhalla.TierOf(l.Rating)));
                PutIn(row, 0, Named(LegendName(l.LegendId), 26));
                sp.Children.Add(row);
            }
            yield return Card(Section("Ranked 1v1 legends", sp));
        }

        if (p.Teams.Count > 0)
        {
            var sp = new StackPanel();
            sp.Children.Add(GridRow(TeamCols, true, "Teammate", "Tier", "Rating", "Peak", "Record", "Region"));
            foreach (var t in p.Teams)
            {
                var row = GridRow(TeamCols, false, t.Partner, null, t.Rating.ToString("N0", CultureInfo.CurrentCulture), t.PeakRating.ToString("N0", CultureInfo.CurrentCulture),
                    t.Games > 0 ? $"{t.Wins}–{t.Games - t.Wins} · {100.0 * t.Wins / t.Games:0}%" : "", t.Region);
                PutIn(row, 1, TierPill(t.Tier.Length > 0 ? t.Tier : Brawlhalla.TierOf(t.Rating)));
                long partner = t.PartnerId;
                sp.Children.Add(Clickable(row, partner > 0 ? () => _ = Open(partner) : null));
            }
            sp.Children.Add(Dim(Loc.T("Click a teammate to open their profile."), 11.5, 8));
            yield return Card(Section("2v2 teams", sp));
        }
    }

    FrameworkElement RankedBox(string title, string tier, int rating, int peak, int wins, int games, string region, int regionRank, int globalRank, string note)
    {
        var c = TierColor(tier);
        var sp = new StackPanel();
        sp.Children.Add(Caps(title, 10.5, Res("TextFaintBrush")));
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        line.Children.Add(new TextBlock { Text = rating.ToString("N0", CultureInfo.CurrentCulture), FontSize = 30, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(c), VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(TierPill(tier).Also(t => t.Margin = new Thickness(10, 0, 0, 0)));
        sp.Children.Add(line);
        // Rating against peak.
        if (peak > 0) sp.Children.Add(Bar(Math.Clamp(rating / (double)peak, 0, 1), c, 250).Also(b => b.Margin = new Thickness(0, 10, 0, 0)));
        sp.Children.Add(Dim(Loc.F("Peak {0}", peak.ToString("N0", CultureInfo.CurrentCulture)) + (games > 0 ? $" · {wins}–{games - wins} ({100.0 * wins / games:0}%)" : ""), 12, 6));
        var where = new List<string>();
        if (regionRank > 0) where.Add(Loc.F("#{0} in {1}", regionRank.ToString("N0", CultureInfo.CurrentCulture), region));
        else if (region.Length > 0) where.Add(region);
        if (globalRank > 0) where.Add(Loc.F("#{0} worldwide", globalRank.ToString("N0", CultureInfo.CurrentCulture)));
        if (note != null) where.Add(note);
        if (where.Count > 0) sp.Children.Add(Dim(string.Join(" · ", where), 12, 2));
        return new Border
        {
            Child = sp, Width = 300, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(18, 16, 18, 16), CornerRadius = new CornerRadius(12),
            Background = new LinearGradientBrush(A(c, 0x1C), A(c, 0x04), 90), BorderBrush = new SolidColorBrush(A(c, 0x60)), BorderThickness = new Thickness(1),
        };
    }

    // ───────── loading ─────────

    async Task Search(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0 || _busy) return;
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0) { await Open(id); return; }
        if (text.Length < 2) { _status = Loc.T("Type at least two letters of their name."); Build(); return; }
        _busy = true; _status = Loc.T("Searching…"); Build();
        try
        {
            _results = await Task.Run(() => Brawlhalla.Find(text));
            _status = _results.Count == 0 ? Loc.T("No ranked player with that name. Try their Brawlhalla ID instead.") : null;
            if (_results.Count == 1 && _results[0].Id > 0) { _busy = false; await Open(_results[0].Id); return; }
        }
        catch (Exception ex)
        {
            // Brawlhalla's search refuses names with accents or symbols outside plain English letters.
            _status = text.Any(c => c > 127) ? Loc.T("Brawlhalla's search doesn't take accented letters or symbols. Use their Brawlhalla ID instead (in game: Inventory, Profile).") : Problem(ex);
        }
        _busy = false;
        Build();
    }

    async Task Open(long id, string tab = "overview", string tier1v1 = null)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _busy = true; _status = Loc.T("Loading their stats…"); Build();
        try
        {
            if (_legends.Count == 0) try { _legends = await Task.Run(() => Brawlhalla.Legends(ct)); } catch { }
            var p = await Task.Run(() => Brawlhalla.Profile(id, ct));
            // Turn what changed since Pulse last saw them into matches.
            var matches = p == null ? new List<BhMatch>() : await Task.Run(() => { BrawlhallaMatches.Record(p); return BrawlhallaMatches.For(p.Id); });
            if (ct.IsCancellationRequested) return;
            if (p == null) _status = Loc.T("No player with that Brawlhalla ID.");
            else
            {
                if (tier1v1 is { Length: > 0 } && p.OneVsOne is { BoardTier: null } one) one.BoardTier = tier1v1;
                _profile = p; _matches = matches; _results = null; _status = null; _tab = tab; _openLegend = 0; _legendLimit = 10; _weaponFilter = null;
                S.Recent.RemoveAll(r => r.Id == p.Id);
                S.Recent.Insert(0, new BhRecent { Id = p.Id, Name = p.Name });
                if (S.Recent.Count > 10) S.Recent.RemoveRange(10, S.Recent.Count - 10);
                if (S.MyId == p.Id) S.MyName = p.Name;
                TweakEngine.Save();
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _status = Problem(ex); }
        _busy = false;
        Build();
    }

    async Task LoadBoard()
    {
        _busy = true; _board = default; Build();
        try { _board = await Task.Run(() => Brawlhalla.Rankings(_boardMode, _boardRegion, _boardPage)); }
        catch (Exception ex) { _status = Problem(ex); }
        _busy = false;
        Build();
    }

    static string Problem(Exception ex) => ex is HttpRequestException or TaskCanceledException
        ? (ex.Message.StartsWith("Brawlhalla's servers say", StringComparison.Ordinal) ? Loc.T(ex.Message) : Loc.T("Couldn't reach Brawlhalla's stats. Check your connection and try again."))
        : Loc.T("Something went wrong reading Brawlhalla's stats.");

    // ───────── pieces ─────────

    string LegendName(int id) => _legends.TryGetValue(id, out var l) && l.Name.Length > 0 ? l.Name : Loc.F("Legend {0}", id);

    /// <summary>A weapon's name as the game shows it ("RocketLance" → "Rocket Lance", "Fists" → "Gauntlets").</summary>
    static string Spaced(string weapon) => string.IsNullOrEmpty(weapon) ? null : weapon switch
    {
        "Fists" => "Gauntlets", "Pistol" => "Blasters", "Hammer" => "Grapple Hammer", "Katar" => "Katars", "Boots" => "Battle Boots",
        _ => System.Text.RegularExpressions.Regex.Replace(weapon, "(?<=[a-z])(?=[A-Z])", " "),
    };
    static Color A(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    static string Hours(long seconds) => seconds >= 3600 ? Loc.F("{0:0.0} h", seconds / 3600.0) : Loc.F("{0} min", seconds / 60);
    static string Clock(long seconds) => $"{seconds / 60}:{seconds % 60:00}";
    static string Short(long n) => n >= 1_000_000 ? (n / 1_000_000.0).ToString("0.0", CultureInfo.CurrentCulture) + "M" : n >= 10_000 ? (n / 1000.0).ToString("0", CultureInfo.CurrentCulture) + "K" : n.ToString("N0", CultureInfo.CurrentCulture);

    static Color TierColor(string tier) => (tier ?? "") switch
    {
        var t when t.StartsWith("Valhallan") => Color.FromRgb(0xE0, 0x6B, 0xFF),
        var t when t.StartsWith("Diamond") => Color.FromRgb(0x7E, 0xC8, 0xFF),
        var t when t.StartsWith("Platinum") => Teal,
        var t when t.StartsWith("Gold") => Gold,
        var t when t.StartsWith("Silver") => Color.FromRgb(0xC4, 0xC9, 0xD2),
        var t when t.StartsWith("Bronze") => Color.FromRgb(0xC9, 0x84, 0x52),
        _ => Grey,
    };

    static Border TierPill(string tier)
    {
        var c = TierColor(tier);
        return new Border
        {
            CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 2, 9, 3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(A(c, 0x22)), BorderBrush = new SolidColorBrush(A(c, 0x70)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = string.IsNullOrEmpty(tier) ? Loc.T("Unranked") : tier, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(c) },
        };
    }

    static Border Badge(string text, Color c) => new()
    {
        CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
        BorderBrush = new SolidColorBrush(A(c, 0x80)), BorderThickness = new Thickness(1), Background = new SolidColorBrush(A(c, 0x14)),
        Child = new TextBlock { Text = text.ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.ExtraBold, Foreground = new SolidColorBrush(c) },
    };

    static FrameworkElement Dot(Color c) => new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(c), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A thin bar filled to <paramref name="fraction"/>.</summary>
    static Border Bar(double fraction, Color c, double width)
    {
        var fill = new Border { Width = Math.Max(0, Math.Min(1, fraction)) * width, Height = 5, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(c), HorizontalAlignment = HorizontalAlignment.Left };
        return new Border { Width = width, Height = 5, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(A(c, 0x26)), Child = fill, HorizontalAlignment = HorizontalAlignment.Left };
    }

    /// <summary>A stacked bar of parts, with a key under it.</summary>
    FrameworkElement Split(string title, (string Label, double Value, Color Color)[] parts, double width = 720)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (title != null) sp.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 0, 0, 6) });
        double total = parts.Sum(p => Math.Max(0, p.Value));
        var bar = new Grid { Height = 10 };
        int col = 0;
        foreach (var (_, v, c) in parts)
        {
            if (v <= 0 || total <= 0) continue;
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(v, GridUnitType.Star) });
            var seg = new Border { Background = new SolidColorBrush(c), Margin = new Thickness(0, 0, 2, 0) };
            Grid.SetColumn(seg, col++);
            bar.Children.Add(seg);
        }
        sp.Children.Add(new Border { Child = bar, CornerRadius = new CornerRadius(5), ClipToBounds = true, Width = width, HorizontalAlignment = HorizontalAlignment.Left });
        var key = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), MaxWidth = width, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (label, v, c) in parts)
        {
            if (v <= 0) continue;
            var k = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 4) };
            k.Children.Add(Dot(c).Also(d => d.Margin = new Thickness(0, 0, 6, 0)));
            k.Children.Add(new TextBlock { Text = $"{Loc.T(label)} {100 * v / Math.Max(1, total):0}%", FontSize = 12, Foreground = Res("TextDimBrush") });
            key.Children.Add(k);
        }
        sp.Children.Add(key);
        return sp;
    }

    FrameworkElement Standout(string title, string name, string detail, Color c, int legendId)
    {
        var sp = new StackPanel();
        sp.Children.Add(Caps(title, 10, new SolidColorBrush(A(c, 0xD0))));
        sp.Children.Add(new TextBlock { Text = name, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(Dim(detail, 12, 2));
        var b = new Border
        {
            Child = sp, Width = 206, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(16, 14, 16, 14), CornerRadius = new CornerRadius(12),
            Background = new LinearGradientBrush(A(c, 0x18), A(c, 0x03), 90), BorderBrush = new SolidColorBrush(A(c, 0x45)), BorderThickness = new Thickness(1),
        };
        return legendId > 0 ? Clickable(b, () => ShowLegend(legendId)) : b;
    }

    FrameworkElement Tile(string label, string value, string sub, Color? valueColor = null)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 12, Foreground = Res("TextDimBrush") });
        sp.Children.Add(new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = valueColor is { } vc ? new SolidColorBrush(vc) : Res("TextBrush"), Margin = new Thickness(0, 2, 0, 0) });
        if (sub != null) sp.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = Res("TextFaintBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        return new Border { Child = sp, Width = 206, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Inset) };
    }

    FrameworkElement Stat(string label, string value)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 12, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, 0, 6, 0) });
        sp.Children.Add(new TextBlock { Text = value, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush") });
        return new Border { Background = new SolidColorBrush(Inset), BorderBrush = new SolidColorBrush(Line), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 4), Margin = new Thickness(0, 0, 6, 6), Child = sp };
    }

    /// <summary>
    /// A table row. Widths: a number is fixed, NaN takes the rest. The first column and the NaN one are names (left,
    /// bright); the others are numbers (right). A null cell is left for <see cref="PutIn"/>.
    /// </summary>
    FrameworkElement GridRow(double[] widths, bool header, params string[] cells)
    {
        var g = new Grid();
        foreach (var w in widths) g.ColumnDefinitions.Add(double.IsNaN(w) ? new ColumnDefinition { MinWidth = 110 } : new ColumnDefinition { Width = new GridLength(w) });
        for (int i = 0; i < cells.Length && i < widths.Length; i++)
        {
            if (cells[i] == null) continue;
            bool left = i == 0 || double.IsNaN(widths[i]);
            bool name = !header && (double.IsNaN(widths[i]) || (i == 0 && !cells[i].StartsWith('#')));
            var t = new TextBlock
            {
                Text = header ? Loc.T(cells[i]).ToUpper(CultureInfo.CurrentCulture) : cells[i],
                FontSize = header ? 10.5 : 13, FontWeight = header ? FontWeights.Bold : name ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = header ? Res("TextFaintBrush") : name ? Res("TextBrush") : Res("TextDimBrush"),
                HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0),
            };
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }
        return new Border
        {
            Child = g, Padding = new Thickness(10, header ? 4 : 8, 10, 8), Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(header ? Line : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    /// <summary>Puts an element into a column of a row made by <see cref="GridRow"/>.</summary>
    static void PutIn(FrameworkElement row, int column, UIElement e)
    {
        if (row is not Border { Child: Grid g }) return;
        Grid.SetColumn(e, column);
        g.Children.Add(e);
    }

    static FrameworkElement Clickable(FrameworkElement row, Action go, bool on = false)
    {
        if (row is not Border b) return row;
        Brush normal = on ? new SolidColorBrush(A(Gold, 0x1A)) : b.Background ?? Brushes.Transparent;
        b.Background = normal;
        if (go == null) return b;
        b.Cursor = Cursors.Hand;
        b.CornerRadius = new CornerRadius(8);
        b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        b.MouseLeave += (_, _) => b.Background = normal;
        b.MouseLeftButtonUp += (_, _) => go();
        return b;
    }

    /// <summary>Joined buttons, like the VALORANT tracker's playlist bar.</summary>
    FrameworkElement Segments(IEnumerable<(string Label, bool On, Action Go)> items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, on, go) in items)
        {
            var b = new Border
            {
                MinWidth = 78, Height = 34, Padding = new Thickness(14, 0, 14, 0), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 1, 0),
                Background = new SolidColorBrush(on ? Gold : Inset),
                Child = new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = on ? Brushes.Black : Res("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            if (!on)
            {
                b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Line);
                b.MouseLeave += (_, _) => b.Background = new SolidColorBrush(Inset);
            }
            b.MouseLeftButtonUp += (_, _) => go();
            sp.Children.Add(b);
        }
        return new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = sp, Margin = new Thickness(0, 0, 10, 8), HorizontalAlignment = HorizontalAlignment.Left };
    }

    Button Ghost(string text, Action go)
    {
        var b = new Button { Style = (Style)FindResource("GhostButton"), Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => go();
        return b;
    }

    StackPanel Section(string title, UIElement body)
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading(title));
        sp.Children.Add(new Border { Margin = new Thickness(0, 12, 0, 0), Child = body });
        return sp;
    }

    Border Card(UIElement child) => new() { Style = (Style)FindResource("Card"), Padding = new Thickness(22, 18, 22, 18), Margin = new Thickness(0, 14, 0, 0), Child = child };

    TextBlock Heading(string t) => new() { Text = Loc.T(t), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont") };
    TextBlock Dim(string t, double size, double top) => new() { Text = t, FontSize = size, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
    static TextBlock Caps(string t, double size, Brush b) => new() { Text = Loc.T(t).ToUpper(CultureInfo.CurrentCulture), FontSize = size, FontWeight = FontWeights.Bold, Foreground = b };

    RadioButton Chip(string text, bool on, Action pick)
    {
        var r = new RadioButton { Style = (Style)FindResource("Chip"), Content = text, IsChecked = on, Margin = new Thickness(0, 0, 6, 6), GroupName = Guid.NewGuid().ToString("N") };
        r.Click += (_, _) => pick();
        return r;
    }

    Brush Res(string key) => (Brush)FindResource(key);
}
