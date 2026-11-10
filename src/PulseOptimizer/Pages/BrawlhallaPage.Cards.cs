using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pulse.Core;
using Pulse.Core.Games.Brawlhalla;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>The Brawlhalla page's legend cards, smurf factor and logged matches.</summary>
public sealed partial class BrawlhallaPage
{
    // ───────── pictures ─────────

    /// <summary>A legend's portrait (or its initial when Pulse has no picture), rounded, with an optional gold rim.</summary>
    Border Portrait(string legend, double size, double radius = 8, double rim = 0)
    {
        var art = BrawlhallaArt.Legend(legend);
        UIElement inner = art != null
            ? new Image { Source = art, Stretch = Stretch.UniformToFill }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality))
            : new TextBlock { Text = string.IsNullOrEmpty(legend) ? "?" : legend[..1], FontSize = size * 0.45, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Gold), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var clip = new Border { CornerRadius = new CornerRadius(Math.Max(0, radius - rim)), Background = new SolidColorBrush(Inset), Child = inner };
        // Round the picture itself (a Border's corners don't clip what's inside).
        clip.SizeChanged += (_, e) => clip.Clip = new RectangleGeometry(new Rect(e.NewSize), Math.Max(0, radius - rim), Math.Max(0, radius - rim));
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(radius), Child = clip,
            BorderThickness = new Thickness(rim), BorderBrush = rim > 0 ? new SolidColorBrush(A(Gold, 0xC0)) : null,
        };
    }

    /// <summary>A portrait and a name, for a table's first column.</summary>
    FrameworkElement Named(string legend, double size)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(Portrait(legend, size, 6));
        sp.Children.Add(new TextBlock { Text = legend, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        return sp;
    }

    FrameworkElement WeaponName(string weapon, double size)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (BrawlhallaArt.Weapon(weapon) is { } icon)
            sp.Children.Add(new Image { Source = icon, Width = size, Height = size, Margin = new Thickness(0, 0, 8, 0) }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
        sp.Children.Add(new TextBlock { Text = Spaced(weapon) ?? weapon, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    /// <summary>Opens the Legends tab with this legend's card first.</summary>
    void ShowLegend(int id)
    {
        _openLegend = id;
        _weaponFilter = null;
        _tab = "legends";
        Build();
        _scroll.ScrollToTop();
    }

    // ───────── Legends: a card for each ─────────

    IEnumerable<FrameworkElement> LegendCards(BhProfile p)
    {
        var played = p.Legends.Where(l => l.Games > 0).ToList();
        int allGames = Math.Max(1, played.Sum(l => l.Games));

        // Sort, and filter by weapon.
        var bar = new StackPanel();
        var sorts = new WrapPanel();
        sorts.Children.Add(Segments(new[] { ("games", "Most played"), ("win", "Win rate"), ("kos", "KOs a game"), ("damage", "Damage a game"), ("rating", "Ranked rating"), ("level", "Level"), ("time", "Playtime") }
            .Select(s => (Loc.T(s.Item2), _sort == s.Item1, (Action)(() => { _sort = s.Item1; _openLegend = 0; Build(); })))));
        bar.Children.Add(sorts);
        var weapons = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        weapons.Children.Add(Chip(Loc.T("All weapons"), _weaponFilter == null, () => { _weaponFilter = null; Build(); }));
        var used = played.SelectMany(l => _legends.TryGetValue(l.LegendId, out var info) ? new[] { info.WeaponOne, info.WeaponTwo } : Array.Empty<string>())
            .Where(w => !string.IsNullOrEmpty(w)).Distinct().OrderBy(w => w).ToList();
        foreach (var w in used)
        {
            var chip = Chip(Spaced(w), _weaponFilter == w, () => { _weaponFilter = w; _openLegend = 0; Build(); });
            if (BrawlhallaArt.Weapon(w) is { } icon)
            {
                var c = new StackPanel { Orientation = Orientation.Horizontal };
                c.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
                c.Children.Add(new TextBlock { Text = Spaced(w), VerticalAlignment = VerticalAlignment.Center });
                chip.Content = c;
            }
            weapons.Children.Add(chip);
        }
        bar.Children.Add(weapons);
        yield return Card(bar);

        IEnumerable<BhLegendStats> rows = played;
        if (_weaponFilter != null)
            rows = rows.Where(l => _legends.TryGetValue(l.LegendId, out var info) && (info.WeaponOne == _weaponFilter || info.WeaponTwo == _weaponFilter));
        rows = _sort switch
        {
            "win" => rows.OrderByDescending(l => l.Games >= 10).ThenByDescending(l => l.Wins / (double)l.Games),
            "kos" => rows.OrderByDescending(l => l.Games >= 10).ThenByDescending(l => l.Kos / (double)l.Games),
            "damage" => rows.OrderByDescending(l => l.Games >= 10).ThenByDescending(l => l.DamageDealt / (double)l.Games),
            "rating" => rows.OrderByDescending(l => l.Rating).ThenByDescending(l => l.Games),
            "level" => rows.OrderByDescending(l => l.Level).ThenByDescending(l => l.Xp),
            "time" => rows.OrderByDescending(l => l.MatchTime),
            _ => rows.OrderByDescending(l => l.Games),
        };
        var list = rows.ToList();
        // A legend opened from elsewhere (a standout, the overview) goes first.
        if (_openLegend > 0 && list.FirstOrDefault(l => l.LegendId == _openLegend) is { } open) { list.Remove(open); list.Insert(0, open); }

        if (list.Count == 0) yield return Card(Dim(Loc.T("No games yet."), 12.5, 0));
        foreach (var l in list.Take(_legendLimit)) yield return LegendCard(l, allGames, l.LegendId == _openLegend);
        if (list.Count > _legendLimit)
        {
            yield return Ghost(Loc.F("Show more ({0} left)", list.Count - _legendLimit), () => { _legendLimit += 10; Build(); })
                .Also(b => b.Margin = new Thickness(0, 14, 0, 0));
        }
    }

    FrameworkElement LegendCard(BhLegendStats l, int allGames, bool picked)
    {
        _legends.TryGetValue(l.LegendId, out var info);
        string name = LegendName(l.LegendId);
        double win = 100.0 * l.Wins / l.Games;
        string tier = l.Rating > 0 ? (l.Tier.Length > 0 ? l.Tier : Brawlhalla.TierOf(l.Rating)) : "";
        var tc = l.Rating > 0 ? TierColor(tier) : Gold;

        // The head: portrait, name and level, ranked, wins and losses, per-game numbers.
        var head = new Grid();
        foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(130), new GridLength(220), new GridLength(110), new GridLength(110), new GridLength(100), new GridLength(100) })
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        head.Children.Add(Portrait(name, 52, 10));
        var who = new StackPanel { Margin = new Thickness(14, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(new TextBlock { Text = name, FontSize = 19, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont"), TextTrimming = TextTrimming.CharacterEllipsis });
        who.Children.Add(Dim((l.Level > 0 ? Loc.F("Level {0} · {1}", l.Level, Hours(l.MatchTime)) : Hours(l.MatchTime)) + $" · {100.0 * l.Games / allGames:0.0}% " + Loc.T("of games"), 11.5, 2));
        if (l.Level > 0) who.Children.Add(Bar(Math.Clamp(l.XpPercent, 0, 1), Gold, 120).Also(b => b.Margin = new Thickness(0, 5, 0, 0)));
        PutAt(head, 1, who);

        var rk = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (l.Rating > 0)
        {
            rk.Children.Add(TierPill(tier));
            rk.Children.Add(new TextBlock { Text = l.Rating.ToString("N0", CultureInfo.CurrentCulture), FontSize = 20, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(tc), Margin = new Thickness(0, 3, 0, 0) });
        }
        else rk.Children.Add(Caps("Unranked", 10, Res("TextFaintBrush")));
        PutAt(head, 2, rk);

        // Wins and losses, with a green/red bar.
        var wl = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        var wlText = new Grid();
        wlText.Children.Add(new TextBlock { Text = Loc.F("{0} W", l.Wins.ToString("N0", CultureInfo.CurrentCulture)), FontSize = 13, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Green) });
        wlText.Children.Add(new TextBlock { Text = $"{win:0.0}%", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), HorizontalAlignment = HorizontalAlignment.Center });
        wlText.Children.Add(new TextBlock { Text = Loc.F("{0} L", (l.Games - l.Wins).ToString("N0", CultureInfo.CurrentCulture)), FontSize = 13, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Red), HorizontalAlignment = HorizontalAlignment.Right });
        wl.Children.Add(wlText);
        var split = new Grid { Height = 5, Margin = new Thickness(0, 5, 0, 0) };
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, l.Wins), GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, l.Games - l.Wins), GridUnitType.Star) });
        split.Children.Add(new Border { Background = new SolidColorBrush(Green), CornerRadius = new CornerRadius(3, 0, 0, 3), Margin = new Thickness(0, 0, 1, 0) });
        split.Children.Add(new Border { Background = new SolidColorBrush(Red), CornerRadius = new CornerRadius(0, 3, 3, 0) }.Also(b => Grid.SetColumn(b, 1)));
        wl.Children.Add(split);
        PutAt(head, 3, wl);

        PutAt(head, 4, HeadStat("Damage / game", (l.DamageDealt / (double)l.Games).ToString("N0", CultureInfo.CurrentCulture)));
        PutAt(head, 5, HeadStat("Taken / game", (l.DamageTaken / (double)l.Games).ToString("N0", CultureInfo.CurrentCulture)));
        PutAt(head, 6, HeadStat("Average game", Clock(l.MatchTime / l.Games)));
        if (l.Xp > 0) PutAt(head, 7, HeadStat("XP", Short(l.Xp)));

        // Combat and ranked.
        var body = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var row1 = new WrapPanel();
        var combat = new WrapPanel();
        combat.Children.Add(MiniStat("KO / fall", l.Falls > 0 ? (l.Kos / (double)l.Falls).ToString("0.00", CultureInfo.CurrentCulture) : "–", l.Falls > 0 ? (l.Kos >= l.Falls ? Green : Red) : null));
        combat.Children.Add(MiniStat("KOs", l.Kos.ToString("N0", CultureInfo.CurrentCulture)));
        combat.Children.Add(MiniStat("KOs / game", (l.Kos / (double)l.Games).ToString("0.00", CultureInfo.CurrentCulture)));
        combat.Children.Add(MiniStat("KOs / min", l.MatchTime > 0 ? (l.Kos / (l.MatchTime / 60.0)).ToString("0.00", CultureInfo.CurrentCulture) : "–"));
        combat.Children.Add(MiniStat("Falls", l.Falls.ToString("N0", CultureInfo.CurrentCulture)));
        combat.Children.Add(MiniStat("Self-KOs", l.Suicides.ToString("N0", CultureInfo.CurrentCulture)));
        combat.Children.Add(MiniStat("Team KOs", l.TeamKos.ToString("N0", CultureInfo.CurrentCulture)));
        combat.Children.Add(MiniStat("Damage dealt", Short(l.DamageDealt)));
        combat.Children.Add(MiniStat("Damage taken", Short(l.DamageTaken)));
        row1.Children.Add(Group("Combat", combat, 18));
        var ranked = new WrapPanel();
        ranked.Children.Add(MiniStat("Matches", l.RankedGames.ToString("N0", CultureInfo.CurrentCulture)));
        ranked.Children.Add(MiniStat("Wins", l.RankedWins.ToString("N0", CultureInfo.CurrentCulture)));
        ranked.Children.Add(MiniStat("Win %", l.RankedGames > 0 ? $"{100.0 * l.RankedWins / l.RankedGames:0.0}%" : "–"));
        ranked.Children.Add(MiniStat("Peak", l.PeakRating > 0 ? l.PeakRating.ToString("N0", CultureInfo.CurrentCulture) : "–"));
        row1.Children.Add(Group("Ranked 1v1", ranked, 0));
        body.Children.Add(row1);

        // Weapons and the rest of their damage.
        var row2 = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        long held = Math.Max(1, l.TimeWeaponOne + l.TimeWeaponTwo);
        foreach (var (weapon, kos, dmg, time, c) in new[] { (info?.WeaponOne, l.KoWeaponOne, l.DamageWeaponOne, l.TimeWeaponOne, Gold), (info?.WeaponTwo, l.KoWeaponTwo, l.DamageWeaponTwo, l.TimeWeaponTwo, Blue) })
        {
            var w = new StackPanel();
            var stats = new WrapPanel();
            stats.Children.Add(MiniStat("KOs", kos.ToString("N0", CultureInfo.CurrentCulture)));
            stats.Children.Add(MiniStat("Damage", Short(dmg)));
            stats.Children.Add(MiniStat("Time held", Hours(time)));
            w.Children.Add(stats);
            w.Children.Add(Bar(time / (double)held, c, 210).Also(b => b.Margin = new Thickness(0, 2, 0, 0)));
            w.Children.Add(Dim(Loc.F("{0:0}% of the time", 100.0 * time / held), 11, 3));
            row2.Children.Add(Group(null, w, 26, weapon == null ? Caps("Weapon", 10.5, Res("TextDimBrush")) : WeaponName(weapon, 18)));
        }
        var other = new WrapPanel();
        other.Children.Add(MiniStat("Gadget KOs", l.KoGadgets.ToString("N0", CultureInfo.CurrentCulture)));
        other.Children.Add(MiniStat("Gadget damage", Short(l.DamageGadgets)));
        other.Children.Add(MiniStat("Thrown damage", Short(l.DamageThrown)));
        other.Children.Add(MiniStat("Unarmed KOs", l.KoUnarmed.ToString("N0", CultureInfo.CurrentCulture)));
        other.Children.Add(MiniStat("Unarmed damage", Short(l.DamageUnarmed)));
        row2.Children.Add(Group("Gadgets, thrown and unarmed", other, 0));
        body.Children.Add(row2);

        var sp = new StackPanel();
        sp.Children.Add(head);
        sp.Children.Add(body);
        // The header strip in the legend's tier colour (gold when unranked).
        var layers = new Grid();
        layers.Children.Add(new Border { Height = 92, VerticalAlignment = VerticalAlignment.Top, Background = new LinearGradientBrush(A(tc, 0x22), A(tc, 0x00), 0) });
        layers.Children.Add(new Border { Child = sp, Padding = new Thickness(20, 18, 20, 18) });
        layers.SizeChanged += (_, e) => layers.Clip = new RectangleGeometry(new Rect(e.NewSize), 11, 11);
        return new Border
        {
            Child = layers, Margin = new Thickness(0, 12, 0, 0), CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(PanelBg),
            BorderBrush = new SolidColorBrush(picked ? A(Gold, 0xA0) : Line), BorderThickness = new Thickness(picked ? 1.5 : 1),
        };
    }

    static void PutAt(Grid g, int col, UIElement e) { Grid.SetColumn(e, col); g.Children.Add(e); }

    FrameworkElement HeadStat(string label, string value)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        sp.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 11, Foreground = Res("TextFaintBrush"), HorizontalAlignment = HorizontalAlignment.Right });
        sp.Children.Add(new TextBlock { Text = value, FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) });
        return sp;
    }

    FrameworkElement MiniStat(string label, string value, Color? c = null)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 20, 8), MinWidth = 54 };
        sp.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 10.5, Foreground = Res("TextFaintBrush") });
        sp.Children.Add(new TextBlock { Text = value, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = c is { } cc ? new SolidColorBrush(cc) : Res("TextBrush"), Margin = new Thickness(0, 1, 0, 0) });
        return sp;
    }

    /// <summary>A titled block inside a card (Combat, Ranked, a weapon), on an inset background.</summary>
    FrameworkElement Group(string title, UIElement content, double right, FrameworkElement titleElement = null)
    {
        var sp = new StackPanel();
        sp.Children.Add(titleElement ?? Caps(title, 10.5, Res("TextDimBrush")));
        sp.Children.Add(new Border { Margin = new Thickness(0, 8, 0, 0), Child = content });
        return new Border { Child = sp, Background = new SolidColorBrush(Inset), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 4, 6), Margin = new Thickness(0, 0, 10, 10) };
    }

    // ───────── smurf factor ─────────

    FrameworkElement SmurfPanel(BhProfile p)
    {
        var sm = BrawlhallaMatches.Smurf(p);
        var c = sm.Score >= 60 ? Red : sm.Score >= 35 ? Gold : sm.Score >= 15 ? Color.FromRgb(0xE5, 0xD1, 0x8A) : Green;
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        g.ColumnDefinitions.Add(new ColumnDefinition());

        var left = new StackPanel();
        left.Children.Add(Heading("Smurf factor"));
        var num = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        num.Children.Add(new TextBlock { Text = sm.Score.ToString(CultureInfo.CurrentCulture), FontSize = 40, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(c) });
        num.Children.Add(new TextBlock { Text = "%", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(A(c, 0xB0)), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 0, 8) });
        left.Children.Add(num);
        left.Children.Add(new TextBlock { Text = Loc.T(sm.Label), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(c) });
        left.Children.Add(Bar(sm.Score / 100.0, c, 190).Also(b => b.Margin = new Thickness(0, 8, 0, 0)));
        g.Children.Add(left);

        var right = new StackPanel { Margin = new Thickness(10, 4, 0, 0) };
        Grid.SetColumn(right, 1);
        if (sm.Signs.Count == 0)
            right.Children.Add(Dim(Loc.T("Nothing unusual about this account: its rating fits its level, games and win rate."), 12.5, 0));
        else
        {
            right.Children.Add(Caps("Signs", 10.5, Res("TextFaintBrush")));
            foreach (var (format, args) in sm.Signs)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
                row.Children.Add(Dot(c).Also(d => d.Margin = new Thickness(0, 0, 9, 0)));
                row.Children.Add(new TextBlock { Text = Loc.F(format, args), FontSize = 13, Foreground = Res("TextBrush"), TextWrapping = TextWrapping.Wrap });
                right.Children.Add(row);
            }
            if (p.Games >= 3000 || p.Level >= 90)
                right.Children.Add(Dim(Loc.T("Kept low: thousands of games make this a main account, however good."), 11.5, 8));
        }
        right.Children.Add(Dim(Loc.T("From level, games, playtime, ranked win rate and how fast the rating came. Signs, not proof: some players are just good."), 11, 12));
        g.Children.Add(right);
        return g;
    }

    // ───────── matches Pulse logged ─────────

    static readonly double[] MatchCols = { double.NaN, 120, 110, 70, 70, 100, 80, 70 };

    FrameworkElement RecentMatches(int count)
    {
        var sp = new StackPanel();
        var head = new Grid();
        head.Children.Add(Heading("Recent matches"));
        head.Children.Add(Ghost(Loc.T("All matches") + " →", () => { _tab = "matches"; Build(); _scroll.ScrollToTop(); })
            .Also(b => { b.HorizontalAlignment = HorizontalAlignment.Right; b.Margin = new Thickness(0); }));
        sp.Children.Add(head);
        sp.Children.Add(new Border { Height = 10 });
        sp.Children.Add(MatchHeader());
        foreach (var m in _matches.Take(count)) sp.Children.Add(MatchRow(m));
        return sp;
    }

    IEnumerable<FrameworkElement> MatchesTab(BhProfile p)
    {
        bool me = S.MyId == p.Id;
        // How matches get here, and the switches (for you).
        var top = new StackPanel();
        top.Children.Add(Heading("Matches"));
        var last = BrawlhallaMatches.LastSeen(p.Id);
        top.Children.Add(Dim(me
            ? Loc.T("Brawlhalla's stats have no match history, so Pulse makes one: while Brawlhalla is open it checks your stats every minute and turns what changed into matches, with your legend, result, KOs, damage and rating change.")
            : Loc.T("Brawlhalla's stats have no match history, so Pulse makes one: each time you open this player, what changed since last time becomes matches. Set \"This is me\" on your own profile to log yours as you play."), 12.5, 4));
        if (me)
        {
            var sw = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            sw.Children.Add(Switch("Log my matches while Brawlhalla is open", S.TrackMatches, v => { S.TrackMatches = v; TweakEngine.Save(); }));
            sw.Children.Add(Switch("Pop up a summary after each match", S.MatchPopup, v => { S.MatchPopup = v; TweakEngine.Save(); }));
            top.Children.Add(sw);
        }
        if (last is { } seen) top.Children.Add(Dim(Loc.F("Last checked {0}", Ago(seen)), 11.5, 8));
        yield return Card(top);

        if (_matches.Count == 0)
        {
            yield return Card(Dim(me
                ? Loc.T("No matches logged yet. Play a match with Brawlhalla open (and Pulse running): it shows up here about a minute after it ends.")
                : Loc.T("No matches since you first opened this player. Open them again after they've played."), 12.5, 0));
            yield break;
        }

        // Totals for everything logged.
        var all = BrawlhallaMatches.Sessions(_matches);
        int games = _matches.Sum(m => m.Games), wins = _matches.Sum(m => m.Wins);
        var tiles = new WrapPanel();
        tiles.Children.Add(Tile("Matches", games.ToString("N0", CultureInfo.CurrentCulture), Loc.F("{0} sessions", all.Count)));
        tiles.Children.Add(Tile("Win %", $"{100.0 * wins / Math.Max(1, games):0.0}%", Loc.F("{0}W {1}L", wins, games - wins), wins * 2 >= games ? Green : Red));
        tiles.Children.Add(Tile("KOs / game", (_matches.Sum(m => m.Kos) / (double)Math.Max(1, games)).ToString("0.00", CultureInfo.CurrentCulture), Loc.F("{0} KOs", _matches.Sum(m => m.Kos))));
        tiles.Children.Add(Tile("Damage per game", (_matches.Sum(m => m.Damage) / (double)Math.Max(1, games)).ToString("N0", CultureInfo.CurrentCulture), Loc.F("{0} dealt in all", Short(_matches.Sum(m => m.Damage)))));
        int rating = _matches.Where(m => m.Mode == "1v1").Sum(m => m.RatingChange);
        tiles.Children.Add(Tile("Ranked 1v1", (rating > 0 ? "+" : "") + rating, Loc.T("rating, across logged games"), rating > 0 ? Green : rating < 0 ? Red : null));
        yield return Card(Section("Logged by Pulse", tiles));

        foreach (var s in all.Take(12))
        {
            var sp = new StackPanel();
            sp.Children.Add(SessionHead(s));
            sp.Children.Add(new Border { Height = 10 });
            sp.Children.Add(MatchHeader());
            foreach (var m in s.Matches) sp.Children.Add(MatchRow(m));
            yield return Card(sp);
        }
    }

    FrameworkElement SessionHead(BhSession s)
    {
        var g = new Grid();
        var left = new StackPanel();
        string when = s.Start.Date == DateTime.Today ? Loc.T("Today") : s.Start.Date == DateTime.Today.AddDays(-1) ? Loc.T("Yesterday") : s.Start.ToString("ddd d MMM", CultureInfo.CurrentCulture);
        left.Children.Add(new TextBlock { Text = Loc.T("Session") + " · " + when, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont") });
        left.Children.Add(Dim($"{s.Start:t} – {s.End:t}", 12, 2));
        g.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        double win = 100.0 * s.Wins / Math.Max(1, s.Games);
        right.Children.Add(HeadStat("Record", Loc.F("{0}W / {1}L", s.Wins, s.Games - s.Wins) + $" ({win:0}%)").Also(e => e.Margin = new Thickness(0, 0, 26, 0)));
        right.Children.Add(HeadStat("KOs", s.Kos.ToString("N0", CultureInfo.CurrentCulture)).Also(e => e.Margin = new Thickness(0, 0, 26, 0)));
        right.Children.Add(HeadStat("Damage / game", (s.Damage / (double)Math.Max(1, s.Games)).ToString("N0", CultureInfo.CurrentCulture)).Also(e => e.Margin = new Thickness(0, 0, 26, 0)));
        if (s.Matches.Any(m => m.Mode == "1v1"))
            right.Children.Add(HeadStat("Ranked 1v1", (s.RatingChange > 0 ? "+" : "") + s.RatingChange).Also(e => e.Margin = new Thickness(0, 0, 26, 0)));
        right.Children.Add(HeadStat("Played", Hours(s.Seconds)));
        g.Children.Add(right);
        return g;
    }

    FrameworkElement MatchHeader() => GridRow(MatchCols, true, "Match", "Mode", "Rating", "KOs", "Falls", "Damage", "XP", "Length");

    FrameworkElement MatchRow(BhMatch m)
    {
        string name = LegendName(m.LegendId);
        var row = GridRow(MatchCols, false, null, null, null, m.Kos.ToString("N0", CultureInfo.CurrentCulture), m.Falls.ToString("N0", CultureInfo.CurrentCulture),
            m.Damage.ToString("N0", CultureInfo.CurrentCulture), m.Xp > 0 ? m.Xp.ToString("N0", CultureInfo.CurrentCulture) : "–", Clock(m.Seconds / Math.Max(1, m.Games)));

        // Who and how it went: portrait, result, legend and when.
        var who = new StackPanel { Orientation = Orientation.Horizontal };
        bool won = m.Wins * 2 > m.Games, lost = m.Wins * 2 < m.Games;
        var rc = won ? Green : lost ? Red : Grey;
        who.Children.Add(new Border { Width = 3, Height = 34, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(rc), Margin = new Thickness(0, 0, 10, 0) });
        who.Children.Add(Portrait(name, 34, 7));
        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        string result = m.Games == 1 ? Loc.T(m.Wins == 1 ? "Victory" : "Defeat") : Loc.F("{0}W / {1}L", m.Wins, m.Losses);
        text.Children.Add(new TextBlock { Text = result, FontSize = 13.5, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(rc) });
        text.Children.Add(new TextBlock { Text = name + " · " + Ago(m.Time), FontSize = 11.5, Foreground = Res("TextDimBrush") });
        who.Children.Add(text);
        PutIn(row, 0, who);

        string mode = m.Mode switch { "1v1" => "Ranked 1v1", "2v2" => "Ranked 2v2", "3v3" => "Ranked 3v3", _ => "Casual" };
        var modeBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        modeBox.Children.Add(Badge(Loc.T(mode), m.Ranked ? Gold : Grey).Also(b => b.HorizontalAlignment = HorizontalAlignment.Left));
        if (m.Partner.Length > 0) modeBox.Children.Add(Dim(Loc.F("with {0}", m.Partner), 11, 3).Also(t => t.TextTrimming = TextTrimming.CharacterEllipsis));
        PutIn(row, 1, modeBox);

        if (m.RatingAfter > 0)
        {
            var rt = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            rt.Children.Add(Dot(TierColor(Brawlhalla.TierOf(m.RatingAfter))).Also(d => d.Margin = new Thickness(0, 0, 6, 0)));
            rt.Children.Add(new TextBlock { Text = m.RatingAfter.ToString("N0", CultureInfo.CurrentCulture), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
            int d = m.RatingChange;
            if (d != 0) rt.Children.Add(new TextBlock { Text = " " + (d > 0 ? "+" : "") + d, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(d > 0 ? Green : Red), VerticalAlignment = VerticalAlignment.Center });
            PutIn(row, 2, rt);
        }
        else PutIn(row, 2, new TextBlock { Text = "–", FontSize = 13, Foreground = Res("TextFaintBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        return Clickable(row, () => ShowLegend(m.LegendId));
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return Loc.T("just now");
        if (d.TotalMinutes < 60) return Loc.F("{0} min ago", (int)d.TotalMinutes);
        if (d.TotalHours < 24) return Loc.F("{0} h ago", (int)d.TotalHours);
        if (d.TotalDays < 7) return Loc.F("{0} days ago", (int)d.TotalDays);
        return t.ToString("d MMM", CultureInfo.CurrentCulture);
    }

    FrameworkElement Switch(string label, bool on, Action<bool> set)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 28, 6), HorizontalAlignment = HorizontalAlignment.Left };
        var cb = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = on, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        cb.Click += (_, _) => set(cb.IsChecked == true);
        sp.Children.Add(cb);
        sp.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 12.5, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }
}
