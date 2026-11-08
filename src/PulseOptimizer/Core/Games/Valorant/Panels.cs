namespace Pulse.Core.Games.Valorant;

/// <summary>
/// The tracker's detail panels, worked out from saved matches: accuracy, weapons, roles, maps, agents, and a full
/// scoreboard for one match. Every number comes from Riot's match data; nothing is estimated.
/// </summary>
public static class Panels
{
    public sealed record Accuracy(int Head, int Body, int Legs, List<double> HsPerMatch)
    {
        public int Hits => Head + Body + Legs;
        public double Pct(int n) => Hits > 0 ? 100.0 * n / Hits : double.NaN;
    }

    /// <summary>Where shots landed over the last <paramref name="last"/> matches, and the headshot rate in each (oldest first).</summary>
    public static Accuracy AccuracyOf(IEnumerable<VMatch> matches, string id, int last = 20)
    {
        int h = 0, b = 0, l = 0;
        var per = new List<double>();
        foreach (var m in matches.Take(last).Reverse())
        {
            var t = Analysis.Totals(m, id);
            h += t.Head; b += t.Body; l += t.Legs;
            if (t.Head + t.Body + t.Legs >= 10) per.Add(t.HeadshotPct);
        }
        return new Accuracy(h, b, l, per);
    }

    public sealed record WeaponLine(string Id, string Name, string Category, int Kills, double AvgMeters, double Share);

    /// <summary>Kills per weapon (abilities and the spike left out), with how far away the kills were, most kills first.</summary>
    public static List<WeaponLine> Weapons(IEnumerable<VMatch> matches, string id)
    {
        var by = new Dictionary<string, (int Kills, double Dist, int DistN)>(StringComparer.OrdinalIgnoreCase);
        int total = 0;
        foreach (var m in matches)
            foreach (var k in m.Kills)
            {
                if (k.Killer != id || k.Victim == id || m.TeamOf(k.Victim) == m.TeamOf(id)) continue;
                if (!k.DamageType.Equals("Weapon", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(k.Weapon)) continue;
                total++;
                var e = by.GetValueOrDefault(k.Weapon);
                e.Kills++;
                if (k.At(id) is { } from) { e.Dist += from.DistanceTo(k.VictimAt) / 100.0; e.DistN++; }
                by[k.Weapon] = e;
            }
        return by.Select(kv => new WeaponLine(kv.Key, MapGeo.Weapon(kv.Key) ?? "Weapon", MapGeo.WeaponCategory(kv.Key), kv.Value.Kills,
                kv.Value.DistN > 0 ? kv.Value.Dist / kv.Value.DistN : double.NaN, total > 0 ? 100.0 * kv.Value.Kills / total : 0))
            .OrderByDescending(w => w.Kills).ToList();
    }

    public sealed record RoleLine(string Role, int Wins, int Losses, int Kills, int Deaths, int Assists)
    {
        public double WinPct => Wins + Losses > 0 ? 100.0 * Wins / (Wins + Losses) : double.NaN;
        public double Kda => Deaths > 0 ? (double)(Kills + Assists) / Deaths : Kills + Assists;
    }

    public static List<RoleLine> Roles(IEnumerable<VMatch> matches, string id)
    {
        var by = new Dictionary<string, RoleLine>();
        foreach (var m in matches)
        {
            var p = m.Players.FirstOrDefault(x => x.Id == id);
            if (p == null) continue;
            var role = Analysis.RoleOf(p.Agent);
            if (role.Length == 0) continue;
            bool won = WonBy(m, p.Team);
            var r = by.GetValueOrDefault(role) ?? new RoleLine(role, 0, 0, 0, 0, 0);
            by[role] = r with { Wins = r.Wins + (won ? 1 : 0), Losses = r.Losses + (won ? 0 : 1), Kills = r.Kills + p.Kills, Deaths = r.Deaths + p.Deaths, Assists = r.Assists + p.Assists };
        }
        return by.Values.OrderByDescending(r => r.Wins + r.Losses).ToList();
    }

    public sealed record MapLine(string Map, int Wins, int Losses)
    {
        public int Games => Wins + Losses;
        public double WinPct => Games > 0 ? 100.0 * Wins / Games : double.NaN;
    }

    public static List<MapLine> Maps(IEnumerable<VMatch> matches, string id)
    {
        var by = new Dictionary<string, (int W, int L)>();
        foreach (var m in matches)
        {
            if (string.IsNullOrEmpty(m.Map) || m.TeamOf(id) is not { Length: > 0 } team) continue;
            var e = by.GetValueOrDefault(m.Map);
            if (WonBy(m, team)) e.W++; else e.L++;
            by[m.Map] = e;
        }
        return by.Select(kv => new MapLine(kv.Key, kv.Value.W, kv.Value.L)).OrderByDescending(x => x.Games).ThenByDescending(x => x.WinPct).ToList();
    }

    public sealed record AgentLine(string Agent, int Matches, int Wins, long PlayMs, PlayerTotals Totals, double Score, string BestMap, double BestMapWin)
    {
        public double WinPct => Matches > 0 ? 100.0 * Wins / Matches : double.NaN;
    }

    public static List<AgentLine> Agents(IEnumerable<VMatch> matches, string id)
    {
        var list = new List<AgentLine>();
        foreach (var g in matches.Where(m => m.Players.Any(p => p.Id == id)).GroupBy(m => m.Players.First(p => p.Id == id).Agent).Where(g => g.Key.Length > 0))
        {
            var t = new PlayerTotals();
            foreach (var m in g) t.Add(Analysis.Totals(m, id));
            var scores = g.Select(m => Tracker.MatchScore(m, id)).Where(x => !double.IsNaN(x)).ToList();
            var maps = Maps(g, id);
            var best = maps.Where(x => x.Games >= 2).OrderByDescending(x => x.WinPct).FirstOrDefault() ?? maps.OrderByDescending(x => x.WinPct).FirstOrDefault();
            list.Add(new AgentLine(g.Key, g.Count(), t.Wins, g.Sum(m => m.LengthMs), t, scores.Count > 0 ? Math.Round(scores.Average()) : double.NaN,
                best?.Map ?? "", best?.WinPct ?? double.NaN));
        }
        return list.OrderByDescending(a => a.Matches).ToList();
    }

    // ───────── one match ─────────

    public sealed record BoardLine(VPlayer Player, PlayerTotals Totals, double Score);

    public sealed record Board(VMatch Match, List<BoardLine> Lines, int TeamScore, int EnemyScore, string Team, double AvgTier);

    /// <summary>Everyone in one match, best first in each team, with the team of <paramref name="id"/> on top.</summary>
    public static Board BoardOf(VMatch m, string id)
    {
        var team = m.TeamOf(id);
        if (team.Length == 0) team = m.Players.FirstOrDefault()?.Team ?? "";
        var lines = m.Players.Select(p => new BoardLine(p, Analysis.Totals(m, p.Id), Tracker.MatchScore(m, p.Id)))
            .OrderBy(l => l.Player.Team == team ? 0 : 1).ThenByDescending(l => l.Totals.Acs).ToList();
        var ranked = m.Players.Where(p => p.Tier >= 3).ToList();
        return new Board(m, lines, m.Rounds.Count(r => r.Winner == team), m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != team), team,
            ranked.Count > 0 ? ranked.Average(p => p.Tier) : 0);
    }

    static bool WonBy(VMatch m, string team) => m.Rounds.Count(r => r.Winner == team) > m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != team);
}
