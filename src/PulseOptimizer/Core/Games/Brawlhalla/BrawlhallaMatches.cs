using System.Text.Json;

namespace Pulse.Core.Games.Brawlhalla;

/// <summary>One logged match (or a few, when they came in between two checks): the legend, the result and the numbers.</summary>
public sealed class BhMatch
{
    public DateTime Time { get; set; }
    public int LegendId { get; set; }
    public int Games { get; set; }
    public int Wins { get; set; }
    public int Kos { get; set; }
    public int Falls { get; set; }
    public int Suicides { get; set; }
    public int TeamKos { get; set; }
    public long Damage { get; set; }
    public long Taken { get; set; }
    public long Seconds { get; set; }
    public long Xp { get; set; }
    /// <summary>"1v1", "2v2", "3v3", or "" for everything else (casual, brawls, custom).</summary>
    public string Mode { get; set; } = "";
    public string Partner { get; set; } = "";
    public int RatingBefore { get; set; }
    public int RatingAfter { get; set; }

    public int Losses => Games - Wins;
    public bool Ranked => Mode.Length > 0;
    public int RatingChange => RatingBefore > 0 && RatingAfter > 0 ? RatingAfter - RatingBefore : 0;
}

/// <summary>A run of matches with less than an hour between them.</summary>
public sealed record BhSession(DateTime Start, DateTime End, List<BhMatch> Matches)
{
    public int Games => Matches.Sum(m => m.Games);
    public int Wins => Matches.Sum(m => m.Wins);
    public int Kos => Matches.Sum(m => m.Kos);
    public int Falls => Matches.Sum(m => m.Falls);
    public long Damage => Matches.Sum(m => m.Damage);
    public long Xp => Matches.Sum(m => m.Xp);
    public long Seconds => Matches.Sum(m => m.Seconds);
    /// <summary>Ranked 1v1 rating change across the session, per legend, added up.</summary>
    public int RatingChange => Matches.Where(m => m.Mode == "1v1").Sum(m => m.RatingChange);
}

/// <summary>How much a player looks like a smurf (0–100) and the signs behind it, in words (English, for Loc).</summary>
public sealed record BhSmurf(int Score, List<(string Format, object[] Args)> Signs)
{
    public string Label => Score >= 60 ? "Likely smurf" : Score >= 35 ? "Possible smurf" : Score >= 15 ? "Unusual, inconclusive" : "Consistent with rank";
}

/// <summary>
/// Brawlhalla's API has no match history, only running totals. Pulse keeps the last totals it saw for a player and,
/// next time, turns the difference into matches: which legend gained games, wins, KOs and damage, and whether ranked
/// 1v1 (that legend's rating), a 2v2 team or 3v3 gained games too. For you, a check every minute while Brawlhalla is
/// open logs each match as it ends. Saved on this PC only.
/// </summary>
public static class BrawlhallaMatches
{
    const int Keep = 400;
    static readonly object Gate = new();
    static string Dir => Path.Combine(TweakEngine.DataDir, "brawlhalla");
    static string MatchFile(long id) => Path.Combine(Dir, $"matches-{id}.json");
    static string SnapFile(long id) => Path.Combine(Dir, $"snap-{id}.json");

    // ───────── the totals Pulse keeps ─────────

    public sealed class Snap
    {
        public DateTime Time { get; set; }
        public int Games { get; set; }
        public int Wins { get; set; }
        public long Xp { get; set; }
        public List<SnapLegend> Legends { get; set; } = new();
        public SnapRanked One { get; set; }
        public SnapRanked Three { get; set; }
        public List<SnapTeam> Teams { get; set; } = new();
    }

    public sealed class SnapLegend
    {
        public int Id { get; set; }
        public int Games { get; set; }
        public int Wins { get; set; }
        public int Kos { get; set; }
        public int Falls { get; set; }
        public int Suicides { get; set; }
        public int TeamKos { get; set; }
        public long Damage { get; set; }
        public long Taken { get; set; }
        public long Time { get; set; }
        public long Xp { get; set; }
        public int RankedGames { get; set; }
        public int Rating { get; set; }
    }

    public sealed class SnapRanked { public int Games { get; set; } public int Wins { get; set; } public int Rating { get; set; } }
    public sealed class SnapTeam { public long Partner { get; set; } public string Name { get; set; } = ""; public int Games { get; set; } public int Wins { get; set; } public int Rating { get; set; } }

    public static Snap SnapOf(BhProfile p, DateTime time) => new()
    {
        Time = time, Games = p.Games, Wins = p.Wins, Xp = p.Xp,
        Legends = p.Legends.Where(l => l.Games > 0).Select(l => new SnapLegend
        {
            Id = l.LegendId, Games = l.Games, Wins = l.Wins, Kos = l.Kos, Falls = l.Falls, Suicides = l.Suicides, TeamKos = l.TeamKos,
            Damage = l.DamageDealt, Taken = l.DamageTaken, Time = l.MatchTime, Xp = l.Xp, RankedGames = l.RankedGames, Rating = l.Rating,
        }).ToList(),
        One = p.OneVsOne is { } o ? new SnapRanked { Games = o.Games, Wins = o.Wins, Rating = o.Rating } : null,
        Three = p.ThreeVsThree is { } t ? new SnapRanked { Games = t.Games, Wins = t.Wins, Rating = t.Rating } : null,
        Teams = p.Teams.Select(t => new SnapTeam { Partner = t.PartnerId, Name = t.Partner, Games = t.Games, Wins = t.Wins, Rating = t.Rating }).ToList(),
    };

    /// <summary>
    /// The matches between two sets of totals: one entry per legend that gained games. Ranked games are matched to
    /// the legend whose ranked count went up (1v1), or to a legend that played when a 2v2 team or 3v3 did.
    /// </summary>
    public static List<BhMatch> Diff(Snap before, Snap after)
    {
        var list = new List<BhMatch>();
        if (before == null || after == null || after.Games <= before.Games) return list;
        // A season reset or a different account: totals went down somewhere. Start again from these.
        if (after.Wins < before.Wins) return list;
        var old = before.Legends.ToDictionary(l => l.Id);
        foreach (var l in after.Legends)
        {
            old.TryGetValue(l.Id, out var o);
            o ??= new SnapLegend { Id = l.Id };
            int games = l.Games - o.Games;
            if (games <= 0) continue;
            var m = new BhMatch
            {
                Time = after.Time, LegendId = l.Id, Games = games, Wins = Math.Max(0, l.Wins - o.Wins),
                Kos = Math.Max(0, l.Kos - o.Kos), Falls = Math.Max(0, l.Falls - o.Falls), Suicides = Math.Max(0, l.Suicides - o.Suicides),
                TeamKos = Math.Max(0, l.TeamKos - o.TeamKos), Damage = Math.Max(0, l.Damage - o.Damage), Taken = Math.Max(0, l.Taken - o.Taken),
                Seconds = Math.Max(0, l.Time - o.Time), Xp = Math.Max(0, l.Xp - o.Xp),
            };
            if (l.RankedGames > o.RankedGames)
            {
                m.Mode = "1v1";
                m.RatingBefore = o.Rating > 0 ? o.Rating : 0;
                m.RatingAfter = l.Rating;
            }
            list.Add(m);
        }
        // 2v2 and 3v3: the team (or 3v3) that gained games goes to a legend that isn't already a 1v1 entry.
        foreach (var t in after.Teams)
        {
            var o = before.Teams.FirstOrDefault(x => x.Partner == t.Partner);
            if (t.Games <= (o?.Games ?? 0)) continue;
            if (list.FirstOrDefault(m => m.Mode.Length == 0) is { } m)
            {
                m.Mode = "2v2"; m.Partner = t.Name;
                m.RatingBefore = o?.Rating ?? 0; m.RatingAfter = t.Rating;
            }
        }
        if (after.Three is { } three && three.Games > (before.Three?.Games ?? 0) && list.FirstOrDefault(m => m.Mode.Length == 0) is { } m3)
        {
            m3.Mode = "3v3";
            m3.RatingBefore = before.Three?.Rating ?? 0; m3.RatingAfter = three.Rating;
        }
        return list;
    }

    // ───────── on this PC ─────────

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>The matches logged for a player, newest first.</summary>
    public static List<BhMatch> For(long id)
    {
        lock (Gate)
        {
            try
            {
                var f = MatchFile(id);
                if (File.Exists(f)) return (JsonSerializer.Deserialize<List<BhMatch>>(File.ReadAllText(f), Json) ?? new()).OrderByDescending(m => m.Time).ToList();
            }
            catch { }
            return new();
        }
    }

    /// <summary>When Pulse last saw this player's totals (null: never).</summary>
    public static DateTime? LastSeen(long id)
    {
        lock (Gate)
        {
            try { return File.Exists(SnapFile(id)) ? JsonSerializer.Deserialize<Snap>(File.ReadAllText(SnapFile(id)), Json)?.Time : null; }
            catch { return null; }
        }
    }

    /// <summary>
    /// Compares a fresh profile with the last totals for that player, logs the new matches and keeps the new totals.
    /// Returns the new matches (none the first time).
    /// </summary>
    public static List<BhMatch> Record(BhProfile p)
    {
        if (p == null || p.Id <= 0) return new();
        lock (Gate)
        {
            var now = DateTime.Now;
            var snap = SnapOf(p, now);
            Snap before = null;
            try { if (File.Exists(SnapFile(p.Id))) before = JsonSerializer.Deserialize<Snap>(File.ReadAllText(SnapFile(p.Id)), Json); } catch { }
            var fresh = Diff(before, snap);
            try
            {
                Directory.CreateDirectory(Dir);
                // Totals that didn't change aren't worth a write (the check runs every minute).
                if (before == null || fresh.Count > 0 || snap.Games != before.Games || snap.Xp != before.Xp)
                    File.WriteAllText(SnapFile(p.Id), JsonSerializer.Serialize(snap, Json));
                if (fresh.Count > 0)
                {
                    var all = For(p.Id);
                    all.InsertRange(0, fresh);
                    File.WriteAllText(MatchFile(p.Id), JsonSerializer.Serialize(all.Take(Keep).ToList(), Json));
                }
            }
            catch { }
            return fresh;
        }
    }

    /// <summary>Matches grouped into sessions: a new one after an hour without a match.</summary>
    public static List<BhSession> Sessions(IEnumerable<BhMatch> matches)
    {
        var sessions = new List<BhSession>();
        List<BhMatch> cur = null;
        DateTime last = default;
        foreach (var m in matches.OrderByDescending(m => m.Time))
        {
            if (cur == null || last - m.Time > TimeSpan.FromHours(1))
            {
                cur = new List<BhMatch>();
                sessions.Add(new BhSession(m.Time, m.Time, cur));
            }
            cur.Add(m);
            last = m.Time;
        }
        return sessions.Select(s => s with { Start = s.Matches.Min(m => m.Time), End = s.Matches.Max(m => m.Time) }).ToList();
    }

    // ───────── smurf factor ─────────

    /// <summary>
    /// How much an account looks like a second account of a stronger player: a high ranked rating reached with few
    /// games, a low level or little playtime, a win rate well above what the rating should give, and few legends.
    /// A long-standing account (thousands of games) can't be one, so it stays low. One sign alone counts at most 40.
    /// </summary>
    public static BhSmurf Smurf(BhProfile p)
    {
        var signs = new List<(string, object[])>();
        if (p == null) return new BhSmurf(0, signs);
        var one = p.OneVsOne;
        int rating = Math.Max(one?.PeakRating ?? 0, p.Teams.Select(t => t.PeakRating).DefaultIfEmpty(0).Max());
        long hours = p.Legends.Sum(l => l.MatchTime) / 3600;
        int legends = p.Legends.Count(l => l.Games >= 10);
        var parts = new List<int>();

        bool high = rating >= 1680;   // Platinum and up
        if (high && p.Level > 0 && p.Level < 40)
        {
            parts.Add(rating >= 2000 ? 40 : 30);
            signs.Add(("Level {0} account with a {1} peak", new object[] { p.Level, Brawlhalla.TierOf(rating) }));
        }
        if (high && p.Games > 0 && p.Games < 600)
        {
            parts.Add(rating >= 2000 ? 35 : 25);
            signs.Add(("Only {0} games in all, peak {1}", new object[] { p.Games.ToString("N0"), rating.ToString("N0") }));
        }
        if (high && hours < 80)
        {
            parts.Add(20);
            signs.Add(("{0} hours played in all", new object[] { hours }));
        }
        if (one is { Games: >= 20 } && one.Wins * 100 / one.Games is var wr && wr >= 65)
        {
            parts.Add(wr >= 75 ? 30 : 18);
            signs.Add(("Wins {0}% of ranked 1v1 ({1} games)", new object[] { wr, one.Games.ToString("N0") }));
        }
        if (one is { Games: > 0 and < 60 } && one.Rating >= 1680)
        {
            parts.Add(25);
            signs.Add(("Reached {0} in {1} ranked games", new object[] { Brawlhalla.TierOf(one.Rating), one.Games }));
        }
        if (high && legends > 0 && legends <= 3 && p.Games < 1500)
        {
            parts.Add(10);
            signs.Add(("Plays only {0} legends", new object[] { legends }));
        }
        if (p.Games is > 0 and < 1000 && p.Wins * 100 / p.Games is var all && all >= 70 && p.Games >= 50)
        {
            parts.Add(15);
            signs.Add(("Wins {0}% of all games", new object[] { all }));
        }

        int score;
        if (parts.Count == 0) score = 0;
        else if (parts.Count == 1) score = Math.Min(40, parts[0]);
        else score = Math.Min(100, parts.Sum());
        // Thousands of games: a main account, not a smurf, however good.
        if (p.Games >= 3000 || p.Level >= 90) score = Math.Min(score, 10);
        return new BhSmurf(score, signs);
    }
}
