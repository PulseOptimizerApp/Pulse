namespace Pulse.Core.Games.Brawlhalla;

/// <summary>A projected win chance and what it was worked out from.</summary>
public sealed record BhOdds(double Chance, string Basis, int MyRating, int TheirRating);

/// <summary>
/// You against one opponent: the chance to win from both ranked ratings (Brawlhalla's ratings work like Elo: 400
/// points apart is about 10 to 1), or, when either of you has no ranked 1v1 this season, a rough one from both overall
/// win rates. Plus their legends and weapons, best first.
/// </summary>
public static class BrawlhallaMatchup
{
    /// <summary>Elo: the chance the first player beats the second.</summary>
    public static double Elo(int mine, int theirs) => 1.0 / (1.0 + Math.Pow(10, (theirs - mine) / 400.0));

    /// <summary>Log5: two win rates against the field → the chance the first beats the second.</summary>
    public static double Log5(double a, double b)
    {
        a = Math.Clamp(a, 0.02, 0.98); b = Math.Clamp(b, 0.02, 0.98);
        return a * (1 - b) / (a * (1 - b) + b * (1 - a));
    }

    public static BhOdds Odds(BhProfile me, BhProfile them)
    {
        if (me == null || them == null) return null;
        int mine = me.OneVsOne?.Rating ?? 0, theirs = them.OneVsOne?.Rating ?? 0;
        if (mine > 0 && theirs > 0) return new BhOdds(Elo(mine, theirs), "ranked", mine, theirs);
        if (me.Games >= 20 && them.Games >= 20)
            return new BhOdds(Log5(me.Wins / (double)me.Games, them.Wins / (double)them.Games), "winrate", mine, theirs);
        return null;
    }

    /// <summary>Their legends worth knowing: most played first; each with games, win rate and KOs a game.</summary>
    public static List<BhLegendStats> TopLegends(BhProfile p, int count) =>
        p.Legends.Where(l => l.Games > 0).OrderByDescending(l => l.Games).Take(count).ToList();

    /// <summary>The legend they win most with (at least 20 games, or 2% of their games).</summary>
    public static BhLegendStats BestLegend(BhProfile p)
    {
        int floor = Math.Max(20, p.Legends.Sum(l => l.Games) / 50);
        return p.Legends.Where(l => l.Games >= floor).OrderByDescending(l => l.Wins / (double)l.Games).FirstOrDefault();
    }

    /// <summary>Weapons added up across legends: (name, seconds held, KOs, damage), most held first.</summary>
    public static List<(string Weapon, long Time, int Kos, long Damage)> Weapons(BhProfile p, Dictionary<int, BhLegend> legends)
    {
        var by = new Dictionary<string, (long Time, int Kos, long Damage)>();
        void Add(string w, long t, int k, long d)
        {
            if (string.IsNullOrEmpty(w)) return;
            by.TryGetValue(w, out var c);
            by[w] = (c.Time + t, c.Kos + k, c.Damage + d);
        }
        foreach (var l in p.Legends)
            if (legends != null && legends.TryGetValue(l.LegendId, out var info))
            {
                Add(info.WeaponOne, l.TimeWeaponOne, l.KoWeaponOne, l.DamageWeaponOne);
                Add(info.WeaponTwo, l.TimeWeaponTwo, l.KoWeaponTwo, l.DamageWeaponTwo);
            }
        return by.Where(x => x.Value.Time > 0).OrderByDescending(x => x.Value.Time).Select(x => (x.Key, x.Value.Time, x.Value.Kos, x.Value.Damage)).ToList();
    }
}
