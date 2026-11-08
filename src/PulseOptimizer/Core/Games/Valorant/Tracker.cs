using Pulse.Core.Games.Live;

namespace Pulse.Core.Games.Valorant;

/// <summary>One skill on a tracker profile and how it's measured.</summary>
public sealed record Skill(string Id, string Name, string Unit, Func<PlayerTotals, double> Get, Func<PlayerTotals, int> Sample, int MinSample, string Explains);

/// <summary>A skill's value, where it sits among players in your matches, and the rank it plays like.</summary>
public sealed record SkillScore(Skill Skill, double Value, double TopPercent, int PlaysLikeTier);

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
    public List<SkillScore> Skills = new();
    /// <summary>For profiles from your matches: games together on your team, and against you.</summary>
    public int With, WithWins, Against, AgainstWins;
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
        new("aim", "Aim", "%", t => t.HeadshotPct, t => t.Head + t.Body + t.Legs, 80, "Headshot rate: the share of hits that land on the head."),
        new("damage", "Damage", "", t => t.Adr, t => t.Rounds, 40, "Damage per round."),
        new("opening", "Opening duels", "%", t => t.OpeningWinPct, t => t.FirstKills + t.FirstDeaths, 8, "How often they win the first fight of the round when they're in it."),
        new("trading", "Trading", "/100", t => t.TradeKillsPer100, t => t.Rounds, 40, "Kills that avenge a teammate within 5 seconds, per 100 rounds."),
        new("positioning", "Positioning", "%", t => 100 - t.AlonePct, t => t.DeathsPlaced, 12, "Deaths with a teammate close enough to trade (higher is better)."),
        new("utility", "Utility", "", t => t.AllCastsPerRound, t => t.CastRounds, 40, "Abilities used per round."),
        new("clutch", "Clutch", "%", t => t.ClutchWinPct, t => t.ClutchChances, 4, "Rounds won when they were the last one alive."),
    };

    // ───────── the rank scale ─────────

    sealed class Scale
    {
        public Dictionary<string, List<double>> All = new();
        public Dictionary<string, Dictionary<int, List<double>>> ByTier = new();
    }

    static Scale _scale;
    static int _scaleCount = -1;

    /// <summary>Every player-match in the saved matches, per skill, overall and by rank (rebuilt when matches are added).</summary>
    static Scale Build(IReadOnlyList<VMatch> matches)
    {
        if (_scale != null && _scaleCount == matches.Count) return _scale;
        var s = new Scale();
        foreach (var k in Skills) { s.All[k.Id] = new(); s.ByTier[k.Id] = new(); }
        foreach (var m in matches)
            foreach (var p in m.Players)
            {
                var t = Analysis.Totals(m, p.Id);
                foreach (var k in Skills)
                {
                    // One match is a small sample: only count it when the number means something.
                    if (k.Sample(t) < Math.Max(1, k.MinSample / 6)) continue;
                    var v = k.Get(t);
                    if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                    s.All[k.Id].Add(v);
                    if (p.Tier >= 3)
                    {
                        if (!s.ByTier[k.Id].TryGetValue(p.Tier, out var l)) s.ByTier[k.Id][p.Tier] = l = new();
                        l.Add(v);
                    }
                }
            }
        foreach (var l in s.All.Values) l.Sort();
        _scale = s;
        _scaleCount = matches.Count;
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
        p.Skills.Clear();
        foreach (var k in Skills)
        {
            if (k.Sample(p.Totals) < k.MinSample) { p.Skills.Add(new SkillScore(k, double.NaN, double.NaN, 0)); continue; }
            var v = k.Get(p.Totals);
            p.Skills.Add(new SkillScore(k, v, TopPercent(scale.All[k.Id], v), PlaysLike(scale.ByTier[k.Id], v)));
        }
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
        Score(p);
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

    /// <summary>For tests.</summary>
    internal static void Reset() { _scale = null; _scaleCount = -1; }
}
