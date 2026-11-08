using L = Pulse.Ui.Loc;

namespace Pulse.Core.Games.Valorant.Intel;

static class Util
{
    public static bool Won(VMatch m, string id)
    {
        var team = m.TeamOf(id);
        return m.Rounds.Count(r => r.Winner == team) > m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != team);
    }
    public static double Mean(IEnumerable<double> v) => v.Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).DefaultIfEmpty(double.NaN).Average();
    public static double Sd(IEnumerable<double> v)
    {
        var l = v.Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).ToList();
        if (l.Count < 2) return double.NaN;
        double m = l.Average();
        return Math.Sqrt(l.Sum(x => (x - m) * (x - m)) / (l.Count - 1));
    }
    /// <summary>Percentile (0–100, higher is better) from Pulse's "top %" scale; NaN when there's no scale yet.</summary>
    public static double Pct(string id, double v) => double.IsNaN(v) ? double.NaN : 100 - Tracker.TopPercentOf(id, v);
}

/// <summary>
/// Skill fingerprint: eleven axes, each 0–100, for the radar. Version 1. Where Pulse has a scale of other players
/// (20 or more), an axis is the player's percentile among every player-match Pulse has seen; otherwise a fixed
/// range is used and the axis is marked as such. Axes and sources:
///   Mechanics: headshot rate (percentile of "hs");      Aim consistency: 100 − 4 × the match-to-match spread of headshot rate;
///   Duels: opening duels won (percentile of "opening"); Opening impact: first kills − first deaths per 100 rounds (percentile of "open");
///   Trading: trade kills per 100 rounds ("trades");     Clutch: clutch win rate, 0–50% mapped to 0–100;
///   Utility: abilities per round ("abil");              Positioning: deaths with a teammate close ("positioning");
///   Agent mastery: best agent's Pulse Score / 10;       Consistency: the consistency index;
///   Against stronger: opponent-adjusted ACS ÷ plain ACS, 0.85–1.15 mapped to 0–100.
/// </summary>
public static class Fingerprint
{
    public sealed record Axis(string Name, double Value, bool FromScale, int Sample);

    public static List<Axis> Of(IReadOnlyList<VMatch> ms, string id)
    {
        var t = Analysis.For(ms, id);
        var per = ms.Select(m => Analysis.Totals(m, id)).ToList();
        var axes = new List<Axis>();
        void Add(string name, double v, bool scale, int n) => axes.Add(new Axis(name, double.IsNaN(v) ? double.NaN : Math.Clamp(v, 0, 100), scale, n));
        Add("Mechanics", Util.Pct("hs", t.HeadshotPct), true, t.Head + t.Body + t.Legs);
        var hsSd = Util.Sd(per.Where(x => x.Head + x.Body + x.Legs >= 15).Select(x => x.HeadshotPct));
        Add("Aim consistency", double.IsNaN(hsSd) ? double.NaN : 100 - 4 * hsSd, false, per.Count);
        Add("Duels", Util.Pct("opening", t.OpeningWinPct), true, t.FirstKills + t.FirstDeaths);
        Add("Opening impact", Util.Pct("open", 100.0 * (t.FirstKills - t.FirstDeaths) / Math.Max(1, t.Rounds)), true, t.Rounds);
        Add("Trading", Util.Pct("trades", t.TradeKillsPer100), true, t.Rounds);
        Add("Clutch", t.ClutchChances >= 3 ? t.ClutchWinPct * 2 : double.NaN, false, t.ClutchChances);
        Add("Utility", Util.Pct("abil", t.AllCastsPerRound), true, t.CastRounds);
        Add("Positioning", Util.Pct("positioning", 100 - t.AlonePct), true, t.DeathsPlaced);
        var best = AgentIntel.Of(ms, id).Where(a => a.Matches >= 3).Select(a => a.Score).Where(x => !double.IsNaN(x)).DefaultIfEmpty(double.NaN).Max();
        Add("Agent mastery", best / 10, false, ms.Count);
        Add("Consistency", Consistency.Of(ms, id)?.Index ?? double.NaN, false, ms.Count);
        var oa = OpponentAdjusted.Of(ms, id);
        Add("Against stronger", oa.Plain > 0 ? (oa.Adjusted / oa.Plain - 0.85) / 0.3 * 100 : double.NaN, false, oa.Matches);
        return axes;
    }
}

/// <summary>
/// Consistency index, 0–100. Version 1. From each match's Pulse Score: index = 100 − 0.4 × standard deviation
/// (a spread of 250 points, alternating great and awful games, gives 0). Also the share of exceptional games
/// (750+) and poor ones (under 300), and the spread per agent and map. Needs 5 matches.
/// </summary>
public static class Consistency
{
    public sealed record Result(double Index, double Mean, double Sd, double ExceptionalPct, double PoorPct, int Matches,
        List<(string Name, double Sd, int N)> ByAgent, List<(string Name, double Sd, int N)> ByMap);

    public static Result Of(IReadOnlyList<VMatch> ms, string id)
    {
        var scores = ms.Select(m => (M: m, S: Tracker.MatchScore(m, id))).Where(x => !double.IsNaN(x.S)).ToList();
        if (scores.Count < 5) return null;
        double sd = Util.Sd(scores.Select(x => x.S));
        List<(string, double, int)> By(Func<VMatch, string> key) => scores.GroupBy(x => key(x.M)).Where(g => g.Count() >= 3)
            .Select(g => (g.Key, Util.Sd(g.Select(x => x.S)), g.Count())).OrderBy(x => x.Item2).ToList();
        return new Result(Math.Clamp(100 - 0.4 * sd, 0, 100), scores.Average(x => x.S), sd,
            100.0 * scores.Count(x => x.S >= 750) / scores.Count, 100.0 * scores.Count(x => x.S < 300) / scores.Count, scores.Count,
            By(m => m.Players.First(p => p.Id == id).Agent), By(m => m.Map));
    }
}

/// <summary>
/// Sessions and performance through them. Version 1. A session is a run of matches less than 45 minutes apart.
/// For each position in a session (1st, 2nd … 5th and later): average Pulse Score, K/D and win rate, with counts.
/// After two or more losses in a row: the next match against the player's average (a possible tilt pattern —
/// correlation only, never proof). Suggested session length: the last position before the average Pulse Score drops
/// more than 40 points below the first matches' level, with at least 3 matches at each position.
/// </summary>
public static class Sessions
{
    public const double GapMinutes = 45;

    public sealed record Slot(int Position, int Matches, double Score, double Kd, double WinPct);
    public sealed record Result(int Sessions, double AvgLength, List<Slot> Slots, double AfterLossStreak, int AfterLossN, double Overall, int SuggestedLength,
        double AfterBreak, int AfterBreakN);

    public static Result Of(IReadOnlyList<VMatch> ms, string id)
    {
        var list = ms.Where(m => m.Players.Any(p => p.Id == id)).OrderBy(m => m.StartUtc).ToList();
        if (list.Count < 5) return null;
        var sessions = new List<List<VMatch>>();
        foreach (var m in list)
        {
            var prev = sessions.LastOrDefault()?.LastOrDefault();
            if (prev == null || (m.StartUtc - (prev.StartUtc + TimeSpan.FromMilliseconds(prev.LengthMs))).TotalMinutes > GapMinutes) sessions.Add(new());
            sessions[^1].Add(m);
        }
        var byPos = new Dictionary<int, List<VMatch>>();
        foreach (var s in sessions)
            for (int i = 0; i < s.Count; i++)
            {
                int pos = Math.Min(5, i + 1);
                if (!byPos.TryGetValue(pos, out var l)) byPos[pos] = l = new();
                l.Add(s[i]);
            }
        var slots = byPos.OrderBy(kv => kv.Key).Select(kv => new Slot(kv.Key, kv.Value.Count,
            Util.Mean(kv.Value.Select(m => Tracker.MatchScore(m, id))),
            Util.Mean(kv.Value.Select(m => Analysis.Totals(m, id).KdRatio)),
            100.0 * kv.Value.Count(m => Util.Won(m, id)) / kv.Value.Count)).ToList();
        // After two losses in a row (inside a session).
        var after = new List<double>();
        foreach (var s in sessions)
            for (int i = 2; i < s.Count; i++)
                if (!Util.Won(s[i - 1], id) && !Util.Won(s[i - 2], id)) after.Add(Tracker.MatchScore(s[i], id));
        // First match of a session after a break of a day or more.
        var breakFirst = new List<double>();
        for (int i = 1; i < sessions.Count; i++)
            if ((sessions[i][0].StartUtc - sessions[i - 1][^1].StartUtc).TotalHours >= 20) breakFirst.Add(Tracker.MatchScore(sessions[i][0], id));
        double overall = Util.Mean(list.Select(m => Tracker.MatchScore(m, id)));
        int suggest = 0;
        var first = slots.FirstOrDefault();
        if (first != null && first.Matches >= 3 && !double.IsNaN(first.Score))
        {
            suggest = slots.Count(x => x.Matches >= 3);
            foreach (var x in slots.Where(x => x.Matches >= 3)) if (x.Score < first.Score - 40) { suggest = Math.Max(1, x.Position - 1); break; }
        }
        return new Result(sessions.Count, sessions.Average(s => s.Count), slots, Util.Mean(after), after.Count, overall, suggest, Util.Mean(breakFirst), breakFirst.Count);
    }
}

/// <summary>
/// Teammates: everyone who played on the player's team at least twice. Version 1. Win rate together against the
/// win rate the MMR formula expected from the two teams' ranks in those matches (so a teammate isn't credited for
/// easy lobbies), the player's Pulse Score with them against without, and whether they queued together (party).
/// Needs 3 matches together before a teammate is called better or worse than expected.
/// </summary>
public static class Synergy
{
    public sealed record Mate(string Id, string Name, int Matches, int Wins, double ExpectedWinPct, double ScoreWith, double ScoreWithout, bool Party,
        List<string> Agents, List<string> Maps)
    {
        public double WinPct => Matches > 0 ? 100.0 * Wins / Matches : double.NaN;
        public double VsExpected => WinPct - ExpectedWinPct;
    }

    public static List<Mate> Of(IReadOnlyList<VMatch> ms, string id)
    {
        var all = ms.Where(m => VMatch.Counts(m.Queue) && m.Players.Any(p => p.Id == id)).ToList();
        var scores = all.ToDictionary(m => m.Id, m => Tracker.MatchScore(m, id));
        var mates = new Dictionary<string, List<VMatch>>();
        foreach (var m in all)
        {
            var me = m.Players.First(p => p.Id == id);
            foreach (var p in m.Players.Where(p => p.Team == me.Team && p.Id != id))
            {
                if (!mates.TryGetValue(p.Id, out var l)) mates[p.Id] = l = new();
                l.Add(m);
            }
        }
        var list = new List<Mate>();
        foreach (var (mid, together) in mates.Where(kv => kv.Value.Count >= 2))
        {
            var exp = together.Select(m =>
            {
                var me = m.Players.First(p => p.Id == id);
                var a = m.Players.Where(p => p.Team == me.Team && (p.Tier >= 3 || p.Id == id)).Select(p => (double)Math.Max(p.Tier, 3)).DefaultIfEmpty(double.NaN).Average();
                var b = m.Players.Where(p => p.Team != me.Team && p.Tier >= 3).Select(p => (double)p.Tier).DefaultIfEmpty(double.NaN).Average();
                return double.IsNaN(a) || double.IsNaN(b) ? 0.5 : MmrModel.Expect(MmrModel.TierRating(a), MmrModel.TierRating(b));
            }).Average();
            var ids = together.Select(m => m.Id).ToHashSet();
            bool party = together.Any(m => m.Players.First(p => p.Id == id).Party is { Length: > 0 } pa && m.Players.First(p => p.Id == mid).Party == pa);
            var last = together.OrderByDescending(m => m.StartUtc).First();
            list.Add(new Mate(mid, Stats.NameOf(last, mid) ?? NameBook.Get(mid) ?? last.Players.First(p => p.Id == mid).Agent, together.Count, together.Count(m => Util.Won(m, id)),
                100 * exp, Util.Mean(together.Select(m => scores[m.Id])), Util.Mean(all.Where(m => !ids.Contains(m.Id)).Select(m => scores[m.Id])), party,
                together.Select(m => m.Players.First(p => p.Id == mid).Agent).Distinct().ToList(), together.Select(m => m.Map).Distinct().ToList()));
        }
        return list.OrderByDescending(x => x.Matches).ThenByDescending(x => x.VsExpected).ToList();
    }
}

/// <summary>
/// Agent intelligence. Version 1. Per agent: matches, win rate, K/D, ACS, ADR, KAST, headshot rate, first-blood
/// difference, rounds, attack and defense combat score per round, best map, Pulse Score (average), its spread
/// (consistency) and best single match (ceiling). Ranking uses a shrunk score so 2 great games don't beat 30 good
/// ones: (n × average + 5 × the player's overall) / (n + 5).
/// </summary>
public static class AgentIntel
{
    public sealed record Agent(string Name, string Role, int Matches, double WinPct, double Kd, double Acs, double Adr, double Kast, double Hs, double FbDiff,
        double AvgRounds, double AtkAcs, double DefAcs, string BestMap, double Score, double Shrunk, double Sd, double Ceiling, double RecentScore5);

    public static List<Agent> Of(IReadOnlyList<VMatch> ms, string id)
    {
        var all = ms.Where(m => VMatch.Counts(m.Queue) && m.Players.Any(p => p.Id == id)).OrderByDescending(m => m.StartUtc).ToList();
        if (all.Count == 0) return new();
        double overall = Util.Mean(all.Select(m => Tracker.MatchScore(m, id)));
        var list = new List<Agent>();
        foreach (var g in all.GroupBy(m => m.Players.First(p => p.Id == id).Agent).Where(g => g.Key.Length > 0))
        {
            var t = Analysis.For(g.ToList(), id);
            var sc = g.Select(m => Tracker.MatchScore(m, id)).ToList();
            double avg = Util.Mean(sc);
            int n = g.Count();
            double atk = 0, def = 0; int atkR = 0, defR = 0;
            foreach (var m in g)
            {
                var me = m.Players.First(p => p.Id == id);
                foreach (var r in m.Rounds)
                {
                    double pts = 150.0 * m.Kills.Count(k => k.Round == r.N && k.Killer == id && m.TeamOf(k.Victim) != me.Team)
                        + r.Damage.Where(d => d.From == id && m.TeamOf(d.To) != me.Team).Sum(d => d.Amount);
                    if (m.Attacking(me.Team, r.N)) { atk += pts; atkR++; } else { def += pts; defR++; }
                }
            }
            var maps = Panels.Maps(g, id).Where(x => x.Games >= 2).OrderByDescending(x => x.WinPct).FirstOrDefault();
            list.Add(new Agent(g.Key, Analysis.RoleOf(g.Key), n, t.WinPct, t.KdRatio, t.Acs, t.Adr, t.KastPct, t.HeadshotPct,
                100.0 * (t.FirstKills - t.FirstDeaths) / Math.Max(1, t.Rounds), (double)t.Rounds / n,
                atkR > 0 ? atk / atkR : double.NaN, defR > 0 ? def / defR : double.NaN, maps?.Map ?? "",
                avg, double.IsNaN(avg) ? double.NaN : (n * avg + 5 * (double.IsNaN(overall) ? 500 : overall)) / (n + 5), Util.Sd(sc),
                sc.Where(x => !double.IsNaN(x)).DefaultIfEmpty(double.NaN).Max(), Util.Mean(sc.Take(5))));
        }
        return list.OrderByDescending(a => a.Shrunk).ToList();
    }

    /// <summary>The best agent (shrunk score) among the last <paramref name="n"/> matches.</summary>
    public static Agent BestRecent(IReadOnlyList<VMatch> newestFirst, string id, int n) =>
        Of(newestFirst.Where(m => VMatch.Counts(m.Queue)).Take(n).ToList(), id).FirstOrDefault();
}

/// <summary>
/// Map intelligence. Version 1. Per map: matches, win rate, attack and defense round win rates, ACS, ADR, K/D and
/// the player's best agent there. Strongest and weakest maps need 3 or more matches; fewer is "not enough data".
/// </summary>
public static class MapIntel
{
    public sealed record Map(string Name, int Matches, double WinPct, double AtkRoundPct, double DefRoundPct, double Acs, double Adr, double Kd, string BestAgent, double BestAgentScore)
    {
        public bool Enough => Matches >= 3;
    }

    public static List<Map> Of(IReadOnlyList<VMatch> ms, string id)
    {
        var all = ms.Where(m => VMatch.Counts(m.Queue) && m.Players.Any(p => p.Id == id) && m.Map.Length > 0).ToList();
        var list = new List<Map>();
        foreach (var g in all.GroupBy(m => m.Map))
        {
            var t = Analysis.For(g.ToList(), id);
            int aw = 0, at = 0, dw = 0, dt = 0;
            foreach (var m in g)
            {
                var team = m.TeamOf(id);
                foreach (var r in m.Rounds.Where(r => r.Winner.Length > 0))
                    if (m.Attacking(team, r.N)) { at++; if (r.Winner == team) aw++; } else { dt++; if (r.Winner == team) dw++; }
            }
            var best = g.GroupBy(m => m.Players.First(p => p.Id == id).Agent).Where(a => a.Count() >= 2)
                .Select(a => (a.Key, S: Util.Mean(a.Select(m => Tracker.MatchScore(m, id))))).OrderByDescending(a => a.S).FirstOrDefault();
            list.Add(new Map(g.Key, g.Count(), t.WinPct, at > 0 ? 100.0 * aw / at : double.NaN, dt > 0 ? 100.0 * dw / dt : double.NaN, t.Acs, t.Adr, t.KdRatio,
                best.Key ?? "", best.Key == null ? double.NaN : best.S));
        }
        return list.OrderByDescending(x => x.Matches).ToList();
    }
}

/// <summary>
/// Career: the best of everything in the saved matches and Riot's act history. Version 1. Streaks count wins or
/// losses in a row (draws break them). Improvement rate: the slope of Pulse Score across matches, per 10 matches
/// (least squares). Milestones are firsts found in the matches.
/// </summary>
public static class Career
{
    public sealed record Result(int LongestWin, int LongestLoss, int CurrentStreak, double ImprovementPer10, string BestAgent, string BestMap,
        double PeakMmr, List<(DateTime When, string What)> Milestones, VMatch BestMatch, double BestMatchScore);

    public static Result Of(IReadOnlyList<VMatch> ms, string id, MmrModel.Estimate mmr)
    {
        var list = ms.Where(m => VMatch.Counts(m.Queue) && m.Players.Any(p => p.Id == id)).OrderBy(m => m.StartUtc).ToList();
        if (list.Count == 0) return null;
        int bw = 0, bl = 0, cw = 0, cl = 0;
        foreach (var m in list)
        {
            var team = m.TeamOf(id);
            int us = m.Rounds.Count(r => r.Winner == team), them = m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != team);
            if (us > them) { cw++; cl = 0; } else if (them > us) { cl++; cw = 0; } else { cw = 0; cl = 0; }
            bw = Math.Max(bw, cw); bl = Math.Max(bl, cl);
        }
        var sc = list.Select(m => Tracker.MatchScore(m, id)).ToList();
        var pts = sc.Select((s, i) => (X: (double)i, Y: s)).Where(p => !double.IsNaN(p.Y)).ToList();
        double slope = double.NaN;
        if (pts.Count >= 5)
        {
            double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
            double den = pts.Sum(p => (p.X - mx) * (p.X - mx));
            slope = den > 0 ? pts.Sum(p => (p.X - mx) * (p.Y - my)) / den * 10 : double.NaN;
        }
        var miles = new List<(DateTime, string)>();
        void First(Func<VMatch, bool> test, string what) { var m = list.FirstOrDefault(test); if (m != null) miles.Add((m.StartUtc, what)); }
        First(m => Stats.Of(new[] { m }, id).Aces > 0, L.T("First ace"));
        First(m => Stats.PlaceOf(m, id).MatchMvp, L.T("First match MVP"));
        First(m => m.Players.First(p => p.Id == id).Kills >= 30, L.T("First 30-kill match"));
        First(m => Analysis.Totals(m, id).Acs >= 300, L.T("First 300+ ACS match"));
        First(m => Stats.Of(new[] { m }, id).ClutchWins > 0, L.T("First clutch won"));
        var bestIdx = sc.Select((s, i) => (s, i)).Where(x => !double.IsNaN(x.s)).OrderByDescending(x => x.s).FirstOrDefault();
        var agents = AgentIntel.Of(list, id);
        var maps = MapIntel.Of(list, id).Where(x => x.Enough).OrderByDescending(x => x.WinPct).FirstOrDefault();
        return new Result(bw, bl, cw > 0 ? cw : -cl, slope, agents.FirstOrDefault()?.Name ?? "", maps?.Name ?? "",
            mmr?.Peak ?? double.NaN, miles.OrderBy(x => x.Item1).ToList(), sc.Count > 0 && !double.IsNaN(bestIdx.s) ? list[bestIdx.i] : null, bestIdx.s);
    }
}

/// <summary>
/// Improvement: the last 10 matches against the 10 before, wins against losses, and when the player plays best.
/// Version 1. A change counts as "improved" or "declined" only when it's larger than half the match-to-match spread
/// of that stat (smaller changes are noise). Needs 12 matches.
/// </summary>
public static class Improvement
{
    public sealed record Change(string Stat, double Before, double Now, bool HigherIsBetter, double Noise)
    {
        public double Delta => Now - Before;
        public bool Real => !double.IsNaN(Noise) && Math.Abs(Delta) > Noise / 2;
        public bool Better => Real && (HigherIsBetter ? Delta > 0 : Delta < 0);
        public bool Worse => Real && (HigherIsBetter ? Delta < 0 : Delta > 0);
    }

    public sealed record Result(List<Change> Changes, List<(string Stat, double Wins, double Losses)> WinVsLoss, List<(string Hours, double Score, int N)> ByTime);

    static readonly (string Name, Func<PlayerTotals, double> F, bool Up)[] Metrics =
    {
        ("K/D", t => t.KdRatio, true), ("ACS", t => t.Acs, true), ("ADR", t => t.Adr, true), ("KAST", t => t.KastPct, true),
        ("Headshot %", t => t.HeadshotPct, true), ("First bloods − first deaths per 100 rounds", t => t.Rounds > 0 ? 100.0 * (t.FirstKills - t.FirstDeaths) / t.Rounds : double.NaN, true),
        ("Untraded deaths %", t => t.UntradedPct, false),
    };

    public static Result Of(IReadOnlyList<VMatch> ms, string id)
    {
        var list = ms.Where(m => VMatch.Counts(m.Queue) && m.Players.Any(p => p.Id == id)).OrderByDescending(m => m.StartUtc).ToList();
        if (list.Count < 12) return null;
        var now = list.Take(10).ToList();
        var before = list.Skip(10).Take(10).ToList();
        var per = list.Select(m => Analysis.Totals(m, id)).ToList();
        var changes = Metrics.Select(x => new Change(x.Name, x.F(Analysis.For(before, id)), x.F(Analysis.For(now, id)), x.Up, Util.Sd(per.Select(x.F)))).ToList();
        var wins = list.Where(m => Util.Won(m, id)).ToList();
        var losses = list.Where(m => !Util.Won(m, id)).ToList();
        var wl = Metrics.Select(x => (x.Name, wins.Count > 0 ? x.F(Analysis.For(wins, id)) : double.NaN, losses.Count > 0 ? x.F(Analysis.For(losses, id)) : double.NaN)).ToList();
        string Bucket(DateTime utc) => L.T(utc.ToLocalTime().Hour switch { < 6 => "Night (0–6)", < 12 => "Morning (6–12)", < 18 => "Afternoon (12–18)", _ => "Evening (18–24)" });
        var byTime = list.GroupBy(m => Bucket(m.StartUtc)).Select(g => (g.Key, Util.Mean(g.Select(m => Tracker.MatchScore(m, id))), g.Count()))
            .OrderByDescending(x => x.Item2).ToList();
        return new Result(changes, wl, byTime);
    }
}

/// <summary>
/// Data checks on every saved match. Version 1. Flags (never silently fixes): scoreboard kills that differ from the
/// kill feed, rounds that don't add up to the two teams' scores, players other than 10, kills with no damage
/// recorded, a missing map or rank data, and matches saved by an older version of Pulse.
/// </summary>
public static class DataQuality
{
    public sealed record Issue(VMatch Match, string Problem);

    public static (int Checked, List<Issue> Issues) Check(IReadOnlyList<VMatch> ms)
    {
        var issues = new List<Issue>();
        foreach (var m in ms)
        {
            int feedKills = m.Kills.Count(k => k.Killer.Length > 0 && k.Killer != k.Victim && m.TeamOf(k.Killer) != m.TeamOf(k.Victim));
            int boardKills = m.Players.Sum(p => p.Kills);
            if (Math.Abs(feedKills - boardKills) > 2) issues.Add(new Issue(m, L.F("Scoreboard kills ({0}) and kill feed ({1}) differ", boardKills, feedKills)));
            int decided = m.Rounds.Count(r => r.Winner.Length > 0);
            if (decided != m.Rounds.Count) issues.Add(new Issue(m, L.F("{0} rounds have no winner", m.Rounds.Count - decided)));
            if (m.Players.Count != 10 && VMatch.Counts(m.Queue)) issues.Add(new Issue(m, L.F("{0} players instead of 10", m.Players.Count)));
            if (m.Kills.Count > 0 && m.Rounds.All(r => r.Damage.Count == 0)) issues.Add(new Issue(m, L.T("No damage recorded")));
            if (m.Map.Length == 0) issues.Add(new Issue(m, L.T("Map unknown")));
            if (m.Version < VMatch.CurrentVersion) issues.Add(new Issue(m, L.T("Saved by an older Pulse (no Riot IDs yet); downloaded again next time")));
        }
        return (ms.Count, issues);
    }
}

/// <summary>
/// Match autopsy: facts measured in one match, then interpretations clearly marked as such. Version 1.
/// Stretches: the player's damage per round in blocks of 5 rounds; the best and worst blocks are reported.
/// Expectation: from <see cref="Expected"/> when available (made from earlier matches only).
/// </summary>
public static class Autopsy
{
    public sealed record Report(List<string> Facts, List<string> Interpretations);

    public static Report Of(VMatch m, string id, Expected.Line expected)
    {
        var facts = new List<string>();
        var notes = new List<string>();
        var me = m.Players.FirstOrDefault(p => p.Id == id);
        if (me == null || m.Rounds.Count == 0) return new Report(facts, notes);
        var t = Analysis.Totals(m, id);
        var o = Stats.Of(new[] { m }, id);
        double lobby = m.Players.Where(p => p.Id != id).Select(p => (double)p.Score).DefaultIfEmpty(0).Average() / m.Rounds.Count;
        var place = Stats.PlaceOf(m, id);
        int us = m.Rounds.Count(r => r.Winner == me.Team), them = m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != me.Team);
        facts.Add(L.F(us > them ? "Won {0}–{1} on {2} as {3}; #{4} of 10 by combat score." : us < them ? "Lost {0}–{1} on {2} as {3}; #{4} of 10 by combat score." : "Drew {0}–{1} on {2} as {3}; #{4} of 10 by combat score.", us, them, m.Map, me.Agent, place.Place));
        facts.Add(L.F("{0}/{1}/{2}, {3:0} ACS (lobby average {4:0}), {5:0} ADR, {6:0}% KAST, {7:0}% headshots.", t.Kills, t.Deaths, t.Assists, t.Acs, lobby, t.Adr, t.KastPct, double.IsNaN(t.HeadshotPct) ? 0 : t.HeadshotPct));
        facts.Add(L.F("Opening duels: {0} won, {1} lost. Clutches: {2} of {3}. Multi-kill rounds: {4}. Aces: {5}.", t.FirstKills, t.FirstDeaths, t.ClutchWins, t.ClutchChances, o.MultiKills, o.Aces));
        // Damage per round in blocks of 5.
        var blocks = new List<(int From, int To, double Adr)>();
        var rounds = m.Rounds.OrderBy(r => r.N).ToList();
        for (int i = 0; i < rounds.Count; i += 5)
        {
            var part = rounds.Skip(i).Take(5).ToList();
            if (part.Count < 3) break;
            double dmg = part.Sum(r => r.Damage.Where(d => d.From == id && m.TeamOf(d.To) != me.Team).Sum(d => d.Amount));
            blocks.Add((part[0].N + 1, part[^1].N + 1, dmg / part.Count));
        }
        if (blocks.Count >= 2)
        {
            var best = blocks.OrderByDescending(b => b.Adr).First();
            var worst = blocks.OrderBy(b => b.Adr).First();
            facts.Add(L.F("Damage per round was highest in rounds {0}–{1} ({2:0}) and lowest in rounds {3}–{4} ({5:0}).", best.From, best.To, best.Adr, worst.From, worst.To, worst.Adr));
            if (best.Adr > 0 && worst.Adr < best.Adr * 0.5) notes.Add(L.F("Impact dropped sharply in rounds {0}–{1}: worth reviewing what changed then (side, economy, or setups).", worst.From, worst.To));
        }
        // Attack and defense.
        double atk = 0, def = 0; int ar = 0, dr = 0;
        foreach (var r in rounds)
        {
            double dmg = r.Damage.Where(d => d.From == id && m.TeamOf(d.To) != me.Team).Sum(d => d.Amount);
            if (m.Attacking(me.Team, r.N)) { atk += dmg; ar++; } else { def += dmg; dr++; }
        }
        if (ar > 0 && dr > 0)
        {
            facts.Add(L.F("Damage per round on attack {0:0}, on defense {1:0}.", atk / ar, def / dr));
            if (atk / ar > def / dr * 1.4) notes.Add(L.T("Much stronger on attack than defense this match."));
            else if (def / dr > atk / ar * 1.4) notes.Add(L.T("Much stronger on defense than attack this match."));
        }
        if (expected != null)
        {
            facts.Add(L.F("Expected from earlier matches: {0:0} ACS, {1:0} ADR, {2:0.00} K/D; win chance {3:0}%.", expected.ExpAcs, expected.ExpAdr, expected.ExpKd, expected.WinChance * 100));
            double d = t.Acs - expected.ExpAcs;
            notes.Add(Math.Abs(d) < 15 ? L.T("Played about as expected for this lobby.") : d > 0 ? L.F("Played above the expected level for this lobby (+{0:0} ACS).", d) : L.F("Played below the expected level for this lobby ({0:0} ACS).", d));
        }
        if (t.FirstDeaths >= 4 && t.FirstDeaths > t.FirstKills * 2) notes.Add(L.T("Died first often without winning openings back: consider safer early-round positions or waiting for utility."));
        if (t.ClutchChances >= 2 && t.ClutchWins == 0) notes.Add(L.T("Clutch chances came up but none were converted."));
        if (!double.IsNaN(t.UntradedPct) && t.UntradedPct >= 70 && t.DeathsWithMates >= 5) notes.Add(L.T("Most deaths weren't traded: playing closer to teammates would help."));
        return new Report(facts, notes);
    }

}

/// <summary>
/// The player network: everyone met in two or more saved matches, as teammate, opponent or both, with how often.
/// Version 1. Only players from the user's own matches; nothing is looked up.
/// </summary>
public static class Network
{
    public sealed record Node(string Id, string Name, string Agent, int Tier, int With, int Against)
    {
        public int Total => With + Against;
    }

    public static List<Node> Of(IReadOnlyList<VMatch> ms, string id, int min = 2, int take = 24)
    {
        var map = new Dictionary<string, (int W, int A, VMatch Last)>();
        foreach (var m in ms.Where(m => m.Players.Any(p => p.Id == id)))
        {
            var team = m.TeamOf(id);
            foreach (var p in m.Players.Where(p => p.Id != id))
            {
                var e = map.GetValueOrDefault(p.Id);
                if (p.Team == team) e.W++; else e.A++;
                if (e.Last == null || m.StartUtc > e.Last.StartUtc) e.Last = m;
                map[p.Id] = e;
            }
        }
        return map.Where(kv => kv.Value.W + kv.Value.A >= min).OrderByDescending(kv => kv.Value.W + kv.Value.A).Take(take).Select(kv =>
        {
            var p = kv.Value.Last.Players.First(x => x.Id == kv.Key);
            return new Node(kv.Key, Stats.NameOf(kv.Value.Last, kv.Key) ?? NameBook.Get(kv.Key) ?? p.Agent, p.Agent, p.Tier, kv.Value.W, kv.Value.A);
        }).ToList();
    }
}
