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
using Pulse.Core.Games.Valorant.Intel;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// Intel: Pulse's models over your saved matches. Hidden MMR estimate, rank forecast and a what-if simulator,
/// matchmaking quality, head-to-head records, expected vs actual performance, skill fingerprint, consistency,
/// sessions, teammates, agents, maps, career, improvement, the player network, a compare lab, data checks, and how
/// every number is worked out. Each model says what it needs and how sure it is; nothing is made up.
/// </summary>
public partial class ValorantPage
{
    static string _inSection = "rating";
    static double _wiWin = double.NaN, _wiGain = double.NaN, _wiLoss = double.NaN;
    static string _cmpA, _cmpB;

    void BuildIntel()
    {
        var ms = CoachMatches();
        if (ms.Count == 0) { Root.Children.Add(CoachHeader(ms)); return; }
        var me = ms[0].Me;
        if (_trMyRank == null && DateTime.UtcNow - _trMyRankAt > TimeSpan.FromMinutes(5)) _ = LoadMyRank();

        var head = new StackPanel();
        head.Children.Add(Heading("Intel"));
        head.Children.Add(Dim(Loc.F("Pulse's own models over your {0} saved matches. They're estimates from match data Riot shares, not Riot's private numbers; each one says how sure it is.", ms.Count), 12.5, 4));
        var chips = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (key, label) in new[] { ("rating", "Rating & forecast"), ("matchmaking", "Matchmaking"), ("opponents", "Opponents"), ("performance", "Performance"),
            ("agents", "Agents"), ("maps", "Maps"), ("sessions", "Sessions"), ("teammates", "Teammates"), ("career", "Career"), ("improve", "Improvement"),
            ("network", "Network"), ("compare", "Compare"), ("data", "Data checks"), ("methods", "Methods") })
            chips.Children.Add(Chip(Loc.T(label), "intel", _inSection == key, () => { _inSection = key; Build(); }));
        head.Children.Add(chips);
        Root.Children.Add(Card(head));

        var mmr = MmrModel.Run(ms, me, _trMyRank?.Tier >= 3 ? _trMyRank.Tier : _trMyRank?.LastTier ?? 0);
        switch (_inSection)
        {
            case "matchmaking": IntelMatchmaking(ms, me); break;
            case "opponents": IntelOpponents(ms, me); break;
            case "performance": IntelPerformance(ms, me, mmr); break;
            case "agents": IntelAgents(ms, me); break;
            case "maps": IntelMaps(ms, me); break;
            case "sessions": IntelSessions(ms, me); break;
            case "teammates": IntelTeammates(ms, me); break;
            case "career": IntelCareer(ms, me, mmr); break;
            case "improve": IntelImprove(ms, me); break;
            case "network": IntelNetwork(ms, me); break;
            case "compare": IntelCompare(ms, me); break;
            case "data": IntelData(); break;
            case "methods": IntelMethods(); break;
            default: IntelRating(ms, me, mmr); break;
        }
    }

    FrameworkElement Note(string text) => Txt(Loc.T(text), 11.5, TpMuted).Also(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; t.Margin = new Thickness(12, 8, 12, 10); });
    FrameworkElement Wait(string title, string text) => Flat(title, Note(text));

    static FrameworkElement Stat(string label, string value, Color? c = null, string sub = null)
    {
        var sp = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
        sp.Children.Add(Txt(Loc.T(label), 11.5, TpMuted));
        sp.Children.Add(Txt(value, 20, c ?? TpInk, true));
        if (!string.IsNullOrEmpty(sub)) sp.Children.Add(Txt(sub, 11, TpMuted).Also(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; }));
        return sp;
    }

    static string Rank(double tier) => tier >= 3 ? ValorantMatches.RankName((int)Math.Round(tier)) : "–";

    // ───────── rating, forecast, what-if, RR efficiency ─────────

    void IntelRating(List<VMatch> ms, string me, MmrModel.Estimate e)
    {
        if (e == null) { Root.Children.Add(Wait("Hidden MMR estimate", "Needs a match where at least 3 enemies were ranked.")); return; }
        int cur = e.CurrentTier;
        var top = new UniformGrid { Columns = 5 };
        top.Children.Add(Stat("Estimated skill", Rank(e.Rating / 100.0 - 0.5), TpGold, Loc.F("{0:0} rating · range {1} to {2}", e.Rating, Rank(e.Low / 100.0 - 0.5), Rank(e.High / 100.0 - 0.5))));
        top.Children.Add(Stat("Displayed rank", cur >= 3 ? ValorantMatches.RankName(cur) : Loc.T("Unknown"), TpInk, cur >= 3 ? null : Loc.T("Open VALORANT to read it")));
        top.Children.Add(Stat("Rank vs skill", double.IsNaN(e.Gap) ? "–" : (e.Gap >= 0 ? "+" : "−") + Math.Abs(e.Gap).ToString("0.0", CultureInfo.CurrentCulture), double.IsNaN(e.Gap) ? TpMuted : e.Gap >= 0.7 ? TpUp : e.Gap <= -0.7 ? TpDown : TpInk,
            double.IsNaN(e.Gap) ? null : Loc.T(e.Gap >= 0.7 ? "tiers: skill above rank, expect to climb" : e.Gap <= -0.7 ? "tiers: rank above current play" : "tiers: rank matches play")));
        top.Children.Add(Stat("Confidence", N(e.Confidence) + "%", e.Confidence >= 70 ? TpUp : e.Confidence >= 40 ? TpInk : TpMuted, e.MatchesToConfident > 0 ? Loc.F("about {0} more matches to reach 80%", e.MatchesToConfident) : Loc.T("solid")));
        top.Children.Add(Stat("Momentum", (e.Momentum >= 0 ? "+" : "−") + Math.Abs(e.Momentum).ToString("0", CultureInfo.CurrentCulture), SignColor(e.Momentum), Loc.T("rating over the last 5 matches")));
        var sp = new StackPanel();
        sp.Children.Add(top);
        sp.Children.Add(new Border { Height = 1, Background = B(TpLine) });
        sp.Children.Add(MmrChart(e));
        sp.Children.Add(Note(Loc.F("Pulse's own estimate (model v{0}), not Riot's hidden MMR, which is private. Built from {1} matches ({2} skipped: too few ranked enemies). Highest estimate: {3}. The shaded band is the uncertainty.", MmrModel.Version, e.Used, e.Skipped, Rank(e.Peak / 100.0 - 0.5))));
        Root.Children.Add(Flat("Hidden MMR estimate", sp));

        // Forecast and what-if.
        int tier = _trMyRank?.Tier ?? 0, rr = _trMyRank?.Rr ?? 0;
        if (tier < 3) tier = _trMyRank?.LastTier ?? 0;
        if (tier < 3) tier = e.CurrentTier >= 3 ? e.CurrentTier : MmrModel.TierOf(e.Rating);
        var baseIn = Forecast.From(ms, me, tier, rr, _trMyRank?.RrChanges);
        var inputs = baseIn with
        {
            WinChance = double.IsNaN(_wiWin) ? baseIn.WinChance : _wiWin,
            RrWin = double.IsNaN(_wiGain) ? baseIn.RrWin : _wiGain,
            RrLoss = double.IsNaN(_wiLoss) ? baseIn.RrLoss : _wiLoss,
        };
        bool whatIf = !double.IsNaN(_wiWin) || !double.IsNaN(_wiGain) || !double.IsNaN(_wiLoss);
        var fc = new StackPanel();
        var rows = new[] { 10, 25, 50 }.Select(n => Forecast.Run(inputs, n)).Select(o => new UIElement[]
        {
            Txt(Loc.F("After {0} matches", o.Matches), 12.5, TpInk, true),
            Txt(ValorantMatches.RankName(o.Conservative), 12.5, TpBody),
            Txt(ValorantMatches.RankName(o.Expected), 12.5, TpInk, true),
            Txt(ValorantMatches.RankName(o.Optimistic), 12.5, TpBody),
            Txt(N(o.NextRankPct) + "%", 12.5, TpBody),
            Txt(N(o.ImmortalPct, "0.#") + "%", 12.5, TpBody),
            Txt(N(o.RadiantPct, "0.#") + "%", 12.5, TpBody),
        });
        fc.Children.Add(Table(new[] { "", "Conservative", "Expected", "Optimistic", "Next rank", "Immortal", "Radiant*" }, new double[] { 0, 110, 110, 110, 80, 80, 80 }, rows));
        // What-if sliders.
        var sliders = new Grid { Margin = new Thickness(12, 10, 12, 4) };
        sliders.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        sliders.ColumnDefinitions.Add(new ColumnDefinition());
        sliders.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        int row = 0;
        void Slide(string label, double min, double max, double value, string fmt, Action<double> set)
        {
            sliders.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Txt(Loc.T(label), 12, TpBody); Grid.SetRow(l, row); sliders.Children.Add(l);
            var v = Txt(value.ToString(fmt, CultureInfo.CurrentCulture), 12, TpInk, true, HorizontalAlignment.Right); Grid.SetRow(v, row); Grid.SetColumn(v, 2); sliders.Children.Add(v);
            var s = new Slider { Minimum = min, Maximum = max, Value = value, Margin = new Thickness(8, 4, 8, 4), IsMoveToPointEnabled = true };
            s.ValueChanged += (_, a) => v.Text = a.NewValue.ToString(fmt, CultureInfo.CurrentCulture);
            s.PreviewMouseLeftButtonUp += (_, _) => { set(s.Value); Build(); };
            s.KeyUp += (_, _) => { set(s.Value); Build(); };
            Grid.SetRow(s, row); Grid.SetColumn(s, 1); sliders.Children.Add(s);
            row++;
        }
        Slide("Win chance", 0.2, 0.8, inputs.WinChance, "0%", x => _wiWin = Math.Round(x, 2));
        Slide("RR per win", 10, 35, inputs.RrWin, "0", x => _wiGain = Math.Round(x));
        Slide("RR per loss", 10, 35, inputs.RrLoss, "0", x => _wiLoss = Math.Round(x));
        fc.Children.Add(Txt(Loc.T("What if…"), 12, TpInk, true).Also(t => t.Margin = new Thickness(12, 10, 12, 0)));
        fc.Children.Add(sliders);
        if (whatIf)
        {
            var reset = Btn("Back to my real numbers");
            reset.Margin = new Thickness(12, 0, 12, 4);
            reset.HorizontalAlignment = HorizontalAlignment.Left;
            reset.Click += (_, _) => { _wiWin = _wiGain = _wiLoss = double.NaN; Build(); };
            fc.Children.Add(reset);
        }
        fc.Children.Add(Note(Loc.F("{0} Starting from {1} {2} RR. Win chance {3:0}% (your last {4} competitive matches, {5} won, pulled toward 50%). {6} 3,000 simulated futures; a range of what could happen, not a promise. *Radiant also needs a leaderboard spot, so that number is an upper limit.",
            whatIf ? Loc.T("What-if: these are your numbers, not your history.") : Loc.T("From your history."),
            ValorantMatches.RankName(tier), rr, inputs.WinChance * 100, baseIn.Games, baseIn.Wins,
            baseIn.RrAssumed ? Loc.F("RR per win and loss assumed at {0:0} and {1:0} (Pulse hasn't read yours yet).", inputs.RrWin, inputs.RrLoss) : Loc.F("RR per win {0:0}, per loss {1:0}, from your recent games.", inputs.RrWin, inputs.RrLoss))));
        Root.Children.Add(Flat("Rank forecast", fc));

        // RR efficiency.
        var eff = RrEfficiency.Of(_trMyRank?.RrChanges, e);
        if (eff == null) Root.Children.Add(Wait("RR efficiency", "Needs your recent RR changes: open VALORANT (or the Riot Client) with a few competitive games played."));
        else
        {
            var g = new UniformGrid { Columns = 5 };
            g.Children.Add(Stat("RR per win", "+" + N(eff.AvgGain)));
            g.Children.Add(Stat("RR per loss", "−" + N(eff.AvgLoss)));
            g.Children.Add(Stat("RR per match", Signed(eff.PerMatch), SignColor(eff.PerMatch)));
            g.Children.Add(Stat("Expected from your play", Signed(eff.ExpectedPerMatch), SignColor(eff.ExpectedPerMatch)));
            g.Children.Add(Stat("Efficiency", Signed(eff.Efficiency), SignColor(eff.Efficiency), Loc.T(eff.Efficiency >= 3 ? "climbing faster than play predicts" : eff.Efficiency <= -3 ? "rank lagging behind play" : "about as expected")));
            var sp2 = new StackPanel();
            sp2.Children.Add(g);
            sp2.Children.Add(Note(Loc.F("From your last {0} RR changes. Expected = win chance (from the MMR estimate against your recent lobbies) × RR per win − the rest × RR per loss.", eff.Samples)));
            Root.Children.Add(Flat("RR efficiency", sp2));
        }
    }

    /// <summary>The estimate over time: the rating line, its uncertainty band, and rank lines.</summary>
    FrameworkElement MmrChart(MmrModel.Estimate e)
    {
        var pts = e.History;
        double w = 900, h = 180;
        double lo = pts.Min(p => p.Rating - p.Rd), hi = pts.Max(p => p.Rating + p.Rd);
        lo = Math.Floor(lo / 100) * 100; hi = Math.Ceiling(hi / 100) * 100;
        if (hi - lo < 300) { hi += 100; lo -= 100; }
        double X(int i) => pts.Count == 1 ? w / 2 : i * w / (pts.Count - 1);
        double Y(double v) => h - (v - lo) / (hi - lo) * h;
        var c = new Canvas { Width = w, Height = h, ClipToBounds = true };
        for (double t = lo; t <= hi; t += 100)
        {
            c.Children.Add(new Line { X1 = 0, X2 = w, Y1 = Y(t), Y2 = Y(t), Stroke = B(TpLine), StrokeThickness = 1 });
            if ((t / 100) is var tier && tier >= 3 && tier <= 27) c.Children.Add(Txt(ValorantMatches.RankName((int)tier), 10, TpMuted).Also(x => { Canvas.SetLeft(x, 4); Canvas.SetTop(x, Y(t) - 14); }));
        }
        var band = new PointCollection();
        for (int i = 0; i < pts.Count; i++) band.Add(new Point(X(i), Y(pts[i].Rating + pts[i].Rd)));
        for (int i = pts.Count - 1; i >= 0; i--) band.Add(new Point(X(i), Y(pts[i].Rating - pts[i].Rd)));
        c.Children.Add(new Polygon { Points = band, Fill = B(Color.FromArgb(0x26, TpGold.R, TpGold.G, TpGold.B)) });
        c.Children.Add(new Polyline { Points = new PointCollection(pts.Select((p, i) => new Point(X(i), Y(p.Rating)))), Stroke = B(TpGold), StrokeThickness = 2 });
        for (int i = 0; i < pts.Count; i++)
            c.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = B(pts[i].Won ? TpUp : TpDown), ToolTip = $"{pts[i].Utc.ToLocalTime():d MMM} · {Rank(pts[i].Rating / 100.0 - 0.5)} ({pts[i].Rating:0})" }
                .Also(d => { Canvas.SetLeft(d, X(i) - 3); Canvas.SetTop(d, Y(pts[i].Rating) - 3); }));
        return new Viewbox { Stretch = Stretch.Fill, Height = 190, Margin = new Thickness(12, 10, 12, 0), Child = c };
    }

    // ───────── matchmaking ─────────

    void IntelMatchmaking(List<VMatch> ms, string me)
    {
        var w = Matchmaking.All(ms, me);
        var rows = w.Select(x => new UIElement[]
        {
            Txt(Loc.F("Last {0}", x.Last), 12.5, TpInk, true),
            Txt(x.Matches.ToString(CultureInfo.CurrentCulture)),
            Txt(Rank(x.LobbyAvg)), Txt(Rank(x.EnemyAvg)), Txt(Rank(x.TeamAvg)),
            Txt(x.Matches > 0 ? $"{Rank(x.LowestEnemy)} – {Rank(x.HighestEnemy)}" : "–"),
            Txt(double.IsNaN(x.TeamGap) ? "–" : (x.TeamGap >= 0 ? "+" : "−") + Math.Abs(x.TeamGap).ToString("0.0", CultureInfo.CurrentCulture), 12.5, double.IsNaN(x.TeamGap) ? TpMuted : SignColor(x.TeamGap)),
            Txt(double.IsNaN(x.HigherPct) ? "–" : N(x.HigherPct) + "%"),
            Txt(double.IsNaN(x.ExpectedWin) ? "–" : N(x.ExpectedWin) + "%"),
            Txt(double.IsNaN(x.ActualWin) ? "–" : N(x.ActualWin) + "%", 12.5, x.ActualWin >= x.ExpectedWin ? TpUp : TpDown, true),
        });
        var sp = new StackPanel();
        sp.Children.Add(Table(new[] { "", "Matches", "Lobby", "Enemies", "Teammates", "Enemy range", "Team − enemy", "Tougher lobbies", "Expected win", "Actual win" },
            new double[] { 0, 64, 100, 100, 100, 170, 90, 100, 90, 80 }, rows));
        sp.Children.Add(Note("Ranks are the ones players had in each match; unranked players are left out. Team − enemy: tiers between your team's average and the enemies'. Tougher lobbies: enemies at least one tier above you. Expected win: from the two teams' ranks with Pulse's rating formula. Opponents' peak ranks need a Riot lookup per player, so they aren't included here."));
        Root.Children.Add(Flat("Matchmaking quality", sp));
    }

    // ───────── opponents ─────────

    void IntelOpponents(List<VMatch> ms, string me)
    {
        var all = HeadToHead.Of(ms, me);
        var reliable = all.Where(l => l.IsReliable).ToList();
        UIElement[] Row(HeadToHead.Line l) => new UIElement[]
        {
            new StackPanel { Orientation = Orientation.Horizontal }.Also(s =>
            {
                if (MatchArt.AgentIcon(l.Agent) is { } ai) s.Children.Add(Pic(ai, 24, 24, 8));
                if (MatchArt.RankIcon(l.LastTier) is { } ri) s.Children.Add(Pic(ri, 20, 20, 8));
                s.Children.Add(Txt(l.Name.Length > 0 ? l.Name : l.Agent, 12.5, TpInk, true));
            }),
            Txt(l.Encounters.ToString(CultureInfo.CurrentCulture), 12.5, l.IsReliable ? TpInk : TpMuted, l.IsReliable),
            Txt($"{l.Wins}–{l.Encounters - l.Wins}", 12.5, l.Wins * 2 >= l.Encounters ? TpUp : TpDown),
            Txt($"{l.KillsOnThem}–{l.DeathsToThem}", 12.5, SignColor(l.Net), true),
            Txt(l.Rounds.ToString(CultureInfo.CurrentCulture)),
            Txt(N(l.YourAcs)),
            Txt(N(l.Difficulty, "0.00"), 12.5, l.Difficulty >= 1.2 ? TpDown : TpBody),
            Txt(string.Join(", ", l.Maps.Take(3)), 11.5, TpMuted),
        };
        var heads = new[] { "Player", "Matches", "Your W–L", "Kills–deaths vs them", "Rounds", "Your ACS", "Difficulty", "Maps" };
        var widths = new double[] { 0, 66, 76, 130, 64, 70, 76, 160 };
        void Section(string title, IEnumerable<HeadToHead.Line> lines, string note)
        {
            var l = lines.ToList();
            if (l.Count == 0) { Root.Children.Add(Wait(title, note)); return; }
            var sp = new StackPanel();
            sp.Children.Add(Table(heads, widths, l.Select(Row), null, null, i => OpenFromIntel(l[i].Id)));
            Root.Children.Add(Flat(title, sp, note == null ? null : Loc.T(note)));
        }
        var last5 = ms.Take(5).SelectMany(m => m.Players.Where(p => p.Team != m.TeamOf(me)).Select(p => p.Id)).ToHashSet();
        Section("Most frequent opponents", all.Take(12), "Everyone you've played against, most matches first");
        Section("You beat them", reliable.Where(l => l.Net > 0 && l.Wins * 2 > l.Encounters).OrderByDescending(l => l.Net).Take(8), "3+ matches, more kills on them than deaths to them, and more wins");
        Section("They beat you", reliable.Where(l => l.Net < 0 && l.Wins * 2 < l.Encounters).OrderBy(l => l.Net).Take(8), "3+ matches, more deaths to them and more losses");
        Section("Toughest opponents", all.Where(l => l.Encounters >= 1).OrderByDescending(l => l.Difficulty).Take(8), "Played best against their lobby, weighted by rank");
        Section("From your last 5 matches", all.Where(l => last5.Contains(l.Id)).OrderByDescending(l => l.LastTier).Take(10), "Their rank in that match; peaks need a Riot lookup");
        Section("Highest-ranked opponents you've met", all.OrderByDescending(l => l.LastTier).Take(8), null);
        Root.Children.Add(Faint(Loc.T("A matchup is called reliable only after 3 matches against the same player; one or two meetings are listed but never ranked as wins or losses. Kills and deaths come from the kill feed."), 11.5, 0));
    }

    void OpenFromIntel(string id)
    {
        _tab = "tracker";
        ShowTab("tracker", false);
        OpenPlayer(id);
    }

    // ───────── performance ─────────

    void IntelPerformance(List<VMatch> ms, string me, MmrModel.Estimate mmr)
    {
        var ex = Expected.Of(ms, me, mmr);
        if (ex.Lines.Count == 0) Root.Children.Add(Wait("Expected vs actual", "Needs 4 matches: the first 3 build the baseline."));
        else
        {
            var g = new UniformGrid { Columns = 5 };
            g.Children.Add(Stat("ACS vs expected", Signed(ex.AvgAcsDiff), SignColor(ex.AvgAcsDiff)));
            g.Children.Add(Stat("ADR vs expected", Signed(ex.AvgAdrDiff), SignColor(ex.AvgAdrDiff)));
            g.Children.Add(Stat("K/D vs expected", (ex.AvgKdDiff >= 0 ? "+" : "−") + Math.Abs(ex.AvgKdDiff).ToString("0.00", CultureInfo.CurrentCulture), SignColor(ex.AvgKdDiff * 100)));
            g.Children.Add(Stat("KAST vs expected", (ex.AvgKastDiff >= 0 ? "+" : "−") + Math.Abs(ex.AvgKastDiff).ToString("0.0", CultureInfo.CurrentCulture) + "%", SignColor(ex.AvgKastDiff)));
            g.Children.Add(Stat("Above expected", N(ex.AboveShare) + "%", TpInk, Loc.F("of {0} matches · typical miss ±{1:0} ACS", ex.Lines.Count, ex.AcsMae)));
            var sp = new StackPanel();
            sp.Children.Add(g);
            var rows = ex.Lines.AsEnumerable().Reverse().Take(15).Select(l => new UIElement[]
            {
                Txt($"{l.Match.Map} · {l.Match.StartUtc.ToLocalTime():d MMM}", 12.5, TpInk, true),
                Txt(N(l.ExpAcs)), Txt(N(l.Acs), 12.5, TpInk, true), Txt(Signed(l.AcsDiff), 12.5, SignColor(l.AcsDiff), true),
                Txt(N(l.ExpKd, "0.00")), Txt(N(l.Kd, "0.00"), 12.5, KdColor(l.Kd)),
                Txt(N(l.WinChance * 100) + "%"), Txt(Loc.T(l.Won ? "Won" : "Lost"), 12.5, l.Won ? TpUp : TpDown),
            }).ToList();
            sp.Children.Add(Table(new[] { "Match", "Expected ACS", "ACS", "Difference", "Expected K/D", "K/D", "Win chance", "Result" }, new double[] { 0, 96, 60, 80, 96, 60, 80, 60 }, rows));
            var (brier, _) = MmrModel.Calibration(mmr);
            sp.Children.Add(Note(Loc.F("Each expectation is made from your earlier matches only, then adjusted for the lobby's ranks against your estimated skill and for your record on that agent (model v{0}). The win chance's accuracy so far: Brier score {1:0.000} (0.25 is a coin flip; lower is better).", Expected.Version, brier)));
            Root.Children.Add(Flat("Expected vs actual", sp));
        }

        var oa = OpponentAdjusted.Of(ms, me);
        var og = new UniformGrid { Columns = 3 };
        og.Children.Add(Stat("ACS", N(oa.Plain)));
        og.Children.Add(Stat("Opponent-adjusted ACS", N(oa.Adjusted), oa.Adjusted >= oa.Plain ? TpUp : TpDown, Loc.T("weighted by how strong the enemies were")));
        og.Children.Add(Stat("ACS against higher ranks", N(oa.VsHigher), TpInk, Loc.F("{0} matches with enemies above your rank", oa.HigherMatches)));
        Root.Children.Add(Flat("Opponent-adjusted performance", og));

        // Fingerprint radar and consistency, side by side.
        var fp = Fingerprint.Of(ms, me);
        var con = Consistency.Of(ms, me);
        var left = Flat("Skill fingerprint", new StackPanel().Also(s => { s.Children.Add(Radar(fp)); s.Children.Add(Note("Percentiles against every player-match Pulse has seen where it has 20 or more (solid labels); fixed ranges otherwise (marked *). Grey axes don't have enough data yet.")); }));
        FrameworkElement right;
        if (con == null) right = Wait("Consistency", "Needs 5 matches.");
        else
        {
            var cs = new StackPanel();
            var cg = new UniformGrid { Columns = 2 };
            cg.Children.Add(Stat("Consistency index", N(con.Index), con.Index >= 60 ? TpUp : con.Index >= 35 ? TpInk : TpDown, Loc.T(con.Index >= 60 ? "steady" : con.Index >= 35 ? "some swings" : "big swings")));
            cg.Children.Add(Stat("Pulse Score spread", "±" + N(con.Sd), TpInk, Loc.F("average {0:0}", con.Mean)));
            cg.Children.Add(Stat("Exceptional games", N(con.ExceptionalPct) + "%", TpGold, Loc.T("750 or more")));
            cg.Children.Add(Stat("Poor games", N(con.PoorPct) + "%", TpDown, Loc.T("under 300")));
            cs.Children.Add(cg);
            if (con.ByAgent.Count > 0) cs.Children.Add(Note(Loc.F("Steadiest agent: {0} (±{1:0}). Steadiest map: {2}.", con.ByAgent[0].Name, con.ByAgent[0].Sd, con.ByMap.Count > 0 ? con.ByMap[0].Name : "–")));
            right = Flat("Consistency", cs);
        }
        Root.Children.Add(PanelRow((left, 1), (right, 1)));
    }

    /// <summary>A radar of the fingerprint's axes (0–100 each).</summary>
    FrameworkElement Radar(List<Fingerprint.Axis> axes)
    {
        double size = 320, cx = size / 2, cy = size / 2, rMax = 110;
        var c = new Canvas { Width = size, Height = size };
        int n = axes.Count;
        Point At(int i, double v) { double a = -Math.PI / 2 + 2 * Math.PI * i / n; return new Point(cx + Math.Cos(a) * rMax * v / 100, cy + Math.Sin(a) * rMax * v / 100); }
        foreach (var ring in new[] { 25, 50, 75, 100 })
            c.Children.Add(new Polygon { Points = new PointCollection(Enumerable.Range(0, n).Select(i => At(i, ring))), Stroke = B(TpLine), StrokeThickness = 1 });
        for (int i = 0; i < n; i++)
        {
            var end = At(i, 100);
            c.Children.Add(new Line { X1 = cx, Y1 = cy, X2 = end.X, Y2 = end.Y, Stroke = B(TpLine) });
            var lp = At(i, 128);
            var known = !double.IsNaN(axes[i].Value);
            var label = Txt(Loc.T(axes[i].Name) + (axes[i].FromScale ? "" : "*") + (known ? $" {axes[i].Value:0}" : ""), 10.5, known ? TpBody : TpMuted, known);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, lp.X - label.DesiredSize.Width / 2); Canvas.SetTop(label, lp.Y - 8);
            c.Children.Add(label);
        }
        var shape = new PointCollection(Enumerable.Range(0, n).Select(i => At(i, double.IsNaN(axes[i].Value) ? 0 : axes[i].Value)));
        c.Children.Add(new Polygon { Points = shape, Fill = B(Color.FromArgb(0x40, TpBlue.R, TpBlue.G, TpBlue.B)), Stroke = B(TpBlue), StrokeThickness = 2 });
        return new Viewbox { Height = 300, Child = c, Margin = new Thickness(12) };
    }

    // ───────── agents and maps ─────────

    void IntelAgents(List<VMatch> ms, string me)
    {
        var list = AgentIntel.Of(ms, me);
        if (list.Count == 0) { Root.Children.Add(Wait("Agents", "No matches yet.")); return; }
        var rows = list.Select(a => new UIElement[]
        {
            new StackPanel { Orientation = Orientation.Horizontal }.Also(s => { if (MatchArt.AgentIcon(a.Name) is { } ai) s.Children.Add(Pic(ai, 26, 26)); s.Children.Add(Txt(a.Name, 12.5, TpInk, true)); }),
            Txt(a.Matches.ToString(CultureInfo.CurrentCulture)), Txt(N(a.WinPct) + "%", 12.5, a.WinPct >= 50 ? TpUp : TpDown),
            Txt(N(a.Kd, "0.00"), 12.5, KdColor(a.Kd), true), Txt(N(a.Acs)), Txt(N(a.Adr)), Txt(N(a.Kast) + "%"), Txt(double.IsNaN(a.Hs) ? "–" : N(a.Hs) + "%"),
            Txt(Signed(a.FbDiff), 12.5, SignColor(a.FbDiff)), Txt(N(a.AtkAcs)), Txt(N(a.DefAcs)),
            Txt(N(a.Score), 12.5, TpInk, true), Txt("±" + N(a.Sd)), Txt(N(a.Ceiling), 12.5, TpGold), Txt(a.BestMap, 11.5, TpMuted),
        });
        var sp = new StackPanel();
        sp.Children.Add(Table(new[] { "Agent", "Matches", "Win %", "K/D", "ACS", "ADR", "KAST", "HS%", "FB diff /100", "Attack impact", "Defense impact", "Score", "Spread", "Best game", "Best map" },
            new double[] { 0, 60, 56, 50, 46, 46, 50, 46, 80, 84, 88, 50, 56, 66, 90 }, rows));
        sp.Children.Add(Note("Ranked by Pulse Score pulled toward your overall average until an agent has more games ((n × agent + 5 × overall) / (n + 5)), so two lucky games don't top the list. Attack and defense impact: kills × 150 plus damage, per round."));
        Root.Children.Add(Flat("Agent performance", sp));

        var recs = new List<string>();
        var best = list.First();
        recs.Add(Loc.F("Your strongest agent so far: {0} (score {1:0} over {2} matches).", best.Name, best.Score, best.Matches));
        foreach (var n in new[] { 5, 10, 25 })
            if (AgentIntel.BestRecent(ms, me, n) is { } b) recs.Add(Loc.F("Best in your last {0} matches: {1}.", n, b.Name));
        foreach (var role in list.GroupBy(a => a.Role).Where(g => g.Key.Length > 0)) recs.Add(Loc.F("Best {0}: {1}.", Loc.T(role.Key), role.First().Name));
        var steady = list.Where(a => a.Matches >= 4 && !double.IsNaN(a.Sd)).OrderBy(a => a.Sd).FirstOrDefault();
        if (steady != null) recs.Add(Loc.F("Most consistent: {0} (±{1:0}).", steady.Name, steady.Sd));
        var ceiling = list.Where(a => !double.IsNaN(a.Ceiling)).OrderByDescending(a => a.Ceiling).FirstOrDefault();
        if (ceiling != null) recs.Add(Loc.F("Highest ceiling: {0} (best game {1:0}).", ceiling.Name, ceiling.Ceiling));
        // A recommendation from strengths: the fingerprint's strongest axis suggests a role.
        var fp = Fingerprint.Of(ms, me).Where(x => !double.IsNaN(x.Value)).OrderByDescending(x => x.Value).FirstOrDefault();
        if (fp != null)
        {
            string role = fp.Name switch { "Mechanics" or "Duels" or "Opening impact" => "Duelist", "Utility" => "Initiator", "Positioning" or "Trading" => "Sentinel", "Clutch" => "Sentinel", _ => null };
            if (role != null)
            {
                var pick = list.Where(a => a.Role == role).FirstOrDefault();
                recs.Add(pick != null ? Loc.F("Your strongest skill is {0}, which suits {1}s; your best one is {2}.", Loc.T(fp.Name), Loc.T(role), pick.Name)
                    : Loc.F("Your strongest skill is {0}, which suits {1}s, a role you haven't played much here.", Loc.T(fp.Name), Loc.T(role)));
            }
        }
        var rs = new StackPanel();
        foreach (var r in recs) rs.Children.Add(Txt("• " + r, 12.5, TpBody).Also(t => { t.Margin = new Thickness(12, 4, 12, 4); t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; }));
        Root.Children.Add(Flat("Agent picks", rs));
        if (Tracker.TierList(VStore.All) is { Count: >= 4 } tiers) Root.Children.Add(TierListCard(tiers, VStore.All.Count(m => VMatch.Counts(m.Queue))));
        Root.Children.Add(Faint(Loc.T("An agent leaderboard across every Pulse user needs a Pulse server that collects matches; Pulse keeps everything on your PC, so the tier list above is from your own lobbies."), 11.5, 0));
    }

    void IntelMaps(List<VMatch> ms, string me)
    {
        var list = MapIntel.Of(ms, me);
        if (list.Count == 0) { Root.Children.Add(Wait("Maps", "No matches yet.")); return; }
        var rows = list.Select(m => new UIElement[]
        {
            new StackPanel { Orientation = Orientation.Horizontal }.Also(s =>
            {
                if (MatchArt.MapPicture(m.Name) is { } pic) s.Children.Add(new Border { Width = 44, Height = 24, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 8, 0), ClipToBounds = true, Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill } });
                s.Children.Add(Txt(m.Name, 12.5, TpInk, true));
                if (!m.Enough) s.Children.Add(Txt("  " + Loc.T("not enough data"), 11, TpMuted));
            }),
            Txt(m.Matches.ToString(CultureInfo.CurrentCulture)), Txt(N(m.WinPct) + "%", 12.5, m.WinPct >= 50 ? TpUp : TpDown, true),
            Txt(N(m.AtkRoundPct) + "%"), Txt(N(m.DefRoundPct) + "%"), Txt(N(m.Acs)), Txt(N(m.Adr)), Txt(N(m.Kd, "0.00"), 12.5, KdColor(m.Kd)),
            Txt(m.BestAgent.Length > 0 ? $"{m.BestAgent} ({N(m.BestAgentScore)})" : "–", 11.5, TpMuted),
        });
        var sp = new StackPanel();
        sp.Children.Add(Table(new[] { "Map", "Matches", "Win %", "Attack rounds won", "Defense rounds won", "ACS", "ADR", "K/D", "Best agent here" }, new double[] { 0, 64, 60, 120, 124, 50, 50, 50, 150 }, rows));
        Root.Children.Add(Flat("Map performance", sp));
        var enough = list.Where(m => m.Enough).OrderByDescending(m => m.WinPct).ToList();
        var recs = new List<string>();
        if (enough.Count >= 2)
        {
            recs.Add(Loc.F("Strongest map: {0} ({1:0}% won over {2}).", enough[0].Name, enough[0].WinPct, enough[0].Matches));
            var weak = enough[^1];
            recs.Add(Loc.F("Weakest map: {0} ({1:0}% won over {2}).", weak.Name, weak.WinPct, weak.Matches));
            if (!double.IsNaN(weak.AtkRoundPct) && !double.IsNaN(weak.DefRoundPct))
                recs.Add(weak.AtkRoundPct < weak.DefRoundPct - 8 ? Loc.F("On {0} your attack is the problem ({1:0}% of attack rounds won vs {2:0}% on defense): learn one or two default executes there.", weak.Name, weak.AtkRoundPct, weak.DefRoundPct)
                    : weak.DefRoundPct < weak.AtkRoundPct - 8 ? Loc.F("On {0} your defense is the problem ({1:0}% of defense rounds won vs {2:0}% on attack): settle on one site setup and hold it.", weak.Name, weak.DefRoundPct, weak.AtkRoundPct)
                    : Loc.F("On {0} attack and defense are both behind; the agent you play there matters most: your best there is {1}.", weak.Name, weak.BestAgent.Length > 0 ? weak.BestAgent : Loc.T("not clear yet")));
        }
        var thin = list.Where(m => !m.Enough).Select(m => m.Name).ToList();
        if (thin.Count > 0) recs.Add(Loc.F("Not enough data yet (under 3 matches): {0}.", string.Join(", ", thin)));
        var rs = new StackPanel();
        foreach (var r in recs) rs.Children.Add(Txt("• " + r, 12.5, TpBody).Also(t => { t.Margin = new Thickness(12, 4, 12, 4); t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; }));
        Root.Children.Add(Flat("What to work on, by map", rs));
    }

    // ───────── sessions and teammates ─────────

    void IntelSessions(List<VMatch> ms, string me)
    {
        var s = Sessions.Of(ms, me);
        if (s == null) { Root.Children.Add(Wait("Sessions", "Needs 5 matches.")); return; }
        var top = new UniformGrid { Columns = 4 };
        top.Children.Add(Stat("Sessions", s.Sessions.ToString(CultureInfo.CurrentCulture), TpInk, Loc.F("{0:0.0} matches each on average", s.AvgLength)));
        top.Children.Add(Stat("Suggested session length", s.SuggestedLength > 0 ? Loc.F("{0} matches", s.SuggestedLength) : "–", TpGold, s.SuggestedLength > 0 ? Loc.T("before your score tends to drop") : Loc.T("needs 3 sessions of each length")));
        top.Children.Add(Stat("After 2 losses in a row", N(s.AfterLossStreak), double.IsNaN(s.AfterLossStreak) ? TpMuted : s.AfterLossStreak < s.Overall - 40 ? TpDown : TpInk, Loc.F("Pulse Score, {0} times · your average {1:0}", s.AfterLossN, s.Overall)));
        top.Children.Add(Stat("First match after a break", N(s.AfterBreak), TpInk, Loc.F("{0} times", s.AfterBreakN)));
        var sp = new StackPanel();
        sp.Children.Add(top);
        var rows = s.Slots.Select(x => new UIElement[]
        {
            Txt(x.Position >= 5 ? Loc.T("5th and later") : Loc.F("Match {0} of a session", x.Position), 12.5, TpInk, true),
            Txt(x.Matches.ToString(CultureInfo.CurrentCulture), 12.5, x.Matches >= 3 ? TpBody : TpMuted),
            Txt(N(x.Score), 12.5, TpInk, true), Txt(N(x.Kd, "0.00"), 12.5, KdColor(x.Kd)), Txt(N(x.WinPct) + "%", 12.5, x.WinPct >= 50 ? TpUp : TpDown),
        });
        sp.Children.Add(Table(new[] { "", "Matches", "Pulse Score", "K/D", "Win %" }, new double[] { 0, 70, 90, 60, 60 }, rows));
        sp.Children.Add(Note("A session is matches less than 45 minutes apart. These are patterns in your results, not proof of fatigue or tilt: a drop late in sessions can also come from tougher lobbies or a different mood. Positions with fewer than 3 matches are greyed."));
        Root.Children.Add(Flat("Sessions and performance", sp));
    }

    void IntelTeammates(List<VMatch> ms, string me)
    {
        var list = Synergy.Of(ms, me);
        if (list.Count == 0) { Root.Children.Add(Wait("Teammates", "Shows anyone who's been on your team twice or more.")); return; }
        var rows = list.Take(20).Select(m => new UIElement[]
        {
            new StackPanel { Orientation = Orientation.Horizontal }.Also(s => { s.Children.Add(Txt(m.Name, 12.5, TpInk, true)); if (m.Party) s.Children.Add(Txt("  " + Loc.T("duo"), 11, TpGold, true)); }),
            Txt(m.Matches.ToString(CultureInfo.CurrentCulture), 12.5, m.Matches >= 3 ? TpInk : TpMuted),
            Txt(N(m.WinPct) + "%", 12.5, m.WinPct >= 50 ? TpUp : TpDown, true), Txt(N(m.ExpectedWinPct) + "%"),
            Txt(m.Matches >= 3 ? Signed(m.VsExpected) : "–", 12.5, m.Matches >= 3 ? SignColor(m.VsExpected) : TpMuted, true),
            Txt(N(m.ScoreWith)), Txt(N(m.ScoreWithout), 12.5, TpMuted),
            Txt(string.Join(", ", m.Agents.Take(3)), 11.5, TpMuted),
        });
        var sp = new StackPanel();
        sp.Children.Add(Table(new[] { "Teammate", "Together", "Win %", "Expected", "Vs expected", "Your score with", "Without", "Their agents" }, new double[] { 0, 70, 60, 70, 86, 100, 70, 160 }, rows, null, null, i => OpenFromIntel(list[i].Id)));
        sp.Children.Add(Note("Expected: the win chance from both teams' ranks in those matches, so a teammate isn't credited for easy lobbies. Vs expected needs 3 matches together. Duo: you queued together at least once."));
        Root.Children.Add(Flat("Teammates", sp));
    }

    // ───────── career and improvement ─────────

    void IntelCareer(List<VMatch> ms, string me, MmrModel.Estimate mmr)
    {
        var c = Career.Of(ms, me, mmr);
        if (c == null) { Root.Children.Add(Wait("Career", "No matches yet.")); return; }
        var acts = _trMyRank?.Acts ?? new();
        var g = new UniformGrid { Columns = 4 };
        var bestAct = acts.Where(a => a.Tier >= 3).OrderByDescending(a => a.Tier).ThenByDescending(a => a.WinPct).FirstOrDefault();
        g.Children.Add(Stat("Highest rank", acts.Count > 0 ? ValorantMatches.RankName(Math.Max(acts.Max(a => a.Peak), acts.Max(a => a.Tier))) : "–", TpGold, bestAct != null ? Loc.F("best act: {0}", bestAct.Name) : Loc.T("open VALORANT to read your acts")));
        g.Children.Add(Stat("Highest estimated skill", Rank(c.PeakMmr / 100.0 - 0.5), TpInk, Loc.T("Pulse's estimate")));
        g.Children.Add(Stat("Best agent", c.BestAgent.Length > 0 ? c.BestAgent : "–"));
        g.Children.Add(Stat("Best map", c.BestMap.Length > 0 ? c.BestMap : "–"));
        g.Children.Add(Stat("Longest win streak", c.LongestWin.ToString(CultureInfo.CurrentCulture), TpUp));
        g.Children.Add(Stat("Longest losing streak", c.LongestLoss.ToString(CultureInfo.CurrentCulture), TpDown));
        g.Children.Add(Stat("Current streak", c.CurrentStreak > 0 ? Loc.F("{0} wins", c.CurrentStreak) : c.CurrentStreak < 0 ? Loc.F("{0} losses", -c.CurrentStreak) : "–", c.CurrentStreak >= 0 ? TpUp : TpDown));
        g.Children.Add(Stat("Improvement", double.IsNaN(c.ImprovementPer10) ? "–" : Signed(c.ImprovementPer10), SignColor(c.ImprovementPer10), Loc.T("Pulse Score per 10 matches (trend)")));
        Root.Children.Add(Flat("Career", g));
        if (acts.Count > 0)
        {
            var rows = acts.Select(a => new UIElement[]
            {
                new StackPanel { Orientation = Orientation.Horizontal }.Also(s => { if (MatchArt.RankIcon(a.Tier) is { } ri) s.Children.Add(Pic(ri, 22, 22)); s.Children.Add(Txt(a.Name.Length > 0 ? a.Name : Loc.T("Earlier act"), 12.5, TpInk, true)); }),
                Txt(a.Tier >= 3 ? ValorantMatches.RankName(a.Tier) : "–"), Txt(a.Peak >= 3 ? ValorantMatches.RankName(a.Peak) : "–"),
                Txt(a.Games.ToString(CultureInfo.CurrentCulture)), Txt(a.Games > 0 ? N(a.WinPct) + "%" : "–", 12.5, a.WinPct >= 50 ? TpUp : TpDown),
            });
            Root.Children.Add(Flat("Every act", Table(new[] { "Act", "Final rank", "Peak", "Games", "Win %" }, new double[] { 0, 130, 130, 70, 70 }, rows)));
        }
        if (c.Milestones.Count > 0 || c.BestMatch != null)
        {
            var sp = new StackPanel();
            foreach (var (when, what) in c.Milestones) sp.Children.Add(Txt($"{when.ToLocalTime():d MMM yyyy} · {what}", 12.5, TpBody).Also(t => t.Margin = new Thickness(12, 4, 12, 4)));
            if (c.BestMatch != null)
            {
                var b = Btn(Loc.F("Open your best match ({0:0}, {1})", c.BestMatchScore, c.BestMatch.Map));
                b.Margin = new Thickness(12, 8, 12, 12);
                b.HorizontalAlignment = HorizontalAlignment.Left;
                var id = c.BestMatch.Id;
                b.Click += (_, _) => { _trSearched = null; _trMatchId = id; _trMatchTab = "scoreboard"; ShowTab("tracker", false); };
                sp.Children.Add(b);
            }
            Root.Children.Add(Flat("Milestones", sp));
        }
    }

    void IntelImprove(List<VMatch> ms, string me)
    {
        var r = Improvement.Of(ms, me);
        if (r == null) { Root.Children.Add(Wait("Improvement", "Needs 12 matches: your last 10 against the 10 before.")); return; }
        var rows = r.Changes.Select(c => new UIElement[]
        {
            Txt(Loc.T(c.Stat), 12.5, TpInk, true), Txt(N(c.Before, "0.0")), Txt(N(c.Now, "0.0"), 12.5, TpInk, true),
            Txt((c.Delta >= 0 ? "+" : "−") + Math.Abs(c.Delta).ToString("0.0", CultureInfo.CurrentCulture), 12.5, c.Better ? TpUp : c.Worse ? TpDown : TpMuted, c.Real),
            Txt(Loc.T(c.Better ? "Improved" : c.Worse ? "Declined" : "Within normal swings"), 12, c.Better ? TpUp : c.Worse ? TpDown : TpMuted),
        });
        Root.Children.Add(Flat("Last 10 matches vs the 10 before", Table(new[] { "Stat", "Before", "Now", "Change", "" }, new double[] { 0, 80, 80, 80, 160 }, rows)));
        var wl = r.WinVsLoss.Select(x => new UIElement[] { Txt(Loc.T(x.Stat), 12.5, TpInk, true), Txt(N(x.Wins, "0.0"), 12.5, TpUp), Txt(N(x.Losses, "0.0"), 12.5, TpDown) });
        Root.Children.Add(Flat("In wins vs in losses", Table(new[] { "Stat", "Wins", "Losses" }, new double[] { 0, 90, 90 }, wl)));
        var bt = r.ByTime.Select(x => new UIElement[] { Txt(x.Hours, 12.5, TpInk, true), Txt(N(x.Score)), Txt(x.N.ToString(CultureInfo.CurrentCulture), 12.5, TpMuted) });
        Root.Children.Add(Flat("When you play best", Table(new[] { "Time of day", "Pulse Score", "Matches" }, new double[] { 0, 100, 80 }, bt)));
        // The plan: the declines and the coach's top weaknesses.
        var plan = new List<string>();
        foreach (var c in r.Changes.Where(c => c.Worse)) plan.Add(Loc.F("{0} has dropped from {1:0.0} to {2:0.0}: look at a few of your recent losses in Death review for the pattern.", Loc.T(c.Stat), c.Before, c.Now));
        try
        {
            var coach = Coach.Analyze(ms);
            foreach (var w in coach.Weaknesses.Take(3)) plan.Add(Loc.F("Work on {0}: {1}", Loc.T(w.Metric.Name), Loc.T(w.Metric.Tip.Steps.Length > 0 ? w.Metric.Tip.Steps[0] : w.Metric.Tip.Why)));
        }
        catch { }
        if (plan.Count == 0) plan.Add(Loc.T("Nothing is declining and nothing stands out as a weakness: pick the stat you'd most like to push and make it this week's focus in Coach."));
        var ps = new StackPanel();
        foreach (var p in plan) ps.Children.Add(Txt("• " + p, 12.5, TpBody).Also(t => { t.Margin = new Thickness(12, 4, 12, 4); t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; }));
        ps.Children.Add(Note("A change counts only when it's bigger than half your normal match-to-match swing for that stat."));
        Root.Children.Add(Flat("Your plan", ps));
    }

    // ───────── network and compare ─────────

    void IntelNetwork(List<VMatch> ms, string me)
    {
        var nodes = Network.Of(ms, me);
        if (nodes.Count == 0) { Root.Children.Add(Wait("Your player network", "Shows players you've met twice or more.")); return; }
        double size = 560, cx = size / 2, cy = size / 2;
        var c = new Canvas { Width = size, Height = size };
        int maxT = nodes.Max(n => n.Total);
        var placed = new List<(Network.Node N, Point P)>();
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            double ring = 110 + 140 * (1 - (double)n.Total / Math.Max(1, maxT));
            double a = 2 * Math.PI * i / nodes.Count - Math.PI / 2;
            placed.Add((n, new Point(cx + Math.Cos(a) * ring, cy + Math.Sin(a) * ring)));
        }
        foreach (var (n, p) in placed)
        {
            var col = n.With > 0 && n.Against > 0 ? Color.FromRgb(0xC9, 0xA2, 0xFF) : n.With > 0 ? TpUp : TpDown;
            c.Children.Add(new Line { X1 = cx, Y1 = cy, X2 = p.X, Y2 = p.Y, Stroke = B(Color.FromArgb(0x60, col.R, col.G, col.B)), StrokeThickness = 1 + 3.0 * n.Total / maxT });
        }
        c.Children.Add(new Ellipse { Width = 26, Height = 26, Fill = B(TpGold) }.Also(e => { Canvas.SetLeft(e, cx - 13); Canvas.SetTop(e, cy - 13); }));
        foreach (var (n, p) in placed)
        {
            var col = n.With > 0 && n.Against > 0 ? Color.FromRgb(0xC9, 0xA2, 0xFF) : n.With > 0 ? TpUp : TpDown;
            double r = 7 + 8.0 * n.Total / maxT;
            var dot = new Ellipse { Width = r * 2, Height = r * 2, Fill = B(col), Cursor = Cursors.Hand, ToolTip = Loc.F("{0}: {1} with you, {2} against", n.Name, n.With, n.Against) };
            var id = n.Id;
            dot.MouseLeftButtonUp += (_, _) => OpenFromIntel(id);
            Canvas.SetLeft(dot, p.X - r); Canvas.SetTop(dot, p.Y - r);
            c.Children.Add(dot);
            var label = Txt(n.Name, 10.5, TpBody);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, p.X - label.DesiredSize.Width / 2); Canvas.SetTop(label, p.Y + r + 1);
            c.Children.Add(label);
        }
        var sp = new StackPanel();
        sp.Children.Add(new Viewbox { Height = 520, Child = c, Margin = new Thickness(12) });
        sp.Children.Add(Note("You're in the middle. Green: teammates, red: opponents, purple: both. Closer and bigger means more matches together. Only players from your own matches; click one to open their profile."));
        Root.Children.Add(Flat("Your player network", sp));
    }

    void IntelCompare(List<VMatch> ms, string me)
    {
        var people = new List<(string Id, string Name)> { (me, NameBook.Get(me) is { } mn ? Loc.T("You") + " · " + mn : Loc.T("You")) };
        foreach (var k in Tracker.People(ms).Take(30)) people.Add((k.Puuid, NameBook.Get(k.Puuid) ?? $"{k.Agent} · {ValorantMatches.RankName(k.Tier)}"));
        if (_trSearched != null && !_trSearched.FromYourMatches) people.Add((_trSearched.Puuid, "🔍 " + _trSearched.Name));
        _cmpA ??= me;
        _cmpB ??= people.Count > 1 ? people[1].Id : me;
        var pick = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        pick.ColumnDefinitions.Add(new ColumnDefinition());
        pick.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        pick.ColumnDefinitions.Add(new ColumnDefinition());
        ComboBox Box(string sel, Action<string> set)
        {
            var b = new ComboBox();
            foreach (var p in people) b.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
            b.SelectedIndex = Math.Max(0, people.FindIndex(p => p.Id == sel));
            b.SelectionChanged += (_, _) => { if (b.SelectedItem is ComboBoxItem ci && ci.Tag is string id) { set(id); Build(); } };
            return b;
        }
        pick.Children.Add(Box(_cmpA, id => _cmpA = id));
        pick.Children.Add(Txt("vs", 13, TpMuted, true, HorizontalAlignment.Center).Also(t => Grid.SetColumn(t, 1)));
        var bb = Box(_cmpB, id => _cmpB = id); Grid.SetColumn(bb, 2); pick.Children.Add(bb);
        Profile P(string id) => _trSearched != null && id == _trSearched.Puuid && !_trSearched.FromYourMatches ? _trSearched : Tracker.FromYourMatches(ms, id);
        var a = P(_cmpA); var b2 = P(_cmpB);
        var oa = Stats.Of(a.Matches, a.Puuid); var ob = Stats.Of(b2.Matches, b2.Puuid);
        var ca = Consistency.Of(a.Matches, a.Puuid); var cb = Consistency.Of(b2.Matches, b2.Puuid);
        var adjA = OpponentAdjusted.Of(a.Matches, a.Puuid); var adjB = OpponentAdjusted.Of(b2.Matches, b2.Puuid);
        var mmrA = MmrModel.Run(a.Matches, a.Puuid); var mmrB = MmrModel.Run(b2.Matches, b2.Puuid);
        var lines = new List<(string Label, double A, double B, string Fmt, bool Higher)>
        {
            ("Matches", oa.Matches, ob.Matches, "0", true),
            ("Rank (tier)", a.Tier >= 3 ? a.Tier : double.NaN, b2.Tier >= 3 ? b2.Tier : double.NaN, "0", true),
            ("Peak (tier)", a.Peak >= 3 ? a.Peak : double.NaN, b2.Peak >= 3 ? b2.Peak : double.NaN, "0", true),
            ("Estimated skill (rating)", mmrA?.Rating ?? double.NaN, mmrB?.Rating ?? double.NaN, "0", true),
            ("K/D", oa.Kd, ob.Kd, "0.00", true), ("ACS", oa.Acs, ob.Acs, "0", true), ("ADR", oa.DamagePerRound, ob.DamagePerRound, "0", true),
            ("KAST", oa.KastPct, ob.KastPct, "0.0", true), ("Headshot %", oa.HeadshotPct, ob.HeadshotPct, "0.0", true), ("Win %", oa.WinPct, ob.WinPct, "0.0", true),
            ("First bloods per match", oa.Matches > 0 ? (double)oa.FirstBloods / oa.Matches : double.NaN, ob.Matches > 0 ? (double)ob.FirstBloods / ob.Matches : double.NaN, "0.0", true),
            ("Clutch %", oa.ClutchPct, ob.ClutchPct, "0", true),
            ("Consistency", ca?.Index ?? double.NaN, cb?.Index ?? double.NaN, "0", true),
            ("Opponent-adjusted ACS", adjA.Adjusted, adjB.Adjusted, "0", true),
            ("Pulse Score", a.Score, b2.Score, "0", true),
        };
        var rows = lines.Select(l =>
        {
            bool known = !double.IsNaN(l.A) && !double.IsNaN(l.B);
            bool aWins = known && (l.Higher ? l.A > l.B : l.A < l.B), bWins = known && (l.Higher ? l.B > l.A : l.B < l.A);
            string F(double v) => l.Label.Contains("tier") ? Rank(v) : N(v, l.Fmt);
            return new UIElement[] { Txt(Loc.T(l.Label), 12.5, TpInk, true), Txt(F(l.A), 12.5, aWins ? TpUp : TpBody, aWins), Txt(F(l.B), 12.5, bWins ? TpUp : TpBody, bWins) };
        });
        var sp = new StackPanel();
        sp.Children.Add(pick);
        sp.Children.Add(Table(new[] { "", a.IsYou ? Loc.T("You") : NameBook.Get(a.Puuid) ?? "A", b2.IsYou ? Loc.T("You") : NameBook.Get(b2.Puuid) ?? (b2.Name.Length > 0 ? b2.Name : "B") }, new double[] { 0, 170, 170 }, rows));
        sp.Children.Add(Note("Players from your own matches are measured from the matches you shared; a searched player from their own recent matches. Comparing two players who are both strangers to you needs a lookup of each (player search)."));
        Root.Children.Add(Flat("Compare players", sp));
    }

    // ───────── data checks and methods ─────────

    void IntelData()
    {
        var all = VStore.All;
        var (n, issues) = DataQuality.Check(all);
        var top = new UniformGrid { Columns = 3 };
        int bad = issues.Select(i => i.Match.Id).Distinct().Count();
        top.Children.Add(Stat("Matches checked", n.ToString(CultureInfo.CurrentCulture)));
        top.Children.Add(Stat("Pass every check", (n - bad).ToString(CultureInfo.CurrentCulture), TpUp));
        top.Children.Add(Stat("With something to note", bad.ToString(CultureInfo.CurrentCulture), bad > 0 ? TpGold : TpMuted));
        var sp = new StackPanel();
        sp.Children.Add(top);
        if (issues.Count > 0)
        {
            var rows = issues.Take(40).Select(i => new UIElement[] { Txt($"{i.Match.Map} · {i.Match.StartUtc.ToLocalTime():d MMM HH:mm}", 12.5, TpInk, true), Txt(i.Problem, 12, TpBody).Also(t => t.HorizontalAlignment = HorizontalAlignment.Left) });
            sp.Children.Add(Table(new[] { "Match", "What doesn't add up" }, new double[] { 220, 0 }, rows));
        }
        sp.Children.Add(Note("Every panel uses the same calculations (one shared engine). When two parts of Riot's data disagree, the match is listed here instead of quietly picking one number; stats use Riot's scoreboard."));
        Root.Children.Add(Flat("Data checks", sp));
    }

    void IntelMethods()
    {
        var items = new (string Name, string How)[]
        {
            ("Hidden MMR estimate (v1)", "Elo/Glicko-style. 100 points per rank tier. Each match: your side's strength = (4 × teammates' average + you) / 5; expected result E = 1 / (1 + 10^((enemies − your side) / 400)); score S = 0.6 × result + 0.4 × your combat score against the lobby (0.5 = average); rating += K × (S − E) with K = 30 + 120 × uncertainty / 350. Uncertainty starts at 350, shrinks 7% a match (floor 50) and grows back with days away. Matches with fewer than 3 ranked enemies are skipped. Not Riot's MMR."),
            ("Rank forecast (v1)", "3,000 simulated futures with a fixed seed. Win chance: your last 20 competitive matches, with 5 wins and 5 losses added (pulls toward 50%). RR per win and loss: Riot's recent changes, else 20 and 17 (shown as assumed). 100 RR per tier; Radiant needs a leaderboard spot, so its chance is an upper limit."),
            ("RR efficiency (v1)", "Your average RR per match from Riot, minus what your win chance (MMR estimate vs your recent lobbies) and RR per win/loss would give."),
            ("Matchmaking quality (v1)", "Averages of each player's rank in that match (unranked left out), over your last 5, 10, 25, 50 and 100 matches. Expected win from the two teams' average ranks."),
            ("Head-to-head (v1)", "Kills on and deaths to each opponent from the kill feed. Reliable after 3 matches. Difficulty: their combat score against their lobby × (1 + 0.05 × tiers above you)."),
            ("Expected vs actual (v1)", "From earlier matches only: your last 20 averaged, pulled toward those lobbies' averages ((n × you + 3 × lobbies) / (n + 3)), × (1 + 0.05 × (your estimated skill − enemies' rank, in tiers)), × your record on that agent (shrunk). Accuracy shown as typical miss and Brier score."),
            ("Opponent-adjusted ACS (v1)", "ACS × (1 + 0.05 × (enemies' average tier − yours)), limited to ±30%."),
            ("Pulse Score", "Six numbers from one match (ACS 30%, KAST 20%, damage delta 20%, ADR 15%, opening duels 10%, trades 5%), each as a z-score against every player-match Pulse has seen (typical VALORANT spreads until there are 200), then placed as a percentile ×10."),
            ("Skill fingerprint (v1)", "Eleven axes 0–100: percentiles against Pulse's player-matches where there are 20+, fixed ranges otherwise (marked *)."),
            ("Consistency index (v1)", "100 − 0.4 × the spread (standard deviation) of your Pulse Scores; needs 5 matches."),
            ("Sessions (v1)", "Matches less than 45 minutes apart form a session. Averages by position in the session; after 2 losses in a row; after a day's break. Correlation only."),
            ("Teammates (v1)", "Win rate together against the win chance both teams' ranks predicted. Needs 3 matches together."),
            ("Agents and maps (v1)", "Per-agent and per-map numbers from your matches; agent ranking pulled toward your overall until there are games: (n × agent + 5 × overall) / (n + 5). Maps need 3 matches."),
            ("Smurf chance", "Win rate (act or Pulse's matches), K/D, account level, KAST, and RR per win from Riot. One sign alone is capped at 40%."),
            ("Cheating signals", "K/D, headshot rate, clutches, win rate and a new account, each against players at the same rank where Pulse has 15+, and levels almost nobody honest reaches. Few rounds lowers it; one sign alone is capped at 25%. Statistics can't prove cheating."),
            ("Not possible from Riot's data", "Time-to-kill (Riot records the kill, not when the damage started); app-wide leaderboards and community libraries (need a Pulse server collecting data); opponents' peak ranks (a lookup per player)."),
        };
        var sp = new StackPanel();
        foreach (var (name, how) in items)
        {
            sp.Children.Add(Txt(Loc.T(name), 13, TpInk, true).Also(t => t.Margin = new Thickness(12, 10, 12, 2)));
            sp.Children.Add(Txt(Loc.T(how), 12, TpBody).Also(t => { t.Margin = new Thickness(12, 0, 12, 6); t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; }));
        }
        Root.Children.Add(Flat("How every number is worked out", sp));
    }
}
