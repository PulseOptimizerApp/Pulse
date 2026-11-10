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
/// ID, then their ranked 1v1, 2v2 and 3v3, legends, weapons and gadgets; and the ranked leaderboards by region.
/// </summary>
public sealed class BrawlhallaPage : UserControl, IPage
{
    static BrawlhallaSettings S => TweakEngine.State.Brawlhalla ??= new BrawlhallaSettings();

    readonly StackPanel _root = new() { Margin = new Thickness(0, 6, 0, 40) };
    BhProfile _profile;
    Dictionary<int, BhLegend> _legends = new();
    List<BhRanking> _results;
    (List<BhRanking> Rows, int Pages) _board;
    string _boardMode = "1v1", _boardRegion = "ALL";
    string _status, _sort = "games";
    bool _busy, _shown;
    CancellationTokenSource _cts;

    static readonly Color Accent = Color.FromRgb(0xF5, 0xB9, 0x42);

    public BrawlhallaPage()
    {
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _root };
        Build();
    }

    public void OnShown(object parameter)
    {
        if (_shown) return;
        _shown = true;
        _ = Start();
    }

    async Task Start()
    {
        try { _legends = await Task.Run(() => Brawlhalla.Legends()); } catch { }
        if (S.MyId > 0) await Open(S.MyId);
        else await LoadBoard();
    }

    // ───────── what's shown ─────────

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
        if (_profile != null) foreach (var part in ProfileParts(_profile)) _root.Children.Add(part);
        else _root.Children.Add(Card(BoardPanel()));
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
        foreach (var r in _results) sp.Children.Add(RankRow(r, showRank: false));
        return sp;
    }

    FrameworkElement BoardPanel()
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Ranked leaderboards"));
        var modes = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var m in new[] { "1v1", "2v2", "3v3" })
            modes.Children.Add(Chip(m, _boardMode == m, () => { _boardMode = m; _ = LoadBoard(); }));
        sp.Children.Add(modes);
        var regions = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
        foreach (var r in Brawlhalla.Regions)
            regions.Children.Add(Chip(r == "ALL" ? Loc.T("Global") : r, _boardRegion == r, () => { _boardRegion = r; _ = LoadBoard(); }));
        sp.Children.Add(regions);
        if (_board.Rows == null) sp.Children.Add(Dim(_busy ? Loc.T("Loading…") : Loc.T("Couldn't load the leaderboard."), 12.5, 6));
        else foreach (var r in _board.Rows) sp.Children.Add(RankRow(r, showRank: true));
        return sp;
    }

    FrameworkElement RankRow(BhRanking r, bool showRank)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var w in new[] { 46.0, double.NaN, 150, 90, 110, 70 })
            g.ColumnDefinitions.Add(double.IsNaN(w) ? new ColumnDefinition() : new ColumnDefinition { Width = new GridLength(w) });
        void Put(int col, string text, Brush fg, bool bold = false, HorizontalAlignment a = HorizontalAlignment.Left)
        {
            var t = new TextBlock { Text = text, FontSize = 13, Foreground = fg, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = a, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(t, col);
            g.Children.Add(t);
        }
        Put(0, showRank ? "#" + r.Rank.ToString("N0", CultureInfo.CurrentCulture) : "", Res("TextDimBrush"));
        Put(1, r.Name, Res("TextBrush"), true);
        var tier = TierPill(r.Tier.Length > 0 ? r.Tier : Brawlhalla.TierOf(r.Rating));
        Grid.SetColumn(tier, 2);
        g.Children.Add(tier);
        Put(3, r.Rating.ToString("N0", CultureInfo.CurrentCulture), Res("TextBrush"), true, HorizontalAlignment.Right);
        int games = r.Wins + r.Losses;
        Put(4, games > 0 ? $"{r.Wins}–{r.Losses}" : "", Res("TextDimBrush"), false, HorizontalAlignment.Right);
        Put(5, r.Region, Res("TextFaintBrush"), false, HorizontalAlignment.Right);
        var b = new Border { Child = g, Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        // Teams (2v2) open the first player.
        if (r.Id > 0) b.MouseLeftButtonUp += (_, _) => _ = Open(r.Id);
        b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        return b;
    }

    // ───────── a player ─────────

    IEnumerable<FrameworkElement> ProfileParts(BhProfile p)
    {
        // Header: name, level, clan, ID; "this is me".
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = p.Name.Length > 0 ? p.Name : Loc.T("Unknown player"), FontSize = 24, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont") });
        var facts = new List<string>();
        if (p.Level > 0) facts.Add(Loc.F("Level {0}", p.Level));
        if (p.Guild.Length > 0) facts.Add(Loc.F("Clan {0}", p.Guild) + (p.GuildRank.Length > 0 ? $" ({p.GuildRank})" : ""));
        facts.Add(Loc.F("ID {0}", p.Id));
        left.Children.Add(Dim(string.Join(" · ", facts), 12.5, 2));
        head.Children.Add(left);
        var me = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 5, 12, 5), VerticalAlignment = VerticalAlignment.Center,
            Content = S.MyId == p.Id ? Loc.T("This is you") : Loc.T("This is me"), IsEnabled = S.MyId != p.Id };
        me.Click += (_, _) => { S.MyId = p.Id; S.MyName = p.Name; TweakEngine.Save(); Build(); };
        Grid.SetColumn(me, 1);
        head.Children.Add(me);
        yield return Card(head, Accent);

        // Ranked.
        var ranked = new WrapPanel();
        if (p.OneVsOne is { } one) ranked.Children.Add(RankedBox("Ranked 1v1", one.Tier, one.Rating, one.PeakRating, one.Wins, one.Games, one.Region, one.RegionRank, one.GlobalRank, null));
        if (p.Teams.FirstOrDefault() is { } best)
            ranked.Children.Add(RankedBox("Ranked 2v2", best.Tier.Length > 0 ? best.Tier : Brawlhalla.TierOf(best.Rating), best.Rating, best.PeakRating, best.Wins, best.Games, best.Region, 0, best.GlobalRank, Loc.F("with {0}", best.Partner)));
        if (p.ThreeVsThree is { } three) ranked.Children.Add(RankedBox("Ranked 3v3", three.Tier, three.Rating, three.PeakRating, three.Wins, three.Games, three.Region, three.RegionRank, three.GlobalRank, null));
        if (ranked.Children.Count == 0) ranked.Children.Add(Dim(Loc.T("No ranked games this season."), 12.5, 0));
        yield return Card(Section("Ranked", ranked));

        // Overall.
        long damage = p.Legends.Sum(l => l.DamageDealt), taken = p.Legends.Sum(l => l.DamageTaken), time = p.Legends.Sum(l => l.MatchTime);
        int kos = p.Legends.Sum(l => l.Kos), falls = p.Legends.Sum(l => l.Falls);
        var overall = new WrapPanel();
        overall.Children.Add(Big("Games", p.Games.ToString("N0", CultureInfo.CurrentCulture), null));
        overall.Children.Add(Big("Win rate", p.Games > 0 ? $"{100.0 * p.Wins / p.Games:0.0}%" : "–", Loc.F("{0} wins", p.Wins.ToString("N0", CultureInfo.CurrentCulture))));
        overall.Children.Add(Big("KOs", kos.ToString("N0", CultureInfo.CurrentCulture), falls > 0 ? Loc.F("{0:0.00} per fall", kos / (double)falls) : null));
        overall.Children.Add(Big("Damage per game", p.Games > 0 ? (damage / (double)p.Games).ToString("N0", CultureInfo.CurrentCulture) : "–", taken > 0 ? Loc.F("{0:0.00} dealt per taken", damage / (double)taken) : null));
        overall.Children.Add(Big("Time played", Hours(time), null));
        yield return Card(Section("Overall", overall));

        // Legends.
        yield return Card(Section("Legends", LegendsTable(p)));
        yield return Card(Section("Weapons", WeaponsTable(p)));
        yield return Card(Section("Gadgets", Gadgets(p)));
    }

    FrameworkElement RankedBox(string title, string tier, int rating, int peak, int wins, int games, string region, int regionRank, int globalRank, string note)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = Loc.T(title).ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Res("TextFaintBrush") });
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        line.Children.Add(new TextBlock { Text = rating.ToString("N0", CultureInfo.CurrentCulture), FontSize = 26, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(TierPill(tier).Also(t => t.Margin = new Thickness(10, 0, 0, 0)));
        sp.Children.Add(line);
        var bits = new List<string> { Loc.F("Peak {0}", peak.ToString("N0", CultureInfo.CurrentCulture)) };
        if (games > 0) bits.Add($"{wins}–{games - wins} ({100.0 * wins / games:0}%)");
        sp.Children.Add(Dim(string.Join(" · ", bits), 12, 4));
        var where = new List<string>();
        if (regionRank > 0) where.Add(Loc.F("#{0} in {1}", regionRank.ToString("N0", CultureInfo.CurrentCulture), region));
        else if (region.Length > 0) where.Add(region);
        if (globalRank > 0) where.Add(Loc.F("#{0} worldwide", globalRank.ToString("N0", CultureInfo.CurrentCulture)));
        if (note != null) where.Add(note);
        if (where.Count > 0) sp.Children.Add(Dim(string.Join(" · ", where), 12, 2));
        return new Border { Child = sp, Width = 280, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(16, 14, 16, 14), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1B, 0x20)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, TierColor(tier).R, TierColor(tier).G, TierColor(tier).B)), BorderThickness = new Thickness(1) };
    }

    FrameworkElement LegendsTable(BhProfile p)
    {
        var sp = new StackPanel();
        var sorts = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (key, label) in new[] { ("games", "Most played"), ("win", "Win rate"), ("rating", "Ranked rating"), ("damage", "Damage per game") })
            sorts.Children.Add(Chip(Loc.T(label), _sort == key, () => { _sort = key; Build(); }));
        sp.Children.Add(sorts);
        var rows = p.Legends.Where(l => l.Games > 0).ToList();
        rows = _sort switch
        {
            "win" => rows.Where(l => l.Games >= 10).OrderByDescending(l => l.Wins / (double)l.Games).Concat(rows.Where(l => l.Games < 10).OrderByDescending(l => l.Games)).ToList(),
            "rating" => rows.OrderByDescending(l => l.Rating).ThenByDescending(l => l.Games).ToList(),
            "damage" => rows.OrderByDescending(l => l.DamageDealt / (double)l.Games).ToList(),
            _ => rows.OrderByDescending(l => l.Games).ToList(),
        };
        sp.Children.Add(TableRow(true, "Legend", "Games", "Win %", "KOs", "Falls", "Damage / game", "Rating", "Time"));
        foreach (var l in rows.Take(30))
            sp.Children.Add(TableRow(false, LegendName(l.LegendId), l.Games.ToString("N0", CultureInfo.CurrentCulture), $"{100.0 * l.Wins / l.Games:0.0}%",
                l.Kos.ToString("N0", CultureInfo.CurrentCulture), l.Falls.ToString("N0", CultureInfo.CurrentCulture),
                (l.DamageDealt / (double)l.Games).ToString("N0", CultureInfo.CurrentCulture),
                l.Rating > 0 ? l.Rating.ToString(CultureInfo.CurrentCulture) : "–", Hours(l.MatchTime)));
        if (rows.Count == 0) sp.Children.Add(Dim(Loc.T("No games yet."), 12.5, 4));
        return sp;
    }

    FrameworkElement WeaponsTable(BhProfile p)
    {
        // Each legend has two weapons; add up damage, KOs and time held per weapon across legends.
        var by = new Dictionary<string, (long Damage, int Kos, long Time)>();
        void Add(string weapon, long damage, int kos, long time)
        {
            if (string.IsNullOrEmpty(weapon)) return;
            var c = by.GetValueOrDefault(weapon);
            by[weapon] = (c.Damage + damage, c.Kos + kos, c.Time + time);
        }
        foreach (var l in p.Legends)
        {
            if (!_legends.TryGetValue(l.LegendId, out var info)) continue;
            Add(info.WeaponOne, l.DamageWeaponOne, l.KoWeaponOne, l.TimeWeaponOne);
            Add(info.WeaponTwo, l.DamageWeaponTwo, l.KoWeaponTwo, l.TimeWeaponTwo);
        }
        var sp = new StackPanel();
        if (by.Count == 0) { sp.Children.Add(Dim(Loc.T("Weapon stats show once the legend list has loaded."), 12.5, 0)); return sp; }
        long total = Math.Max(1, by.Values.Sum(v => v.Time));
        sp.Children.Add(TableRow(true, "Weapon", "KOs", "Damage", "Time held", "Share", "", "", ""));
        foreach (var (w, v) in by.OrderByDescending(x => x.Value.Time))
            sp.Children.Add(TableRow(false, w, v.Kos.ToString("N0", CultureInfo.CurrentCulture), v.Damage.ToString("N0", CultureInfo.CurrentCulture), Hours(v.Time), $"{100.0 * v.Time / total:0}%", "", "", ""));
        return sp;
    }

    FrameworkElement Gadgets(BhProfile p)
    {
        var w = new WrapPanel();
        w.Children.Add(Big("Bombs", Loc.F("{0} KOs", p.KoBomb), Loc.F("{0} damage", p.DamageBomb.ToString("N0", CultureInfo.CurrentCulture))));
        w.Children.Add(Big("Mines", Loc.F("{0} KOs", p.KoMine), Loc.F("{0} damage", p.DamageMine.ToString("N0", CultureInfo.CurrentCulture))));
        w.Children.Add(Big("Spike balls", Loc.F("{0} KOs", p.KoSpikeball), Loc.F("{0} damage", p.DamageSpikeball.ToString("N0", CultureInfo.CurrentCulture))));
        w.Children.Add(Big("Sidekicks", Loc.F("{0} KOs", p.KoSidekick), Loc.F("{0} damage", p.DamageSidekick.ToString("N0", CultureInfo.CurrentCulture))));
        if (p.HitSnowball > 0) w.Children.Add(Big("Snowballs", Loc.F("{0} KOs", p.KoSnowball), Loc.F("{0} hits", p.HitSnowball)));
        return w;
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
        catch (Exception ex) { _status = Problem(ex); }
        _busy = false;
        Build();
    }

    async Task Open(long id)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _busy = true; _status = Loc.T("Loading their stats…"); Build();
        try
        {
            if (_legends.Count == 0) try { _legends = await Task.Run(() => Brawlhalla.Legends(ct)); } catch { }
            var p = await Task.Run(() => Brawlhalla.Profile(id, ct));
            if (ct.IsCancellationRequested) return;
            if (p == null) _status = Loc.T("No player with that Brawlhalla ID.");
            else
            {
                _profile = p; _results = null; _status = null;
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
        try { _board = await Task.Run(() => Brawlhalla.Rankings(_boardMode, _boardRegion)); }
        catch (Exception ex) { _status = Problem(ex); }
        _busy = false;
        Build();
    }

    static string Problem(Exception ex) => ex is HttpRequestException or TaskCanceledException
        ? (ex.Message.StartsWith("Brawlhalla", StringComparison.Ordinal) ? Loc.T(ex.Message) : Loc.T("Couldn't reach Brawlhalla's stats. Check your connection and try again."))
        : Loc.T("Something went wrong reading Brawlhalla's stats.");

    // ───────── pieces ─────────

    string LegendName(int id) => _legends.TryGetValue(id, out var l) ? l.Name : Loc.F("Legend {0}", id);

    static string Hours(long seconds) => seconds >= 3600 ? Loc.F("{0:0.0} h", seconds / 3600.0) : Loc.F("{0} min", seconds / 60);

    static Color TierColor(string tier) => (tier ?? "") switch
    {
        var t when t.StartsWith("Valhallan") => Color.FromRgb(0xE0, 0x6B, 0xFF),
        var t when t.StartsWith("Diamond") => Color.FromRgb(0x7E, 0xC8, 0xFF),
        var t when t.StartsWith("Platinum") => Color.FromRgb(0x4F, 0xD1, 0xC5),
        var t when t.StartsWith("Gold") => Color.FromRgb(0xF5, 0xB9, 0x42),
        var t when t.StartsWith("Silver") => Color.FromRgb(0xC4, 0xC9, 0xD2),
        var t when t.StartsWith("Bronze") => Color.FromRgb(0xC9, 0x84, 0x52),
        _ => Color.FromRgb(0x8A, 0x8F, 0x98),
    };

    static Border TierPill(string tier)
    {
        var c = TierColor(tier);
        return new Border
        {
            CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 2, 9, 3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, c.R, c.G, c.B)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = string.IsNullOrEmpty(tier) ? Loc.T("Unranked") : tier, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(c) },
        };
    }

    FrameworkElement TableRow(bool header, string first, params string[] rest)
    {
        var g = new Grid { Margin = new Thickness(0, header ? 0 : 1, 0, 1) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        foreach (var _ in rest) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 70 });
        var brush = header ? Res("TextFaintBrush") : Res("TextDimBrush");
        g.Children.Add(new TextBlock { Text = header ? Loc.T(first).ToUpper(CultureInfo.CurrentCulture) : first, FontSize = header ? 10.5 : 13, FontWeight = header ? FontWeights.Bold : FontWeights.SemiBold, Foreground = header ? brush : Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        for (int i = 0; i < rest.Length; i++)
        {
            var t = new TextBlock { Text = header ? Loc.T(rest[i]).ToUpper(CultureInfo.CurrentCulture) : rest[i], FontSize = header ? 10.5 : 13, FontWeight = header ? FontWeights.Bold : FontWeights.Normal, Foreground = brush, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(t, i + 1);
            g.Children.Add(t);
        }
        return new Border { Child = g, Padding = new Thickness(8, header ? 4 : 6, 8, header ? 6 : 6), BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0, 0, 0, header ? 1 : 0) };
    }

    FrameworkElement Big(string label, string value, string sub)
    {
        var sp = new StackPanel { Width = 190, Margin = new Thickness(0, 0, 12, 12) };
        sp.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 12, Foreground = Res("TextDimBrush") });
        sp.Children.Add(new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 2, 0, 0) });
        if (sub != null) sp.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = Res("TextFaintBrush") });
        return sp;
    }

    StackPanel Section(string title, UIElement body)
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading(title));
        sp.Children.Add(new Border { Margin = new Thickness(0, 12, 0, 0), Child = body });
        return sp;
    }

    Border Card(UIElement child, Color? glow = null)
    {
        var b = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(22, 18, 22, 18), Margin = new Thickness(0, 14, 0, 0), Child = child };
        if (glow is { } c) b.BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, c.R, c.G, c.B));
        return b;
    }

    TextBlock Heading(string t) => new() { Text = Loc.T(t), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont") };
    TextBlock Dim(string t, double size, double top) => new() { Text = t, FontSize = size, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };

    RadioButton Chip(string text, bool on, Action pick)
    {
        var r = new RadioButton { Style = (Style)FindResource("Chip"), Content = text, IsChecked = on, Margin = new Thickness(0, 0, 6, 6), GroupName = Guid.NewGuid().ToString("N") };
        r.Click += (_, _) => pick();
        return r;
    }

    Brush Res(string key) => (Brush)FindResource(key);
}
