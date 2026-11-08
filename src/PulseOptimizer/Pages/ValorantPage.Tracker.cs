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
/// The tracker: CS2-tracker-style profiles for you, for the people in your matches, and (when turned on) for any
/// Riot ID. Skills are placed against every player in the matches saved on this PC, with the rank they play like.
/// </summary>
public partial class ValorantPage
{
    string _trSel;                       // puuid of the profile shown (null: you)
    static Profile _trSearched;
    static string _trStatus;
    static bool _trBusy;
    static Dictionary<string, string> _trNames = new();
    static (int Tier, int Rr, int Peak, string PeakAct, int Win, int Games)? _trMyRank;
    static DateTime _trMyRankAt;
    TextBlock _trStatusLabel;
    static List<int> _trRr;
    string _trAgent;
    static readonly Dictionary<string, (int Tier, int Peak)> _trPeaks = new();
    static bool _trPeaksBusy;

    void BuildTracker()
    {
        var matches = CoachMatches();
        Root.Children.Add(SearchCard());
        if (matches.Count == 0 && _trSearched == null)
        {
            Root.Children.Add(CoachHeader(matches));
            return;
        }
        var me = matches.Count > 0 ? matches[0].Me : null;
        var people = Tracker.People(matches).Take(14).ToList();
        if (people.Count > 0 && S.PlayerSearch && people.Any(p => !_trNames.ContainsKey(p.Puuid)) && !_trBusy) _ = LoadNames(people.Select(p => p.Puuid).ToList());

        // Who to show.
        var pick = new WrapPanel();
        if (me != null) pick.Children.Add(Chip(Loc.T("You"), "trwho", _trSel == null || _trSel == me, () => { _trSel = me; Build(); }));
        if (_trSearched != null)
        {
            var sp = _trSearched;
            pick.Children.Add(Chip("🔍 " + sp.Name, "trwho", _trSel == sp.Puuid && !sp.FromYourMatches, () => { _trSel = sp.Puuid; Build(); }));
        }
        foreach (var k in people)
        {
            var id = k.Puuid;
            var label = _trNames.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : $"{k.Agent} · {ValorantMatches.RankName(k.Tier)}".Trim(' ', '·');
            pick.Children.Add(Chip($"{label}  ×{k.Games}", "trwho", _trSel == id, () => { _trSel = id; Build(); }));
        }
        var pickCard = new StackPanel();
        pickCard.Children.Add(Label(people.Count > 0 ? "You and the people from your matches" : "Profile"));
        pickCard.Children.Add(pick);
        if (people.Count > 0 && !S.PlayerSearch)
            pickCard.Children.Add(Faint(Loc.T("People show as agent and rank. Their names appear when player search is on."), 11.5, 4));
        Root.Children.Add(Card(pickCard));

        Profile p;
        if (_trSearched != null && _trSel == _trSearched.Puuid && !_trSearched.FromYourMatches) p = _trSearched;
        else if (me != null)
        {
            p = Tracker.FromYourMatches(matches, _trSel ?? me);
            p.Name = p.IsYou ? Loc.T("You") : _trNames.GetValueOrDefault(p.Puuid) ?? "";
            if (p.IsYou)
            {
                if (_trMyRank is { } r) { p.Tier = r.Tier; p.Rr = r.Rr; p.Peak = Math.Max(r.Peak, r.Tier); p.PeakAct = r.PeakAct; p.ActWinPct = r.Win; p.ActGames = r.Games; }
                if (DateTime.UtcNow - _trMyRankAt > TimeSpan.FromMinutes(5)) _ = LoadMyRank();
            }
        }
        else return;
        foreach (var e in ProfileView(p)) Root.Children.Add(e);
    }

    // ───────── search ─────────

    FrameworkElement SearchCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Player tracker"));
        sp.Children.Add(Dim(Loc.T("Ranks, skills and recent form, measured against the players in your own matches."), 12.5, 4));
        var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { MaxWidth = 380 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new TextBox { Style = (Style)FindResource("SearchBox"), Tag = Loc.T("Search a Riot ID: Name#TAG"), IsEnabled = S.PlayerSearch, VerticalContentAlignment = VerticalAlignment.Center };
        row.Children.Add(box);
        var go = Btn(S.PlayerSearch ? "Search" : "Turn on player search", true);
        go.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(go, 1);
        row.Children.Add(go);
        sp.Children.Add(row);
        async Task Run()
        {
            if (_trBusy) return;
            _trBusy = true;
            _trStatus = Loc.T("Searching…");
            SetTrStatus();
            var progress = new Progress<string>(m =>
            {
                _trStatus = m.StartsWith("match:", StringComparison.Ordinal) && m.Split(':') is { Length: 3 } a ? Loc.F("Reading match {0} of {1}…", a[1], a[2]) : Loc.T(m);
                SetTrStatus();
            });
            ValorantSearch.Result r;
            try { r = await Task.Run(() => ValorantSearch.Search(box.Text, progress)); }
            catch { r = new ValorantSearch.Result(null, "Search failed. Try again in a minute."); }
            _trBusy = false;
            if (r.Profile != null) { _trSearched = r.Profile; _trSel = r.Profile.Puuid; }
            _trStatus = r.Error != null ? Loc.T(r.Error) : null;
            if (r.Profile != null) TweakEngine.AddLog("valorant", "Tracker search");
            if (_tab == "tracker") Build();
        }
        go.Click += async (_, _) =>
        {
            if (!S.PlayerSearch)
            {
                bool ok = await MainWindow.Instance.Confirm(Loc.T("Turn on player search?"),
                    Loc.T("Searching a Riot ID reads that player's rank and recent matches through the Riot Client's login on your PC, the way tracker apps do. It isn't an official Riot feature, and Riot's own rules only let trackers show players who opted in, so it carries a risk to your account, like the live lobby. Pulse doesn't save anything about the players you search."),
                    Loc.T("Turn it on"), Loc.T("Cancel"));
                if (!ok) return;
                S.PlayerSearch = true;
                TweakEngine.AddLog("valorant", "VALORANT player search turned on (risk accepted)");
                TweakEngine.Save();
                Build();
                return;
            }
            await Run();
        };
        box.KeyDown += async (_, e) => { if (e.Key == Key.Enter && S.PlayerSearch) await Run(); };
        _trStatusLabel = Faint(_trStatus ?? "", 11.5, 8);
        _trStatusLabel.Visibility = _trStatus != null ? Visibility.Visible : Visibility.Collapsed;
        sp.Children.Add(_trStatusLabel);
        if (S.PlayerSearch)
        {
            var off = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Turn off player search"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            off.Click += (_, _) => { S.PlayerSearch = false; _trSearched = null; _trNames.Clear(); TweakEngine.Save(); Build(); };
            sp.Children.Add(off);
        }
        return Card(sp);
    }

    void SetTrStatus()
    {
        if (_trStatusLabel == null) return;
        _trStatusLabel.Text = _trStatus ?? "";
        _trStatusLabel.Visibility = _trStatus != null ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task LoadNames(List<string> ids)
    {
        _trBusy = true;
        try { foreach (var kv in await Task.Run(() => ValorantSearch.NamesFor(ids))) _trNames[kv.Key] = kv.Value; }
        catch { }
        foreach (var id in ids) _trNames.TryAdd(id, null);
        _trBusy = false;
        if (_tab == "tracker") Build();
    }

    async Task LoadMyRank()
    {
        _trMyRankAt = DateTime.UtcNow;
        try { _trMyRank = await Task.Run(() => ValorantSearch.MyRank()); } catch { }
        try { _trRr = await Task.Run(() => ValorantSearch.MyRrChanges()); } catch { }
        if (_trMyRank != null && _tab == "tracker") Build();
    }

    // ───────── the profile ─────────

    IEnumerable<FrameworkElement> ProfileView(Profile p)
    {
        // Header.
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mainAgent = p.Matches.Select(m => m.Players.FirstOrDefault(x => x.Id == p.Puuid)?.Agent).Where(a => !string.IsNullOrEmpty(a))
            .GroupBy(a => a).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var face = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(12), Background = Res("Surface2Brush"), ClipToBounds = true, Margin = new Thickness(0, 0, 16, 0) };
        if (MatchArt.AgentIcon(mainAgent) is { } art) face.Child = new Image { Source = art, Stretch = Stretch.UniformToFill }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality));
        head.Children.Add(face);
        var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(who, 1);
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(new TextBlock { Text = p.Name.Length > 0 ? p.Name : mainAgent ?? "?", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont"), VerticalAlignment = VerticalAlignment.Center });
        if (p.Smurf.Score >= 35)
            nameRow.Children.Add(Pill(Loc.T(p.Smurf.Label), true).Also(b => { b.Margin = new Thickness(12, 0, 0, 0); b.VerticalAlignment = VerticalAlignment.Center; }));
        who.Children.Add(nameRow);
        var sub = new List<string>();
        if (p.FromYourMatches && !p.IsYou)
        {
            if (p.With > 0) sub.Add(Loc.F("{0}–{1} together", p.WithWins, p.With - p.WithWins));
            if (p.Against > 0) sub.Add(Loc.F("you went {0}–{1} against them", p.AgainstWins, p.Against - p.AgainstWins));
        }
        if (mainAgent != null) sub.Add(Loc.F("mostly {0}", mainAgent));
        sub.Add(Loc.F("{0} matches", p.Matches.Count));
        who.Children.Add(Dim(string.Join(" · ", sub), 12.5, 4));
        head.Children.Add(who);
        var ranks = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(ranks, 2);
        ranks.Children.Add(RankBlock(p.FromYourMatches && !p.IsYou ? "Last seen at" : "Current rank", p.Tier, p.Rr > 0 ? $"{p.Rr} RR" : ""));
        if (p.Peak > 0) ranks.Children.Add(RankBlock("Peak", p.Peak, p.PeakAct));
        head.Children.Add(ranks);
        yield return Card(head);

        if (p.Matches.Count == 0)
        {
            yield return Card(Dim(Loc.T("No competitive, unrated, premier or swiftplay matches to measure yet."), 12.5, 0));
            yield break;
        }
        if (p.IsYou)
        {
            var mine = CoachMatches();
            if (Tracker.Against(mine) is { } opp) yield return OppositionCard(opp);
            yield return MmrCard(Tracker.Mmr(mine, _trRr), p);
        }

        // Gauges.
        var t = p.Totals;
        var gauges = new UniformGrid { Columns = 4 };
        gauges.Children.Add(Gauge("Win rate", t.WinPct, 100, $"{t.WinPct:0}%", t.WinPct >= 55 ? "Climbing" : t.WinPct <= 45 ? "Struggling" : "Even", Loc.F("{0} won of {1}", t.Wins, t.Matches)));
        gauges.Children.Add(Gauge("K/D", t.KdRatio, 2, t.KdRatio.ToString("0.00", CultureInfo.CurrentCulture), t.KdRatio >= 1.2 ? "Strong" : t.KdRatio >= 0.9 ? "Average" : "Below average", Loc.F("{0} kills, {1} deaths", t.Kills, t.Deaths)));
        gauges.Children.Add(Gauge("Headshots", t.HeadshotPct, 40, double.IsNaN(t.HeadshotPct) ? "–" : $"{t.HeadshotPct:0}%", t.HeadshotPct >= 25 ? "Sharp" : t.HeadshotPct >= 17 ? "Average" : "Low", Loc.F("{0:0} damage per round", t.Adr)));
        if (!p.IsYou)
            gauges.Children.Add(Gauge("Smurf chance", p.Smurf.Score, 100, $"{p.Smurf.Score}%", p.Smurf.Score >= 60 ? "Likely smurf" : p.Smurf.Score >= 35 ? "Possible smurf" : "Low chance",
                p.Smurf.Signs.Count > 0 ? string.Join(" · ", p.Smurf.Signs) : Loc.T("Nothing unusual about this account")));
        else
            gauges.Children.Add(Gauge("KAST", t.KastPct, 100, double.IsNaN(t.KastPct) ? "–" : $"{t.KastPct:0}%", t.KastPct >= 72 ? "Reliable" : t.KastPct >= 64 ? "Average" : "Low", Loc.T("Rounds with a kill, assist, survival or trade")));
        yield return gauges;

        // Skills.
        var skills = new StackPanel();
        var sh = new Grid();
        sh.ColumnDefinitions.Add(new ColumnDefinition());
        sh.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sh.Children.Add(Heading("Skills"));
        var scaleN = new TextBlock { Text = Loc.F("{0} rounds measured", t.Rounds), FontSize = 11.5, Foreground = Res("TextFaintBrush"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(scaleN, 1);
        sh.Children.Add(scaleN);
        skills.Children.Add(sh);
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var s in p.Skills) grid.Children.Add(SkillTile(s));
        skills.Children.Add(grid);
        if (!p.IsYou) skills.Children.Add(Faint(Loc.T("Smurf chance adds up warning signs; it's a hint, not proof. Some players are just good, or improving fast."), 11.5, 10));
        skills.Children.Add(Faint(Loc.T("\"Top %\" and \"Plays like\" compare with every player in the matches saved on this PC, using the rank each of them had in that match. They get more precise the more you play."), 11.5, 10));
        yield return Card(skills);

        // Recent matches.
        var recent = new StackPanel();
        recent.Children.Add(Heading("Recent matches"));
        var strip = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) };
        foreach (var m in p.Matches.Take(20).Reverse())
        {
            bool won = WonFor(m, p.Puuid);
            strip.Children.Add(new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 4, 4),
                Background = new SolidColorBrush(won ? Color.FromArgb(0x55, 0x3D, 0xDC, 0x84) : Color.FromArgb(0x55, 0xFF, 0x5A, 0x4E)),
                BorderBrush = new SolidColorBrush(won ? Color.FromRgb(0x3D, 0xDC, 0x84) : Color.FromRgb(0xFF, 0x5A, 0x4E)), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = Loc.T(won ? "W" : "L"), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                ToolTip = $"{m.Map} · {m.Players.FirstOrDefault(x => x.Id == p.Puuid)?.Agent}",
            });
        }
        recent.Children.Add(strip);
        foreach (var m in p.Matches.Take(8)) recent.Children.Add(MatchRow(m, p.Puuid));
        yield return Card(recent);

        if (p.IsYou)
        {
            var all = VStore.All;
            var mineAll = CoachMatches();
            var yours = Tracker.YourAgents(mineAll, all);
            if (yours.Count > 0) yield return YourAgentsCard(yours, all, p.Puuid);
            var tiers = Tracker.TierList(all);
            if (tiers.Count >= 4) yield return TierListCard(tiers, all.Count(m => VMatch.Counts(m.Queue)));
            yield break;
        }

        // Agents.
        var agents = p.Matches.GroupBy(m => m.Players.FirstOrDefault(x => x.Id == p.Puuid)?.Agent ?? "").Where(g => g.Key.Length > 0)
            .OrderByDescending(g => g.Count()).Take(4).ToList();
        if (agents.Count > 0)
        {
            var ag = new StackPanel();
            ag.Children.Add(Heading("Agents"));
            var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var g in agents)
            {
                var at = Analysis.For(g, p.Puuid);
                var b = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 28, 8) };
                if (MatchArt.AgentIcon(g.Key) is { } ai) b.Children.Add(new Image { Source = ai, Width = 36, Height = 36, Margin = new Thickness(0, 0, 10, 0) }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
                var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                txt.Children.Add(new TextBlock { Text = g.Key, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
                txt.Children.Add(new TextBlock { Text = Loc.F("{0} matches · {1:0}% won · {2:0.00} K/D", g.Count(), at.WinPct, at.KdRatio), FontSize = 11.5, Foreground = Res("TextDimBrush") });
                b.Children.Add(txt);
                row.Children.Add(b);
            }
            ag.Children.Add(row);
            yield return Card(ag);
        }
        if (!p.FromYourMatches)
            yield return Card(Faint(Loc.T("Searched profiles use that player's last 10 competitive, unrated, premier and swiftplay matches. Nothing about them is saved."), 11.5, 0));
    }

    FrameworkElement YourAgentsCard(List<Tracker.AgentLine> lines, IReadOnlyList<VMatch> all, string me)
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("How you play your agents"));
        sp.Children.Add(Dim(Loc.T("Each agent you've played twice or more, against every other player on that agent in the matches saved on this PC. Ranked by combat score per round (ACS), the number VALORANT's scoreboard uses."), 12.5, 4));
        _trAgent ??= lines[0].Agent;
        foreach (var a in lines)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (MatchArt.AgentIcon(a.Agent) is { } ai) g.Children.Add(new Image { Source = ai, Width = 36, Height = 36, HorizontalAlignment = HorizontalAlignment.Left }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Text = a.Agent, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
            name.Children.Add(new TextBlock { Text = Loc.F("{0} matches · {1:0}% won", a.Matches, a.WinPct), FontSize = 11.5, Foreground = Res("TextDimBrush") });
            Grid.SetColumn(name, 1); g.Children.Add(name);
            var stats = new TextBlock
            {
                Text = Loc.F("{0:0} ACS · {1:0.00} K/D · {2:0} ADR · {3:0}% HS · {4:0.0} first kills per 100 rounds · {5:0.0} abilities a round",
                    a.Acs, a.Kd, a.Adr, double.IsNaN(a.HsPct) ? 0 : a.HsPct, a.FirstKillsPer100, double.IsNaN(a.CastsPerRound) ? 0 : a.CastsPerRound),
                FontSize = 12, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
            };
            Grid.SetColumn(stats, 2); g.Children.Add(stats);
            var tags = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            var c = double.IsNaN(a.TopPercent) ? Color.FromRgb(0x8A, 0x8F, 0x98) : a.TopPercent <= 25 ? Color.FromRgb(0x3D, 0xDC, 0x84) : a.TopPercent <= 60 ? Color.FromRgb(0xFA, 0xD6, 0x63) : Color.FromRgb(0xFF, 0x5A, 0x4E);
            tags.Children.Add(new TextBlock
            {
                Text = double.IsNaN(a.TopPercent) ? Loc.F("{0} other {1} players so far", a.OthersSeen, a.Agent) : Loc.F("Top {0:0}% of {1} players", a.TopPercent, a.Agent),
                FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(c), HorizontalAlignment = HorizontalAlignment.Right,
            });
            if (a.PlaysLikeTier > 0) tags.Children.Add(new TextBlock { Text = Loc.F("plays like {0}", ValorantMatches.RankName(a.PlaysLikeTier)), FontSize = 11.5, Foreground = Res("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Right });
            Grid.SetColumn(tags, 3); g.Children.Add(tags);
            var agent = a.Agent;
            var row = new Border
            {
                Background = _trAgent == a.Agent ? Res("Surface2Brush") : Brushes.Transparent, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 12, 8),
                Margin = new Thickness(0, 8, 0, 0), Child = g, Cursor = Cursors.Hand, ToolTip = Loc.F("See the best {0} players you've met", a.Agent),
            };
            row.MouseLeftButtonUp += (_, _) => { _trAgent = agent; Build(); };
            sp.Children.Add(row);
        }

        // Best players on the chosen agent.
        var top = Tracker.TopOn(_trAgent, all, me);
        if (top.Count > 1)
        {
            sp.Children.Add(Label(Loc.F("Best {0} players you've met", _trAgent)).Also(l => l.Margin = new Thickness(0, 18, 0, 6)));
            int rank = 1;
            foreach (var t in top)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 3) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.Children.Add(new TextBlock { Text = $"#{rank++}", FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = Res(t.You ? "AccentBrush" : "TextFaintBrush"), VerticalAlignment = VerticalAlignment.Center });
                if (MatchArt.RankIcon(t.Tier) is { } ri) { var im = new Image { Source = ri, Width = 24, Height = 24 }; Grid.SetColumn(im, 1); g.Children.Add(im); }
                var who = t.You ? Loc.T("You") : S.PlayerSearch && _trNames.TryGetValue(t.Puuid, out var n) && !string.IsNullOrEmpty(n) ? n : ValorantMatches.RankName(t.Tier) is { Length: > 0 } rn ? Loc.F("{0} player", rn) : Loc.T("Player");
                var w = new TextBlock { Text = who, FontSize = 12.5, FontWeight = t.You ? FontWeights.Bold : FontWeights.SemiBold, Foreground = Res(t.You ? "AccentBrush" : "TextBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(w, 2); g.Children.Add(w);
                var st = new TextBlock { Text = Loc.F("{0:0} ACS · {1:0.00} K/D · {2} matches", t.Acs, t.Kd, t.Matches), FontSize = 12, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(st, 3); g.Children.Add(st);
                var row = new Border { Background = t.You ? Res("Surface2Brush") : Brushes.Transparent, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 4), Child = g };
                if (!t.You) { var id = t.Puuid; row.Cursor = Cursors.Hand; row.MouseLeftButtonUp += (_, _) => { _trSel = id; Build(); }; }
                sp.Children.Add(row);
            }
        }
        sp.Children.Add(Faint(Loc.T("Riot's match data has the moment of each kill but not when the damage started, so time-to-kill can't be measured honestly; ACS and first kills show how fast you win fights instead."), 11.5, 10));
        return Card(sp);
    }

    FrameworkElement TierListCard(List<Tracker.TierEntry> tiers, int matches)
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Agent tier list in your lobbies"));
        sp.Children.Add(Dim(Loc.F("Win rate of every agent across all players in the {0} matches saved on this PC. It's your rank and servers, not the whole game, and it gets steadier with more matches.", matches), 12.5, 4));
        foreach (var grade in new[] { "S", "A", "B", "C", "D" })
        {
            var group = tiers.Where(t => t.Grade == grade).ToList();
            if (group.Count == 0) continue;
            var col = grade switch { "S" => Color.FromRgb(0xFF, 0x7A, 0x59), "A" => Color.FromRgb(0xFA, 0xD6, 0x63), "B" => Color.FromRgb(0x3D, 0xDC, 0x84), "C" => Color.FromRgb(0x4D, 0xA3, 0xFF), _ => Color.FromRgb(0x8A, 0x8F, 0x98) };
            var g = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(col), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = grade, FontSize = 20, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            var wrap = new WrapPanel();
            foreach (var t in group)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 6) };
                if (MatchArt.AgentIcon(t.Agent) is { } ai) item.Children.Add(new Image { Source = ai, Width = 34, Height = 34, Margin = new Thickness(0, 0, 6, 0) }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
                var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                txt.Children.Add(new TextBlock { Text = t.Agent, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
                txt.Children.Add(new TextBlock { Text = Loc.F("{0:0}% win · picked in {1:0}% · {2} games", t.WinPct, t.PickPct, t.Picks), FontSize = 11, Foreground = Res("TextDimBrush") });
                item.Children.Add(txt);
                wrap.Children.Add(item);
            }
            Grid.SetColumn(wrap, 1);
            g.Children.Add(wrap);
            sp.Children.Add(g);
        }
        sp.Children.Add(Faint(Loc.T("Grades by win rate: S 55%+, A 52%+, B 48%+, C 45%+, D below. Agents need 15 games here to be listed."), 11.5, 10));
        return Card(sp);
    }

    static string RankOf(double avgTier) => avgTier >= 3 ? ValorantMatches.RankName((int)Math.Round(avgTier)) : "–";

    /// <summary>How far an average rank is from yours, in divisions (Gold 1 → Gold 2 is one).</summary>
    static string RankGap(double them, double me)
    {
        double d = them - me;
        if (me < 3 || them < 3 || Math.Abs(d) < 1) return Loc.T("about your rank");
        int n = (int)Math.Round(Math.Abs(d));
        return d > 0 ? Loc.F("{0} divisions above you", n) : Loc.F("{0} divisions below you", n);
    }

    /// <summary>Who you've been up against in your last 5 ranked matches.</summary>
    FrameworkElement OppositionCard(Tracker.Opposition o)
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Who you're up against"));
        sp.Children.Add(Dim(Loc.F("Your last {0} ranked matches. Each player's rank is the one they had in that match.", o.Matches), 12.5, 4));
        var row = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        void Stat(string label, double tier, string sub)
        {
            var b = new StackPanel { Margin = new Thickness(0, 0, 34, 8) };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            if (MatchArt.RankIcon((int)Math.Round(tier)) is { } icon) line.Children.Add(new Image { Source = icon, Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 0) }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
            line.Children.Add(new TextBlock { Text = RankOf(tier), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
            b.Children.Add(new TextBlock { Text = Loc.T(label).ToUpper(CultureInfo.CurrentCulture), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, 0, 0, 4) });
            b.Children.Add(line);
            if (sub.Length > 0) b.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 2, 0, 0) });
            row.Children.Add(b);
        }
        Stat("Average enemy", o.EnemyAvg, RankGap(o.EnemyAvg, o.MyAvg));
        Stat("Average teammate", o.TeamAvg, RankGap(o.TeamAvg, o.MyAvg));
        if (o.Toughest != null) Stat("Toughest enemy", o.Toughest.Tier, o.Toughest.Agent);
        sp.Children.Add(row);

        // Peaks: their best rank ever (a Riot lookup per player, so only with player search on).
        if (S.PlayerSearch)
        {
            var known = o.EnemyIds.Where(_trPeaks.ContainsKey).Select(id => _trPeaks[id]).ToList();
            if (known.Count > 0)
            {
                double avgPeak = known.Where(k => k.Peak >= 3).Select(k => (double)k.Peak).DefaultIfEmpty(0).Average();
                var best = known.Max(k => k.Peak);
                sp.Children.Add(Dim(Loc.F("Their peaks: average {0}, highest {1}.", RankOf(avgPeak), ValorantMatches.RankName(best)), 12.5, 4)
                    .Also(t => t.Foreground = Res("TextBrush")));
            }
            else
            {
                var b = Btn(_trPeaksBusy ? "Looking up peaks…" : "Look up their peak ranks");
                b.IsEnabled = !_trPeaksBusy;
                b.HorizontalAlignment = HorizontalAlignment.Left;
                b.Margin = new Thickness(0, 6, 0, 0);
                b.Click += async (_, _) =>
                {
                    _trPeaksBusy = true;
                    Build();
                    try { foreach (var kv in await Task.Run(() => ValorantSearch.Peaks(o.EnemyIds))) _trPeaks[kv.Key] = kv.Value; } catch { }
                    _trPeaksBusy = false;
                    if (_tab == "tracker") Build();
                };
                sp.Children.Add(b);
            }
        }
        else sp.Children.Add(Faint(Loc.T("Turn on player search to see their peak ranks too."), 11.5, 4));

        if (o.Repeats.Count > 0)
        {
            sp.Children.Add(Label("Enemies you keep meeting").Also(l => l.Margin = new Thickness(0, 16, 0, 6)));
            foreach (var r in o.Repeats)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                if (MatchArt.AgentIcon(r.Agent) is { } ai) g.Children.Add(new Image { Source = ai, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
                var name = S.PlayerSearch && _trNames.TryGetValue(r.Puuid, out var n) && !string.IsNullOrEmpty(n) ? n : $"{r.Agent} · {ValorantMatches.RankName(r.Tier)}";
                var t1 = new TextBlock { Text = name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(t1, 1); g.Children.Add(t1);
                var t2 = new TextBlock { Text = Loc.F("met {0} times · you won {1}", r.Times, r.YourWins), FontSize = 12, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(t2, 2); g.Children.Add(t2);
                var row2 = new Border { Background = Res("Surface2Brush"), CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 0, 4), Child = g, Cursor = Cursors.Hand };
                var id = r.Puuid;
                row2.MouseLeftButtonUp += (_, _) => { _trSel = id; Build(); };
                sp.Children.Add(row2);
            }
        }
        return Card(sp);
    }

    /// <summary>The hidden MMR estimate: lobby ranks and RR per win/loss, and what they say together.</summary>
    FrameworkElement MmrCard(Tracker.MmrEstimate e, Profile p)
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Hidden MMR estimate"));
        sp.Children.Add(Dim(Loc.T("Riot never shows your MMR (the hidden rating matchmaking uses). This estimates it the way MMR calculators do: from the ranks of the lobbies Riot puts you in, and from how much RR you gain and lose."), 12.5, 4));
        if (e.Side == Tracker.MmrSide.Unknown)
        {
            sp.Children.Add(Dim(Loc.T("Play a few more competitive matches (and keep VALORANT open so Pulse can read your RR) for an estimate."), 12.5, 10));
            return Card(sp);
        }
        var (word, color, explain) = e.Side switch
        {
            Tracker.MmrSide.Above => ("Above your rank", Color.FromRgb(0x3D, 0xDC, 0x84), "Your MMR looks higher than your rank: expect bigger wins and smaller losses until your rank catches up."),
            Tracker.MmrSide.Below => ("Below your rank", Color.FromRgb(0xFF, 0x5A, 0x4E), "Your MMR looks lower than your rank: wins give less and losses take more until it catches up. Winning streaks fix it fastest."),
            _ => ("About your rank", Color.FromRgb(0xFA, 0xD6, 0x63), "Your MMR looks about the same as your rank: wins and losses should be roughly even."),
        };
        var big = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        if (e.EstimateTier >= 3 && MatchArt.RankIcon(e.EstimateTier) is { } icon) big.Children.Add(new Image { Source = icon, Width = 46, Height = 46, Margin = new Thickness(0, 0, 12, 0) }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
        var bt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        bt.Children.Add(new TextBlock { Text = Loc.T(word), FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color), FontFamily = (FontFamily)FindResource("DisplayFont") });
        if (e.EstimateTier >= 3) bt.Children.Add(new TextBlock { Text = Loc.F("Estimated around {0}", ValorantMatches.RankName(e.EstimateTier)), FontSize = 12.5, Foreground = Res("TextDimBrush") });
        big.Children.Add(bt);
        sp.Children.Add(big);
        sp.Children.Add(Dim(Loc.T(explain), 12.5, 10));
        var ev = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        void Ev(string label, string value)
        {
            var b = new StackPanel { Margin = new Thickness(0, 0, 30, 6) };
            b.Children.Add(new TextBlock { Text = value, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
            b.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 11, Foreground = Res("TextFaintBrush") });
            ev.Children.Add(b);
        }
        if (e.Matches >= 3) { Ev("Your lobbies' average rank", RankOf(e.LobbyAvg)); Ev("Your rank in those matches", RankOf(e.MyAvg)); }
        if (!double.IsNaN(e.AvgWin)) Ev("RR per win", $"+{e.AvgWin:0}");
        if (!double.IsNaN(e.AvgLoss)) Ev("RR per loss", $"−{e.AvgLoss:0}");
        sp.Children.Add(ev);
        sp.Children.Add(Faint(Loc.F("Based on your last {0} competitive matches. It's an estimate: Riot's real number is private.", Math.Max(e.Matches, _trRr?.Count ?? 0)), 11.5, 6));
        return Card(sp);
    }

    static bool WonFor(VMatch m, string puuid)
    {
        var team = m.TeamOf(puuid);
        return m.Rounds.Count(r => r.Winner == team) > m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != team);
    }

    FrameworkElement RankBlock(string label, int tier, string sub)
    {
        var sp = new StackPanel { Margin = new Thickness(18, 0, 0, 0), MinWidth = 90 };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (MatchArt.RankIcon(tier) is { } icon) row.Children.Add(new Image { Source = icon, Width = 34, Height = 34, Margin = new Thickness(0, 0, 8, 0) }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
        var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        txt.Children.Add(new TextBlock { Text = tier > 0 ? ValorantMatches.RankName(tier) : Loc.T("Unrated"), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
        if (!string.IsNullOrEmpty(sub)) txt.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = Res("TextDimBrush") });
        row.Children.Add(txt);
        sp.Children.Add(new TextBlock { Text = Loc.T(label).ToUpper(CultureInfo.CurrentCulture), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, 0, 0, 4) });
        sp.Children.Add(row);
        return sp;
    }

    /// <summary>A ring gauge card: the value, a word for it, and one line of detail.</summary>
    FrameworkElement Gauge(string title, double value, double max, string text, string word, string detail)
    {
        const double size = 110, stroke = 9;
        double frac = double.IsNaN(value) ? 0 : Math.Clamp(value / max, 0, 1);
        var c = frac >= 0.66 ? Color.FromRgb(0x3D, 0xDC, 0x84) : frac >= 0.4 ? Color.FromRgb(0xFA, 0xD6, 0x63) : Color.FromRgb(0xFF, 0x5A, 0x4E);
        if (title == "Smurf chance") c = value >= 60 ? Color.FromRgb(0xFF, 0x5A, 0x4E) : value >= 35 ? Color.FromRgb(0xFA, 0xD6, 0x63) : Color.FromRgb(0x3D, 0xDC, 0x84);
        var ring = new Grid { Width = size, Height = size, Margin = new Thickness(0, 10, 0, 8), HorizontalAlignment = HorizontalAlignment.Center };
        ring.Children.Add(Arc(size, stroke, 1, Color.FromArgb(0x30, c.R, c.G, c.B)));
        if (frac > 0) ring.Children.Add(Arc(size, stroke, frac, c));
        ring.Children.Add(new TextBlock { Text = text, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(c), FontFamily = (FontFamily)FindResource("DisplayFont"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = Loc.T(title).ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Res("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(ring);
        sp.Children.Add(new TextBlock { Text = Loc.T(word).ToUpper(CultureInfo.CurrentCulture), FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(c), HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Foreground = Res("TextFaintBrush"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        return new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(14, 14, 14, 14), Margin = new Thickness(0, 0, 10, 14), Child = sp };
    }

    /// <summary>An arc that starts at the bottom left and sweeps clockwise (a 270° gauge), <paramref name="frac"/> of the way.</summary>
    static System.Windows.Shapes.Path Arc(double size, double stroke, double frac, Color c)
    {
        double r = (size - stroke) / 2, cx = size / 2, cy = size / 2;
        double start = 135, sweep = 270 * Math.Min(frac, 0.9999);
        Point P(double deg) => new(cx + r * Math.Cos(deg * Math.PI / 180), cy + r * Math.Sin(deg * Math.PI / 180));
        var fig = new PathFigure { StartPoint = P(start), IsClosed = false };
        fig.Segments.Add(new ArcSegment(P(start + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));
        return new System.Windows.Shapes.Path
        {
            Data = new PathGeometry(new[] { fig }), Stroke = new SolidColorBrush(c), StrokeThickness = stroke,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
    }

    FrameworkElement SkillTile(SkillScore s)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = Loc.T(s.Skill.Name).ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Res("TextDimBrush"), ToolTip = Loc.T(s.Skill.Explains) });
        bool known = !double.IsNaN(s.Value);
        string val = !known ? "–" : s.Skill.Unit switch
        {
            "%" => $"{s.Value:0}%",
            "/100" => s.Value.ToString("0.0", CultureInfo.CurrentCulture),
            _ => s.Value.ToString(s.Value < 10 ? "0.00" : "0", CultureInfo.CurrentCulture),
        };
        double top = s.TopPercent;
        var c = double.IsNaN(top) ? Color.FromRgb(0x8A, 0x8F, 0x98) : top <= 25 ? Color.FromRgb(0x3D, 0xDC, 0x84) : top <= 60 ? Color.FromRgb(0xFA, 0xD6, 0x63) : Color.FromRgb(0xFF, 0x5A, 0x4E);
        sp.Children.Add(new TextBlock { Text = val, FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(c), FontFamily = (FontFamily)FindResource("DisplayFont"), Margin = new Thickness(0, 4, 0, 6) });
        // Ten-segment bar like the big trackers: how many tenths of players it beats.
        var bar = new UniformGrid { Columns = 10, Height = 5 };
        int lit = double.IsNaN(top) ? 0 : (int)Math.Round((100 - top) / 10);
        for (int i = 0; i < 10; i++)
            bar.Children.Add(new Border { Margin = new Thickness(0, 0, 3, 0), CornerRadius = new CornerRadius(1.5), Background = i < lit ? new SolidColorBrush(c) : Res("Surface2Brush") });
        sp.Children.Add(bar);
        var tags = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        if (!known) tags.Children.Add(new TextBlock { Text = Loc.T("Not enough data yet"), FontSize = 11, Foreground = Res("TextFaintBrush") });
        else
        {
            if (!double.IsNaN(top)) tags.Children.Add(Tag(Loc.F("TOP {0:0}%", top), c));
            if (s.PlaysLikeTier > 0) tags.Children.Add(Tag(Loc.F("PLAYS LIKE {0}", ValorantMatches.RankName(s.PlaysLikeTier).ToUpper(CultureInfo.CurrentCulture)), Color.FromRgb(0xC9, 0xA2, 0xFF)));
        }
        sp.Children.Add(tags);
        return new Border { Background = Res("Surface2Brush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 10, 10), Child = sp };

        Border Tag(string text, Color col) => new()
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 0, 6, 4),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, col.R, col.G, col.B)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(col) },
        };
    }

    FrameworkElement MatchRow(VMatch m, string puuid)
    {
        var pl = m.Players.FirstOrDefault(x => x.Id == puuid);
        bool won = WonFor(m, puuid);
        var team = m.TeamOf(puuid);
        int us = m.Rounds.Count(r => r.Winner == team), them = m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != team);
        var t = Analysis.Totals(m, puuid);
        var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        if (MatchArt.AgentIcon(pl?.Agent) is { } ai) g.Children.Add(new Image { Source = ai, Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
        void Cell(int col, string text, Brush b, bool bold = false)
        {
            var tb = new TextBlock { Text = text, FontSize = 12.5, Foreground = b, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(tb, col); g.Children.Add(tb);
        }
        Cell(1, $"{m.Map} · {pl?.Agent}", Res("TextBrush"), true);
        Cell(2, $"{us}–{them}", new SolidColorBrush(won ? Color.FromRgb(0x3D, 0xDC, 0x84) : Color.FromRgb(0xFF, 0x5A, 0x4E)), true);
        Cell(3, $"{t.Kills} / {t.Deaths} / {pl?.Assists ?? 0}", Res("TextBrush"));
        Cell(4, Loc.F("{0:0} ADR · {1:0}% HS", t.Adr, double.IsNaN(t.HeadshotPct) ? 0 : t.HeadshotPct) + " · " + m.StartUtc.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture), Res("TextDimBrush"));
        return new Border { Background = Res("Surface2Brush"), CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 5, 10, 5), Child = g };
    }
}
