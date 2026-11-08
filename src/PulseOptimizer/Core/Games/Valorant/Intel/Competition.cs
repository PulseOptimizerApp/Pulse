namespace Pulse.Core.Games.Valorant.Intel;

/// <summary>
/// Matchmaking quality over the last 5, 10, 25, 50 and 100 matches. Version 1.
/// Ranks are the ones each player had in that match (Riot's match record); unranked players are left out of
/// averages. Expected win chance = Pulse's MMR formula with the two teams' average ranks (the player counted at
/// their own rank). "Higher-ranked lobby": the enemies' average is at least one tier above the player.
/// Peak ranks of opponents would need a Riot lookup per player, so they aren't part of this.
/// </summary>
public static class Matchmaking
{
    public sealed record Window(int Last, int Matches, double LobbyAvg, double EnemyAvg, double TeamAvg, int HighestEnemy, int LowestEnemy,
        double TeamGap, double HigherPct, double ExpectedWin, double ActualWin);

    public static Window Of(IReadOnlyList<VMatch> newestFirst, string id, int last)
    {
        var ms = newestFirst.Where(m => VMatch.Counts(m.Queue) && m.Players.Any(p => p.Id == id)).Take(last).ToList();
        var lobby = new List<double>(); var enemy = new List<double>(); var team = new List<double>(); var exp = new List<double>();
        int hi = 0, lo = 99, higher = 0, used = 0, wins = 0;
        foreach (var m in ms)
        {
            var me = m.Players.First(p => p.Id == id);
            var en = m.Players.Where(p => p.Team != me.Team && p.Tier >= 3).ToList();
            var mt = m.Players.Where(p => p.Team == me.Team && p.Tier >= 3).ToList();
            if (en.Count < 3) continue;
            used++;
            double ea = en.Average(p => p.Tier), ta = mt.Count > 0 ? mt.Average(p => p.Tier) : double.NaN;
            enemy.Add(ea);
            if (!double.IsNaN(ta)) team.Add(ta);
            lobby.Add(m.Players.Where(p => p.Id != id && p.Tier >= 3).Average(p => p.Tier));
            hi = Math.Max(hi, en.Max(p => p.Tier)); lo = Math.Min(lo, en.Min(p => p.Tier));
            if (me.Tier >= 3 && ea >= me.Tier + 1) higher++;
            if (!double.IsNaN(ta)) exp.Add(MmrModel.Expect(MmrModel.TierRating(ta), MmrModel.TierRating(ea)));
            int us = m.Rounds.Count(r => r.Winner == me.Team), them = m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != me.Team);
            if (us > them) wins++;
        }
        double Avg(List<double> l) => l.Count > 0 ? l.Average() : double.NaN;
        return new Window(last, used, Avg(lobby), Avg(enemy), Avg(team), used > 0 ? hi : 0, used > 0 ? lo : 0,
            team.Count > 0 && enemy.Count > 0 ? Avg(team) - Avg(enemy) : double.NaN,
            used > 0 ? 100.0 * higher / used : double.NaN, exp.Count > 0 ? 100 * exp.Average() : double.NaN, used > 0 ? 100.0 * wins / used : double.NaN);
    }

    public static List<Window> All(IReadOnlyList<VMatch> newestFirst, string id) => new[] { 5, 10, 25, 50, 100 }.Select(n => Of(newestFirst, id, n)).ToList();
}

/// <summary>
/// Head-to-head records with every opponent met in saved matches. Version 1.
/// Kills on them and deaths to them come from the kill feed (exact). A record needs 3 or more matches against
/// someone to be called reliable; one or two meetings are shown but never ranked as "you beat them".
/// Difficulty = their combat score against the lobby average in those matches × (1 + 0.05 × tiers above you).
/// </summary>
public static class HeadToHead
{
    public const int Reliable = 3;

    public sealed record Line(string Id, string Name, string Agent, int LastTier, int Encounters, int Wins, int Rounds, int KillsOnThem, int DeathsToThem,
        double YourAcs, double TheirAcsRatio, double Difficulty, List<string> Maps, DateTime Last)
    {
        public bool IsReliable => Encounters >= Reliable;
        public int Net => KillsOnThem - DeathsToThem;
        public double WinPct => Encounters > 0 ? 100.0 * Wins / Encounters : double.NaN;
    }

    public static List<Line> Of(IReadOnlyList<VMatch> matches, string me)
    {
        var map = new Dictionary<string, List<VMatch>>();
        foreach (var m in matches.Where(m => VMatch.Counts(m.Queue)))
        {
            var team = m.TeamOf(me);
            if (team.Length == 0) continue;
            foreach (var p in m.Players.Where(p => p.Team != team))
            {
                if (!map.TryGetValue(p.Id, out var l)) map[p.Id] = l = new();
                l.Add(m);
            }
        }
        var list = new List<Line>();
        foreach (var (id, ms) in map)
        {
            int wins = 0, rounds = 0, k = 0, d = 0;
            double acs = 0, theirs = 0;
            foreach (var m in ms)
            {
                var team = m.TeamOf(me);
                if (m.Rounds.Count(r => r.Winner == team) > m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != team)) wins++;
                rounds += m.Rounds.Count;
                k += m.Kills.Count(x => x.Killer == me && x.Victim == id);
                d += m.Kills.Count(x => x.Killer == id && x.Victim == me);
                var mine = m.Players.First(p => p.Id == me);
                var them = m.Players.First(p => p.Id == id);
                double lobby = m.Players.Average(p => (double)p.Score);
                acs += m.Rounds.Count > 0 ? (double)mine.Score / m.Rounds.Count : 0;
                theirs += lobby > 0 ? them.Score / lobby : 1;
            }
            var last = ms.OrderByDescending(m => m.StartUtc).First();
            var lp = last.Players.First(p => p.Id == id);
            int myTier = last.Players.First(p => p.Id == me).Tier;
            double ratio = theirs / ms.Count;
            double diff = ratio * (1 + 0.05 * (lp.Tier >= 3 && myTier >= 3 ? lp.Tier - myTier : 0));
            list.Add(new Line(id, Stats.NameOf(last, id) ?? NameBook.Get(id) ?? "", lp.Agent, lp.Tier, ms.Count, wins, rounds, k, d,
                acs / ms.Count, ratio, diff, ms.Select(m => m.Map).Distinct().ToList(), last.StartUtc));
        }
        return list.OrderByDescending(l => l.Encounters).ThenByDescending(l => l.Last).ToList();
    }
}

/// <summary>
/// Expected versus actual performance. Version 1. For each match, an expectation is made from earlier matches only
/// (never from the match itself or later ones):
///   baseline = average of the player's previous (up to 20) matches, pulled toward the average of the lobbies in
///   those matches (their ACS, ADR and KAST; 1.0 for K/D) when there are few: (n × own + 3 × lobbies) / (n + 3);
///   lobby difficulty: × (1 + 0.05 × (player's rating − enemies' average, in tiers)), from Pulse's MMR model;
///   agent: × the player's previous ratio on that agent to their overall, shrunk: (1 + (r − 1) × n / (n + 5)).
/// Metrics: ACS, ADR, K/D, KAST. Win chance comes from the MMR model. Error is tracked (mean absolute error)
/// so the expectation's own accuracy is visible. Needs 3 earlier matches.
/// </summary>
public static class Expected
{
    public const int Version = 1;

    public sealed record Line(VMatch Match, double ExpAcs, double Acs, double ExpAdr, double Adr, double ExpKd, double Kd, double ExpKast, double Kast, double WinChance, bool Won)
    {
        public double AcsDiff => Acs - ExpAcs;
    }

    public sealed record Summary(List<Line> Lines, double AvgAcsDiff, double AvgAdrDiff, double AvgKdDiff, double AvgKastDiff, double AcsMae, double AboveShare);

    public static Summary Of(IReadOnlyList<VMatch> matches, string id, MmrModel.Estimate mmr)
    {
        var ms = matches.Where(m => VMatch.Counts(m.Queue) && m.Players.Any(p => p.Id == id) && m.Rounds.Count > 0).OrderBy(m => m.StartUtc).ToList();
        var before = MmrModel.RatingBefore(mmr);
        var past = new List<(VMatch M, PlayerTotals T, double LobbyAcs, double LobbyAdr, double LobbyKast)>();
        var lines = new List<Line>();
        foreach (var m in ms)
        {
            var me = m.Players.First(p => p.Id == id);
            var t = Analysis.Totals(m, id);
            if (past.Count >= 3)
            {
                // Everything here comes from earlier matches, plus what's known before this one starts: the ranks.
                var recent = past.TakeLast(20).ToList();
                int n = recent.Count;
                double Mean(IEnumerable<double> v, double fallback) => v.Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).DefaultIfEmpty(fallback).Average();
                double Base(Func<PlayerTotals, double> f, double prior) => (n * Mean(recent.Select(x => f(x.T)), prior) + 3 * prior) / (n + 3);
                var enemies = m.Players.Where(p => p.Team != me.Team && p.Tier >= 3).ToList();
                double rating = before.TryGetValue(m.Id, out var r) ? r : double.NaN;
                double lobbyAdj = !double.IsNaN(rating) && enemies.Count >= 3 ? 1 + 0.05 * (rating / 100.0 - (enemies.Average(p => p.Tier) + 0.5)) : 1;
                lobbyAdj = Math.Clamp(lobbyAdj, 0.7, 1.3);
                var onAgent = recent.Where(x => x.M.Players.First(p => p.Id == id).Agent == me.Agent).ToList();
                double overall = Mean(recent.Select(x => x.T.Acs / Math.Max(1, x.LobbyAcs)), 1);
                double agentRatio = onAgent.Count > 0 && overall > 0 ? Mean(onAgent.Select(x => x.T.Acs / Math.Max(1, x.LobbyAcs)), overall) / overall : 1;
                double adj = lobbyAdj * (1 + (agentRatio - 1) * onAgent.Count / (onAgent.Count + 5.0));
                double win = !double.IsNaN(rating) && enemies.Count >= 3 ? MmrModel.Expect(rating, MmrModel.TierRating(enemies.Average(p => p.Tier))) : 0.5;
                int us = m.Rounds.Count(x => x.Winner == me.Team), them = m.Rounds.Count(x => x.Winner.Length > 0 && x.Winner != me.Team);
                lines.Add(new Line(m,
                    Base(x => x.Acs, Mean(recent.Select(x => x.LobbyAcs), 200)) * adj, t.Acs,
                    Base(x => x.Adr, Mean(recent.Select(x => x.LobbyAdr), 135)) * adj, t.Adr,
                    Base(x => x.KdRatio, 1) * adj, t.KdRatio,
                    Math.Min(100, Base(x => x.KastPct, Mean(recent.Select(x => x.LobbyKast), 70)) * Math.Sqrt(adj)), t.KastPct, win, us > them));
            }
            // This match's lobby (without the player), for later expectations.
            var others = m.Players.Where(p => p.Id != id).ToList();
            var lobbyT = others.Select(p => Analysis.Totals(m, p.Id)).ToList();
            double LobbyMean(IEnumerable<double> v) => v.Where(x => !double.IsNaN(x)).DefaultIfEmpty(double.NaN).Average();
            past.Add((m, t, others.Count > 0 ? others.Average(p => (double)p.Score) / m.Rounds.Count : t.Acs, LobbyMean(lobbyT.Select(x => x.Adr)), LobbyMean(lobbyT.Select(x => x.KastPct))));
        }
        if (lines.Count == 0) return new Summary(lines, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        double Avg(Func<Line, double> f) => lines.Select(f).Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).DefaultIfEmpty(double.NaN).Average();
        return new Summary(lines, Avg(l => l.Acs - l.ExpAcs), Avg(l => l.Adr - l.ExpAdr), Avg(l => l.Kd - l.ExpKd), Avg(l => l.Kast - l.ExpKast),
            Avg(l => Math.Abs(l.Acs - l.ExpAcs)), 100.0 * lines.Count(l => l.Acs > l.ExpAcs) / lines.Count);
    }
}

/// <summary>
/// Opponent-adjusted combat score: ACS × (1 + 0.05 × (enemies' average tier − the player's tier)), so 250 ACS against
/// Diamonds counts for more than 250 against Silvers. Version 1. Matches without 3 ranked enemies use plain ACS.
/// </summary>
public static class OpponentAdjusted
{
    public sealed record Result(double Plain, double Adjusted, double VsHigher, int HigherMatches, int Matches);

    public static Result Of(IReadOnlyList<VMatch> matches, string id)
    {
        var plain = new List<double>(); var adj = new List<double>(); var higher = new List<double>();
        foreach (var m in matches.Where(m => VMatch.Counts(m.Queue) && m.Rounds.Count > 0))
        {
            var me = m.Players.FirstOrDefault(p => p.Id == id);
            if (me == null) continue;
            double acs = (double)me.Score / m.Rounds.Count;
            var en = m.Players.Where(p => p.Team != me.Team && p.Tier >= 3).ToList();
            double gap = en.Count >= 3 && me.Tier >= 3 ? en.Average(p => p.Tier) - me.Tier : 0;
            plain.Add(acs);
            adj.Add(acs * Math.Clamp(1 + 0.05 * gap, 0.7, 1.3));
            if (gap >= 1) higher.Add(acs);
        }
        double A(List<double> l) => l.Count > 0 ? l.Average() : double.NaN;
        return new Result(A(plain), A(adj), A(higher), higher.Count, plain.Count);
    }
}
