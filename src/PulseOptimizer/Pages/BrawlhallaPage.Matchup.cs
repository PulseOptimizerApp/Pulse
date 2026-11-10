using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pulse.Core;
using Pulse.Core.Games.Brawlhalla;

namespace Pulse.Pages;

/// <summary>
/// Matchup: you against the player you just got matched with. Brawlhalla doesn't tell other apps who that is (and
/// Pulse doesn't read the game), so you type the name from the loading screen; Ctrl+Shift+B brings this up while
/// Brawlhalla is open. Shows the projected win chance, their best legends and weapons, how your numbers compare, and
/// their smurf factor.
/// </summary>
public sealed partial class BrawlhallaPage
{
    bool _matchup, _foeBusy;
    BhProfile _me, _foe;
    DateTime _meAt;
    List<BhRanking> _foeResults;
    string _foeStatus;
    TextBox _foeBox;
    CancellationTokenSource _foeCts;

    /// <summary>Shows the matchup panel (from the hotkey or the chip), with the name box ready to type in.</summary>
    void ShowMatchup()
    {
        _matchup = true;
        Build();
        _scroll.ScrollToTop();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { _foeBox?.Focus(); Keyboard.Focus(_foeBox); _foeBox?.SelectAll(); });
        // Your own numbers, fresh enough (your rating moves every ranked match).
        if (S.MyId > 0 && (_me == null || DateTime.Now - _meAt > TimeSpan.FromMinutes(2))) _ = LoadMe();
    }

    async Task LoadMe()
    {
        try
        {
            var me = await Task.Run(() => Brawlhalla.Profile(S.MyId, lookUpTier: false));
            if (me != null) { _me = me; _meAt = DateTime.Now; }
        }
        catch { }
        if (_matchup) Build();
    }

    async Task FindFoe(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0 || _foeBusy) return;
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0) { await LoadFoe(id); return; }
        if (text.Length < 2) { _foeStatus = Loc.T("Type at least two letters of their name."); Build(); return; }
        if (text.Any(c => c > 127))
        {
            _foeStatus = Loc.T("Brawlhalla's search doesn't take accented letters or symbols. Use their Brawlhalla ID instead (in game: Inventory, Profile).");
            Build();
            return;
        }
        _foeBusy = true; _foeStatus = Loc.T("Searching…"); Build();
        try
        {
            _foeResults = await Task.Run(() => Brawlhalla.Find(text));
            _foeStatus = _foeResults.Count == 0 ? Loc.T("No ranked player with that name. Try their Brawlhalla ID instead.") : null;
            // An exact name: straight to them.
            var exact = _foeResults.Where(r => string.Equals(r.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1 || _foeResults.Count == 1) { _foeBusy = false; await LoadFoe((exact.Count == 1 ? exact[0] : _foeResults[0]).Id); return; }
        }
        catch (Exception ex) { _foeStatus = Problem(ex); }
        _foeBusy = false;
        Build();
    }

    async Task LoadFoe(long id)
    {
        _foeCts?.Cancel();
        _foeCts = new CancellationTokenSource();
        var ct = _foeCts.Token;
        _foeBusy = true; _foeStatus = Loc.T("Loading their stats…"); Build();
        try
        {
            if (_legends.Count == 0) try { _legends = await Task.Run(() => Brawlhalla.Legends(ct)); } catch { }
            var p = await Task.Run(() => Brawlhalla.Profile(id, ct));
            if (ct.IsCancellationRequested) return;
            if (p == null) _foeStatus = Loc.T("Brawlhalla's servers sent no stats for that player. If the ID is right, try again in a moment.");
            else
            {
                _foe = p; _foeResults = null; _foeStatus = null;
                S.Opponents.RemoveAll(r => r.Id == p.Id);
                S.Opponents.Insert(0, new BhRecent { Id = p.Id, Name = p.Name });
                if (S.Opponents.Count > 8) S.Opponents.RemoveRange(8, S.Opponents.Count - 8);
                TweakEngine.Save();
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _foeStatus = Problem(ex); }
        _foeBusy = false;
        Build();
    }

    IEnumerable<FrameworkElement> MatchupParts()
    {
        // Who: a name box, recent opponents, and players found.
        var top = new StackPanel();
        top.Children.Add(Heading("Matchup"));
        top.Children.Add(Dim(Loc.T("Who are you up against? Type the name from the loading screen. Brawlhalla doesn't tell other apps who you're matched with, so Pulse asks you instead of reading the game."), 12.5, 4));
        var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { MaxWidth = 420 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _foeBox = new TextBox { Style = (Style)FindResource("SearchBox"), Tag = Loc.T("Opponent's name or Brawlhalla ID"), FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center };
        row.Children.Add(_foeBox);
        var go = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T("Check"), Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(16, 6, 16, 6), IsEnabled = !_foeBusy };
        Grid.SetColumn(go, 1);
        row.Children.Add(go);
        var box = _foeBox;
        go.Click += (_, _) => _ = FindFoe(box.Text);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = FindFoe(box.Text); } };
        top.Children.Add(row);
        if (_foeStatus != null) top.Children.Add(new TextBlock { Text = _foeStatus, FontSize = 12, Foreground = Res(_foeBusy ? "TextDimBrush" : "BadBrush"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        if (S.Opponents.Count > 0)
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var o in S.Opponents.Take(8))
                chips.Children.Add(Chip(o.Name.Length > 0 ? o.Name : o.Id.ToString(CultureInfo.InvariantCulture), _foe?.Id == o.Id, () => _ = LoadFoe(o.Id)));
            top.Children.Add(chips);
        }
        top.Children.Add(Switch("Ctrl+Shift+B opens Matchup while Brawlhalla is open", S.MatchupHotkey, v => { S.MatchupHotkey = v; TweakEngine.Save(); })
            .Also(e => e.Margin = new Thickness(0, 12, 0, 0)));
        if (!Pulse.Ui.BrawlhallaWatch.HotkeyWorks)
            top.Children.Add(new TextBlock { Text = Loc.T("Another app already uses Ctrl+Shift+B, so the shortcut can't work."), FontSize = 11.5, Foreground = Res("WarnBrush"), Margin = new Thickness(0, 4, 0, 0) });
        if (_foeResults is { Count: > 0 })
        {
            top.Children.Add(new Border { Height = 10 });
            top.Children.Add(BoardHeader());
            foreach (var r in _foeResults) top.Children.Add(RankRow(r, true, false, r.Id > 0 ? () => _ = LoadFoe(r.Id) : null));
        }
        yield return Card(top);
        if (_foe == null) yield break;

        var foe = _foe;
        yield return Versus(_me, foe);

        // Their legends: most played, with how well each goes.
        var legends = new StackPanel();
        var best = BrawlhallaMatchup.BestLegend(foe);
        if (best != null)
            legends.Children.Add(Dim(Loc.F("Their best: {0}, winning {1:0}% of {2} games.", LegendName(best.LegendId), 100.0 * best.Wins / best.Games, best.Games.ToString("N0", CultureInfo.CurrentCulture)), 12.5, 0).Also(t => t.Margin = new Thickness(0, 0, 0, 10)));
        legends.Children.Add(LegendHeader());
        int all = Math.Max(1, foe.Legends.Sum(l => l.Games));
        foreach (var l in BrawlhallaMatchup.TopLegends(foe, 6)) legends.Children.Add(LegendRow(l, all, clickable: false));
        yield return Card(Section("Their legends", legends));

        // Their weapons: what you'll be dodging.
        var ws = BrawlhallaMatchup.Weapons(foe, _legends);
        if (ws.Count > 0)
        {
            long total = Math.Max(1, ws.Sum(w => w.Time)), most = Math.Max(1, ws[0].Time);
            var weapons = new StackPanel();
            weapons.Children.Add(GridRow(MatchupWeaponCols, true, "Weapon", "Time held", "Share", "KOs", "KOs / hour"));
            foreach (var w in ws.Take(5))
            {
                var r = GridRow(MatchupWeaponCols, false, null, null, $"{100.0 * w.Time / total:0.0}%", w.Kos.ToString("N0", CultureInfo.CurrentCulture),
                    (w.Kos / (w.Time / 3600.0)).ToString("0.0", CultureInfo.CurrentCulture));
                PutIn(r, 0, WeaponName(w.Weapon, 22));
                PutIn(r, 1, Bar(w.Time / (double)most, Gold, 160).Also(b => b.VerticalAlignment = VerticalAlignment.Center));
                weapons.Children.Add(r);
            }
            yield return Card(Section("Their weapons", weapons));
        }

        // You next to them, and your best picks.
        if (_me != null && _me.Id != foe.Id) yield return Card(Section("You and them", Compare(_me, foe)));
        yield return Card(SmurfPanel(foe));
        var open = Ghost(Loc.F("Open {0}'s full profile", foe.Name), () => { _matchup = false; _ = Open(foe.Id); });
        yield return open.Also(b => b.Margin = new Thickness(0, 14, 0, 0));
    }

    static readonly double[] MatchupWeaponCols = { 190, double.NaN, 80, 90, 100 };

    FrameworkElement Versus(BhProfile me, BhProfile foe)
    {
        var odds = BrawlhallaMatchup.Odds(me, foe);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(Side(me, left: true));
        PutAt(g, 2, Side(foe, left: false));

        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 16, 0) };
        mid.Children.Add(Caps("Projected win chance", 10.5, Res("TextFaintBrush")).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center));
        if (odds == null)
        {
            mid.Children.Add(new TextBlock { Text = "–", FontSize = 40, FontWeight = FontWeights.Bold, Foreground = Res("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Center });
            mid.Children.Add(Dim(me == null ? Loc.T("Set \"This is me\" on your profile so Pulse knows your rating.") : Loc.T("Not enough games to tell."), 11.5, 2).Also(t => t.TextAlignment = TextAlignment.Center));
        }
        else
        {
            double pct = odds.Chance * 100;
            var c = pct >= 55 ? Green : pct <= 45 ? Red : Gold;
            mid.Children.Add(new TextBlock { Text = $"{pct:0}%", FontSize = 46, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(c), HorizontalAlignment = HorizontalAlignment.Center });
            // You (green) against them (red).
            var bar = new Grid { Height = 8, Margin = new Thickness(0, 6, 0, 0) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.01, odds.Chance), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.01, 1 - odds.Chance), GridUnitType.Star) });
            bar.Children.Add(new Border { Background = new SolidColorBrush(Green), CornerRadius = new CornerRadius(4, 0, 0, 4), Margin = new Thickness(0, 0, 1, 0) });
            bar.Children.Add(new Border { Background = new SolidColorBrush(Red), CornerRadius = new CornerRadius(0, 4, 4, 0) }.Also(b => Grid.SetColumn(b, 1)));
            mid.Children.Add(bar);
            string basis = odds.Basis == "ranked"
                ? Loc.F("From ranked 1v1 ratings: {0} against {1}", odds.MyRating.ToString("N0", CultureInfo.CurrentCulture), odds.TheirRating.ToString("N0", CultureInfo.CurrentCulture))
                : Loc.T("Rough: from both overall win rates (one of you has no ranked 1v1 this season)");
            mid.Children.Add(Dim(basis, 11.5, 8).Also(t => t.TextAlignment = TextAlignment.Center));
            if (BrawlhallaMatches.Smurf(foe).Score >= 35)
                mid.Children.Add(new TextBlock { Text = Loc.T("They may be a smurf: likely better than their rating."), FontSize = 11.5, Foreground = new SolidColorBrush(Red), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        }
        PutAt(g, 1, mid);

        var layers = new Grid();
        layers.Children.Add(new Border { Background = new LinearGradientBrush(A(Green, 0x16), A(Red, 0x16), 0) });
        layers.Children.Add(new Border { Child = g, Padding = new Thickness(24, 22, 24, 22) });
        layers.SizeChanged += (_, e) => layers.Clip = new RectangleGeometry(new Rect(e.NewSize), 13, 13);
        return new Border { Child = layers, Background = new SolidColorBrush(PanelBg), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 14, 0, 0), BorderBrush = new SolidColorBrush(A(Gold, 0x55)), BorderThickness = new Thickness(1) };
    }

    FrameworkElement Side(BhProfile p, bool left)
    {
        var sp = new StackPanel { HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var align = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        sp.Children.Add(Caps(left ? "You" : "Them", 10.5, Res("TextFaintBrush")).Also(t => t.HorizontalAlignment = align));
        if (p == null)
        {
            sp.Children.Add(Dim(Loc.T("Set \"This is me\" on your own profile first."), 12.5, 6).Also(t => { t.HorizontalAlignment = align; t.MaxWidth = 220; }));
            return sp;
        }
        var main = p.Legends.Where(l => l.Games > 0).OrderByDescending(l => l.Games).FirstOrDefault();
        var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = align, Margin = new Thickness(0, 8, 0, 0) };
        var portrait = Portrait(main != null ? LegendName(main.LegendId) : "", 56, 12, 2);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = left ? new Thickness(12, 0, 0, 0) : new Thickness(0, 0, 12, 0) };
        text.Children.Add(new TextBlock { Text = p.Name, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), HorizontalAlignment = align, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis });
        var one = p.OneVsOne;
        if (one != null)
        {
            var r = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = align, Margin = new Thickness(0, 3, 0, 0) };
            r.Children.Add(new TextBlock { Text = one.Rating.ToString("N0", CultureInfo.CurrentCulture), FontSize = 15, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(TierColor(one.Tier)), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            r.Children.Add(TierPill(one.Tier));
            text.Children.Add(r);
        }
        else text.Children.Add(Dim(Loc.T("No ranked 1v1 this season"), 11.5, 3).Also(t => t.HorizontalAlignment = align));
        text.Children.Add(Dim(Loc.F("Level {0} · {1}% wins", p.Level, p.Games > 0 ? (100 * p.Wins / p.Games) : 0), 11.5, 3).Also(t => t.HorizontalAlignment = align));
        if (left) { line.Children.Add(portrait); line.Children.Add(text); }
        else { line.Children.Add(text); line.Children.Add(portrait); }
        sp.Children.Add(line);
        return sp;
    }

    /// <summary>Your numbers next to theirs, the better one highlighted; then your legends that win most.</summary>
    FrameworkElement Compare(BhProfile me, BhProfile foe)
    {
        static (int Kos, int Falls, int Self, long Dealt, long Taken, long Time, int Games) Totals(BhProfile p)
        {
            var ls = p.Legends.Where(l => l.Games > 0).ToList();
            return (ls.Sum(l => l.Kos), ls.Sum(l => l.Falls), ls.Sum(l => l.Suicides), ls.Sum(l => l.DamageDealt), ls.Sum(l => l.DamageTaken), ls.Sum(l => l.MatchTime), Math.Max(1, ls.Sum(l => l.Games)));
        }
        var a = Totals(me); var b = Totals(foe);
        var sp = new StackPanel();
        sp.Children.Add(GridRow(CompareCols, true, "", "You", "Them"));
        void Row(string label, double mine, double theirs, string fmt, bool higherBetter)
        {
            var r = GridRow(CompareCols, false, Loc.T(label), null, null);
            bool meBetter = higherBetter ? mine > theirs : mine < theirs;
            PutIn(r, 1, Num(mine.ToString(fmt, CultureInfo.CurrentCulture), meBetter ? Green : (Color?)null));
            PutIn(r, 2, Num(theirs.ToString(fmt, CultureInfo.CurrentCulture), !meBetter && mine != theirs ? Red : (Color?)null));
            sp.Children.Add(r);
        }
        Row("Win %", 100.0 * me.Wins / Math.Max(1, me.Games), 100.0 * foe.Wins / Math.Max(1, foe.Games), "0.0", true);
        Row("KO / fall", a.Falls > 0 ? a.Kos / (double)a.Falls : 0, b.Falls > 0 ? b.Kos / (double)b.Falls : 0, "0.00", true);
        Row("KOs / game", a.Kos / (double)a.Games, b.Kos / (double)b.Games, "0.00", true);
        Row("Damage per game", a.Dealt / (double)a.Games, b.Dealt / (double)b.Games, "N0", true);
        Row("Damage ratio", a.Taken > 0 ? a.Dealt / (double)a.Taken : 0, b.Taken > 0 ? b.Dealt / (double)b.Taken : 0, "0.00", true);
        Row("Self-KOs per 100 falls", a.Falls > 0 ? 100.0 * a.Self / a.Falls : 0, b.Falls > 0 ? 100.0 * b.Self / b.Falls : 0, "0.0", false);
        Row("Games", me.Games, foe.Games, "N0", true);

        var picks = me.Legends.Where(l => l.Games >= 20).OrderByDescending(l => l.Wins / (double)l.Games).Take(3).ToList();
        if (picks.Count > 0)
        {
            sp.Children.Add(Caps("Your best picks", 10.5, Res("TextFaintBrush")).Also(t => t.Margin = new Thickness(0, 16, 0, 8)));
            var w = new WrapPanel();
            foreach (var l in picks)
            {
                var c = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 22, 6) };
                c.Children.Add(Portrait(LegendName(l.LegendId), 36, 8));
                var t = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                t.Children.Add(new TextBlock { Text = LegendName(l.LegendId), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
                t.Children.Add(Dim(Loc.F("{0:0}% wins in {1} games", 100.0 * l.Wins / l.Games, l.Games.ToString("N0", CultureInfo.CurrentCulture)), 11.5, 1));
                c.Children.Add(t);
                w.Children.Add(c);
            }
            sp.Children.Add(w);
        }
        return sp;
    }

    static readonly double[] CompareCols = { 220, 120, 120 };

    FrameworkElement Num(string text, Color? c) => new TextBlock
    {
        Text = text, FontSize = 13.5, FontWeight = c != null ? FontWeights.Bold : FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Right,
        Foreground = c is { } cc ? new SolidColorBrush(cc) : Res("TextDimBrush"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
    };
}
