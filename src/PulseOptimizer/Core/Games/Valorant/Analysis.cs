namespace Pulse.Core.Games.Valorant;

/// <summary>What a death looked like, worked out from where everyone stood when it happened.</summary>
public enum DeathTag
{
    /// <summary>The first death of the round.</summary>
    FirstDeath,
    /// <summary>A teammate was alive but nobody killed your killer within 5 seconds.</summary>
    NotTraded,
    /// <summary>No teammate within 15 m.</summary>
    Alone,
    /// <summary>Two or more enemies within 20 m.</summary>
    Outnumbered,
    /// <summary>No ability used that round (only when Riot sends per-round ability data).</summary>
    NoAbility,
    /// <summary>You were the last one alive on your team.</summary>
    LastAlive,
    /// <summary>You had a much cheaper loadout than your killer.</summary>
    Outgunned,
    /// <summary>A teammate killed your killer within 5 seconds.</summary>
    Traded,
}

public sealed record DeathInfo(
    VMatch Match, VKill Kill, bool Attacking, List<DeathTag> Tags, DeathTag Main,
    double NearestMateM, int EnemiesNear, string KillerAgent, string KillerWeapon)
{
    /// <summary>Seconds into the round, counted from when the barriers dropped (an estimate; see <see cref="Analysis.RoundClockMs"/>).</summary>
    public double RoundSeconds => Analysis.RoundClockMs(Match, Kill) / 1000.0;
}

/// <summary>Totals for one player (or a group of players) over some matches.</summary>
public sealed class PlayerTotals
{
    public int Matches, Wins, Rounds, Kills, Deaths, Assists, Damage, Head, Body, Legs;
    /// <summary>Damage taken from enemies (for the damage delta).</summary>
    public int DamageTaken;
    public int KastRounds, FirstKills, FirstDeaths, TradeKills;
    /// <summary>Deaths where a teammate was still alive (the ones that could have been traded).</summary>
    public int DeathsWithMates, Untraded;
    /// <summary>Deaths where positions were known (and a teammate was alive), and how many of those had nobody close.</summary>
    public int DeathsPlaced, Alone, Outnumbered;
    /// <summary>Deaths in rounds with per-round ability data, and how many of them came before any ability was used.</summary>
    public int DeathsAbilityKnown, NoAbility;
    /// <summary>Ability uses (C, Q, E, X) and the rounds they were counted over.</summary>
    public int[] Casts = new int[4];
    public int CastRounds;
    /// <summary>Rounds where you were the last one alive against at least one enemy, and how many of those you won.</summary>
    public int ClutchChances, ClutchWins;
    /// <summary>Kills (from the kill feed) and how many of them were with a sniper (Operator, Marshal, Outlaw).</summary>
    public int FeedKills, SniperKills;
    /// <summary>Riot's combat score, added up (ACS = this per round).</summary>
    public int Score;

    public void Add(PlayerTotals o)
    {
        Matches += o.Matches; Wins += o.Wins; Rounds += o.Rounds; Kills += o.Kills; Deaths += o.Deaths; Assists += o.Assists;
        Damage += o.Damage; DamageTaken += o.DamageTaken; Head += o.Head; Body += o.Body; Legs += o.Legs; KastRounds += o.KastRounds;
        FirstKills += o.FirstKills; FirstDeaths += o.FirstDeaths; TradeKills += o.TradeKills;
        DeathsWithMates += o.DeathsWithMates; Untraded += o.Untraded; DeathsPlaced += o.DeathsPlaced; Alone += o.Alone; Outnumbered += o.Outnumbered;
        DeathsAbilityKnown += o.DeathsAbilityKnown; NoAbility += o.NoAbility;
        ClutchChances += o.ClutchChances; ClutchWins += o.ClutchWins; Score += o.Score;
        FeedKills += o.FeedKills; SniperKills += o.SniperKills;
        for (int i = 0; i < 4; i++) Casts[i] += o.Casts[i];
        CastRounds += o.CastRounds;
    }

    static double Div(double a, double b) => b > 0 ? a / b : double.NaN;
    public double Adr => Div(Damage, Rounds);
    /// <summary>Damage delta per round: damage dealt minus damage taken (DDΔ on trackers).</summary>
    public double DamageDelta => Div(Damage - DamageTaken, Rounds);
    /// <summary>First kills minus first deaths, per 100 rounds.</summary>
    public double OpeningNetPer100 => Div(100.0 * (FirstKills - FirstDeaths), Rounds);
    public double HeadshotPct => Div(100.0 * Head, Head + Body + Legs);
    public double KastPct => Div(100.0 * KastRounds, Rounds);
    public double FirstDeathsPer100 => Div(100.0 * FirstDeaths, Rounds);
    public double FirstKillsPer100 => Div(100.0 * FirstKills, Rounds);
    public double OpeningWinPct => Div(100.0 * FirstKills, FirstKills + FirstDeaths);
    public double UntradedPct => Div(100.0 * Untraded, DeathsWithMates);
    public double AlonePct => Div(100.0 * Alone, DeathsPlaced);
    public double OutnumberedPct => Div(100.0 * Outnumbered, DeathsPlaced);
    public double TradeKillsPer100 => Div(100.0 * TradeKills, Rounds);
    public double NoAbilityPct => Div(100.0 * NoAbility, DeathsAbilityKnown);
    public double KillsPerRound => Div(Kills, Rounds);

    /// <summary>Share of kills made with a sniper (0–1).</summary>
    public double SniperShare => FeedKills > 0 ? (double)SniperKills / FeedKills : 0;

    /// <summary>
    /// Aim, 50 = a typical player: headshot rate, damage per round and kills per round together, so it isn't only
    /// headshots. Headshots count less the more of the kills are with a sniper, since an Operator kills with a body
    /// shot and lowers headshot rate without meaning worse aim. Typical values used to put them on one scale:
    /// 25% headshots, 140 damage and 0.75 kills a round.
    /// </summary>
    public double AimRating
    {
        get
        {
            if (Rounds == 0 || double.IsNaN(HeadshotPct)) return double.NaN;
            double wHs = 0.45 * (1 - SniperShare), wAdr = 0.30, wKpr = 0.25;
            double v = wHs * HeadshotPct / 25 + wAdr * Adr / 140 + wKpr * KillsPerRound / 0.75;
            return 50 * v / (wHs + wAdr + wKpr);
        }
    }
    public double WinPct => Div(100.0 * Wins, Matches);
    public double CastsPerRound(int slot) => Div(Casts[slot], CastRounds);
    public double AllCastsPerRound => Div(Casts.Sum(), CastRounds);
    public double ClutchWinPct => Div(100.0 * ClutchWins, ClutchChances);
    /// <summary>Average combat score per round, the number VALORANT's own scoreboard ranks players by.</summary>
    public double Acs => Div(Score, Rounds);
    public double KdRatio => Deaths > 0 ? (double)Kills / Deaths : Kills;
}

/// <summary>
/// Works out what happened in your matches from Riot's own match data: who traded whom, who died first, who was
/// alone, who was outnumbered. Every number is counted the same way for you and for the other players in your
/// matches, so the coach compares you with players at your own level.
/// </summary>
public static class Analysis
{
    /// <summary>A trade: your killer dies to one of your teammates within this long.</summary>
    public const long TradeWindowMs = 5000;
    /// <summary>"Alone": no teammate within 15 m (game units are centimetres).</summary>
    public const double AloneCm = 1500;
    /// <summary>"Outnumbered": two or more enemies within 20 m.</summary>
    public const double CloseCm = 2000;

    public static readonly Dictionary<string, string> Roles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Jett"] = "Duelist", ["Phoenix"] = "Duelist", ["Reyna"] = "Duelist", ["Raze"] = "Duelist", ["Yoru"] = "Duelist",
        ["Neon"] = "Duelist", ["Iso"] = "Duelist", ["Waylay"] = "Duelist",
        ["Sova"] = "Initiator", ["Breach"] = "Initiator", ["Skye"] = "Initiator", ["KAY/O"] = "Initiator", ["Fade"] = "Initiator",
        ["Gekko"] = "Initiator", ["Tejo"] = "Initiator",
        ["Brimstone"] = "Controller", ["Viper"] = "Controller", ["Omen"] = "Controller", ["Astra"] = "Controller",
        ["Harbor"] = "Controller", ["Clove"] = "Controller",
        ["Sage"] = "Sentinel", ["Cypher"] = "Sentinel", ["Killjoy"] = "Sentinel", ["Chamber"] = "Sentinel",
        ["Deadlock"] = "Sentinel", ["Vyse"] = "Sentinel", ["Veto"] = "Sentinel",
    };

    public static string RoleOf(string agent) => Roles.TryGetValue(agent ?? "", out var r) ? r : "";

    /// <summary>
    /// Milliseconds since the barriers dropped. Riot's round time either starts at the buy phase or at the barrier
    /// drop depending on the data; when no kill in the whole match happened in the first 30 seconds of "round time",
    /// the buy phase is in it, and it's taken off (45 s in the first round of each half and overtime, 30 s otherwise).
    /// </summary>
    public static long RoundClockMs(VMatch m, VKill k)
    {
        if (!IncludesBuyPhase(m)) return Math.Max(0, k.RoundMs);
        int h = m.HalfLength;
        long buy = k.Round == 0 || k.Round == h || k.Round == 2 * h ? 45000 : 30000;
        return Math.Max(0, k.RoundMs - buy);
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VMatch, object> _buy = new();

    internal static bool IncludesBuyPhase(VMatch m)
    {
        if (_buy.TryGetValue(m, out var v)) return (bool)v;
        var firsts = m.Kills.GroupBy(k => k.Round).Select(g => g.Min(k => k.RoundMs)).ToList();
        bool yes = m.Queue != "spikerush" && firsts.Count >= 3 && firsts.Min() >= 30000;
        _buy.AddOrUpdate(m, yes);
        return yes;
    }

    /// <summary>"Early" (first 25 s), "Mid", "Late" or "Post-plant" for an enemy position at a kill.</summary>
    public static string Phase(VMatch m, VKill k)
    {
        var r = m.Rounds.FirstOrDefault(x => x.N == k.Round);
        if (r != null && r.PlantMs > 0 && k.RoundMs >= r.PlantMs) return "Post-plant";
        var s = RoundClockMs(m, k) / 1000.0;
        return s < 25 ? "Early" : s < 60 ? "Mid" : "Late";
    }

    static bool Traded(VMatch m, VKill death, List<VKill> roundKills)
    {
        var team = m.TeamOf(death.Victim);
        return roundKills.Any(k => k.Victim == death.Killer && k.GameMs > death.GameMs && k.GameMs - death.GameMs <= TradeWindowMs && m.TeamOf(k.Killer) == team);
    }

    /// <summary>Each of a player's deaths in a match, with what it looked like.</summary>
    public static List<DeathInfo> Deaths(VMatch m, string who)
    {
        var list = new List<DeathInfo>();
        var team = m.TeamOf(who);
        var byRound = m.Kills.GroupBy(k => k.Round).ToDictionary(g => g.Key, g => g.OrderBy(k => k.GameMs).ToList());
        foreach (var k in m.Kills.Where(k => k.Victim == who && k.Killer != who))
        {
            var rk = byRound[k.Round];
            var tags = new List<DeathTag>();
            if (rk[0] == k) tags.Add(DeathTag.FirstDeath);
            var mates = k.Pos.Where(p => p.Id != who && m.TeamOf(p.Id) == team).ToList();
            var enemies = k.Pos.Where(p => m.TeamOf(p.Id) is { Length: > 0 } t && t != team).ToList();
            var at = k.VictimAt;
            double nearest = mates.Count > 0 ? mates.Min(p => at.DistanceTo(new Pt(p.X, p.Y))) / 100.0 : double.NaN;
            int close = enemies.Count(p => at.DistanceTo(new Pt(p.X, p.Y)) <= CloseCm);
            bool placed = k.Pos.Count > 0;
            bool traded = Traded(m, k, rk);
            if (placed && mates.Count == 0) tags.Add(DeathTag.LastAlive);
            else if (traded) tags.Add(DeathTag.Traded);
            else tags.Add(DeathTag.NotTraded);
            if (placed && mates.Count > 0 && nearest * 100 > AloneCm) tags.Add(DeathTag.Alone);
            if (placed && close >= 2) tags.Add(DeathTag.Outnumbered);
            var round = m.Rounds.FirstOrDefault(r => r.N == k.Round);
            if (round?.UsedAbility != null && !round.UsedAbility.Contains(who)) tags.Add(DeathTag.NoAbility);
            if (round != null && round.Eco.TryGetValue(who, out var mine) && round.Eco.TryGetValue(k.Killer, out var theirs)
                && mine.Loadout < 2000 && theirs.Loadout >= 3900) tags.Add(DeathTag.Outgunned);
            list.Add(new DeathInfo(m, k, m.Attacking(team, k.Round), tags, MainCause(tags), nearest, close,
                m.Players.FirstOrDefault(p => p.Id == k.Killer)?.Agent ?? "", k.Weapon));
        }
        return list;
    }

    /// <summary>The one cause a death is filed under: the most costly mistake first, then the situations.</summary>
    public static DeathTag MainCause(List<DeathTag> t)
    {
        foreach (var c in new[] { DeathTag.FirstDeath, DeathTag.Outnumbered, DeathTag.Alone, DeathTag.NoAbility, DeathTag.NotTraded, DeathTag.Outgunned, DeathTag.LastAlive, DeathTag.Traded })
            if (t.Contains(c) && !(c == DeathTag.FirstDeath && t.Contains(DeathTag.Traded))) return c;
        return t.Count > 0 ? t[0] : DeathTag.NotTraded;
    }

    /// <summary>One player's totals in one match.</summary>
    public static PlayerTotals Totals(VMatch m, string who)
    {
        var t = new PlayerTotals();
        var me = m.Players.FirstOrDefault(p => p.Id == who);
        if (me == null) return t;
        var team = me.Team;
        var rounds = m.Rounds.Select(r => r.N).ToList();
        var byRound = m.Kills.GroupBy(k => k.Round).ToDictionary(g => g.Key, g => g.OrderBy(k => k.GameMs).ToList());
        t.Matches = 1;
        t.Wins = m.Rounds.Count(r => r.Winner == team) > m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != team) ? 1 : 0;
        t.Rounds = rounds.Count;
        foreach (var r in m.Rounds)
        {
            var rk = byRound.TryGetValue(r.N, out var l) ? l : new List<VKill>();
            bool k = rk.Any(x => x.Killer == who && x.Victim != who && m.TeamOf(x.Victim) != team);
            bool a = rk.Any(x => x.Assists.Contains(who));
            var death = rk.FirstOrDefault(x => x.Victim == who);
            bool s = death == null;
            bool tr = death != null && Traded(m, death, rk);
            if (k || a || s || tr) t.KastRounds++;
            if (rk.Count > 0 && rk[0].Killer == who && m.TeamOf(rk[0].Victim) != team) t.FirstKills++;
            if (rk.Count > 0 && rk[0].Victim == who) t.FirstDeaths++;
            // Trade kills: you killed someone within 5 s of them killing a teammate.
            foreach (var x in rk.Where(x => x.Killer == who && m.TeamOf(x.Victim) != team))
                if (rk.Any(y => y.Killer == x.Victim && y.Victim != who && m.TeamOf(y.Victim) == team && x.GameMs > y.GameMs && x.GameMs - y.GameMs <= TradeWindowMs))
                    t.TradeKills++;
            // Clutches: you became the last one alive with at least one enemy still up.
            {
                var alive = new HashSet<string>(m.Players.Select(p => p.Id));
                foreach (var x in rk)
                {
                    alive.Remove(x.Victim);
                    if (!alive.Contains(who)) break;
                    int mates = alive.Count(id => id != who && m.TeamOf(id) == team), enemies = alive.Count(id => m.TeamOf(id) is { Length: > 0 } tt && tt != team);
                    if (mates == 0 && enemies > 0)
                    {
                        t.ClutchChances++;
                        if (r.Winner == team) t.ClutchWins++;
                        break;
                    }
                }
            }
            foreach (var d in r.Damage.Where(d => d.From == who && m.TeamOf(d.To) != team))
            {
                t.Damage += d.Amount; t.Head += d.Head; t.Body += d.Body; t.Legs += d.Legs;
            }
            foreach (var d in r.Damage.Where(d => d.To == who && m.TeamOf(d.From) is { Length: > 0 } ft && ft != team)) t.DamageTaken += d.Amount;
        }
        // Kills and deaths as on Riot's own scoreboard (what trackers show); the kill feed only when the scoreboard is empty.
        int feedKills = m.Kills.Count(x => x.Killer == who && x.Victim != who && m.TeamOf(x.Victim) != team), feedDeaths = m.Kills.Count(x => x.Victim == who);
        t.FeedKills = feedKills;
        t.SniperKills = m.Kills.Count(x => x.Killer == who && x.Victim != who && m.TeamOf(x.Victim) != team && MapGeo.WeaponCategory(x.Weapon) == "Sniper");
        t.Kills = me.Kills + me.Deaths > 0 ? me.Kills : feedKills;
        t.Deaths = me.Kills + me.Deaths > 0 ? me.Deaths : feedDeaths;
        t.Assists = me.Assists;
        t.Score = me.Score;
        foreach (var d in Deaths(m, who))
        {
            if (d.Tags.Contains(DeathTag.LastAlive)) continue;
            if (d.Kill.Pos.Count > 0 || d.Tags.Contains(DeathTag.Traded) || d.Tags.Contains(DeathTag.NotTraded))
            {
                t.DeathsWithMates++;
                if (d.Tags.Contains(DeathTag.NotTraded)) t.Untraded++;
            }
            if (d.Kill.Pos.Count > 0)
            {
                t.DeathsPlaced++;
                if (d.Tags.Contains(DeathTag.Alone)) t.Alone++;
                if (d.Tags.Contains(DeathTag.Outnumbered)) t.Outnumbered++;
            }
            if (m.Rounds.FirstOrDefault(r => r.N == d.Kill.Round)?.UsedAbility != null)
            {
                t.DeathsAbilityKnown++;
                if (d.Tags.Contains(DeathTag.NoAbility)) t.NoAbility++;
            }
        }
        if (me.Casts is { Length: 4 } c && me.Rounds > 0)
        {
            for (int i = 0; i < 4; i++) t.Casts[i] = c[i];
            t.CastRounds = me.Rounds;
        }
        return t;
    }

    /// <summary>One player's totals over the matches they're in.</summary>
    public static PlayerTotals For(IEnumerable<VMatch> matches, string who)
    {
        var t = new PlayerTotals();
        foreach (var m in matches.Where(m => m.Players.Any(p => p.Id == who))) t.Add(Totals(m, who));
        return t;
    }

    /// <summary>Your totals over these matches.</summary>
    public static PlayerTotals Mine(IEnumerable<VMatch> matches, Func<VMatch, bool> filter = null)
    {
        var t = new PlayerTotals();
        foreach (var m in matches.Where(m => filter == null || filter(m))) t.Add(Totals(m, m.Me));
        return t;
    }

    /// <summary>
    /// The other nine players in your matches, added up: the "players at your level" every number is compared with.
    /// <paramref name="who"/> can narrow it to, say, players on the same agent or role.
    /// </summary>
    public static PlayerTotals Lobby(IEnumerable<VMatch> matches, Func<VMatch, VPlayer, bool> who = null)
    {
        var t = new PlayerTotals();
        foreach (var m in matches)
            foreach (var p in m.Players.Where(p => p.Id != m.Me && (who == null || who(m, p))))
                t.Add(Totals(m, p.Id));
        return t;
    }

    /// <summary>A weapon's name from its id (from the game's own list, embedded at build time).</summary>
    public static string WeaponName(VKill k)
    {
        if (k.DamageType == "Bomb") return "the spike";
        if (k.DamageType == "Fall") return "a fall";
        if (k.DamageType == "Melee") return "a knife";
        if (k.Weapon is "Ultimate") return "an ultimate";
        if (k.Weapon is "Ability1" or "Ability2" or "GrenadeAbility" || k.DamageType == "Ability") return "an ability";
        return MapGeo.Weapon(k.Weapon) ?? "";
    }
}
