using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Pulse.Core;
using Pulse.Core.Games;
using Pulse.Core.Games.Live;
using Pulse.Core.Games.Valorant;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// A match in tracker.gg's layout, in a panel over the profile: the header (mode, map, score, date, length, average
/// rank), then Scoreboard (round timeline and both teams), Performance (one player round by round, attack and
/// defense, opponents and weapons), Economy (the credit swing between the teams), Rounds (each round's players,
/// event log and where everyone stood) and Duels (who beat whom), plus Pulse's own Autopsy.
/// </summary>
public partial class ValorantPage
{
    static readonly Color TeamA = Color.FromRgb(0x2D, 0xD4, 0xA7), TeamB = Color.FromRgb(0xFF, 0x54, 0x68),
        TeamABar = Color.FromRgb(0x17, 0x4D, 0x44), TeamBBar = Color.FromRgb(0x52, 0x1F, 0x2C), MatchHeadBg = Color.FromRgb(0x27, 0x30, 0x3C);

    static string _sbSort = "acs";
    static bool _sbAsc;
    static string _pfPlayer;
    static int _rdRound;
    static int _rdEvent = -1;
    static string _ecoMode = "total";
    bool _matchHooked;

    const string GlyphElim = "M5 5 L19 19 M19 5 L5 19 M3 3 H21 V21 H3 Z";
    const string GlyphDefuse = "M12 3 L19.5 6 V12 C19.5 16.4 16.3 19.8 12 21 C7.7 19.8 4.5 16.4 4.5 12 V6 Z M8.5 12 H15.5";
    const string GlyphDetonate = "M12 2 L14 9 L21 8 L15.5 12.5 L19 20 L12 15.5 L5 20 L8.5 12.5 L3 8 L10 9 Z";
    const string GlyphTime = "M12 3 A9 9 0 1 0 12.01 3 Z M12 7 V12 L15.5 14.5";
    const string GlyphFlag = "M6 21 V4 M6 4 H17 L14.5 8 L17 12 H6";
    const string GlyphSkull = "M12 3 C7 3 4 6.5 4 10.5 C4 13 5.2 14.7 7 15.6 V19.5 H17 V15.6 C18.8 14.7 20 13 20 10.5 C20 6.5 17 3 12 3 Z M9 9 A1.7 1.7 0 1 0 9.01 9 Z M15 9 A1.7 1.7 0 1 0 15.01 9 Z";

    static string RoundGlyph(string result) => result switch
    {
        "Bomb defused" or "Defuse" => GlyphDefuse,
        "Bomb detonated" or "Detonate" => GlyphDetonate,
        "Round timer expired" => GlyphTime,
        "Surrendered" => GlyphFlag,
        _ => GlyphElim,
    };

    static FrameworkElement Skull(Color c, double size) => new System.Windows.Shapes.Path
    {
        Data = Geometry.Parse(GlyphSkull), Fill = B(c), Width = size, Height = size, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center,
    };

    static string TeamLabel(string team) => team == "Blue" ? "Team A" : team == "Red" ? "Team B" : team;
    static Color TeamColor(string team) => team == "Blue" ? TeamA : TeamB;
    static int TeamScore(VMatch m, string team) => m.Rounds.Count(r => r.Winner == team);

    // ───────── opening and closing ─────────

    void OpenMatch(string id)
    {
        _trMatchId = id;
        _trMatchTab = "scoreboard";
        _pfPlayer = null;
        _rdRound = 0;
        _rdEvent = -1;
        ShowMatch(true);
    }

    void CloseMatch()
    {
        _trMatchId = null;
        MatchLayer.Visibility = Visibility.Collapsed;
        MatchBody.Content = null;
    }

    void HookMatchLayer()
    {
        if (_matchHooked) return;
        _matchHooked = true;
        MatchDim.MouseLeftButtonUp += (_, _) => CloseMatch();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && MatchLayer.Visibility == Visibility.Visible) { CloseMatch(); e.Handled = true; }
        };
    }

    static VMatch FindMatch(string id) => (_trSearched?.Matches ?? new()).Concat(VStore.All).FirstOrDefault(m => m.Id == id);

    string MatchViewer(VMatch m)
    {
        var me = CoachMatches().FirstOrDefault()?.Me;
        string viewer = _trSearched != null && _trSel == _trSearched.Puuid && !_trSearched.FromYourMatches ? _trSearched.Puuid : _trSel ?? me ?? m.Me;
        if (m.Players.All(x => x.Id != viewer)) viewer = m.Me;
        return viewer;
    }

    void ShowMatch(bool opening = false)
    {
        HookMatchLayer();
        var m = _trMatchId == null ? null : FindMatch(_trMatchId);
        if (m == null) { CloseMatch(); return; }
        var viewer = MatchViewer(m);
        LoadMatchNames(m);
        try
        {
            MatchHead.Content = MatchHeader(m);
            MatchTabs.Content = MatchTabBar(m);
            MatchBody.Content = MatchTabBody(m, viewer);
        }
        catch (Exception ex)
        {
            TweakEngine.AddLog("valorant", "Match page failed: " + ex.GetType().Name + ": " + ex.Message);
            MatchBody.Content = Txt(Loc.T("This match couldn't be shown. Open VALORANT so Pulse can download it again."), 13, TgDim).Also(t => t.Margin = new Thickness(20));
        }
        if (opening || MatchLayer.Visibility != Visibility.Visible)
        {
            MatchLayer.Visibility = Visibility.Visible;
            MatchScroll.ScrollToTop();
            MatchScroll.ScrollToLeftEnd();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            MatchLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            var tt = new TranslateTransform();
            MatchPanel.RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            Focusable = true;
            Keyboard.Focus(this);
        }
    }

    // ───────── header and tabs ─────────

    FrameworkElement MatchHeader(VMatch m)
    {
        var g = new Grid { Background = B(MatchHeadBg) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 12, 18, 12), VerticalAlignment = VerticalAlignment.Center };
        StackPanel Block(string small, UIElement big, double right = 34)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, right, 0), VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(Txt(small, 10.5, TgDim));
            sp.Children.Add(big);
            return sp;
        }
        if (MatchArt.MapPicture(m.Map) is { } pic)
            row.Children.Add(new Border { Width = 56, Height = 36, CornerRadius = new CornerRadius(4), ClipToBounds = true, Margin = new Thickness(0, 0, 12, 0), Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill } });
        row.Children.Add(Block(Loc.T(QueueTitle(m.Queue)), Txt(m.Map, 15, TgText, true)));
        if (m.Queue != "deathmatch")
        {
            var score = new StackPanel { Orientation = Orientation.Horizontal };
            score.Children.Add(Txt(TeamScore(m, "Blue").ToString(CultureInfo.CurrentCulture), 17, TeamA, true));
            score.Children.Add(Txt("  :  ", 15, TgDim, true));
            score.Children.Add(Txt(TeamScore(m, "Red").ToString(CultureInfo.CurrentCulture), 17, TeamB, true));
            var labels = new StackPanel { Orientation = Orientation.Horizontal };
            labels.Children.Add(Txt(Loc.T("Team A"), 10.5, TeamA));
            labels.Children.Add(Txt("   ", 10.5, TgDim));
            labels.Children.Add(Txt(Loc.T("Team B"), 10.5, TeamB));
            var sc = new StackPanel { Margin = new Thickness(0, 0, 34, 0), VerticalAlignment = VerticalAlignment.Center };
            sc.Children.Add(labels);
            sc.Children.Add(score);
            row.Children.Add(sc);
        }
        var len = TimeSpan.FromMilliseconds(m.LengthMs);
        row.Children.Add(Block(m.StartUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            Txt(len.TotalHours >= 1 ? $"{(int)len.TotalHours}h {len.Minutes}m" : $"{len.Minutes}m {len.Seconds}s", 15, TgText, true)));
        var ranked = m.Players.Where(p => p.Tier >= 3).ToList();
        if (ranked.Count >= 2)
        {
            int avg = (int)Math.Round(ranked.Average(p => p.Tier));
            var r = new StackPanel { Orientation = Orientation.Horizontal };
            if (MatchArt.RankIcon(avg) is { } ri) r.Children.Add(Pic(ri, 20, 20, 6));
            r.Children.Add(Txt(ValorantMatches.RankName(avg), 15, TgText, true));
            row.Children.Add(Block(Loc.T("Average Rank"), r));
        }
        g.Children.Add(row);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        Border HeadBtn(string text, string tip, Action go)
        {
            var b = new Border { Padding = new Thickness(10, 6, 10, 7), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand, ToolTip = Loc.T(tip), Margin = new Thickness(4, 0, 0, 0), Child = Txt(text, 13, TgText, true) };
            Hover(b, Colors.Transparent, Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            b.MouseLeftButtonUp += (_, _) => go();
            return b;
        }
        right.Children.Add(HeadBtn("⧉  " + Loc.T("Copy"), "Copy the scoreboard as text, to paste in Discord.", () =>
        {
            try { Clipboard.SetText(MatchText(m)); MainWindow.Instance.Toast(Loc.T("Scoreboard copied")); } catch { }
        }));
        right.Children.Add(HeadBtn("✕", "Close (Esc)", CloseMatch));
        g.Children.Add(right);
        return g;
    }

    /// <summary>The scoreboard as plain text (the header's Copy button).</summary>
    static string MatchText(VMatch m)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"VALORANT · {m.Map} · {QueueTitle(m.Queue)} · {m.StartUtc.ToLocalTime():g}");
        foreach (var team in new[] { "Blue", "Red" })
        {
            var players = m.Players.Where(p => p.Team == team).OrderByDescending(p => p.Score).ToList();
            if (players.Count == 0) continue;
            sb.AppendLine($"{TeamLabel(team)} {TeamScore(m, team)}");
            foreach (var p in players)
            {
                var t = Analysis.Totals(m, p.Id);
                sb.AppendLine($"  {Stats.NameOf(m, p.Id) ?? p.Agent} ({p.Agent}) {p.Kills}/{p.Deaths}/{p.Assists} · {N(t.Acs)} ACS · {N(t.Adr)} ADR");
            }
        }
        return sb.ToString();
    }

    FrameworkElement MatchTabBar(VMatch m)
    {
        var tabs = m.Queue == "deathmatch"
            ? new[] { ("scoreboard", "Scoreboard") }
            : new[] { ("scoreboard", "Scoreboard"), ("performance", "Performance"), ("economy", "Economy"), ("rounds", "Rounds"), ("duels", "Duels"), ("autopsy", "Autopsy") };
        if (tabs.All(t => t.Item1 != _trMatchTab)) _trMatchTab = "scoreboard";
        var g = new UniformGrid { Rows = 1, Columns = tabs.Length, Background = B(MatchHeadBg) };
        foreach (var (key, label) in tabs)
        {
            bool on = _trMatchTab == key;
            var cell = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand, Height = 40 };
            var t = Txt(Loc.T(label), 13, on ? TgText : TgSub, true, HorizontalAlignment.Center);
            cell.Children.Add(t);
            cell.Children.Add(new Border { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, Background = on ? B(TgRed) : B(TgLine) });
            cell.MouseEnter += (_, _) => { if (!on) t.Foreground = B(TgText); };
            cell.MouseLeave += (_, _) => { if (!on) t.Foreground = B(TgSub); };
            var k = key;
            cell.MouseLeftButtonUp += (_, _) => { _trMatchTab = k; ShowMatch(); MatchScroll.ScrollToTop(); };
            g.Children.Add(cell);
        }
        return g;
    }

    FrameworkElement MatchTabBody(VMatch m, string viewer)
    {
        var sp = new StackPanel { MinWidth = 1060 };
        switch (_trMatchTab)
        {
            case "performance": sp.Children.Add(PerformanceGg(m, viewer)); break;
            case "economy": sp.Children.Add(EconomyGg(m)); sp.Children.Add(TeamTables(m, viewer)); break;
            case "rounds": sp.Children.Add(RoundsGg(m)); break;
            case "duels": sp.Children.Add(DuelsGg(m)); break;
            case "autopsy": sp.Children.Add(AutopsyView(m, viewer).Also(x => x.Margin = new Thickness(12))); break;
            default:
                if (m.Queue != "deathmatch") sp.Children.Add(RoundTimeline(m));
                sp.Children.Add(TeamTables(m, viewer));
                break;
        }
        return sp;
    }

    // ───────── scoreboard ─────────

    FrameworkElement RoundTimeline(VMatch m)
    {
        var rounds = m.Rounds.OrderBy(r => r.N).ToList();
        var g = new Grid { Margin = new Thickness(18, 12, 18, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        foreach (var _ in rounds) g.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 30 });
        for (int i = 0; i < 3; i++) g.RowDefinitions.Add(new RowDefinition { Height = i < 2 ? new GridLength(28) : GridLength.Auto });
        foreach (var (team, row) in new[] { ("Blue", 0), ("Red", 1) })
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(Txt(Loc.T(TeamLabel(team)), 10.5, TgDim));
            label.Children.Add(Txt(" " + TeamScore(m, team), 17, TeamColor(team), true));
            Grid.SetRow(label, row); g.Children.Add(label);
        }
        for (int i = 0; i < rounds.Count; i++)
        {
            var r = rounds[i];
            var gap = r.N == m.HalfLength && rounds.Count > m.HalfLength ? 10 : 0;
            foreach (var (team, row) in new[] { ("Blue", 0), ("Red", 1) })
            {
                FrameworkElement el = r.Winner == team
                    ? Icon(RoundGlyph(r.Result), TeamColor(team), 17)
                    : new Ellipse { Width = 3, Height = 3, Fill = B(TgDim) };
                el.HorizontalAlignment = HorizontalAlignment.Center;
                el.VerticalAlignment = VerticalAlignment.Center;
                el.Margin = new Thickness(gap, 0, 0, 0);
                el.ToolTip = $"{Loc.F("Round {0}", r.N + 1)} · {Loc.T(TeamLabel(r.Winner))} · {Loc.T(RoundHow(r.Result))}";
                Grid.SetRow(el, row); Grid.SetColumn(el, i + 1); g.Children.Add(el);
            }
            var n = Txt((r.N + 1).ToString(CultureInfo.CurrentCulture), 10, TgDim, false, HorizontalAlignment.Center).Also(t => t.Margin = new Thickness(gap, 4, 0, 0));
            Grid.SetRow(n, 2); Grid.SetColumn(n, i + 1); g.Children.Add(n);
        }
        return new Border { Background = B(TgPanel), Child = g, BorderBrush = B(TgLine), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    sealed record SbRow(VPlayer P, PlayerTotals T, double Score, int Multi);

    FrameworkElement TeamTables(VMatch m, string viewer)
    {
        var sp = new StackPanel();
        var parties = Parties(m);
        var rows = Memo("sbrows:" + m.Id, () => m.Players.Select(p => new SbRow(p, TotalsOf(m, p.Id), ScoreOf(m, p.Id), Stats.MultiKills(m, p.Id).Skip(3).Sum())).ToList());
        var mvp = rows.OrderByDescending(r => r.P.Score).FirstOrDefault()?.P.Id;
        var cols = new (string Key, string Head, string Tip, double W)[]
        {
            ("rank", "Match Rank", "Their rank in this match", 86), ("pulse", "Pulse", "Pulse Score: one match's overall impact (0–1000)", 74), ("acs", "ACS", "Average combat score", 58),
            ("k", "K", "Kills", 44), ("d", "D", "Deaths", 44), ("a", "A", "Assists", 44), ("pm", "+/-", "Kills minus deaths", 48), ("kd", "K/D", "Kills per death", 52),
            ("dd", "DDΔ", "Damage dealt minus taken, per round", 56), ("adr", "ADR", "Damage per round", 60), ("hs", "HS%", "Headshot rate", 54), ("kast", "KAST", "Rounds with a kill, assist, survival or trade", 58),
            ("fk", "FK", "First kills", 42), ("fd", "FD", "First deaths", 42), ("mk", "MK", "Rounds with 3 or more kills", 42),
        };
        double Key(SbRow r) => _sbSort switch
        {
            "rank" => r.P.Tier, "pulse" => double.IsNaN(r.Score) ? -1 : r.Score, "k" => r.T.Kills, "d" => r.T.Deaths, "a" => r.T.Assists, "pm" => r.T.Kills - r.T.Deaths,
            "kd" => r.T.KdRatio, "dd" => double.IsNaN(r.T.DamageDelta) ? -9999 : r.T.DamageDelta, "adr" => double.IsNaN(r.T.Adr) ? -1 : r.T.Adr,
            "hs" => double.IsNaN(r.T.HeadshotPct) ? -1 : r.T.HeadshotPct, "kast" => double.IsNaN(r.T.KastPct) ? -1 : r.T.KastPct,
            "fk" => r.T.FirstKills, "fd" => r.T.FirstDeaths, "mk" => r.Multi, _ => double.IsNaN(r.T.Acs) ? r.P.Score : r.T.Acs,
        };
        var teams = m.Queue == "deathmatch" ? new[] { "" } : new[] { "Blue", "Red" };
        foreach (var team in teams)
        {
            var lines = rows.Where(r => team.Length == 0 || r.P.Team == team).ToList();
            lines = (_sbAsc ? lines.OrderBy(Key) : lines.OrderByDescending(Key)).ToList();
            if (lines.Count == 0) continue;
            var teamMvp = lines.OrderByDescending(r => r.P.Score).First().P.Id;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 250 });
            foreach (var c in cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c.W) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            var bar = new Border { Background = B(team == "Blue" ? TeamABar : team == "Red" ? TeamBBar : TgHead) };
            Grid.SetColumnSpan(bar, cols.Length + 1); g.Children.Add(bar);
            var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var tc = team.Length == 0 ? TgText : TeamColor(team);
            title.Children.Add(Txt(team.Length == 0 ? Loc.T("Players") : Loc.T(TeamLabel(team)), 11.5, tc, true));
            var ranked = lines.Where(l => l.P.Tier >= 3).ToList();
            if (ranked.Count > 0)
            {
                int avg = (int)Math.Round(ranked.Average(l => l.P.Tier));
                title.Children.Add(Txt("  •  " + Loc.T("Avg. Rank:") + " ", 11, TgSub));
                if (MatchArt.RankIcon(avg) is { } ai) title.Children.Add(Pic(ai, 14, 14, 4));
                title.Children.Add(Txt(ValorantMatches.RankName(avg), 11, tc, true));
            }
            g.Children.Add(title);
            for (int c = 0; c < cols.Length; c++)
            {
                var col = cols[c];
                bool sorted = _sbSort == col.Key;
                var h = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = Loc.T(col.Tip), Child = Txt((sorted ? (_sbAsc ? "▴ " : "▾ ") : "") + Loc.T(col.Head), 11, sorted ? TgText : TgSub, sorted, HorizontalAlignment.Center) };
                var key = col.Key;
                h.MouseLeftButtonUp += (_, _) => { if (_sbSort == key) _sbAsc = !_sbAsc; else { _sbSort = key; _sbAsc = false; } ShowMatch(); };
                Grid.SetColumn(h, c + 1); g.Children.Add(h);
            }
            int r = 1;
            foreach (var l in lines)
            {
                var t = l.T;
                bool you = l.P.Id == viewer;
                g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
                var line = new Border { Cursor = Cursors.Hand, ToolTip = Loc.T("Open their profile"), BorderBrush = B(TgLine), BorderThickness = new Thickness(0, 0, 0, 1) };
                var normal = you ? Color.FromArgb(0x26, TgYellow.R, TgYellow.G, TgYellow.B) : r % 2 == 1 ? TgPanel : TgCard;
                Hover(line, normal, TgHead);
                var pid = l.P.Id;
                line.MouseLeftButtonUp += (_, _) => { CloseMatch(); OpenPlayer(pid); };
                Grid.SetRow(line, r); Grid.SetColumnSpan(line, cols.Length + 1); g.Children.Add(line);
                if (parties.TryGetValue(l.P.Id, out var pc))
                {
                    var stripe = new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Background = B(pc), IsHitTestVisible = false, ToolTip = Loc.T("Queued together") };
                    Grid.SetRow(stripe, r); g.Children.Add(stripe);
                }
                var who = PlayerFace(m, l.P, you, l.P.Id == mvp ? "MVP" : l.P.Id == teamMvp && team.Length > 0 ? "Team MVP" : null);
                who.Margin = new Thickness(8, 0, 0, 0);
                who.IsHitTestVisible = false;
                Grid.SetRow(who, r); g.Children.Add(who);
                int pm = t.Kills - t.Deaths;
                var pulse = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, ToolTip = Loc.T("Pulse Score: one match's overall impact (0–1000)") };
                pulse.Children.Add(ScoreBadge(l.Score, 20).Also(i => i.Margin = new Thickness(0, 0, 7, 0)));
                pulse.Children.Add(Txt(N(l.Score), 14, TgText, true));
                var cells = new UIElement[]
                {
                    MatchArt.RankIcon(l.P.Tier) is { } rk ? Pic(rk, 26, 26, 0).Also(i => i.ToolTip = ValorantMatches.RankName(l.P.Tier)) : Txt("–", 13, TgDim),
                    pulse,
                    Txt(N(t.Acs), 14, TgText, true),
                    Txt(t.Kills.ToString(CultureInfo.CurrentCulture), 14, TgText, true), Txt(t.Deaths.ToString(CultureInfo.CurrentCulture), 14, TgSub, true), Txt(t.Assists.ToString(CultureInfo.CurrentCulture), 14, TgSub, true),
                    Txt(pm == 0 ? "" : Signed(pm), 14, SignColor(pm), true),
                    Txt(N(t.KdRatio, "0.0"), 14, KdColor(t.KdRatio), true),
                    Txt(Signed(t.DamageDelta), 14, SignColor(t.DamageDelta), true),
                    Txt(N(t.Adr, "0.0"), 14, TgText, true),
                    Txt(double.IsNaN(t.HeadshotPct) ? "–" : N(t.HeadshotPct) + "%", 14, TgText, true),
                    Txt(double.IsNaN(t.KastPct) ? "–" : N(t.KastPct) + "%", 14, TgText, true),
                    Txt(t.FirstKills.ToString(CultureInfo.CurrentCulture), 14, TgText, true), Txt(t.FirstDeaths.ToString(CultureInfo.CurrentCulture), 14, TgText, true),
                    Txt(l.Multi.ToString(CultureInfo.CurrentCulture), 14, TgText, true),
                };
                for (int c = 0; c < cells.Length; c++)
                {
                    if (cells[c] is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Center; fe.VerticalAlignment = VerticalAlignment.Center; fe.IsHitTestVisible = c == 0; }
                    if (_sbSort == cols[c].Key)
                    {
                        var shade = new Border { Background = B(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)), IsHitTestVisible = false };
                        Grid.SetRow(shade, r); Grid.SetColumn(shade, c + 1); g.Children.Add(shade);
                    }
                    Grid.SetRow(cells[c], r); Grid.SetColumn(cells[c], c + 1); g.Children.Add(cells[c]);
                }
                r++;
            }
            sp.Children.Add(g);
        }
        sp.Children.Add(Txt(Loc.T("Click a player to open their profile, or a column to sort. Colored edges mark players who queued together."), 11, TgDim).Also(t => t.Margin = new Thickness(14, 8, 14, 12)));
        return sp;
    }

    /// <summary>An agent's face with the account level on it, then the Riot ID (tag dimmer), rank and an MVP tag.</summary>
    FrameworkElement PlayerFace(VMatch m, VPlayer p, bool you, string mvp)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var face = new Grid { Width = 38, Height = 38, Margin = new Thickness(0, 0, 10, 0) };
        face.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4), ClipToBounds = true, ToolTip = p.Agent,
            Background = MatchArt.AgentIcon(p.Agent) is { } ai ? new ImageBrush(ai) { Stretch = Stretch.UniformToFill } : B(TgLine),
            BorderBrush = B(you ? TgYellow : Colors.Transparent), BorderThickness = new Thickness(you ? 1.5 : 0),
        });
        if (p.Level > 0)
            face.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Background = B(Color.FromArgb(0xD0, 0x10, 0x12, 0x16)),
                CornerRadius = new CornerRadius(0, 3, 0, 4), Padding = new Thickness(2, 0, 3, 0), Child = Txt(p.Level.ToString(CultureInfo.CurrentCulture), 8.5, TgText, true),
            });
        row.Children.Add(face);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var riotId = Stats.NameOf(m, p.Id) ?? NameBook.Get(p.Id);
        var line = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = riotId == null ? Loc.T("Name not loaded yet: it shows once the Riot Client is open.") : null };
        int hash = riotId?.LastIndexOf('#') ?? -1;
        if (hash > 0)
        {
            line.Children.Add(Txt(riotId[..hash], 13, you ? TgYellow : TgText, true).Also(t => t.MaxWidth = 150));
            line.Children.Add(Txt(" #" + riotId[(hash + 1)..], 10, TgDim).Also(t => t.VerticalAlignment = VerticalAlignment.Bottom));
        }
        else line.Children.Add(Txt(riotId ?? (p.Agent.Length > 0 ? p.Agent : Loc.T("Unknown player")), 13, you ? TgYellow : TgText, true));
        if (mvp != null)
            line.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = B(A(mvp == "MVP" ? TgYellow : TgSub, 0x26)), Child = Txt(Loc.T(mvp).ToUpper(CultureInfo.CurrentCulture), 8.5, mvp == "MVP" ? TgYellow : TgSub, true),
            });
        names.Children.Add(line);
        var sub = new StackPanel { Orientation = Orientation.Horizontal };
        if (MatchArt.RankIcon(p.Tier) is { } ri) sub.Children.Add(Pic(ri, 12, 12, 4));
        sub.Children.Add(Txt(p.Tier >= 3 ? ValorantMatches.RankName(p.Tier) : riotId != null && p.Agent.Length > 0 ? p.Agent : Loc.T("Unranked"), 10, TgDim));
        names.Children.Add(sub);
        row.Children.Add(names);
        return row;
    }

    // ───────── performance (one player) ─────────

    FrameworkElement PerformanceGg(VMatch m, string viewer)
    {
        var pid = _pfPlayer != null && m.Players.Any(x => x.Id == _pfPlayer) ? _pfPlayer : viewer;
        var p = m.Players.First(x => x.Id == pid);
        var sp = new StackPanel();

        // Who: both teams' agents; click one.
        var pick = new Grid { Height = 56 };
        pick.ColumnDefinitions.Add(new ColumnDefinition());
        pick.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        pick.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (team, col) in new[] { ("Blue", 0), ("Red", 2) })
        {
            var c = TeamColor(team);
            var bg = new Border { Background = team == "Blue" ? new LinearGradientBrush(A(c, 0x05), A(c, 0x40), 0) : new LinearGradientBrush(A(c, 0x40), A(c, 0x05), 0) };
            Grid.SetColumn(bg, col); pick.Children.Add(bg);
            var faces = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = team == "Blue" ? HorizontalAlignment.Right : HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            if (team == "Blue") faces.Children.Add(Txt(Loc.T(TeamLabel(team)), 12, c, true).Also(t => t.Margin = new Thickness(0, 0, 12, 0)));
            foreach (var x in m.Players.Where(x => x.Team == team).OrderByDescending(x => x.Score))
            {
                bool on = x.Id == pid;
                var f = new Grid { Width = 40, Height = 46, Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand, ToolTip = (Stats.NameOf(m, x.Id) ?? x.Agent) + " · " + x.Agent };
                f.Children.Add(new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(4), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Top, Opacity = on ? 1 : 0.75, Background = MatchArt.AgentIcon(x.Agent) is { } ai ? new ImageBrush(ai) { Stretch = Stretch.UniformToFill } : B(TgLine) });
                f.Children.Add(new Border { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Background = on ? B(TgRed) : Brushes.Transparent });
                var id = x.Id;
                f.MouseLeftButtonUp += (_, _) => { _pfPlayer = id; ShowMatch(); };
                faces.Children.Add(f);
            }
            if (team == "Red") faces.Children.Add(Txt(Loc.T(TeamLabel(team)), 12, c, true).Also(t => t.Margin = new Thickness(12, 0, 0, 0)));
            Grid.SetColumn(faces, col); pick.Children.Add(faces);
        }
        var vs = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = B(TgHead), Child = Txt("VS", 10, TgDim, true, HorizontalAlignment.Center), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(vs, 1); pick.Children.Add(vs);
        sp.Children.Add(pick);

        // The player card.
        var tc = TeamColor(p.Team);
        var t = TotalsOf(m, pid);
        var card = new Grid { Background = new LinearGradientBrush(A(tc, 0x26), A(tc, 0x08), 0) };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.ColumnDefinitions.Add(new ColumnDefinition());
        var portrait = MatchArt.AgentBust(p.Agent) ?? MatchArt.AgentIcon(p.Agent);
        if (portrait != null) card.Children.Add(new Image { Source = portrait, Width = 130, Height = 120, Stretch = Stretch.UniformToFill, Margin = new Thickness(18, 8, 18, 0), VerticalAlignment = VerticalAlignment.Bottom });
        var info = new StackPanel { Margin = new Thickness(portrait == null ? 18 : 0, 16, 18, 14) };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        if (MatchArt.RankIcon(p.Tier) is { } rnk) nameRow.Children.Add(Pic(rnk, 26, 26, 8));
        var riotId = Stats.NameOf(m, pid) ?? NameBook.Get(pid) ?? p.Agent;
        int hash = riotId.LastIndexOf('#');
        nameRow.Children.Add(Txt(hash > 0 ? riotId[..hash] : riotId, 22, TgText, true));
        if (hash > 0) nameRow.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = B(TgHead), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = Txt("#" + riotId[(hash + 1)..], 13, TgSub, true) });
        info.Children.Add(nameRow);
        var nums = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        void Num(string label, string value) => nums.Children.Add(LabelValue(label, value, TgText, 17).Also(s => s.Margin = new Thickness(0, 0, 28, 0)));
        Num("K/D/A", $"{t.Kills} / {t.Deaths} / {t.Assists}");
        Num("K/D", N(t.KdRatio, "0.00"));
        Num("ADR", N(t.Adr));
        Num("ACS", N(t.Acs));
        Num("HS%", double.IsNaN(t.HeadshotPct) ? "–" : N(t.HeadshotPct, "0.0") + "%");
        Num("KAST", double.IsNaN(t.KastPct) ? "–" : N(t.KastPct, "0.0") + "%");
        info.Children.Add(nums);
        Grid.SetColumn(info, 1); card.Children.Add(info);
        var view = new Border { Padding = new Thickness(12, 6, 12, 7), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 16, 18, 0), Child = Txt(Loc.T("View Profile"), 12, TgText, true) };
        Hover(view, Color.FromArgb(0x50, 0x40, 0x46, 0x52), Color.FromArgb(0x80, 0x50, 0x56, 0x62));
        view.MouseLeftButtonUp += (_, _) => { CloseMatch(); OpenPlayer(pid); };
        Grid.SetColumn(view, 1); card.Children.Add(view);
        sp.Children.Add(card);

        // Round by round: kills as skulls, a red skull if they died, a green or red line for the round.
        var strip = new UniformGrid { Rows = 1, Columns = Math.Max(1, m.Rounds.Count), Margin = new Thickness(12, 10, 12, 10), Height = 106 };
        foreach (var r in m.Rounds.OrderBy(r => r.N))
        {
            int k = Stats.RoundKills(m, r.N, pid);
            bool died = m.Kills.Any(x => x.Round == r.N && x.Victim == pid);
            bool won = r.Winner == p.Team;
            var col = new Grid { Margin = new Thickness(2, 0, 2, 0), Background = B(TgCard), ToolTip = Loc.F("Round {0}", r.N + 1) + " · " + Loc.F("{0} kills", k) + (died ? " · " + Loc.T("died") : "") + " · " + Loc.T(won ? "Won" : "Lost") };
            col.Children.Add(Txt((r.N + 1).ToString(CultureInfo.CurrentCulture), 10, TgDim, false, HorizontalAlignment.Center).Also(x => { x.VerticalAlignment = VerticalAlignment.Top; x.Margin = new Thickness(0, 5, 0, 0); }));
            var skulls = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 22) };
            for (int i = 0; i < Math.Min(k, 5); i++) skulls.Children.Add(Skull(TgText, 11).Also(x => x.Margin = new Thickness(0, 1, 0, 1)));
            col.Children.Add(skulls);
            col.Children.Add(new Border { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(5, 0, 5, 17), Background = B(won ? TgWin : TgLoss) });
            if (died) col.Children.Add(Skull(TgLoss, 10).Also(x => { x.VerticalAlignment = VerticalAlignment.Bottom; x.Margin = new Thickness(0, 0, 0, 3); }));
            strip.Children.Add(col);
        }
        sp.Children.Add(new Border { Background = B(TgPanel), Child = strip });

        // Attack and defense.
        var (atk, def) = Stats.Sides(m, pid);
        sp.Children.Add(Txt(Loc.T("All Rounds"), 16, TgText, true).Also(x => x.Margin = new Thickness(18, 10, 18, 6)));
        var sides = new UniformGrid { Columns = 2, Margin = new Thickness(18, 0, 6, 12) };
        foreach (var (label, s, icon) in new[] { ("Attack", atk, GlyphDetonate), ("Defense", def, GlyphDefuse) })
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            var head = new Grid();
            var hl = new StackPanel { Orientation = Orientation.Horizontal };
            hl.Children.Add(Icon(icon, TgSub, 16).Also(i => i.Margin = new Thickness(0, 0, 8, 0)));
            hl.Children.Add(Txt(Loc.T(label), 16, TgText, true));
            head.Children.Add(hl);
            var hr = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            void Kv(string k, string v) { hr.Children.Add(Txt(Loc.T(k) + " ", 12, TgDim)); hr.Children.Add(Txt(v + "    ", 13, TgText, true)); }
            Kv("Kills", s.Kills.ToString(CultureInfo.CurrentCulture));
            Kv("Assists", s.Assists.ToString(CultureInfo.CurrentCulture));
            Kv("Deaths", s.Deaths.ToString(CultureInfo.CurrentCulture));
            Kv("K/D", N(s.Kd, "0.00"));
            head.Children.Add(hr);
            box.Children.Add(head);
            var bar = new Grid { Height = 6, Margin = new Thickness(0, 8, 0, 0) };
            double total = Math.Max(1, s.Kills + s.Assists + s.Deaths);
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, s.Kills / total), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, s.Assists / total), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, s.Deaths / total), GridUnitType.Star) });
            foreach (var (i, c) in new[] { (0, TgWin), (1, TgYellow), (2, TgLoss) }) { var seg = new Border { Background = B(A(c, 0xC0)), Margin = new Thickness(0, 0, 1, 0) }; Grid.SetColumn(seg, i); bar.Children.Add(seg); }
            box.Children.Add(bar);
            sides.Children.Add(box);
        }
        sp.Children.Add(sides);

        // Opponents and weapons.
        var vsRows = Stats.Versus(m, pid);
        var opp = new Grid();
        var oppW = new double[] { 64, 64, 70, 76, 0 };
        foreach (var w in oppW) opp.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
        void Cell(Grid g, int row, int col, UIElement el, Color? bg = null)
        {
            var host = new Border { Child = el, Padding = new Thickness(8, 0, 8, 0), Background = bg.HasValue ? B(bg.Value) : null };
            if (el is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; if (col < 4) fe.HorizontalAlignment = HorizontalAlignment.Center; }
            Grid.SetRow(host, row); Grid.SetColumn(host, col); g.Children.Add(host);
        }
        opp.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        var hdrs = new[] { "Kills", "Deaths", "Dealt", "Received", "Opponent" };
        for (int i = 0; i < hdrs.Length; i++) Cell(opp, 0, i, Txt(Loc.T(hdrs[i]), 10.5, TgSub, true), TgHead);
        int rr = 1;
        foreach (var v in vsRows)
        {
            opp.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
            var mine = A(TeamColor(p.Team), 0x30);
            Cell(opp, rr, 0, Txt(v.Kills.ToString(CultureInfo.CurrentCulture), 14, TgText, true), mine);
            Cell(opp, rr, 1, Txt(v.Deaths.ToString(CultureInfo.CurrentCulture), 14, TgText, true), mine);
            Cell(opp, rr, 2, Txt(v.Dealt.ToString("N0", CultureInfo.CurrentCulture), 13, TgText, true), mine);
            Cell(opp, rr, 3, Txt(v.Received.ToString("N0", CultureInfo.CurrentCulture), 13, TgText, true), mine);
            var who = new StackPanel { Orientation = Orientation.Horizontal };
            who.Children.Add(Txt("vs", 11, TgDim).Also(x => x.Margin = new Thickness(0, 0, 8, 0)));
            if (MatchArt.AgentIcon(v.Enemy.Agent) is { } ai) who.Children.Add(Pic(ai, 26, 26, 8));
            var nm = Stats.NameOf(m, v.Enemy.Id) ?? NameBook.Get(v.Enemy.Id) ?? v.Enemy.Agent;
            who.Children.Add(Txt(nm, 12.5, TgText, true));
            Cell(opp, rr, 4, who, A(TeamColor(v.Enemy.Team), 0x26));
            rr++;
        }
        var weap = new Grid();
        foreach (var w in new double[] { 0, 130, 60, 70 }) weap.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
        weap.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        var wh = new[] { "Weapon", "", "Kills", "Share" };
        for (int i = 0; i < wh.Length; i++) Cell(weap, 0, i, Txt(Loc.T(wh[i]), 10.5, TgSub, true), TgHead);
        var wk = Stats.WeaponKills(m, pid);
        int totalK = Math.Max(1, wk.Sum(x => x.Kills));
        int wr = 1;
        foreach (var (wid, dmg, kills) in wk)
        {
            weap.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
            string name = dmg.Equals("Weapon", StringComparison.OrdinalIgnoreCase) ? MapGeo.Weapon(wid) ?? Loc.T("Weapon") : Loc.T(dmg switch { "Ability" => "Ability", "Bomb" => "Spike", "Melee" => "Melee", "Fall" => "Fall", _ => dmg });
            Cell(weap, wr, 0, Txt(name, 13, TgText, true).Also(x => x.HorizontalAlignment = HorizontalAlignment.Left), TgCard);
            Cell(weap, wr, 1, dmg.Equals("Weapon", StringComparison.OrdinalIgnoreCase) && MatchArt.WeaponIcon(wid) is { } wi ? (UIElement)Pic(wi, 100, 26, 0) : new Border(), TgCard);
            Cell(weap, wr, 2, Txt(kills.ToString(CultureInfo.CurrentCulture), 14, TgText, true), A(TgBlue, 0x26));
            Cell(weap, wr, 3, Txt($"{100.0 * kills / totalK:0}%", 13, TgSub, true), TgCard);
            wr++;
        }
        if (wk.Count == 0) { weap.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) }); Cell(weap, 1, 0, Txt(Loc.T("No kills this match."), 12, TgDim)); }
        var two = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        two.ColumnDefinitions.Add(new ColumnDefinition());
        two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        two.ColumnDefinitions.Add(new ColumnDefinition());
        two.Children.Add(opp);
        Grid.SetColumn(weap, 2); two.Children.Add(weap);
        sp.Children.Add(two);
        sp.Children.Add(Txt(Loc.T("Damage dealt and received come from Riot's per-round damage record. Riot doesn't record which weapon did each hit, so weapons show kills only."), 11, TgDim).Also(x => x.Margin = new Thickness(14, 0, 14, 12)));
        return sp;
    }

    // ───────── economy ─────────

    FrameworkElement EconomyGg(VMatch m)
    {
        var rounds = m.Rounds.OrderBy(r => r.N).ToList();
        int Val(VRound r, string team) => r.Eco.Where(e => m.TeamOf(e.Key) == team).Sum(e => _ecoMode == "bank" ? e.Value.Remaining : _ecoMode == "loadout" ? e.Value.Loadout : e.Value.Remaining + e.Value.Loadout);
        bool any = rounds.Any(r => r.Eco.Count > 0);
        var sp = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        var head = new Grid();
        var hl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        hl.Children.Add(Txt(Loc.T("Economy"), 18, TgText, true));
        hl.Children.Add(new Border { Width = 1, Height = 18, Background = B(TgLine), Margin = new Thickness(14, 0, 14, 0) });
        if (any)
        {
            double Avg(string team, Func<VEco, int> f) => rounds.Where(r => r.Eco.Count > 0).Select(r => (double)r.Eco.Where(e => m.TeamOf(e.Key) == team).Sum(e => f(e.Value))).DefaultIfEmpty(0).Average();
            void Pair(string label, Func<VEco, int> f)
            {
                hl.Children.Add(Txt(Loc.T(label) + " ", 12, TgSub));
                hl.Children.Add(Txt(Avg("Blue", f).ToString("N0", CultureInfo.CurrentCulture), 12, TeamA, true));
                hl.Children.Add(Txt(" / ", 12, TgDim));
                hl.Children.Add(Txt(Avg("Red", f).ToString("N0", CultureInfo.CurrentCulture) + "     ", 12, TeamB, true));
            }
            Pair("Avg. Bank:", e => e.Remaining);
            Pair("Avg. Loadout:", e => e.Loadout);
        }
        head.Children.Add(hl);
        var modes = (Border)Segments(new (string, bool, Action)[]
        {
            (Loc.T("Bank"), _ecoMode == "bank", () => { _ecoMode = "bank"; ShowMatch(); }),
            (Loc.T("Loadout"), _ecoMode == "loadout", () => { _ecoMode = "loadout"; ShowMatch(); }),
            (Loc.T("Total"), _ecoMode == "total", () => { _ecoMode = "total"; ShowMatch(); }),
        }, 64);
        modes.HorizontalAlignment = HorizontalAlignment.Right;
        modes.Margin = new Thickness(0);
        head.Children.Add(modes);
        sp.Children.Add(head);
        if (!any)
        {
            sp.Children.Add(Txt(Loc.T("Riot didn't send the economy for this match."), 12.5, TgDim).Also(t => t.Margin = new Thickness(0, 12, 0, 8)));
            return new Border { Background = B(TgPanel), Child = sp };
        }

        // The swing: Team A's credits minus Team B's each round (above the line Team A had more).
        var diffs = rounds.Select(r => (double)(Val(r, "Blue") - Val(r, "Red"))).ToList();
        double max = Math.Max(5000, Math.Ceiling(diffs.Select(Math.Abs).DefaultIfEmpty(0).Max() / 5000) * 5000);
        const double H = 170;
        var chart = new Grid { Height = H + 8, Margin = new Thickness(0, 14, 0, 0) };
        chart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        chart.ColumnDefinitions.Add(new ColumnDefinition());
        var axis = new Canvas();
        var plot = new Canvas { ClipToBounds = false, Background = Brushes.Transparent };
        for (double v = -max; v <= max + 1; v += max / 3)
        {
            double y = H / 2 - v / max * (H / 2);
            axis.Children.Add(Txt((v >= 0 ? "" : "−") + (Math.Abs(v) / 1000).ToString("0", CultureInfo.CurrentCulture) + "k", 9.5, TgDim).Also(t => { Canvas.SetTop(t, y - 7); Canvas.SetLeft(t, 0); }));
        }
        Grid.SetColumn(plot, 1);
        chart.Children.Add(axis);
        chart.Children.Add(plot);
        void Draw(double w)
        {
            plot.Children.Clear();
            for (double v = -max; v <= max + 1; v += max / 3)
            {
                double y = H / 2 - v / max * (H / 2);
                plot.Children.Add(new Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = B(Math.Abs(v) < 1 ? TgSub : TgLine), StrokeThickness = Math.Abs(v) < 1 ? 1 : 0.6, Opacity = Math.Abs(v) < 1 ? 0.6 : 1 });
            }
            int n = diffs.Count;
            if (n == 0) return;
            double step = n > 1 ? w / n : w;
            var pts = diffs.Select((d, i) => new Point(step * (i + 0.5), H / 2 - d / max * (H / 2))).ToList();
            var area = new PointCollection(pts) { new Point(pts[^1].X, H / 2), new Point(pts[0].X, H / 2) };
            var fill = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(0, H) };
            fill.GradientStops.Add(new GradientStop(A(TeamA, 0x55), 0));
            fill.GradientStops.Add(new GradientStop(A(TeamA, 0x18), 0.5));
            fill.GradientStops.Add(new GradientStop(A(TeamB, 0x18), 0.5));
            fill.GradientStops.Add(new GradientStop(A(TeamB, 0x55), 1));
            plot.Children.Add(new Polygon { Points = area, Fill = fill });
            plot.Children.Add(new Polyline { Points = new PointCollection(pts), Stroke = B(TgSub), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round });
            for (int i = 0; i < n; i++)
            {
                var c = diffs[i] >= 0 ? TeamA : TeamB;
                var dot = new Ellipse { Width = 8, Height = 8, Fill = B(c), Stroke = B(TgPanel), StrokeThickness = 1.5, Cursor = Cursors.Hand,
                    ToolTip = Loc.F("Round {0}", rounds[i].N + 1) + $" · {Loc.T("Team A")} {Val(rounds[i], "Blue"):N0} / {Loc.T("Team B")} {Val(rounds[i], "Red"):N0}" };
                Canvas.SetLeft(dot, pts[i].X - 4); Canvas.SetTop(dot, pts[i].Y - 4);
                plot.Children.Add(dot);
            }
        }
        plot.SizeChanged += (_, e) => Draw(e.NewSize.Width);
        sp.Children.Add(chart);

        // Round results under the chart, lined up with the points.
        var res = new Grid { Margin = new Thickness(40, 8, 0, 0) };
        for (int i = 0; i < rounds.Count; i++)
        {
            res.ColumnDefinitions.Add(new ColumnDefinition());
            var r = rounds[i];
            var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            cell.Children.Add(Txt((r.N + 1).ToString(CultureInfo.CurrentCulture), 9.5, TgDim, false, HorizontalAlignment.Center));
            if (r.Winner.Length > 0) cell.Children.Add(Icon(RoundGlyph(r.Result), TeamColor(r.Winner), 15).Also(x => x.Margin = new Thickness(0, 3, 0, 0)));
            Grid.SetColumn(cell, i); res.Children.Add(cell);
        }
        sp.Children.Add(res);

        // How each kind of buy went, per team (Pulse's extra).
        var buys = new UniformGrid { Columns = 2, Margin = new Thickness(0, 14, 0, 4) };
        foreach (var team in new[] { "Blue", "Red" })
        {
            var eco = Stats.Economy(m, team);
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(Txt(Loc.T(TeamLabel(team)) + "   ", 12, TeamColor(team), true));
            foreach (var kind in new[] { Stats.Buy.FullBuy, Stats.Buy.SemiBuy, Stats.Buy.SemiEco, Stats.Buy.Eco })
            {
                var these = eco.Where(e => e.TeamBuy == kind).ToList();
                if (these.Count == 0) continue;
                int w = these.Count(e => e.Winner == team);
                line.Children.Add(Txt(Loc.T(BuyName(kind)) + " ", 11.5, TgDim));
                line.Children.Add(Txt($"{w}/{these.Count}   ", 12, w * 2 >= these.Count ? TgWin : TgLoss, true));
            }
            buys.Children.Add(line);
        }
        sp.Children.Add(buys);
        sp.Children.Add(Txt(Loc.T("Above the line Team A had more credits, below it Team B. Buy rounds show rounds won of rounds played with each kind of buy."), 11, TgDim));
        return new Border { Background = B(TgPanel), Child = sp, BorderBrush = B(TgLine), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    // ───────── rounds ─────────

    FrameworkElement RoundsGg(VMatch m)
    {
        var rounds = m.Rounds.OrderBy(r => r.N).ToList();
        if (rounds.Count == 0) return Txt(Loc.T("No rounds in this match."), 12.5, TgDim).Also(t => t.Margin = new Thickness(18));
        _rdRound = Math.Clamp(_rdRound, 0, rounds.Count - 1);
        var r = rounds[_rdRound];
        var sp = new StackPanel();

        // Round picker: each team's loadout as two bars, and how it ended.
        double maxLoad = Math.Max(1, rounds.Max(x => Math.Max(x.Eco.Where(e => m.TeamOf(e.Key) == "Blue").Sum(e => e.Value.Loadout), x.Eco.Where(e => m.TeamOf(e.Key) == "Red").Sum(e => e.Value.Loadout))));
        var pick = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 8, 8, 4) };
        for (int i = 0; i < rounds.Count; i++)
        {
            var x = rounds[i];
            bool on = i == _rdRound;
            var g = new Grid { Width = 40, Height = 82 };
            g.Children.Add(Txt((x.N + 1).ToString(CultureInfo.CurrentCulture), 10, on ? TgText : TgDim, true, HorizontalAlignment.Center).Also(t => { t.VerticalAlignment = VerticalAlignment.Top; t.Margin = new Thickness(0, 5, 0, 0); }));
            var bars = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 24) };
            foreach (var team in new[] { "Blue", "Red" })
            {
                double v = x.Eco.Where(e => m.TeamOf(e.Key) == team).Sum(e => e.Value.Loadout);
                bars.Children.Add(new Border { Width = 7, Height = Math.Max(2, 30 * v / maxLoad), Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Bottom, Background = B(TeamColor(team)) });
            }
            g.Children.Add(bars);
            if (x.Winner.Length > 0) g.Children.Add(Icon(RoundGlyph(x.Result), TeamColor(x.Winner), 13).Also(ic => { ic.VerticalAlignment = VerticalAlignment.Bottom; ic.HorizontalAlignment = HorizontalAlignment.Center; ic.Margin = new Thickness(0, 0, 0, 6); }));
            var tile = new Border { Child = g, Margin = new Thickness(0, 0, 3, 0), CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand, BorderBrush = B(on ? TgSub : Colors.Transparent), BorderThickness = new Thickness(1),
                ToolTip = Loc.F("Round {0}", x.N + 1) + " · " + Loc.T(TeamLabel(x.Winner)) + " · " + Loc.T(RoundHow(x.Result)) };
            Hover(tile, on ? TgHead : TgCard, TgHead);
            int idx = i;
            tile.MouseLeftButtonUp += (_, _) => { _rdRound = idx; _rdEvent = -1; ShowMatch(); };
            pick.Children.Add(tile);
        }
        sp.Children.Add(new Border { Background = B(TgPanel), Child = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = pick } });

        // Title.
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 14, 18, 10) };
        title.Children.Add(Txt(Loc.F("Round {0}", r.N + 1).ToUpper(CultureInfo.CurrentCulture), 18, TgText, true));
        title.Children.Add(new Border { Width = 1, Height = 18, Background = B(TgLine), Margin = new Thickness(12, 0, 12, 0) });
        if (r.Winner.Length > 0)
        {
            title.Children.Add(Icon(RoundGlyph(r.Result), TeamColor(r.Winner), 17).Also(i => i.Margin = new Thickness(0, 0, 8, 0)));
            string how = r.Result switch { "Bomb defused" or "Defuse" => "Defuse", "Bomb detonated" or "Detonate" => "Detonate", "Round timer expired" => "Time", "Surrendered" => "Surrender", _ => "Elimination" };
            title.Children.Add(Txt(Loc.F("{0} {1} win", Loc.T(TeamLabel(r.Winner)), Loc.T(how)).ToUpper(CultureInfo.CurrentCulture), 17, TeamColor(r.Winner), true));
        }
        string cer = r.Ceremony switch { "CeremonyAce" => "Ace", "CeremonyTeamAce" => "Team ace", "CeremonyClutch" => "Clutch", "CeremonyFlawless" => "Flawless", "CeremonyThrifty" => "Thrifty", "CeremonyCloser" => "Closer", _ => null };
        if (cer != null) title.Children.Add(Txt("   " + Loc.T(cer).ToUpper(CultureInfo.CurrentCulture), 13, TgYellow, true));
        sp.Children.Add(title);

        // Players, event log, positions.
        var kills = m.Kills.Where(k => k.Round == r.N).OrderBy(k => k.RoundMs).ToList();
        var three = new Grid { Margin = new Thickness(12, 0, 12, 14) };
        three.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        three.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        three.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var players = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        foreach (var team in new[] { "Blue", "Red" })
        {
            var g = new Grid();
            foreach (var w in new double[] { 0, 40, 40, 40, 58, 74 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
            var bar = new Border { Background = B(team == "Blue" ? TeamABar : TeamBBar) };
            Grid.SetColumnSpan(bar, 6); g.Children.Add(bar);
            var hd = new[] { TeamLabel(team), "K", "D", "A", "Dmg", "Econ" };
            for (int i = 0; i < hd.Length; i++)
            {
                var t = Txt(Loc.T(hd[i]), 10.5, i == 0 ? TeamColor(team) : TgSub, true, i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center).Also(x => x.Margin = new Thickness(i == 0 ? 10 : 0, 0, 0, 0));
                Grid.SetColumn(t, i); g.Children.Add(t);
            }
            int row = 1;
            foreach (var p in m.Players.Where(x => x.Team == team).OrderByDescending(x => Stats.RoundKills(m, r.N, x.Id)).ThenBy(x => x.Agent))
            {
                g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
                bool died = kills.Any(k => k.Victim == p.Id);
                var bg = new Border { Background = B(row % 2 == 1 ? TgCard : TgPanel) };
                Grid.SetRow(bg, row); Grid.SetColumnSpan(bg, 6); g.Children.Add(bg);
                var who = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), Opacity = died ? 0.6 : 1 };
                if (MatchArt.AgentIcon(p.Agent) is { } ai) who.Children.Add(Pic(ai, 28, 28, 8));
                var pname = NameOrAgent(m, p);
                int hashAt = pname.LastIndexOf('#');
                who.Children.Add(Txt(hashAt > 0 ? pname[..hashAt] : pname, 12.5, TgText, true).Also(t => t.MaxWidth = 140));
                Grid.SetRow(who, row); g.Children.Add(who);
                int k2 = Stats.RoundKills(m, r.N, p.Id), d2 = died ? 1 : 0, a2 = kills.Count(k => k.Assists.Contains(p.Id));
                int dmg = r.Damage.Where(d => d.From == p.Id).Sum(d => d.Amount);
                var cells = new[] { k2.ToString(CultureInfo.CurrentCulture), d2.ToString(CultureInfo.CurrentCulture), a2.ToString(CultureInfo.CurrentCulture), dmg.ToString(CultureInfo.CurrentCulture) };
                for (int i = 0; i < cells.Length; i++) { var t = Txt(cells[i], 13, TgText, true, HorizontalAlignment.Center); Grid.SetRow(t, row); Grid.SetColumn(t, i + 1); g.Children.Add(t); }
                var econ = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (r.Eco.TryGetValue(p.Id, out var e))
                {
                    econ.Children.Add(Txt(e.Loadout.ToString("N0", CultureInfo.CurrentCulture), 12.5, TgText, true, HorizontalAlignment.Center));
                    econ.Children.Add(Txt(e.Remaining.ToString("N0", CultureInfo.CurrentCulture), 9.5, TgDim, false, HorizontalAlignment.Center));
                    econ.ToolTip = Loc.T("Loadout value, and credits left in the bank");
                }
                else econ.Children.Add(Txt("–", 12, TgDim));
                Grid.SetRow(econ, row); Grid.SetColumn(econ, 5); g.Children.Add(econ);
                row++;
            }
            players.Children.Add(g.Also(x => x.Margin = new Thickness(0, 0, 0, 8)));
        }
        three.Children.Add(players);

        // Event log.
        var log = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        log.Children.Add(new Border { Background = B(TgHead), Height = 26, Child = Txt(Loc.T("Event Log"), 10.5, TgSub, true, HorizontalAlignment.Center) });
        var events = new List<(long Ms, Action<Grid> Fill, string Team, int KillIndex)>();
        for (int i = 0; i < kills.Count; i++)
        {
            var k = kills[i];
            int ki = i;
            events.Add((k.RoundMs, g =>
            {
                var killer = m.Players.FirstOrDefault(x => x.Id == k.Killer);
                var victim = m.Players.FirstOrDefault(x => x.Id == k.Victim);
                var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (MatchArt.AgentIcon(killer?.Agent) is { } ka) left.Children.Add(Pic(ka, 24, 24, 6).Also(im => im.ToolTip = killer == null ? null : NameOrAgent(m, killer)));
                left.Children.Add(Txt(Clock(k.RoundMs), 12, TgText, true).Also(t => t.Margin = new Thickness(0, 0, 8, 0)));
                FrameworkElement how = k.DamageType.Equals("Weapon", StringComparison.OrdinalIgnoreCase) && MatchArt.WeaponIcon(k.Weapon) is { } wi
                    ? Pic(wi, 58, 18, 0).Also(im => im.ToolTip = MapGeo.Weapon(k.Weapon))
                    : Txt(Loc.T(k.DamageType switch { "Ability" => "Ability", "Bomb" => "Spike", "Melee" => "Knife", "Fall" => "Fall", _ => k.DamageType }), 10.5, TgSub);
                left.Children.Add(how);
                g.Children.Add(left);
                if (MatchArt.AgentIcon(victim?.Agent) is { } va) g.Children.Add(Pic(va, 24, 24, 0).Also(im => { im.HorizontalAlignment = HorizontalAlignment.Right; im.ToolTip = victim == null ? null : NameOrAgent(m, victim); }));
            }, m.TeamOf(k.Killer), ki));
        }
        if (r.PlantMs >= 0 && r.Planter != null)
            events.Add((r.PlantMs, g =>
            {
                var pl = m.Players.FirstOrDefault(x => x.Id == r.Planter);
                var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (MatchArt.AgentIcon(pl?.Agent) is { } pa) left.Children.Add(Pic(pa, 24, 24, 6));
                left.Children.Add(Txt(Clock(r.PlantMs), 12, TgText, true).Also(t => t.Margin = new Thickness(0, 0, 8, 0)));
                left.Children.Add(Icon(GlyphDetonate, TgSub, 14));
                g.Children.Add(left);
                g.Children.Add(Txt(Loc.T("Planted") + (r.Site != null ? " " + r.Site : ""), 11.5, TgSub, true, HorizontalAlignment.Right));
            }, m.TeamOf(r.Planter), -1));
        if (r.Defuser != null)
            events.Add((long.MaxValue, g =>
            {
                var df = m.Players.FirstOrDefault(x => x.Id == r.Defuser);
                var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (MatchArt.AgentIcon(df?.Agent) is { } da) left.Children.Add(Pic(da, 24, 24, 6));
                left.Children.Add(Icon(GlyphDefuse, TgSub, 14));
                g.Children.Add(left);
                g.Children.Add(Txt(Loc.T("Defused"), 11.5, TgSub, true, HorizontalAlignment.Right));
            }, m.TeamOf(r.Defuser), -1));
        int selected = _rdEvent >= 0 && _rdEvent < kills.Count ? _rdEvent : kills.Count - 1;
        foreach (var ev in events.OrderBy(e => e.Ms))
        {
            var g = new Grid { Margin = new Thickness(8, 0, 8, 0), Height = 34 };
            ev.Fill(g);
            var c = TeamColor(ev.Team);
            bool on = ev.KillIndex >= 0 && ev.KillIndex == selected;
            var b = new Border { Child = g, Margin = new Thickness(0, 1, 0, 0), BorderBrush = B(on ? TgText : Colors.Transparent), BorderThickness = new Thickness(on ? 1 : 0) };
            Hover(b, A(c, on ? (byte)0x50 : (byte)0x2C), A(c, 0x48));
            if (ev.KillIndex >= 0)
            {
                b.Cursor = Cursors.Hand;
                b.ToolTip = Loc.T("Show where everyone was");
                int ki = ev.KillIndex;
                b.MouseLeftButtonUp += (_, _) => { _rdEvent = ki; ShowMatch(); };
            }
            log.Children.Add(b);
        }
        if (events.Count == 0) log.Children.Add(Txt(Loc.T("Nothing recorded this round."), 11.5, TgDim).Also(t => t.Margin = new Thickness(8)));
        Grid.SetColumn(log, 1); three.Children.Add(log);

        // Positions at the chosen kill.
        var posBox = new StackPanel();
        posBox.Children.Add(new Border { Background = B(TgHead), Height = 26, Child = Txt(Loc.T("Player Positions"), 10.5, TgSub, true).Also(t => t.Margin = new Thickness(10, 0, 0, 0)) });
        posBox.Children.Add(PositionsMap(m, kills, selected));
        Grid.SetColumn(posBox, 2); three.Children.Add(posBox);
        sp.Children.Add(three);
        return sp;
    }

    /// <summary>Everyone alive at a kill, on the minimap: agent faces in their team's color, the victim crossed out.</summary>
    FrameworkElement PositionsMap(VMatch m, List<VKill> kills, int index)
    {
        const double size = 330;
        var grid = new Grid { Width = size, Height = size, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 4) };
        if (MapGeo.Minimap(m.Map) is not { } img || index < 0 || index >= kills.Count)
        {
            grid.Children.Add(Txt(Loc.T(index < 0 ? "No kills this round." : "No minimap for this map yet."), 12, TgDim, false, HorizontalAlignment.Center));
            return grid;
        }
        grid.Children.Add(new Image { Source = img, Stretch = Stretch.Uniform, Opacity = 0.8 }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
        var canvas = new Canvas();
        grid.Children.Add(canvas);
        Point? At(double x, double y) => MapGeo.ToMinimap(m.Map, x, y) is Point p ? new Point(p.X * size, p.Y * size) : null;
        var k = kills[index];
        foreach (var d in kills.Take(index))
            if (At(d.VX, d.VY) is Point p) Cross(canvas, p, TeamColor(m.TeamOf(d.Victim)), 0.4, Agent(m, d.Victim));
        var from = k.At(k.Killer) is Pt kp ? At(kp.X, kp.Y) : null;
        var to = At(k.VX, k.VY);
        if (from is Point a && to is Point b)
            canvas.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 }, Stroke = B(TeamColor(m.TeamOf(k.Killer))) });
        foreach (var p in k.Pos)
        {
            if (p.Id == k.Victim || At(p.X, p.Y) is not Point pt) continue;
            Marker(canvas, pt, Agent(m, p.Id), TeamColor(m.TeamOf(p.Id)), false, p.Id == k.Killer);
        }
        if (to is Point v) Cross(canvas, v, TeamColor(m.TeamOf(k.Victim)), 1, Agent(m, k.Victim));
        var sp = new StackPanel();
        sp.Children.Add(grid);
        var call = MapGeo.CalloutAt(m.Map, k.VX, k.VY);
        sp.Children.Add(Txt(Clock(k.RoundMs) + " · " + Loc.F("{0} killed {1}", Agent(m, k.Killer), Agent(m, k.Victim)) + (call.Length > 0 ? " · " + call : ""), 11.5, TgSub, false, HorizontalAlignment.Center));
        sp.Children.Add(Txt(Loc.T("Click a kill in the event log to see that moment."), 10.5, TgDim, false, HorizontalAlignment.Center).Also(t => t.Margin = new Thickness(0, 2, 0, 0)));
        return sp;
    }

    // ───────── duels ─────────

    FrameworkElement DuelsGg(VMatch m)
    {
        var duels = Stats.Duels(m);
        int K(string a, string b) => duels.TryGetValue(a, out var row) ? row.GetValueOrDefault(b) : 0;
        var teamA = m.Players.Where(p => p.Team == "Blue").OrderByDescending(p => p.Score).ToList();
        var teamB = m.Players.Where(p => p.Team == "Red").OrderByDescending(p => p.Score).ToList();
        var sp = new StackPanel();

        Color Box(int mine, int theirs) => mine > theirs ? Color.FromRgb(0x2E, 0x9E, 0x4F) : mine < theirs ? Color.FromRgb(0xB8, 0x3A, 0x46) : Color.FromRgb(0x8A, 0x7A, 0x2E);
        Border Num(int n, Color c) => new() { Width = 22, Height = 22, CornerRadius = new CornerRadius(3), Background = B(c), Margin = new Thickness(3, 0, 3, 0), Child = Txt(n.ToString(CultureInfo.CurrentCulture), 12, Colors.White, true, HorizontalAlignment.Center) };

        // The three headline pairs.
        var (top, misA, misB) = Stats.Rivalries(m, "Blue");
        var cards = new UniformGrid { Columns = 3, Margin = new Thickness(8, 8, 8, 8) };
        FrameworkElement Pair(string title, Stats.Rivalry r)
        {
            var g = new Grid { Height = 104 };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            if (r == null) { g.Children.Add(Txt(Loc.T("No duels"), 12, TgDim)); return g; }
            FrameworkElement Side(VPlayer p, int kills, int other, Color team)
            {
                var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, ToolTip = NameOrAgent(m, p) };
                s.Children.Add(new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(4), ClipToBounds = true, BorderBrush = B(team), BorderThickness = new Thickness(1.5), HorizontalAlignment = HorizontalAlignment.Center,
                    Background = MatchArt.AgentIcon(p.Agent) is { } ai ? new ImageBrush(ai) { Stretch = Stretch.UniformToFill } : B(TgLine) });
                s.Children.Add(Txt(Loc.T("Kills vs Rival"), 10, TgSub, false, HorizontalAlignment.Center).Also(t => t.Margin = new Thickness(0, 4, 0, 4)));
                s.Children.Add(Num(kills, Box(kills, other)).Also(x => x.HorizontalAlignment = HorizontalAlignment.Center));
                return new Border { Background = B(A(team, 0x22)), Child = s };
            }
            g.Children.Add(Side(r.A, r.AKills, r.BKills, TeamA));
            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(Txt(Loc.T(title), 13, TgText, true, HorizontalAlignment.Center));
            mid.Children.Add(Txt("VS", 13, TgSub, true, HorizontalAlignment.Center));
            Grid.SetColumn(mid, 1); g.Children.Add(mid);
            var right = Side(r.B, r.BKills, r.AKills, TeamB);
            Grid.SetColumn(right, 2); g.Children.Add(right);
            return new Border { Background = B(TgCard), CornerRadius = new CornerRadius(4), Margin = new Thickness(4), ClipToBounds = true, Child = g };
        }
        cards.Children.Add(Pair("Top Rivalry", top));
        cards.Children.Add(Pair("Mismatch A", misA));
        cards.Children.Add(Pair("Mismatch B", misB));
        sp.Children.Add(cards);

        // Everyone against everyone.
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        foreach (var _ in teamB) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        var corner = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        corner.Children.Add(new Border { Background = B(A(TeamA, 0x50)), Padding = new Thickness(6, 2, 6, 3), Child = Txt("A", 12, TeamA, true) });
        corner.Children.Add(Txt("  vs  ", 11, TgDim));
        corner.Children.Add(new Border { Background = B(A(TeamB, 0x50)), Padding = new Thickness(6, 2, 6, 3), Child = Txt("B", 12, TeamB, true) });
        grid.Children.Add(corner);
        FrameworkElement Who(VPlayer p, Color team)
        {
            var s = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (MatchArt.AgentIcon(p.Agent) is { } ai) s.Children.Add(Pic(ai, 34, 34, 8));
            var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nm = NameOrAgent(m, p);
            int h = nm.LastIndexOf('#');
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(Txt(h > 0 ? nm[..h] : nm, 12.5, TgText, true).Also(x => x.MaxWidth = 110));
            if (h > 0) line.Children.Add(Txt(" #" + nm[(h + 1)..], 9.5, TgDim).Also(x => x.VerticalAlignment = VerticalAlignment.Bottom));
            t.Children.Add(line);
            t.Children.Add(Txt(p.Agent, 10.5, TgSub));
            s.Children.Add(t);
            return new Border { Background = B(A(team, 0x40)), Child = s, Padding = new Thickness(4, 0, 4, 0) };
        }
        for (int c = 0; c < teamB.Count; c++) { var w = Who(teamB[c], TeamB); w.Margin = new Thickness(1, 0, 0, 1); Grid.SetColumn(w, c + 1); grid.Children.Add(w); }
        for (int r = 0; r < teamA.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            var w = Who(teamA[r], TeamA);
            w.Margin = new Thickness(0, 0, 0, 1);
            Grid.SetRow(w, r + 1); grid.Children.Add(w);
            for (int c = 0; c < teamB.Count; c++)
            {
                int a = K(teamA[r].Id, teamB[c].Id), b = K(teamB[c].Id, teamA[r].Id);
                var pair = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                pair.Children.Add(Num(a, a + b == 0 ? TgLine : Box(a, b)));
                pair.Children.Add(Num(b, a + b == 0 ? TgLine : Box(b, a)));
                var cell = new Border { Background = B(r % 2 == 0 ? TgPanel : TgCard), Child = pair, Margin = new Thickness(1, 0, 0, 1),
                    ToolTip = Loc.F("{0} killed {1} {2} times; {1} killed {0} {3} times", NameOrAgent(m, teamA[r]), NameOrAgent(m, teamB[c]), a, b) };
                Grid.SetRow(cell, r + 1); Grid.SetColumn(cell, c + 1); grid.Children.Add(cell);
            }
        }
        sp.Children.Add(grid);
        sp.Children.Add(Txt(Loc.T("Each pair: kills by the row's player on the column's player, then the other way round. Green won that matchup, red lost it."), 11, TgDim).Also(t => t.Margin = new Thickness(14, 0, 14, 12)));
        return sp;
    }
}
