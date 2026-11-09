namespace Pulse.Core.Games.Valorant.Intel;

/// <summary>
/// Pulse's own estimate of a player's matchmaking skill. NOT Riot's hidden MMR (which is private): an independent
/// Elo/Glicko-style rating built only from Pulse's saved matches. Model version 1.
///
/// Scale: 100 points per rank tier (Iron 1 = tier 3 = 300…; Radiant = 2700), so a rating reads as a rank.
///
/// Inputs per match, oldest first:
///   • result: win 1, loss 0, draw 0.5;
///   • team strength: average tier of the player's ranked teammates and of the ranked enemies (tier×100+50);
///   • personal performance: combat score per round against the lobby average (q).
/// Update, per match:
///   own side = (4 × teammates + rating) / 5, expected E = 1 / (1 + 10^((enemies − own side) / 400));
///   score S = 0.6 × result + 0.4 × clamp(0.5 + (q − 1), 0, 1)  (performance counts, but winning counts most);
///   rating += K × (S − E), K = 30 + 120 × RD / 350;
///   RD (uncertainty) shrinks 7% a match (floor 50) and grows back with time away: RD² += 10² × days.
/// Start: the player's own rank in their first match (or its lobby average), RD 350.
/// Missing data: a match with fewer than 3 ranked enemies changes nothing (time still passes).
/// Confidence: 100% at RD 50, 0% at RD 350. Interval: rating ± RD.
/// Known failure modes: lobbies of unranked players carry no information; few matches = wide interval;
/// teammates' skill is taken from their visible rank, which smurfs and boosted accounts distort.
/// </summary>
public static class MmrModel
{
    public const int Version = 1;
    public const double Scale = 400, RdStart = 350, RdFloor = 50;

    public sealed record Point(DateTime Utc, string MatchId, double Before, double Rating, double Rd, double Expected, double Score, bool Won, double EnemyRating);

    public sealed record Estimate(double Rating, double Rd, double Confidence, int Tier, int CurrentTier, double Momentum, int MatchesToConfident,
        double Peak, List<Point> History, int Used, int Skipped)
    {
        /// <summary>Estimated rating minus the displayed rank (in tiers): positive = rank should rise.</summary>
        public double Gap => CurrentTier >= 3 ? Rating / 100.0 - (CurrentTier + 0.5) : double.NaN;
        public double Low => Rating - Rd;
        public double High => Rating + Rd;
    }

    public static double TierRating(double tier) => tier * 100 + 50;
    public static int TierOf(double rating) => Math.Clamp((int)Math.Floor(rating / 100), 3, 27);
    public static double ConfidenceOf(double rd) => Math.Clamp(100 * (1 - (rd - RdFloor) / (RdStart - RdFloor)), 0, 100);
    public static double Expect(double own, double opp) => 1 / (1 + Math.Pow(10, (opp - own) / Scale));

    /// <summary>The ranked players of one side, excluding the player.</summary>
    static List<VPlayer> Side(VMatch m, string id, bool mates)
    {
        var team = m.TeamOf(id);
        return m.Players.Where(p => p.Id != id && p.Tier >= 3 && (p.Team == team) == mates).ToList();
    }

    /// <summary>Runs the model over the matches (any order; sorted oldest first inside). Null when no match is usable.</summary>
    public static Estimate Run(IEnumerable<VMatch> matches, string id, int currentTier = 0)
    {
        var ms = matches.Where(m => VMatch.Counts(m.Queue) && m.Players.Any(p => p.Id == id)).OrderBy(m => m.StartUtc).ToList();
        if (ms.Count == 0) return null;
        double r = double.NaN, rd = RdStart, peak = double.MinValue;
        DateTime? last = null;
        var hist = new List<Point>();
        int skipped = 0;
        foreach (var m in ms)
        {
            var me = m.Players.First(p => p.Id == id);
            var enemies = Side(m, id, false);
            var mates = Side(m, id, true);
            if (double.IsNaN(r))
            {
                double start = me.Tier >= 3 ? me.Tier : enemies.Concat(mates).Select(p => (double)p.Tier).DefaultIfEmpty(double.NaN).Average();
                if (double.IsNaN(start)) { skipped++; continue; }
                r = TierRating(start);
            }
            if (last is { } l) rd = Math.Min(RdStart, Math.Sqrt(rd * rd + 100 * Math.Max(0, (m.StartUtc - l).TotalDays)));
            last = m.StartUtc;
            if (enemies.Count < 3) { skipped++; continue; }
            double opp = TierRating(enemies.Average(p => p.Tier));
            double team = mates.Count > 0 ? TierRating(mates.Average(p => p.Tier)) : r;
            double own = (4 * team + r) / 5;
            double e = Expect(own, opp);
            int us = m.Rounds.Count(x => x.Winner == me.Team), them = m.Rounds.Count(x => x.Winner.Length > 0 && x.Winner != me.Team);
            int res = m.Outcome(me.Team);
            double result = res > 0 ? 1 : res < 0 ? 0 : 0.5;
            double lobby = m.Players.Average(p => (double)p.Score);
            double q = lobby > 0 ? me.Score / lobby : 1;
            double s = 0.6 * result + 0.4 * Math.Clamp(0.5 + (q - 1), 0, 1);
            double k = 30 + 120 * rd / RdStart;
            double before = r;
            r += k * (s - e);
            rd = Math.Max(RdFloor, rd * 0.93);
            peak = Math.Max(peak, r);
            hist.Add(new Point(m.StartUtc, m.Id, before, r, rd, e, s, result == 1, opp));
        }
        if (hist.Count == 0) return null;
        double momentum = hist.Count > 5 ? r - hist[^6].Rating : r - hist[0].Rating;
        int need = 0;
        for (double x = rd; ConfidenceOf(x) < 80 && need < 200; x = Math.Max(RdFloor, x * 0.93)) need++;
        return new Estimate(r, rd, ConfidenceOf(rd), TierOf(r), currentTier, momentum, need, peak, hist, hist.Count, skipped);
    }

    /// <summary>The model's rating just before each match (for expected-performance work without looking ahead).</summary>
    public static Dictionary<string, double> RatingBefore(Estimate e)
    {
        var d = new Dictionary<string, double>();
        if (e == null) return d;
        foreach (var p in e.History) d[p.MatchId] = p.Before;
        return d;
    }

    /// <summary>
    /// How well the model's win expectations matched reality: Brier score (0 = perfect, 0.25 = a coin flip) and
    /// calibration in 5 buckets of expected win chance. Each expectation is made before the match it predicts.
    /// </summary>
    public static (double Brier, List<(double Expected, double Actual, int N)> Buckets) Calibration(Estimate e)
    {
        if (e == null || e.History.Count == 0) return (double.NaN, new());
        double brier = e.History.Average(p => Math.Pow(p.Expected - (p.Won ? 1 : 0), 2));
        var buckets = e.History.GroupBy(p => Math.Min(4, (int)(p.Expected * 5)))
            .OrderBy(g => g.Key).Select(g => (g.Average(p => p.Expected), g.Average(p => p.Won ? 1.0 : 0.0), g.Count())).ToList();
        return (brier, buckets);
    }
}

/// <summary>
/// Rank forecast: thousands of simulated futures from the player's win chance and RR per win and loss. Version 1.
/// Win chance = recent competitive win rate shrunk toward 50% (5 imagined wins and 5 losses), or what the user sets.
/// RR per win and loss = Riot's recent RR changes when known, else 20 and 17 (stated as an assumption).
/// Each tier is 100 RR; Immortal 3 plus another 100 RR is counted as "Radiant", which in the game also needs a
/// leaderboard place, so the Radiant chance is an upper bound. Not a prediction of what will happen: a range of
/// what happens if the player keeps playing the way the inputs describe.
/// </summary>
public static class Forecast
{
    public const int Version = 1;

    public sealed record Inputs(int Tier, int Rr, double WinChance, double RrWin, double RrLoss, bool RrAssumed, int Games, int Wins);

    public sealed record Outcome(int Matches, int Conservative, int Expected, int Optimistic, double NextRankPct, double ImmortalPct, double RadiantPct);

    public static Inputs From(IEnumerable<VMatch> ms, string id, int tier, int rr, IReadOnlyList<int> rrChanges)
    {
        var comp = ms.Where(m => m.Queue == "competitive" && m.Players.Any(p => p.Id == id)).OrderByDescending(m => m.StartUtc).Take(20).ToList();
        int wins = comp.Count(m => m.Rounds.Count(r => r.Winner == m.TeamOf(id)) > m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != m.TeamOf(id)));
        double p = (wins + 5.0) / (comp.Count + 10.0);
        var w = rrChanges?.Where(x => x > 0).ToList() ?? new();
        var l = rrChanges?.Where(x => x < 0).Select(x => -x).ToList() ?? new();
        bool assumed = w.Count < 2 || l.Count < 2;
        return new Inputs(tier, rr, p, w.Count >= 2 ? w.Average() : 20, l.Count >= 2 ? l.Average() : 17, assumed, comp.Count, wins);
    }

    /// <summary>Simulates <paramref name="runs"/> futures of <paramref name="matches"/> games (fixed seed: same inputs, same answer).</summary>
    public static Outcome Run(Inputs i, int matches, int runs = 3000, int seed = 7)
    {
        var rnd = new Random(seed);
        var ends = new int[runs];
        int next = 0, imm = 0, rad = 0;
        double start = Math.Max(3, i.Tier) * 100 + Math.Clamp(i.Rr, 0, 99);
        for (int k = 0; k < runs; k++)
        {
            double pts = start;
            for (int g = 0; g < matches; g++) pts += rnd.NextDouble() < i.WinChance ? i.RrWin : -i.RrLoss;
            pts = Math.Max(300, pts);
            ends[k] = (int)pts;
            if (pts >= (Math.Max(3, i.Tier) + 1) * 100) next++;
            if (pts >= 2400) imm++;
            if (pts >= 2700) rad++;
        }
        Array.Sort(ends);
        int T(double q) => Math.Clamp(ends[(int)(q * (runs - 1))] / 100, 3, 27);
        return new Outcome(matches, T(0.2), T(0.5), T(0.8), 100.0 * next / runs, 100.0 * imm / runs, 100.0 * rad / runs);
    }
}

/// <summary>
/// RR efficiency: whether the rank is rising as fast as the player's play suggests it should. Version 1.
/// RR per match = average of Riot's recent RR changes. Expected RR per match = win chance × RR per win −
/// (1 − win chance) × RR per loss, with the win chance from Pulse's MMR model (rating vs typical lobby).
/// Efficiency = actual − expected (positive: climbing faster than play alone predicts).
/// </summary>
public static class RrEfficiency
{
    public sealed record Result(double AvgGain, double AvgLoss, double PerMatch, double ExpectedPerMatch, int Samples)
    {
        public double Efficiency => PerMatch - ExpectedPerMatch;
    }

    public static Result Of(IReadOnlyList<int> rrChanges, MmrModel.Estimate e)
    {
        if (rrChanges == null || rrChanges.Count < 3) return null;
        var w = rrChanges.Where(x => x > 0).ToList();
        var l = rrChanges.Where(x => x < 0).Select(x => -x).ToList();
        double gain = w.Count > 0 ? w.Average() : 0, loss = l.Count > 0 ? l.Average() : 0;
        double p = e != null && e.History.Count >= 3 ? e.History.TakeLast(10).Average(x => x.Expected) : 0.5;
        // The model's expectation is for the lobbies the player met; a rating above them means a higher chance.
        if (e != null && e.History.Count >= 3) p = Math.Clamp(MmrModel.Expect(e.Rating, e.History.TakeLast(10).Average(x => x.EnemyRating)), 0.05, 0.95);
        return new Result(gain, loss, rrChanges.Average(), p * gain - (1 - p) * loss, rrChanges.Count);
    }
}
