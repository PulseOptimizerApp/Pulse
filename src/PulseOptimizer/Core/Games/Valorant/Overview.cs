namespace Pulse.Core.Games.Valorant;

/// <summary>Which matches a profile counts: one playlist (or all) and, optionally, only since a date (this act).</summary>
public sealed record TrackerFilter(string Queue = "all", DateTime? SinceUtc = null, DateTime? UntilUtc = null)
{
    /// <summary>"All" means the real matches (not Deathmatch); Deathmatch shows only when it's picked, like the trackers.</summary>
    public IEnumerable<VMatch> Apply(IEnumerable<VMatch> matches) =>
        matches.Where(m => (Queue == "all" ? VMatch.Counts(m.Queue) : m.Queue == Queue) && (SinceUtc is not { } s || m.StartUtc >= s) && (UntilUtc is not { } u || m.StartUtc < u));
}

/// <summary>
/// A profile's numbers the way the big trackers show them: damage per round, K/D, headshot %, win %, KAST, damage
/// delta, kills, deaths, assists, ACS, KAD, kills per round, first bloods, flawless rounds, aces, multi-kills,
/// clutches, plants and defuses. Everything is counted from Riot's match data.
/// </summary>
public sealed class Overview
{
    public int Matches, Wins, Losses, Draws, Rounds, Kills, Deaths, Assists, Score, Damage, DamageTaken, Head, Body, Legs;
    public int KastRounds, FirstBloods, FirstDeaths, Flawless, Aces, TwoK, ThreeK, FourK, FiveK, ClutchChances, ClutchWins, Plants, Defuses;
    public long Spent, PlayMs;

    static double Div(double a, double b) => b > 0 ? a / b : double.NaN;
    public double DamagePerRound => Div(Damage, Rounds);
    public double Kd => Deaths > 0 ? (double)Kills / Deaths : Kills;
    public double HeadshotPct => Div(100.0 * Head, Head + Body + Legs);
    public double WinPct => Div(100.0 * Wins, Matches);
    public double KastPct => Div(100.0 * KastRounds, Rounds);
    public double DdPerRound => Div(Damage - DamageTaken, Rounds);
    public double Acs => Div(Score, Rounds);
    public double Kad => Deaths > 0 ? (double)(Kills + Assists) / Deaths : Kills + Assists;
    public double KillsPerRound => Div(Kills, Rounds);
    public double ClutchPct => Div(100.0 * ClutchWins, ClutchChances);
    /// <summary>Damage per 1,000 credits spent (econ rating).</summary>
    public double Econ => Div(1000.0 * Damage, Spent);
    public int MultiKills => TwoK + ThreeK + FourK + FiveK;
}

/// <summary>Works out overviews, placements, days, and every part of a match page.</summary>
public static class Stats
{
    /// <summary>One player's numbers over some matches (each match counted from that player's side).</summary>
    public static Overview Of(IEnumerable<VMatch> matches, string id)
    {
        var o = new Overview();
        foreach (var m in matches)
        {
            var me = m.Players.FirstOrDefault(p => p.Id == id);
            if (me == null) continue;
            var t = Analysis.Totals(m, id);
            o.Matches++;
            int us = m.Rounds.Count(r => r.Winner == me.Team), them = m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != me.Team);
            int res = m.Outcome(me.Team);
            if (res > 0) o.Wins++; else if (res < 0) o.Losses++; else o.Draws++;
            o.Rounds += t.Rounds; o.Kills += me.Kills; o.Deaths += me.Deaths; o.Assists += me.Assists; o.Score += me.Score;
            o.Damage += t.Damage; o.DamageTaken += t.DamageTaken; o.Head += t.Head; o.Body += t.Body; o.Legs += t.Legs;
            o.KastRounds += t.KastRounds; o.FirstBloods += t.FirstKills; o.FirstDeaths += t.FirstDeaths;
            o.ClutchChances += t.ClutchChances; o.ClutchWins += t.ClutchWins;
            o.PlayMs += m.LengthMs;
            foreach (var r in m.Rounds)
            {
                int k = RoundKills(m, r.N, id);
                if (k == 2) o.TwoK++; else if (k == 3) o.ThreeK++; else if (k == 4) o.FourK++; else if (k >= 5) { o.FiveK++; o.Aces++; }
                if (r.Winner == me.Team && Flawless(m, r)) o.Flawless++;
                if (r.Planter == id) o.Plants++;
                if (r.Defuser == id) o.Defuses++;
                if (r.Eco.TryGetValue(id, out var e)) o.Spent += e.Spent;
            }
        }
        return o;
    }

    /// <summary>Enemies a player killed in one round.</summary>
    public static int RoundKills(VMatch m, int round, string id)
    {
        var team = m.TeamOf(id);
        return m.Kills.Count(k => k.Round == round && k.Killer == id && k.Victim != id && m.TeamOf(k.Victim) != team);
    }

    /// <summary>The round winner lost nobody.</summary>
    public static bool Flawless(VMatch m, VRound r) =>
        r.Winner.Length > 0 && !m.Kills.Any(k => k.Round == r.N && m.TeamOf(k.Victim) == r.Winner);

    // ───────── match history ─────────

    /// <summary>Where a player finished in a match by combat score: 1 = match MVP; team MVP = best on their team.</summary>
    public sealed record Placement(int Place, bool MatchMvp, bool TeamMvp);

    public static Placement PlaceOf(VMatch m, string id)
    {
        var order = m.Players.OrderByDescending(p => p.Score).ToList();
        int place = order.FindIndex(p => p.Id == id) + 1;
        if (place <= 0) return new Placement(0, false, false);
        var team = m.TeamOf(id);
        bool teamBest = order.First(p => p.Team == team).Id == id;
        return new Placement(place, place == 1, teamBest && place != 1);
    }

    public sealed record Day(DateTime Date, List<VMatch> Matches, Overview Totals);

    /// <summary>Matches grouped by the local day they were played, newest first.</summary>
    public static List<Day> Days(IEnumerable<VMatch> matches, string id) =>
        matches.GroupBy(m => m.StartUtc.ToLocalTime().Date).OrderByDescending(g => g.Key)
            .Select(g => new Day(g.Key, g.OrderByDescending(m => m.StartUtc).ToList(), Of(g, id))).ToList();

    // ───────── one match ─────────

    public sealed record PerfLine(VPlayer Player, int TwoK, int ThreeK, int FourK, int FiveK, int ClutchWins, int ClutchChances, int Plants, int Defuses,
        double Econ, int FirstBloods, int FirstDeaths, double KastPct);

    /// <summary>The performance tab: multi-kills, clutches, plants, defuses and econ rating for every player.</summary>
    public static List<PerfLine> Performance(VMatch m) => m.Players.Select(p =>
    {
        var o = Of(new[] { m }, p.Id);
        return new PerfLine(p, o.TwoK, o.ThreeK, o.FourK, o.FiveK, o.ClutchWins, o.ClutchChances, o.Plants, o.Defuses, o.Econ, o.FirstBloods, o.FirstDeaths, o.KastPct);
    }).ToList();

    /// <summary>Kills between every pair of opponents: [killer][victim].</summary>
    public static Dictionary<string, Dictionary<string, int>> Duels(VMatch m)
    {
        var d = new Dictionary<string, Dictionary<string, int>>();
        foreach (var k in m.Kills)
        {
            if (k.Killer.Length == 0 || k.Killer == k.Victim || m.TeamOf(k.Killer) == m.TeamOf(k.Victim)) continue;
            if (!d.TryGetValue(k.Killer, out var row)) d[k.Killer] = row = new();
            row[k.Victim] = row.GetValueOrDefault(k.Victim) + 1;
        }
        return d;
    }

    public enum Buy { Eco, SemiEco, SemiBuy, FullBuy }

    /// <summary>What a team bought, from the average loadout value of its players.</summary>
    public static Buy BuyOf(double avgLoadout) => avgLoadout < 1000 ? Buy.Eco : avgLoadout < 2000 ? Buy.SemiEco : avgLoadout < 3900 ? Buy.SemiBuy : Buy.FullBuy;

    public sealed record EcoRound(int N, string Winner, double TeamLoadout, double EnemyLoadout, int TeamSpent, int EnemySpent, int TeamBank, int EnemyBank, Buy TeamBuy, Buy EnemyBuy);

    /// <summary>The economy tab: both teams' loadouts, spending and bank each round, from <paramref name="team"/>'s side.</summary>
    public static List<EcoRound> Economy(VMatch m, string team)
    {
        var list = new List<EcoRound>();
        foreach (var r in m.Rounds.OrderBy(r => r.N))
        {
            var mine = r.Eco.Where(e => m.TeamOf(e.Key) == team).Select(e => e.Value).ToList();
            var theirs = r.Eco.Where(e => m.TeamOf(e.Key) is { Length: > 0 } t && t != team).Select(e => e.Value).ToList();
            if (mine.Count == 0 && theirs.Count == 0) continue;
            double ml = mine.Sum(e => e.Loadout), tl = theirs.Sum(e => e.Loadout);
            list.Add(new EcoRound(r.N, r.Winner, ml, tl, mine.Sum(e => e.Spent), theirs.Sum(e => e.Spent), mine.Sum(e => e.Remaining), theirs.Sum(e => e.Remaining),
                BuyOf(mine.Count > 0 ? ml / mine.Count : 0), BuyOf(theirs.Count > 0 ? tl / theirs.Count : 0)));
        }
        return list;
    }

    public sealed record FeedKill(long RoundMs, VPlayer Killer, VPlayer Victim, string Weapon, string DamageType);
    public sealed record RoundFeed(VRound Round, List<FeedKill> Kills);

    /// <summary>The rounds tab: each round's result and kill feed in order.</summary>
    public static List<RoundFeed> Feed(VMatch m)
    {
        var byId = m.Players.ToDictionary(p => p.Id);
        return m.Rounds.OrderBy(r => r.N).Select(r => new RoundFeed(r, m.Kills.Where(k => k.Round == r.N).OrderBy(k => k.RoundMs)
            .Select(k => new FeedKill(k.RoundMs, byId.GetValueOrDefault(k.Killer), byId.GetValueOrDefault(k.Victim), k.Weapon, k.DamageType)).ToList())).ToList();
    }

    // ───────── tracker extras: clutches, multi-kills, encounters, map sides, one player's match ─────────

    /// <summary>A round where the player was the last one alive on their team, against <see cref="Vs"/> enemies.</summary>
    public sealed record Clutch(int Round, int Vs, bool Won);

    /// <summary>Every clutch situation the player had in a match (the first moment they were left alone, each round).</summary>
    public static List<Clutch> Clutches(VMatch m, string id)
    {
        var list = new List<Clutch>();
        var team = m.TeamOf(id);
        var mates = m.Players.Where(p => p.Team == team).Select(p => p.Id).ToList();
        var foes = m.Players.Where(p => p.Team.Length > 0 && p.Team != team).Select(p => p.Id).ToList();
        if (team.Length == 0 || mates.Count < 2 || foes.Count == 0) return list;
        foreach (var r in m.Rounds.OrderBy(r => r.N))
        {
            var aliveMates = new HashSet<string>(mates);
            var aliveFoes = new HashSet<string>(foes);
            foreach (var k in m.Kills.Where(k => k.Round == r.N).OrderBy(k => k.RoundMs))
            {
                aliveMates.Remove(k.Victim);
                aliveFoes.Remove(k.Victim);
                if (!aliveMates.Contains(id)) break;
                if (aliveMates.Count == 1 && aliveFoes.Count > 0)
                {
                    list.Add(new Clutch(r.N, aliveFoes.Count, r.Winner == team));
                    break;
                }
            }
        }
        return list;
    }

    /// <summary>How many rounds the player got exactly 2, 3, 4 and 5+ kills in: [2] = 2k rounds… [5] = aces.</summary>
    public static int[] MultiKills(VMatch m, string id)
    {
        var n = new int[6];
        foreach (var r in m.Rounds)
        {
            int k = RoundKills(m, r.N, id);
            if (k >= 2) n[Math.Min(5, k)]++;
        }
        return n;
    }

    /// <summary>Someone the player has met: as a teammate, as an enemy, and how those games went.</summary>
    public sealed record Encounter(string Id, string Name, string Agent, int Tier, int With, int WithWins, int Against, int AgainstWins, DateTime Last);

    /// <summary>
    /// For lobbies: who you've met before in your saved matches, by player id. Leaves out one match (the one the lobby is
    /// for). Only reads matches already on this PC: no requests to Riot.
    /// </summary>
    public static Dictionary<string, Encounter> MetBefore(IEnumerable<VMatch> matches, string me, string skipMatchId = null) =>
        string.IsNullOrEmpty(me) ? new()
        : Encounters(matches.Where(m => m.Id != skipMatchId && m.Players.Any(p => p.Id == me)), me)
            .Where(e => e.Id != me && e.With + e.Against > 0).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());

    /// <summary>Everyone the player has played with or against in these matches, most met first.</summary>
    public static List<Encounter> Encounters(IEnumerable<VMatch> matches, string id)
    {
        var map = new Dictionary<string, (string Name, string Agent, int Tier, int With, int WithWins, int Against, int AgainstWins, DateTime Last)>();
        foreach (var m in matches.OrderBy(m => m.StartUtc))
        {
            var me = m.Players.FirstOrDefault(p => p.Id == id);
            if (me == null || me.Team.Length == 0 || m.Players.Count(p => p.Team == me.Team) < 2) continue;
            int us = m.Rounds.Count(r => r.Winner == me.Team), them = m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != me.Team);
            int res = m.Outcome(me.Team);
            bool won = res > 0;
            foreach (var p in m.Players)
            {
                if (p.Id == id || p.Id.Length == 0) continue;
                var e = map.GetValueOrDefault(p.Id);
                e.Name = NameOf(m, p.Id) ?? e.Name;
                if (p.Agent.Length > 0) e.Agent = p.Agent;
                if (p.Tier >= 3) e.Tier = p.Tier;
                if (p.Team == me.Team) { e.With++; if (won) e.WithWins++; }
                else { e.Against++; if (won) e.AgainstWins++; }
                e.Last = m.StartUtc;
                map[p.Id] = e;
            }
        }
        return map.Select(kv => new Encounter(kv.Key, kv.Value.Name, kv.Value.Agent ?? "", kv.Value.Tier, kv.Value.With, kv.Value.WithWins, kv.Value.Against, kv.Value.AgainstWins, kv.Value.Last))
            .OrderByDescending(e => e.With + e.Against).ThenByDescending(e => e.Last).ToList();
    }

    /// <summary>A map's record for one player, with rounds won on attack and on defense.</summary>
    public sealed record MapDetail(string Map, int Wins, int Losses, int AtkWon, int AtkPlayed, int DefWon, int DefPlayed, PlayerTotals Totals)
    {
        public int Matches => Wins + Losses;
        public double WinPct => Matches > 0 ? 100.0 * Wins / Matches : double.NaN;
        public double AtkPct => AtkPlayed > 0 ? 100.0 * AtkWon / AtkPlayed : double.NaN;
        public double DefPct => DefPlayed > 0 ? 100.0 * DefWon / DefPlayed : double.NaN;
    }

    public static List<MapDetail> MapDetails(IEnumerable<VMatch> matches, string id)
    {
        var list = new List<MapDetail>();
        foreach (var g in matches.Where(m => m.Map.Length > 0 && m.Players.Any(p => p.Id == id)).GroupBy(m => m.Map))
        {
            int w = 0, l = 0, aw = 0, ap = 0, dw = 0, dp = 0;
            var tot = new PlayerTotals();
            foreach (var m in g)
            {
                var team = m.TeamOf(id);
                int res = m.Outcome(team);
                if (res > 0) w++; else if (res < 0) l++;
                foreach (var r in m.Rounds.Where(r => r.Winner.Length > 0))
                {
                    if (m.Attacking(team, r.N)) { ap++; if (r.Winner == team) aw++; }
                    else { dp++; if (r.Winner == team) dw++; }
                }
                tot.Add(Analysis.Totals(m, id));
            }
            list.Add(new MapDetail(g.Key, w, l, aw, ap, dw, dp, tot));
        }
        return list.OrderByDescending(x => x.Matches).ThenByDescending(x => x.WinPct).ToList();
    }

    /// <summary>One side of a match for one player: kills, assists and deaths on attack or defense.</summary>
    public sealed record SideLine(int Kills, int Assists, int Deaths)
    {
        public double Kd => Deaths > 0 ? (double)Kills / Deaths : Kills;
    }

    public static (SideLine Attack, SideLine Defense) Sides(VMatch m, string id)
    {
        var team = m.TeamOf(id);
        SideLine Of(bool atk)
        {
            var ks = m.Kills.Where(k => m.Attacking(team, k.Round) == atk).ToList();
            return new SideLine(ks.Count(k => k.Killer == id && k.Victim != id && m.TeamOf(k.Victim) != team),
                ks.Count(k => k.Assists.Contains(id)), ks.Count(k => k.Victim == id));
        }
        return (Of(true), Of(false));
    }

    /// <summary>One enemy, from a player's side: kills on them, deaths to them, damage dealt and received.</summary>
    public sealed record VersusLine(VPlayer Enemy, int Kills, int Deaths, int Dealt, int Received);

    public static List<VersusLine> Versus(VMatch m, string id)
    {
        var team = m.TeamOf(id);
        return m.Players.Where(p => p.Team.Length > 0 && p.Team != team).Select(e => new VersusLine(e,
            m.Kills.Count(k => k.Killer == id && k.Victim == e.Id), m.Kills.Count(k => k.Killer == e.Id && k.Victim == id),
            m.Rounds.Sum(r => r.Damage.Where(d => d.From == id && d.To == e.Id).Sum(d => d.Amount)),
            m.Rounds.Sum(r => r.Damage.Where(d => d.From == e.Id && d.To == id).Sum(d => d.Amount))))
            .OrderByDescending(v => v.Kills).ThenBy(v => v.Deaths).ToList();
    }

    /// <summary>Kills per weapon for one player in one match (weapon id, kills), most first.</summary>
    public static List<(string Weapon, string DamageType, int Kills)> WeaponKills(VMatch m, string id) =>
        m.Kills.Where(k => k.Killer == id && k.Victim != id && m.TeamOf(k.Victim) != m.TeamOf(id))
            .GroupBy(k => (k.Weapon ?? "", k.DamageType ?? "")).Select(g => (g.Key.Item1, g.Key.Item2, g.Count())).OrderByDescending(x => x.Item3).ToList();

    /// <summary>A rivalry: how often <see cref="A"/> killed <see cref="B"/> and the other way round.</summary>
    public sealed record Rivalry(VPlayer A, VPlayer B, int AKills, int BKills);

    /// <summary>
    /// The duels tab's three headline pairs: the pair with the most kills between them, the one where Team A's player
    /// most outdid Team B's, and the other way round.
    /// </summary>
    public static (Rivalry Top, Rivalry MismatchA, Rivalry MismatchB) Rivalries(VMatch m, string teamA)
    {
        var d = Duels(m);
        int K(string a, string b) => d.TryGetValue(a, out var row) ? row.GetValueOrDefault(b) : 0;
        var pairs = (from a in m.Players.Where(p => p.Team == teamA)
                     from b in m.Players.Where(p => p.Team.Length > 0 && p.Team != teamA)
                     select new Rivalry(a, b, K(a.Id, b.Id), K(b.Id, a.Id))).Where(r => r.AKills + r.BKills > 0).ToList();
        if (pairs.Count == 0) return (null, null, null);
        return (pairs.OrderByDescending(r => r.AKills + r.BKills).ThenBy(r => Math.Abs(r.AKills - r.BKills)).First(),
            pairs.OrderByDescending(r => r.AKills - r.BKills).ThenByDescending(r => r.AKills).First(),
            pairs.OrderByDescending(r => r.BKills - r.AKills).ThenByDescending(r => r.BKills).First());
    }

    /// <summary>A player's Riot ID when the match has it, else null.</summary>
    public static string NameOf(VMatch m, string id) =>
        m.Players.FirstOrDefault(p => p.Id == id)?.Name is { Length: > 0 } n ? n : m.Names != null && m.Names.TryGetValue(id, out var x) ? x : null;
}
