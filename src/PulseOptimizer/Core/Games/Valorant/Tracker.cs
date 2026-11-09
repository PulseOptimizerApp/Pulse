using Pulse.Core.Games.Live;

namespace Pulse.Core.Games.Valorant;

/// <summary>One skill on a tracker profile and how it's measured.</summary>
public sealed record Skill(string Id, string Name, string Unit, Func<PlayerTotals, double> Get, Func<PlayerTotals, int> Sample, int MinSample, string Explains);

/// <summary>A skill's value, where it sits among players in your matches, and the rank it plays like.</summary>
public sealed record SkillScore(Skill Skill, double Value, double TopPercent, int PlaysLikeTier);

/// <summary>Pulse rating: combat score per round against the average in the same matches (1.00 = average), and by side.</summary>
public sealed record Rating(double Overall, double Attack, double Defense, double TopPercent);

/// <summary>Everything a tracker profile shows.</summary>
public sealed class Profile
{
    public string Puuid = "", Name = "";
    public bool IsYou;
    /// <summary>True when this profile comes only from matches you played in (not a search).</summary>
    public bool FromYourMatches;
    public List<VMatch> Matches = new();
    public PlayerTotals Totals = new();
    public int Tier, Rr, Peak, ActWinPct, ActGames, Level;
    public string PeakAct = "";
    public Smurf Smurf = Smurf.None;
    /// <summary>Warning signs of cheating, measured against players at the same rank.</summary>
    public Smurf Cheat = Smurf.None;
    public int CheatChecks;
    /// <summary>The rank after their last ranked game, when they haven't played ranked this act.</summary>
    public int LastTier;
    public long PlayMs;
    public Rating Rating;
    /// <summary>Pulse Score (0–1000) averaged over the matches, and each match's own (newest first, NaN when unknown).</summary>
    public double Score = double.NaN;
    public List<double> ScoreHistory = new();
    public List<SkillScore> Skills = new();
    /// <summary>The detailed stat tiles (headshots, ADR, KAST, first kills, untraded deaths, trades, abilities, ACS).</summary>
    public List<SkillScore> Stats = new();
    /// <summary>The rank to compare with: their rank now, else their last ranked one, else the typical rank of their lobbies.</summary>
    public int CompareTier;
    /// <summary>For profiles from your matches: games together on your team, and against you.</summary>
    public int With, WithWins, Against, AgainstWins;
    /// <summary>Searched profiles: the account's own smurf signs (kept to score again after loading more), and where the match history continues.</summary>
    public Smurf Account = Smurf.None;
    /// <summary>Competitive history act by act, newest first (from Riot's rank record).</summary>
    public List<Pulse.Core.Games.Live.ValorantLobby.ActLine> Acts = new();
    public List<string> PendingIds = new();
    public int HistoryNext;
    public bool HistoryEnd;
}

/// <summary>
/// Tracker profiles in the style of the big CS2 trackers, measured from real VALORANT matches: skills with "top X%"
/// and "plays like Diamond 2". The rank scale comes from every player in every match saved on this PC: all ten
/// players' ranks are in each match, so Pulse can see how players at each rank actually perform.
/// </summary>
public static class Tracker
{
    public static readonly Skill[] Skills =
    {
        new("aim", "Aim", "", t => t.AimRating, t => t.Head + t.Body + t.Legs, 50, "Headshots, damage and kills together (50 is typical). Headshots count less for sniper players, since an Operator kills with body shots."),
        new("damage", "Damage", "", t => t.Adr, t => t.Rounds, 20, "Damage per round."),
        new("opening", "Opening duels", "%", t => t.OpeningWinPct, t => t.FirstKills + t.FirstDeaths, 6, "How often they win the first fight of the round when they're in it."),
        new("trading", "Trading", "/100", t => t.TradeKillsPer100, t => t.Rounds, 20, "Kills that avenge a teammate within 5 seconds, per 100 rounds."),
        new("positioning", "Positioning", "%", t => 100 - t.AlonePct, t => t.DeathsPlaced, 10, "Deaths with a teammate close enough to trade (higher is better)."),
        new("utility", "Utility", "", t => t.AllCastsPerRound, t => t.CastRounds, 20, "Abilities used per round."),
        new("clutch", "Clutch", "%", t => t.ClutchWinPct, t => t.ClutchChances, 3, "Rounds won when they were the last one alive."),
    };

    /// <summary>The detailed stat tiles. Untraded deaths are lower-is-better, so they're scored as their opposite.</summary>
    public static readonly Skill[] StatTiles =
    {
        new("hs", "Headshot rate", "%", t => t.HeadshotPct, t => t.Head + t.Body + t.Legs, 50, "The share of hits that land on the head."),
        new("adr", "Damage per round", "", t => t.Adr, t => t.Rounds, 20, "Average damage dealt each round."),
        new("kast", "KAST", "%", t => t.KastPct, t => t.Rounds, 20, "Rounds with a kill, assist, survival or trade."),
        new("fk", "First kills", "/100 rounds", t => t.FirstKillsPer100, t => t.Rounds, 20, "Opening kills per 100 rounds."),
        new("untraded", "Untraded deaths", "%", t => 100 - t.UntradedPct, t => t.DeathsWithMates, 10, "Deaths no teammate traded."),
        new("trades", "Trades", "/100 rounds", t => t.TradeKillsPer100, t => t.Rounds, 20, "Kills that avenge a teammate within 5 seconds, per 100 rounds."),
        new("abil", "Abilities a round", "", t => t.AllCastsPerRound, t => t.CastRounds, 20, "Abilities used per round."),
        new("acs", "ACS", "", t => t.Acs, t => t.Rounds, 20, "Average combat score per round, as on VALORANT's scoreboard."),
    };

    /// <summary>The value to show for a stat tile (untraded deaths are kept inverted for scoring).</summary>
    public static double Shown(SkillScore s) => s.Skill.Id == "untraded" && !double.IsNaN(s.Value) ? 100 - s.Value : s.Value;

    // ───────── the rank scale ─────────

    sealed class Scale
    {
        public Dictionary<string, List<double>> All = new();
        public Dictionary<string, Dictionary<int, List<double>>> ByTier = new();
        /// <summary>Mean and spread of each Pulse Score ingredient, and every player-match's combined value (sorted).</summary>
        public Dictionary<string, (double Mean, double Sd)> Spread = new();
        public List<double> Composite = new();
    }

    // ───────── Pulse Score ─────────

    /// <summary>
    /// What goes into the Pulse Score, and how much: combat score, damage dealt and the damage delta, KAST, opening
    /// duels won minus lost, and trades. Each is measured against every player-match Pulse has seen.
    /// </summary>
    static readonly (string Id, double W)[] ScoreParts = { ("acs", 0.30), ("kast", 0.20), ("dd", 0.20), ("adr", 0.15), ("open", 0.10), ("trades", 0.05) };

    /// <summary>
    /// Typical spreads for one player in one match across all ranks, used until Pulse has seen enough players of its
    /// own (about 200 per number). Pulse's own players replace these as soon as there are enough of them.
    /// </summary>
    static readonly Dictionary<string, (double Mean, double Sd)> Typical = new()
    {
        ["acs"] = (200, 65), ["kast"] = (70, 12), ["dd"] = (0, 45), ["adr"] = (135, 40), ["open"] = (0, 12), ["trades"] = (12, 7),
    };

    /// <summary>How spread out the combined value is when the ingredients are typical (they move together, so less than 1).</summary>
    const double TypicalCompositeSd = 0.83;

    static double NormalCdf(double z)
    {
        // Abramowitz–Stegun 7.1.26, accurate to about 1e-7.
        double t = 1 / (1 + 0.3275911 * Math.Abs(z) / Math.Sqrt(2));
        double erf = 1 - t * (0.254829592 + t * (-0.284496736 + t * (1.421413741 + t * (-1.453152027 + t * 1.061405429)))) * Math.Exp(-z * z / 2);
        return z >= 0 ? (1 + erf) / 2 : (1 - erf) / 2;
    }

    static double Combine(Dictionary<string, double> v, Dictionary<string, (double Mean, double Sd)> spread)
    {
        double sum = 0, w = 0;
        foreach (var (id, weight) in ScoreParts)
            if (v.TryGetValue(id, out var x) && spread.TryGetValue(id, out var s) && s.Sd > 0)
            {
                sum += weight * Math.Clamp((x - s.Mean) / s.Sd, -4, 4);
                w += weight;
            }
        return w >= 0.6 ? sum / w : double.NaN;
    }

    /// <summary>
    /// Pulse Score, 0 to 1000: how one match's play ranks against every player-match Pulse has seen. 500 is the
    /// middle player, 900 means better than 90% of them. Needs about 100 player-matches to compare with.
    /// </summary>
    public static double PulseScore(Dictionary<string, double> values)
    {
        var s = Build(VStore.All);
        var c = Combine(values, s.Spread);
        if (double.IsNaN(c)) return double.NaN;
        // Few players seen so far: place the value on the typical curve instead of Pulse's own list.
        if (s.Composite.Count < 300) return Math.Clamp(Math.Round(1000 * NormalCdf(c / TypicalCompositeSd)), 1, 999);
        int lo = 0, hi = s.Composite.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (s.Composite[mid] < c) lo = mid + 1; else hi = mid; }
        return Math.Clamp(Math.Round(1000.0 * lo / s.Composite.Count), 1, 999);
    }

    /// <summary>One player's Pulse Score in one match.</summary>
    public static double MatchScore(VMatch m, string id) => m.Players.FirstOrDefault(p => p.Id == id) is { } p ? PulseScore(ScaleValues(m, p)) : double.NaN;

    static Scale _scale;
    static (int Matches, int Samples) _scaleKey = (-1, -1);

    /// <summary>One player's numbers in one match, for the scale (only the ones with enough behind them to mean something).</summary>
    internal static Dictionary<string, double> ScaleValues(VMatch m, VPlayer p)
    {
        var o = new Dictionary<string, double>();
        var t = Analysis.Totals(m, p.Id);
        double avgAcs = m.Players.Select(x => (double)x.Score).DefaultIfEmpty(0).Average() / Math.Max(1, m.Rounds.Count);
        if (avgAcs > 0 && !double.IsNaN(t.Acs)) o["rating"] = t.Acs / avgAcs;
        if (t.Rounds >= 5)
        {
            o["kd"] = t.KdRatio;
            o["kpr"] = t.KillsPerRound;
            o["kad"] = t.Deaths > 0 ? (double)(t.Kills + t.Assists) / t.Deaths : t.Kills + t.Assists;
            if (!double.IsNaN(t.DamageDelta)) o["dd"] = t.DamageDelta;
            if (!double.IsNaN(t.OpeningNetPer100)) o["open"] = t.OpeningNetPer100;
        }
        foreach (var k in Skills.Concat(StatTiles))
        {
            if (o.ContainsKey(k.Id) || k.Sample(t) < Math.Max(1, k.MinSample / 4)) continue;
            var v = k.Get(t);
            if (!double.IsNaN(v) && !double.IsInfinity(v)) o[k.Id] = v;
        }
        return o;
    }

    /// <summary>
    /// Every player-match Pulse has seen, per skill, overall and by rank: all ten players of your saved matches, plus
    /// the anonymous rank samples from searched players' matches (rebuilt when either grows).
    /// </summary>
    static Scale Build(IReadOnlyList<VMatch> matches)
    {
        var key = (matches.Count, RankSamples.Version);
        if (_scale != null && _scaleKey == key) return _scale;
        var s = new Scale();
        foreach (var k in Skills.Concat(StatTiles)) { s.All[k.Id] = new(); s.ByTier[k.Id] = new(); }
        s.All["rating"] = new();
        s.All["dd"] = new();
        s.All["open"] = new();
        s.All["kd"] = new();
        s.All["kpr"] = new();
        s.All["kad"] = new();
        var rows = new List<Dictionary<string, double>>();
        void Add(int tier, Dictionary<string, double> vals)
        {
            rows.Add(vals);
            foreach (var (id, v) in vals)
            {
                if (!s.All.TryGetValue(id, out var all)) continue;
                all.Add(v);
                if (tier >= 3 && s.ByTier.TryGetValue(id, out var by))
                {
                    if (!by.TryGetValue(tier, out var l)) by[tier] = l = new();
                    l.Add(v);
                }
            }
        }
        foreach (var m in matches)
        {
            if (!VMatch.Counts(m.Queue)) continue;
            foreach (var p in m.Players) Add(p.Tier, ScaleValues(m, p));
        }
        foreach (var r in RankSamples.Rows) Add(r.T, r.V);
        foreach (var l in s.All.Values) l.Sort();
        foreach (var d in s.ByTier.Values) foreach (var l in d.Values) l.Sort();
        foreach (var (id, _) in ScoreParts)
            if (!s.All.TryGetValue(id, out var l) || l.Count < 200) s.Spread[id] = Typical[id];
            else
            {
                double mean = l.Average();
                s.Spread[id] = (mean, Math.Sqrt(l.Sum(x => (x - mean) * (x - mean)) / l.Count));
            }
        foreach (var r in rows) { var c = Combine(r, s.Spread); if (!double.IsNaN(c)) s.Composite.Add(c); }
        s.Composite.Sort();
        _scale = s;
        _scaleKey = key;
        return s;
    }

    static double Median(List<double> l)
    {
        var o = l.OrderBy(x => x).ToList();
        return o.Count == 0 ? double.NaN : o.Count % 2 == 1 ? o[o.Count / 2] : (o[o.Count / 2 - 1] + o[o.Count / 2]) / 2;
    }

    /// <summary>"Top X%": the share of player-matches in your games that did better (lower is better for the player).</summary>
    internal static double TopPercent(List<double> sorted, double v)
    {
        if (sorted.Count < 20 || double.IsNaN(v)) return double.NaN;
        int below = 0;
        foreach (var x in sorted) if (x < v) below++; else break;
        return Math.Clamp(100.0 - 100.0 * below / sorted.Count, 1, 100);
    }

    /// <summary>
    /// The rank whose players typically score closest to this value (from ranks with at least 8 samples), or 0
    /// when there isn't enough to say. Ranks are smoothed so a noisy middle rank doesn't jump the scale.
    /// </summary>
    internal static int PlaysLike(Dictionary<int, List<double>> byTier, double v)
    {
        var tiers = byTier.Where(kv => kv.Value.Count >= 8).OrderBy(kv => kv.Key).Select(kv => (Tier: kv.Key, Median: Median(kv.Value))).ToList();
        if (tiers.Count < 3 || double.IsNaN(v)) return 0;
        // Make the medians rise with rank: neighbouring ranks that go the wrong way are pooled.
        var pts = new List<(int Tier, double Median, double W)>();
        foreach (var t in tiers)
        {
            pts.Add((t.Tier, t.Median, 1));
            while (pts.Count > 1 && pts[^1].Median < pts[^2].Median)
            {
                var hi = pts[^1];
                var lo = pts[^2];
                pts.RemoveAt(pts.Count - 1);
                pts[^1] = (lo.Tier, (lo.Median * lo.W + hi.Median * hi.W) / (lo.W + hi.W), lo.W + hi.W);
            }
        }
        if (pts.Count == 1 || v <= pts[0].Median) return pts[0].Tier;
        if (v >= pts[^1].Median) return pts[^1].Tier;
        for (int i = 1; i < pts.Count; i++)
            if (v <= pts[i].Median)
            {
                double f = (v - pts[i - 1].Median) / Math.Max(1e-9, pts[i].Median - pts[i - 1].Median);
                return (int)Math.Round(pts[i - 1].Tier + f * (pts[i].Tier - pts[i - 1].Tier));
            }
        return pts[^1].Tier;
    }

    /// <summary>Fills in the skills of a profile against the players in the matches saved on this PC.</summary>
    public static void Score(Profile p)
    {
        var scale = Build(VStore.All);
        SkillScore One(Skill k)
        {
            if (k.Sample(p.Totals) < k.MinSample) return new SkillScore(k, double.NaN, double.NaN, 0);
            var v = k.Get(p.Totals);
            return new SkillScore(k, v, TopPercent(scale.All[k.Id], v), PlaysLike(scale.ByTier[k.Id], v));
        }
        p.Skills = Skills.Select(One).ToList();
        p.Stats = StatTiles.Select(One).ToList();
        p.PlayMs = p.Matches.Sum(m => m.LengthMs);
        p.ScoreHistory = p.Matches.Select(m => MatchScore(m, p.Puuid)).ToList();
        var known = p.ScoreHistory.Where(x => !double.IsNaN(x)).ToList();
        p.Score = known.Count > 0 ? Math.Round(known.Average()) : double.NaN;
        p.Rating = RatingOf(p, scale);
    }

    /// <summary>Combat score per round against everyone in the same matches, overall and per side.</summary>
    static Rating RatingOf(Profile p, Scale scale)
    {
        double mine = 0, avg = 0, atkMine = 0, atkAvg = 0, defMine = 0, defAvg = 0;
        foreach (var m in p.Matches)
        {
            var me = m.Players.FirstOrDefault(x => x.Id == p.Puuid);
            if (me == null || m.Rounds.Count == 0 || m.Players.Count == 0) continue;
            mine += me.Score;
            avg += m.Players.Average(x => (double)x.Score);
            // Per side: kills (150 each, like combat score's kill points) plus damage, against the match average.
            var team = me.Team;
            foreach (var r in m.Rounds)
            {
                bool atk = m.Attacking(team, r.N);
                double Impact(string id) => 150.0 * m.Kills.Count(k => k.Round == r.N && k.Killer == id && m.TeamOf(k.Victim) != m.TeamOf(id))
                    + r.Damage.Where(d => d.From == id && m.TeamOf(d.To) != m.TeamOf(id)).Sum(d => d.Amount);
                double my = Impact(p.Puuid), all = m.Players.Average(x => Impact(x.Id));
                if (atk) { atkMine += my; atkAvg += all; } else { defMine += my; defAvg += all; }
            }
        }
        if (avg <= 0) return null;
        double overall = mine / avg;
        return new Rating(overall, atkAvg > 0 ? atkMine / atkAvg : double.NaN, defAvg > 0 ? defMine / defAvg : double.NaN, TopPercent(scale.All["rating"], overall));
    }

    /// <summary>"Top X%" for any number Pulse keeps per player-match ("adr", "kd", "hs", "kast", "dd", "acs", "kpr", "kad"…); NaN until there are 20.</summary>
    public static double TopPercentOf(string id, double value)
    {
        var scale = Build(VStore.All);
        return scale.All.TryGetValue(id, out var l) ? TopPercent(l, value) : double.NaN;
    }

    /// <summary>The typical value of a skill or stat at one rank (NaN when there aren't 8 players at it yet).</summary>
    public static double MedianAt(string id, int tier)
    {
        var scale = Build(VStore.All);
        return scale.ByTier.TryGetValue(id, out var d) && d.TryGetValue(tier, out var l) && l.Count >= 8 ? Median(l) : double.NaN;
    }

    /// <summary>Where a bar for this skill or stat ends: about the best 5% of players in the saved matches.</summary>
    public static double BarMax(string id, double value)
    {
        var scale = Build(VStore.All);
        var top = scale.All.TryGetValue(id, out var l) ? Quantile(l, 0.95) : double.NaN;
        if (double.IsNaN(top) || top <= 0) top = double.IsNaN(value) ? 1 : value * 1.25;
        return Math.Max(top, double.IsNaN(value) ? 0 : value) * 1.05;
    }

    /// <summary>The value a share of the way up a sorted list (0.975: higher than 97.5% of it).</summary>
    static double Quantile(List<double> sorted, double q)
    {
        if (sorted == null || sorted.Count == 0) return double.NaN;
        var i = Math.Clamp((int)Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[i];
    }

    /// <summary>
    /// Cheating signals, from the four numbers a pro would look at: K/D, headshot rate, clutches and win rate. Each is
    /// checked against players at the same rank in the saved matches where there are enough of them (their top 2.5%),
    /// and against levels almost nobody honest reaches. Very good players trip one now and then; several together
    /// (above all aim and K/D at once) are what's worth a look. It's never proof.
    /// </summary>
    public static (Smurf Signals, int Checked) CheatSignals(Profile p)
    {
        var scale = Build(VStore.All);
        int tier = p.CompareTier;
        var signs = new List<string>();
        int done = 0;
        double score = 0;
        double Top(string id) => tier >= 3 && scale.ByTier.TryGetValue(id, out var by) && by.TryGetValue(tier, out var l) && l.Count >= 15 ? Quantile(l, 0.975)
            : scale.All.TryGetValue(id, out var all) && all.Count >= 40 ? Quantile(all, 0.99) : double.NaN;
        string rank = tier >= 3 ? ValorantMatches.RankName(tier) : Pulse.Ui.Loc.T("their rank");
        var t = p.Totals;
        bool aim = false, frags = false;

        // Headshot rate: the hardest number to fake honestly.
        if (t.Head + t.Body + t.Legs >= 150)
        {
            done++;
            double lim = Top("hs");
            if (t.HeadshotPct >= 40 || (!double.IsNaN(lim) && t.HeadshotPct >= Math.Max(lim, 33)))
            {
                aim = true;
                score += t.HeadshotPct >= 40 ? 30 : 20;
                signs.Add(Pulse.Ui.Loc.F("Headshot rate {0:0}%, higher than almost every {1} player", t.HeadshotPct, rank));
            }
        }
        // K/D: 1.4 and up is already rare; 2.0 almost never happens honestly over several matches.
        if (t.Matches >= 5)
        {
            done++;
            double lim = Top("kd");
            if (t.KdRatio >= 2.0) { frags = true; score += 30; signs.Add(Pulse.Ui.Loc.F("K/D of {0:0.00} over {1} matches", t.KdRatio, t.Matches)); }
            else if (t.KdRatio >= 1.4 || (!double.IsNaN(lim) && t.KdRatio >= Math.Max(lim, 1.4)))
            { frags = true; score += 20; signs.Add(Pulse.Ui.Loc.F("K/D of {0:0.00} over {1} matches", t.KdRatio, t.Matches)); }
        }
        // Clutches: most players win about 1 in 5.
        if (t.ClutchChances >= 6)
        {
            done++;
            if (t.ClutchWinPct >= 50) { score += 20; signs.Add(Pulse.Ui.Loc.F("Won {0} of {1} clutches ({2:0}%; most players win about 20%)", t.ClutchWins, t.ClutchChances, t.ClutchWinPct)); }
            else if (t.ClutchWinPct >= 38) { score += 10; signs.Add(Pulse.Ui.Loc.F("Won {0} of {1} clutches ({2:0}%; most players win about 20%)", t.ClutchWins, t.ClutchChances, t.ClutchWinPct)); }
        }
        // Win rate: the act's when Riot shares it, else the matches Pulse saw. These are recent games, so once a
        // player reaches the rank they belong at and their win rate settles near 50%, this sign goes away by itself.
        bool act = p.ActGames >= 10 && p.ActWinPct >= 0;
        double win = act ? p.ActWinPct : t.WinPct;
        int games = act ? p.ActGames : t.Matches;
        if (games >= 10)
        {
            done++;
            if (win >= 75) { score += 35; signs.Add(Pulse.Ui.Loc.F("Won {0:0}% of {1} games", win, games)); }
            else if (win >= 65) { score += 20; signs.Add(Pulse.Ui.Loc.F("Won {0:0}% of {1} games", win, games)); }
        }
        // A new account winning like that is the classic pattern.
        int level = LevelOf(p);
        if (level > 0 && win >= 65 && games >= 10)
        {
            if (level <= 30) { score += 20; signs.Add(Pulse.Ui.Loc.F("New account (level {0})", level)); }
            else if (level <= 60) { score += 10; signs.Add(Pulse.Ui.Loc.F("Fairly new account (level {0})", level)); }
        }
        // Aim and frags far out together is the pattern that matters most.
        if (aim && frags) score += 15;
        if (t.Rounds < 120) score *= 0.6;            // few rounds: less certain
        if (signs.Count == 1) score = Math.Min(score, 25);
        return (new Smurf((int)Math.Round(Math.Min(100, score)), signs), done);
    }

    // ───────── profiles from your matches ─────────

    public sealed record Known(string Puuid, string Agent, int Tier, int Games, int With, int Against, DateTime Last);

    /// <summary>People who were in your saved matches, most games first (at least two games).</summary>
    public static List<Known> People(IReadOnlyList<VMatch> mine, int min = 2)
    {
        var map = new Dictionary<string, (List<VMatch> Ms, int With)>();
        foreach (var m in mine)
            foreach (var p in m.Players.Where(p => p.Id != m.Me))
            {
                if (!map.TryGetValue(p.Id, out var e)) e = (new List<VMatch>(), 0);
                e.Ms.Add(m);
                if (p.Team == m.MyTeam) e.With++;
                map[p.Id] = e;
            }
        return map.Where(kv => kv.Value.Ms.Count >= min).Select(kv =>
        {
            var last = kv.Value.Ms.OrderByDescending(m => m.StartUtc).First();
            var pl = last.Players.First(p => p.Id == kv.Key);
            return new Known(kv.Key, pl.Agent, pl.Tier, kv.Value.Ms.Count, kv.Value.With, kv.Value.Ms.Count - kv.Value.With, last.StartUtc);
        }).OrderByDescending(k => k.Games).ThenByDescending(k => k.Last).ToList();
    }

    /// <summary>A profile built only from matches you played (you, or someone you played with or against).</summary>
    public static Profile FromYourMatches(IReadOnlyList<VMatch> mine, string puuid)
    {
        var ms = mine.Where(m => m.Players.Any(p => p.Id == puuid)).OrderByDescending(m => m.StartUtc).ToList();
        var p = new Profile { Puuid = puuid, IsYou = ms.Count > 0 && ms[0].Me == puuid, FromYourMatches = true, Matches = ms };
        p.Totals = Analysis.For(ms, puuid);
        if (ms.Count > 0)
        {
            var last = ms[0].Players.First(x => x.Id == puuid);
            p.Tier = last.Tier;
            p.Peak = ms.SelectMany(m => m.Players.Where(x => x.Id == puuid)).Max(x => x.Tier);
        }
        foreach (var m in ms.Where(m => m.Me != puuid))
        {
            bool sameTeam = m.TeamOf(puuid) == m.MyTeam;
            if (sameTeam) { p.With++; if (m.Won) p.WithWins++; }
            else { p.Against++; if (m.Won) p.AgainstWins++; }
        }
        p.CompareTier = p.Tier >= 3 ? p.Tier : TypicalLobby(ms);
        Score(p);
        p.Smurf = SmurfChance(p, Smurf.None);
        (p.Cheat, p.CheatChecks) = CheatSignals(p);
        return p;
    }

    /// <summary>A profile as seen from one player's own matches (theirs, not yours): wins and the rest count from their side.</summary>
    public static Profile FromTheirMatches(string puuid, List<VMatch> theirs)
    {
        var p = new Profile { Puuid = puuid, Matches = theirs.OrderByDescending(m => m.StartUtc).ToList() };
        p.Totals = Analysis.For(theirs, puuid);
        Score(p);
        return p;
    }

    /// <summary>The typical rank of the other players in these matches (for players without a rank of their own).</summary>
    public static int TypicalLobby(IEnumerable<VMatch> ms)
    {
        var tiers = ms.SelectMany(m => m.Players.Where(p => p.Id != m.Me && p.Tier >= 3).Select(p => p.Tier)).OrderBy(t => t).ToList();
        return tiers.Count == 0 ? 0 : tiers[tiers.Count / 2];
    }

    /// <summary>Once the live rank is known: what to compare with, the smurf chance and the cheating signals.</summary>
    public static void Finish(Profile p, Smurf account)
    {
        p.CompareTier = p.Tier >= 3 ? p.Tier : p.LastTier >= 3 ? p.LastTier : TypicalLobby(p.Matches);
        Score(p);
        p.Smurf = SmurfChance(p, account);
        (p.Cheat, p.CheatChecks) = CheatSignals(p);
    }

    /// <summary>
    /// How likely the account is a smurf, from what gives smurfs away: a win rate far above 50%, a high K/D, a low
    /// account level, and KAST (being useful in almost every round). Plus RR per win from the account itself: big RR
    /// gains are Riot saying the hidden rating is well above the rank. One sign alone never says "likely".
    /// </summary>
    /// <summary>The account level: Riot's lookup when known, else the highest in their matches (0 if unknown).</summary>
    static int LevelOf(Profile p) => p.Level > 0 ? p.Level : p.Matches.Select(m => m.Players.FirstOrDefault(x => x.Id == p.Puuid)?.Level ?? 0).DefaultIfEmpty(0).Max();

    public static Smurf SmurfChance(Profile p, Smurf account)
    {
        int score = 0;
        var signs = new List<string>();
        // The account's own signs (RR per win, act win rate, level, peak) from Riot.
        score += account.Score;
        signs.AddRange(account.Signs);
        var t = p.Totals;
        bool actWinCounted = p.ActGames >= 10 && p.ActWinPct >= 65;
        // Win rate in the matches Pulse sees (when the account's act win rate didn't already count).
        if (!actWinCounted && t.Matches >= 8)
        {
            if (t.WinPct >= 75) { score += 40; signs.Add(Pulse.Ui.Loc.F("Won {0:0}% of the {1} matches Pulse saw", t.WinPct, t.Matches)); }
            else if (t.WinPct >= 65) { score += 25; signs.Add(Pulse.Ui.Loc.F("Won {0:0}% of the {1} matches Pulse saw", t.WinPct, t.Matches)); }
            else if (t.WinPct >= 58) { score += 10; signs.Add(Pulse.Ui.Loc.F("Won {0:0}% of the {1} matches Pulse saw", t.WinPct, t.Matches)); }
        }
        if (t.Matches >= 5)
        {
            if (t.KdRatio >= 1.4) { score += 25; signs.Add(Pulse.Ui.Loc.F("K/D of {0:0.00} over {1} matches", t.KdRatio, t.Matches)); }
            else if (t.KdRatio >= 1.2) { score += 15; signs.Add(Pulse.Ui.Loc.F("K/D of {0:0.00} over {1} matches", t.KdRatio, t.Matches)); }
            if (t.KastPct >= 80) { score += 20; signs.Add(Pulse.Ui.Loc.F("KAST {0:0}%: useful in almost every round", t.KastPct)); }
            else if (t.KastPct >= 75) { score += 12; signs.Add(Pulse.Ui.Loc.F("KAST {0:0}%: useful in almost every round", t.KastPct)); }
        }
        // Account level from the matches, when Riot's own lookup didn't give one (it's counted there otherwise).
        if (p.Level == 0 && LevelOf(p) is var level and > 0 && (p.Tier >= 3 ? p.Tier : p.CompareTier) >= 3)
        {
            if (level <= 30) { score += 25; signs.Add(Pulse.Ui.Loc.F("New account (level {0}) in ranked", level)); }
            else if (level <= 60) { score += 15; signs.Add(Pulse.Ui.Loc.F("Fairly new account (level {0})", level)); }
        }
        if (signs.Count == 1) score = Math.Min(score, 40);
        return new Smurf(Math.Min(100, score), signs);
    }

    // ───────── agents: how well you play them, a tier list from your lobbies, the best players you've met ─────────

    public sealed record AgentLine(string Agent, string Role, int Matches, double WinPct, double Acs, double Kd, double Adr, double HsPct,
        double FirstKillsPer100, double CastsPerRound, double TopPercent, int PlaysLikeTier, int OthersSeen);

    public sealed record TierEntry(string Agent, string Role, string Grade, int Picks, double WinPct, double PickPct, double Acs);

    public sealed record TopPlayer(string Puuid, string Agent, int Tier, int Matches, double Acs, double Kd, bool You);

    /// <summary>Every player-match on each agent in the saved matches: (match, player, totals).</summary>
    static Dictionary<string, List<(VMatch M, VPlayer P, PlayerTotals T)>> ByAgent(IReadOnlyList<VMatch> all)
    {
        var d = new Dictionary<string, List<(VMatch, VPlayer, PlayerTotals)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in all.Where(m => VMatch.Counts(m.Queue)))
            foreach (var p in m.Players.Where(p => p.Agent.Length > 0))
            {
                if (!d.TryGetValue(p.Agent, out var l)) d[p.Agent] = l = new();
                l.Add((m, p, Analysis.Totals(m, p.Id)));
            }
        return d;
    }

    /// <summary>
    /// How you play each agent you've played at least twice, against every other player on that agent in the
    /// matches saved on this PC: "Top X%" by combat score per round, and the rank whose players score like you on it.
    /// </summary>
    public static List<AgentLine> YourAgents(IReadOnlyList<VMatch> mine, IReadOnlyList<VMatch> all)
    {
        var by = ByAgent(all);
        var me = mine.Count > 0 ? mine[0].Me : null;
        var list = new List<AgentLine>();
        foreach (var g in mine.Where(m => m.MyAgent.Length > 0).GroupBy(m => m.MyAgent).Where(g => g.Count() >= 2))
        {
            var t = Analysis.For(g, me);
            var others = by.TryGetValue(g.Key, out var l) ? l.Where(x => x.P.Id != me && x.T.Rounds >= 8).ToList() : new();
            var acsSorted = others.Select(x => x.T.Acs).Where(v => !double.IsNaN(v)).OrderBy(v => v).ToList();
            var byTier = others.Where(x => x.P.Tier >= 3).GroupBy(x => x.P.Tier).ToDictionary(x => x.Key, x => x.Select(y => y.T.Acs).ToList());
            list.Add(new AgentLine(g.Key, Analysis.RoleOf(g.Key), g.Count(), t.WinPct, t.Acs, t.KdRatio, t.Adr, t.HeadshotPct, t.FirstKillsPer100,
                t.AllCastsPerRound, TopPercent(acsSorted, t.Acs), PlaysLike(byTier, t.Acs), others.Count));
        }
        return list.OrderByDescending(a => a.Matches).ToList();
    }

    /// <summary>
    /// An agent tier list from your lobbies: win rate and pick rate of each agent across every player in the saved
    /// matches (at least 15 picks). Graded on win rate: S 55%+, A 52%+, B 48%+, C 45%+, D below.
    /// </summary>
    public static List<TierEntry> TierList(IReadOnlyList<VMatch> all, int minPicks = 8)
    {
        var by = ByAgent(all);
        int totalMatches = all.Count(m => VMatch.Counts(m.Queue));
        return by.Where(kv => kv.Value.Count >= minPicks).Select(kv =>
        {
            double win = 100.0 * kv.Value.Count(x => WonFor(x.M, x.P.Team)) / kv.Value.Count;
            var acs = kv.Value.Select(x => x.T.Acs).Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Average();
            string grade = win >= 55 ? "S" : win >= 52 ? "A" : win >= 48 ? "B" : win >= 45 ? "C" : "D";
            return new TierEntry(kv.Key, Analysis.RoleOf(kv.Key), grade, kv.Value.Count, win, totalMatches > 0 ? Math.Min(100, 100.0 * kv.Value.Select(x => x.M.Id).Distinct().Count() / totalMatches) : 0, acs);
        }).OrderBy(e => "SABCD".IndexOf(e.Grade)).ThenByDescending(e => e.WinPct).ToList();
    }

    static bool WonFor(VMatch m, string team) =>
        m.Rounds.Count(r => r.Winner == team) > m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != team);

    /// <summary>The best players on one agent in the saved matches, by combat score per round (you included).</summary>
    public static List<TopPlayer> TopOn(string agent, IReadOnlyList<VMatch> all, string me, int take = 10)
    {
        var by = ByAgent(all);
        if (!by.TryGetValue(agent, out var l)) return new();
        return l.GroupBy(x => x.P.Id).Select(g =>
        {
            var t = new PlayerTotals();
            foreach (var x in g) t.Add(x.T);
            var last = g.OrderByDescending(x => x.M.StartUtc).First();
            return new TopPlayer(g.Key, agent, last.P.Tier, g.Count(), t.Acs, t.KdRatio, g.Key == me);
        }).Where(p => p.Matches >= 1 && !double.IsNaN(p.Acs)).OrderByDescending(p => p.Acs).Take(take).ToList();
    }

    // ───────── who you're up against, and an MMR estimate ─────────

    public sealed record Opponent(string Puuid, string Agent, int Tier, int Times, int YourWins);

    public sealed record Opposition(int Matches, double EnemyAvg, double TeamAvg, double LobbyAvg, double MyAvg, Opponent Toughest, List<Opponent> Repeats, List<string> EnemyIds);

    /// <summary>
    /// The ranks you've been playing against in your last <paramref name="n"/> ranked matches (Competitive and Premier,
    /// where Riot records everyone's rank in that match), the toughest enemy, and enemies you keep meeting.
    /// </summary>
    public static Opposition Against(IReadOnlyList<VMatch> mine, int n = 5)
    {
        var ranked = mine.Where(m => VMatch.Counts(m.Queue) && m.Players.Count(p => p.Tier >= 3) >= 6).OrderByDescending(m => m.StartUtc).Take(n).ToList();
        if (ranked.Count == 0) return null;
        var enemies = ranked.SelectMany(m => m.Players.Where(p => p.Team != m.MyTeam && p.Tier >= 3).Select(p => (m, p))).ToList();
        var mates = ranked.SelectMany(m => m.Players.Where(p => p.Team == m.MyTeam && p.Id != m.Me && p.Tier >= 3)).ToList();
        var lobby = ranked.SelectMany(m => m.Players.Where(p => p.Id != m.Me && p.Tier >= 3)).ToList();
        var me = ranked.Select(m => m.MeP).Where(p => p != null && p.Tier >= 3).ToList();
        var toughest = enemies.OrderByDescending(e => e.p.Tier).Select(e => new Opponent(e.p.Id, e.p.Agent, e.p.Tier, 1, e.m.Won ? 1 : 0)).FirstOrDefault();
        // Enemies met more than once anywhere in your saved matches.
        var all = mine.SelectMany(m => m.Players.Where(p => p.Team != m.MyTeam).Select(p => (m, p))).GroupBy(x => x.p.Id)
            .Where(g => g.Count() >= 2)
            .Select(g =>
            {
                var last = g.OrderByDescending(x => x.m.StartUtc).First();
                return new Opponent(g.Key, last.p.Agent, last.p.Tier, g.Count(), g.Count(x => x.m.Won));
            })
            .OrderByDescending(o => o.Times).ThenByDescending(o => o.Tier).Take(5).ToList();
        double Avg(IEnumerable<int> t) => t.DefaultIfEmpty(0).Average();
        return new Opposition(ranked.Count, Avg(enemies.Select(e => e.p.Tier)), Avg(mates.Select(p => p.Tier)), Avg(lobby.Select(p => p.Tier)),
            Avg(me.Select(p => p.Tier)), toughest, all, enemies.Select(e => e.p.Id).Distinct().ToList());
    }

    public enum MmrSide { Unknown, Above, Even, Below }

    public sealed record MmrEstimate(int Matches, double LobbyAvg, double MyAvg, double AvgWin, double AvgLoss, MmrSide Side, int EstimateTier);

    /// <summary>
    /// Riot never shows MMR, so this estimates it the way MMR calculators do: Riot builds lobbies around your MMR, so
    /// the average rank of the other players in your recent ranked matches is close to it; and Riot gives more RR per
    /// win (and takes less per loss) when your MMR is above your rank. Both are shown, and they have to agree for a
    /// firm answer.
    /// </summary>
    public static MmrEstimate Mmr(IReadOnlyList<VMatch> mine, IReadOnlyList<int> rrChanges, int n = 10)
    {
        var ranked = mine.Where(m => m.Queue == "competitive" && m.MeP is { Tier: >= 3 } && m.Players.Count(p => p.Tier >= 3) >= 6)
            .OrderByDescending(m => m.StartUtc).Take(n).ToList();
        double lobby = ranked.Count == 0 ? 0 : ranked.Average(m => m.Players.Where(p => p.Id != m.Me && p.Tier >= 3).Average(p => p.Tier));
        double myAvg = ranked.Count == 0 ? 0 : ranked.Average(m => m.MeP.Tier);
        var wins = rrChanges?.Where(r => r > 0).ToList() ?? new();
        var losses = rrChanges?.Where(r => r < 0).Select(r => -r).ToList() ?? new();
        double avgWin = wins.Count >= 2 ? wins.Average() : double.NaN, avgLoss = losses.Count >= 2 ? losses.Average() : double.NaN;

        int lobbyVote = ranked.Count < 3 ? 0 : lobby - myAvg >= 1.5 ? 1 : lobby - myAvg <= -1.5 ? -1 : 0;
        int rrVote = 0;
        if (!double.IsNaN(avgWin) && !double.IsNaN(avgLoss))
            rrVote = avgWin - avgLoss >= 5 ? 1 : avgLoss - avgWin >= 5 ? -1 : 0;
        else if (!double.IsNaN(avgWin))
            rrVote = avgWin >= 24 ? 1 : avgWin <= 14 ? -1 : 0;
        var side = ranked.Count < 3 && double.IsNaN(avgWin) ? MmrSide.Unknown
            : lobbyVote + rrVote >= 1 && !(lobbyVote == -1 || rrVote == -1) ? MmrSide.Above
            : lobbyVote + rrVote <= -1 && !(lobbyVote == 1 || rrVote == 1) ? MmrSide.Below
            : MmrSide.Even;
        int est = ranked.Count >= 3 ? (int)Math.Round(lobby) : 0;
        return new MmrEstimate(ranked.Count, lobby, myAvg, avgWin, avgLoss, side, est);
    }

    /// <summary>
    /// Stats from one match that almost nobody at that rank reaches (their top 1%), for the after-match lobby. One
    /// match is a small sample, so the bar is higher than the profile's cheating signals, and it's worded as unusual
    /// stats, never as cheating.
    /// </summary>
    public static Smurf UnusualInMatch(VMatch m, string who, int tier)
    {
        var scale = Build(VStore.All);
        var t = Analysis.Totals(m, who);
        var signs = new List<string>();
        double Top(string id) => tier >= 3 && scale.ByTier[id].TryGetValue(tier, out var l) && l.Count >= 15 ? Quantile(l, 0.99) : Quantile(scale.All[id], 0.995);
        string rank = tier >= 3 ? ValorantMatches.RankName(tier) : Pulse.Ui.Loc.T("their rank");
        if (t.Head + t.Body + t.Legs >= 40 && Top("hs") is var hs && !double.IsNaN(hs) && t.HeadshotPct >= Math.Max(hs, 45))
            signs.Add(Pulse.Ui.Loc.F("{0:0}% headshots this match (very rare at {1})", t.HeadshotPct, rank));
        if (t.Rounds >= 15 && Top("adr") is var adr && !double.IsNaN(adr) && t.Adr >= Math.Max(adr, 220))
            signs.Add(Pulse.Ui.Loc.F("{0:0} damage per round this match (very rare at {1})", t.Adr, rank));
        if (t.Rounds >= 15 && t.Deaths > 0 && t.KdRatio >= 4)
            signs.Add(Pulse.Ui.Loc.F("K/D of {0:0.0} this match", t.KdRatio));
        int score = signs.Count == 0 ? 0 : signs.Count == 1 ? 30 : 60;
        return new Smurf(score, signs);
    }

    /// <summary>For tests.</summary>
    internal static void Reset() { _scale = null; _scaleKey = (-1, -1); }
}
