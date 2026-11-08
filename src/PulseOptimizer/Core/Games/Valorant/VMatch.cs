using System.IO.Compression;
using System.Text.Json;
using Pulse.Core.Games.Live;

namespace Pulse.Core.Games.Valorant;

/// <summary>A point on a VALORANT map, in the game's own units (centimetres).</summary>
public readonly record struct Pt(double X, double Y)
{
    public double DistanceTo(Pt o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y));
}

public sealed class VPlayer
{
    public string Id { get; set; } = "";
    public string Team { get; set; } = "";
    public string Agent { get; set; } = "";
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int Score { get; set; }
    public int Rounds { get; set; }
    public int Tier { get; set; }
    /// <summary>Ability uses over the whole match: C (grenade slot), Q, E, X. Null when Riot didn't say.</summary>
    public int[] Casts { get; set; }
}

public sealed class VPos
{
    public string Id { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Which way the player was looking (radians), as Riot reports it.</summary>
    public double View { get; set; }
}

public sealed class VKill
{
    public int Round { get; set; }
    public long GameMs { get; set; }
    public long RoundMs { get; set; }
    public string Killer { get; set; } = "";
    public string Victim { get; set; } = "";
    public List<string> Assists { get; set; } = new();
    /// <summary>"Weapon", "Ability", "Bomb", "Melee", "Fall"…</summary>
    public string DamageType { get; set; } = "";
    /// <summary>The weapon's id, or "Ability1" / "Ultimate"… for abilities.</summary>
    public string Weapon { get; set; } = "";
    public double VX { get; set; }
    public double VY { get; set; }
    /// <summary>Where every living player stood at that moment.</summary>
    public List<VPos> Pos { get; set; } = new();

    public Pt VictimAt => new(VX, VY);
    public Pt? At(string id) => Pos.FirstOrDefault(p => p.Id == id) is { } p ? new Pt(p.X, p.Y) : null;
}

public sealed class VEco
{
    public int Loadout { get; set; }
    public int Spent { get; set; }
    public int Remaining { get; set; }
}

public sealed class VDamage
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int Amount { get; set; }
    public int Head { get; set; }
    public int Body { get; set; }
    public int Legs { get; set; }
}

public sealed class VRound
{
    public int N { get; set; }
    public string Winner { get; set; } = "";
    /// <summary>"Elimination", "Detonate", "Defuse", "Surrendered"…</summary>
    public string Result { get; set; } = "";
    public string Planter { get; set; }
    public long PlantMs { get; set; } = -1;
    public string Site { get; set; }
    public Dictionary<string, VEco> Eco { get; set; } = new();
    public List<VDamage> Damage { get; set; } = new();
    /// <summary>Players who used at least one ability this round (only filled when Riot sends per-round ability data).</summary>
    public List<string> UsedAbility { get; set; }
}

/// <summary>
/// One of your VALORANT matches with everything the coach needs: every kill with where all ten players stood, the
/// rounds, damage and economy. Saved compressed in Pulse's data folder (valorant\matches), only on this PC.
/// Player names are never saved; players are kept by Riot's random account id only.
/// </summary>
public sealed class VMatch
{
    public string Id { get; set; } = "";
    public string Map { get; set; } = "";
    public string MapUrl { get; set; } = "";
    public string Queue { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public long LengthMs { get; set; }
    /// <summary>When game time 0 was on this PC's clock (UTC), lined up with VALORANT's log; null when it couldn't be.</summary>
    public DateTime? ZeroUtc { get; set; }
    public string Me { get; set; } = "";
    public List<VPlayer> Players { get; set; } = new();
    public List<VRound> Rounds { get; set; } = new();
    public List<VKill> Kills { get; set; } = new();
    /// <summary>The team that attacked in the first round ("Red" or "Blue").</summary>
    public string FirstAttack { get; set; } = "Red";
    public int Version { get; set; } = 1;
    /// <summary>Riot IDs from the match record, kept in memory only (never saved): for the after-match lobby.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public Dictionary<string, string> Names { get; set; }

    public VPlayer MeP => Players.FirstOrDefault(p => p.Id == Me);
    public string MyTeam => MeP?.Team ?? "";
    public string TeamOf(string id) => Players.FirstOrDefault(p => p.Id == id)?.Team ?? "";
    public bool Won => Rounds.Count(r => r.Winner == MyTeam) > Rounds.Count(r => r.Winner != MyTeam && r.Winner.Length > 0);
    public string MyAgent => MeP?.Agent ?? "";

    /// <summary>Queues the coach uses: the modes played as real rounds of 5 against 5.</summary>
    public static bool Counts(string queue) => queue is "competitive" or "unrated" or "premier" or "swiftplay";

    /// <summary>The local time a moment in the match happened on this PC (exact when lined up with the game's log).</summary>
    public DateTime LocalTime(long gameMs) => ((ZeroUtc ?? StartUtc) + TimeSpan.FromMilliseconds(gameMs)).ToLocalTime();

    /// <summary>Rounds per half: 12 in competitive and unrated, 4 in Swiftplay and Spike Rush.</summary>
    public int HalfLength => Queue is "swiftplay" or "spikerush" ? 4 : 12;

    /// <summary>The attacking team in round <paramref name="n"/> (0-based), from the first half's attackers.</summary>
    public string Attackers(int n)
    {
        string other = FirstAttack == "Red" ? "Blue" : "Red";
        int h = HalfLength;
        if (n < h) return FirstAttack;
        if (n < 2 * h) return other;
        return (n - 2 * h) % 2 == 0 ? FirstAttack : other; // overtime: sides swap every round
    }

    public bool Attacking(string team, int round) => Attackers(round) == team;

    // ───────── reading Riot's match details ─────────

    static readonly string[] Slots = { "grenadeCasts", "ability1Casts", "ability2Casts", "ultimateCasts" };

    public static VMatch Parse(JsonElement root, string me, IReadOnlyDictionary<string, string> agents, IReadOnlyDictionary<string, string> maps)
    {
        var info = root.Get("matchInfo") ?? default;
        if (info.ValueKind != JsonValueKind.Object) return null;
        var m = new VMatch
        {
            Id = info.Str("matchId"), MapUrl = info.Str("mapId"), Map = ValorantMatches.MapName(info.Str("mapId"), maps),
            Queue = info.Str("queueID"), Me = me,
            StartUtc = info.Get("gameStartMillis") is { ValueKind: JsonValueKind.Number } s ? DateTimeOffset.FromUnixTimeMilliseconds(s.GetInt64()).UtcDateTime : DateTime.UtcNow,
            LengthMs = info.Get("gameLengthMillis") is { ValueKind: JsonValueKind.Number } l ? l.GetInt64() : 0,
        };
        if (root.Get("players") is { ValueKind: JsonValueKind.Array } players)
            foreach (var p in players.EnumerateArray())
            {
                var charId = p.Str("characterId");
                int[] casts = null;
                if (p.Get("stats", "abilityCasts") is { ValueKind: JsonValueKind.Object } ac)
                    casts = Slots.Select(k => ac.Int(k)).ToArray();
                m.Players.Add(new VPlayer
                {
                    Id = p.Str("subject"), Team = p.Str("teamId"),
                    Agent = agents != null && agents.TryGetValue(charId, out var a) && !string.IsNullOrEmpty(a) ? a : ValorantMatches.AgentName(charId),
                    Kills = p.Int("stats", "kills"), Deaths = p.Int("stats", "deaths"), Assists = p.Int("stats", "assists"),
                    Score = p.Int("stats", "score"), Rounds = p.Int("stats", "roundsPlayed"), Tier = p.Int("competitiveTier"), Casts = casts,
                });
            }
        if (m.Players.All(p => p.Id != me)) return null;
        if (root.Get("players") is { ValueKind: JsonValueKind.Array } named)
        {
            m.Names = new();
            foreach (var p in named.EnumerateArray())
                if (p.Str("gameName") is { Length: > 0 } gn) m.Names[p.Str("subject")] = gn + "#" + p.Str("tagLine");
        }

        bool anyAbilityData = false;
        if (root.Get("roundResults") is { ValueKind: JsonValueKind.Array } rounds)
            foreach (var r in rounds.EnumerateArray())
            {
                var round = new VRound
                {
                    N = r.Int("roundNum"), Winner = r.Str("winningTeam"), Result = r.Str("roundResultCode"),
                    Planter = NullIfEmpty(r.Str("bombPlanter")), Site = NullIfEmpty(r.Str("plantSite")),
                    PlantMs = r.Str("bombPlanter").Length > 0 && Long(r, "plantRoundTime") > 0 ? Long(r, "plantRoundTime") : -1,
                };
                var used = new List<string>();
                if (r.Get("playerStats") is { ValueKind: JsonValueKind.Array } ps)
                    foreach (var p in ps.EnumerateArray())
                    {
                        var id = p.Str("subject");
                        if (p.Get("economy") is { ValueKind: JsonValueKind.Object } e)
                            round.Eco[id] = new VEco { Loadout = e.Int("loadoutValue"), Spent = e.Int("spent"), Remaining = e.Int("remaining") };
                        if (p.Get("damage") is { ValueKind: JsonValueKind.Array } dmg)
                            foreach (var d in dmg.EnumerateArray())
                                round.Damage.Add(new VDamage { From = id, To = d.Str("receiver"), Amount = d.Int("damage"), Head = d.Int("headshots"), Body = d.Int("bodyshots"), Legs = d.Int("legshots") });
                        if (p.Get("ability") is { ValueKind: JsonValueKind.Object } ab)
                        {
                            bool any = false;
                            foreach (var k in new[] { "grenadeEffects", "ability1Effects", "ability2Effects", "ultimateEffects" })
                                if (ab.Get(k) is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined }) { any = true; anyAbilityData = true; }
                            if (any) used.Add(id);
                        }
                    }
                round.UsedAbility = used;
                m.Rounds.Add(round);
            }
        // Per-round ability use only means something when Riot sent it for this match.
        if (!anyAbilityData) foreach (var r in m.Rounds) r.UsedAbility = null;

        IEnumerable<JsonElement> kills = Enumerable.Empty<JsonElement>();
        if (root.Get("kills") is { ValueKind: JsonValueKind.Array } top && top.GetArrayLength() > 0) kills = top.EnumerateArray();
        else if (root.Get("roundResults") is { ValueKind: JsonValueKind.Array } rr)
            kills = rr.EnumerateArray()
                .SelectMany(r => r.Get("playerStats") is { ValueKind: JsonValueKind.Array } ps ? ps.EnumerateArray() : Enumerable.Empty<JsonElement>())
                .SelectMany(p => p.Get("kills") is { ValueKind: JsonValueKind.Array } k ? k.EnumerateArray() : Enumerable.Empty<JsonElement>());
        foreach (var k in kills)
        {
            var kill = new VKill
            {
                Round = k.Int("round"), GameMs = Long(k, "gameTime"), RoundMs = Long(k, "roundTime"),
                Killer = k.Str("killer"), Victim = k.Str("victim"),
                DamageType = k.Str("finishingDamage", "damageType"), Weapon = k.Str("finishingDamage", "damageItem"),
                VX = Num(k, "victimLocation", "x"), VY = Num(k, "victimLocation", "y"),
            };
            if (k.Get("assistants") is { ValueKind: JsonValueKind.Array } asst)
                foreach (var a in asst.EnumerateArray()) if (a.ValueKind == JsonValueKind.String) kill.Assists.Add(a.GetString());
            if (k.Get("playerLocations") is { ValueKind: JsonValueKind.Array } locs)
                foreach (var p in locs.EnumerateArray())
                    kill.Pos.Add(new VPos { Id = p.Str("subject"), X = Num(p, "location", "x"), Y = Num(p, "location", "y"), View = Num(p, "viewRadians") });
            m.Kills.Add(kill);
        }
        // The same kill can be listed both at the top and per round in older data: keep one of each.
        m.Kills = m.Kills.GroupBy(k => (k.GameMs, k.Killer, k.Victim)).Select(g => g.First()).OrderBy(k => k.GameMs).ToList();
        m.FirstAttack = GuessFirstAttack(m);
        return m;
    }

    static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
    static long Long(JsonElement e, params string[] path) => e.Get(path) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var l) ? l : 0;
    static double Num(JsonElement e, params string[] path) => e.Get(path) is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : 0;

    /// <summary>
    /// Which team attacked first. Red attacks first in Riot's matches; the spike plants confirm it (only attackers
    /// plant), and they win if they disagree.
    /// </summary>
    internal static string GuessFirstAttack(VMatch m)
    {
        int agree = 0, disagree = 0;
        m.FirstAttack = "Red";
        foreach (var r in m.Rounds.Where(r => r.Planter != null))
        {
            var t = m.TeamOf(r.Planter);
            if (t.Length == 0) continue;
            if (m.Attackers(r.N) == t) agree++; else disagree++;
        }
        return disagree > agree ? "Blue" : "Red";
    }
}

/// <summary>Your saved VALORANT matches (valorant\matches\*.json.gz in Pulse's data folder), newest first.</summary>
public static class VStore
{
    static readonly object Gate = new();
    static List<VMatch> _cache;
    static string Dir => Path.Combine(TweakEngine.DataDir, "valorant", "matches");
    /// <summary>Raised (on any thread) when a match is saved.</summary>
    public static event Action Changed;

    public static bool Has(string id) => File.Exists(Path.Combine(Dir, id + ".json.gz"));

    public static void Save(VMatch m)
    {
        if (m == null || string.IsNullOrEmpty(m.Id)) return;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var file = Path.Combine(Dir, m.Id + ".json.gz");
                using (var fs = File.Create(file + ".tmp"))
                using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
                    JsonSerializer.Serialize(gz, m);
                File.Move(file + ".tmp", file, true);
                if (_cache != null) { _cache.RemoveAll(x => x.Id == m.Id); _cache.Add(m); _cache.Sort((a, b) => b.StartUtc.CompareTo(a.StartUtc)); }
            }
            catch { }
        }
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Every saved match, newest first (read once, then kept in memory).</summary>
    public static List<VMatch> All
    {
        get
        {
            lock (Gate)
            {
                if (_cache == null)
                {
                    _cache = new();
                    try
                    {
                        if (Directory.Exists(Dir))
                            foreach (var f in Directory.EnumerateFiles(Dir, "*.json.gz"))
                                try
                                {
                                    using var fs = File.OpenRead(f);
                                    using var gz = new GZipStream(fs, CompressionMode.Decompress);
                                    if (JsonSerializer.Deserialize<VMatch>(gz) is { } m) _cache.Add(m);
                                }
                                catch { }
                    }
                    catch { }
                    _cache.Sort((a, b) => b.StartUtc.CompareTo(a.StartUtc));
                }
                return _cache.ToList();
            }
        }
    }

    /// <summary>Your matches that the coach uses (real 5v5 rounds), for the account playing now when it's known.</summary>
    public static List<VMatch> ForCoach(string me = null) =>
        All.Where(m => VMatch.Counts(m.Queue) && m.Rounds.Count >= 5 && (me == null || m.Me == me)).ToList();

    /// <summary>For tests: forget what's in memory.</summary>
    internal static void Reset() { lock (Gate) _cache = null; }
}
