using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Pulse.Core.Games.Live;

/// <summary>One player in the live lobby.</summary>
public sealed record LobbyPlayer(string Puuid, bool Ally, bool Me, string Agent, string Name, bool Hidden, int Level,
    int Tier, int Rr, int PeakTier, string PeakAct, int WinPercent, int Games, Smurf Smurf)
{
    /// <summary>After a match: their line from it ("21/8/4 · 312 ACS").</summary>
    public string Extra { get; init; }
    /// <summary>After a match: stats almost nobody at their rank reaches, in that match (a hint, never proof).</summary>
    public Smurf Unusual { get; init; }
}

/// <summary>How much a player looks like a smurf (0–100) and the signs behind it, in words.</summary>
public sealed record Smurf(int Score, List<string> Signs)
{
    public static readonly Smurf None = new(0, new());
    /// <summary>"Likely smurf", "Possible smurf" or null.</summary>
    public string Label => Score >= 60 ? "Likely smurf" : Score >= 35 ? "Possible smurf" : null;
}

/// <summary>The match you just loaded into: map, mode, and both teams.</summary>
public sealed record Lobby(string MatchId, string Map, string Mode, List<LobbyPlayer> Players);

/// <summary>
/// The live lobby: when you load into a VALORANT match, the players in it with their agent, level, current rank, peak
/// rank and win rate this act, read through the Riot Client's own login on this PC (like the "tracker" apps do).
/// Players who turned on "Hide my name" stay hidden: Pulse never looks their name up. Only on when the user turned it
/// on after reading the risk (Riot considers showing ranks mid-match "hidden information").
/// </summary>
public static class ValorantLobby
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static Dictionary<string, string> _agents;
    static List<(string Id, DateTime Start, bool Act, bool Active)> _seasons;
    static readonly Dictionary<string, string> _actNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Episode 9 · Act 2" style name of an act from its id (from Riot's own list); "" when unknown.</summary>
    public static string ActName(string id) => id != null && _actNames.TryGetValue(id, out var n) ? n : "";

    /// <summary>Riot's act and episode names in sentence case: "EPISODE 9" → "Episode 9", "ACT II" → "Act 2".</summary>
    internal static string Friendly(string name)
    {
        var parts = (name ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w =>
        {
            var roman = new Dictionary<string, string> { ["I"] = "1", ["II"] = "2", ["III"] = "3", ["IV"] = "4", ["V"] = "5", ["VI"] = "6" };
            if (roman.TryGetValue(w.ToUpperInvariant(), out var r)) return r;
            return w.Length > 1 && w.All(char.IsLetter) ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant() : w;
        });
        return string.Join(' ', parts);
    }
    static readonly DateTime AscendantAdded = new(2022, 6, 21);   // Episode 5: Ascendant was added and tiers renumbered

    public static Lobby Last { get; private set; }

    public static async Task<Lobby> Load()
    {
        var s = await ValorantMatches.Login();
        if (s == null || string.IsNullOrEmpty(s.Region)) return null;
        var glz = $"https://glz-{s.Region}-1.{s.Shard}.a.pvp.net";
        using var me = await ValorantMatches.Get(s, $"{glz}/core-game/v1/players/{s.Puuid}");
        var matchId = me?.RootElement.Str("MatchID");
        if (string.IsNullOrEmpty(matchId)) return null;
        using var match = await ValorantMatches.Get(s, $"{glz}/core-game/v1/matches/{matchId}");
        if (match?.RootElement.Get("Players") is not { ValueKind: JsonValueKind.Array } players) return null;
        await LoadContent(s);

        string myTeam = players.EnumerateArray().FirstOrDefault(p => p.Str("Subject") == s.Puuid).Str("TeamID");
        var raw = players.EnumerateArray().Select(p => (
            Puuid: p.Str("Subject"), Team: p.Str("TeamID"), Agent: p.Str("CharacterID"),
            Incognito: p.Get("PlayerIdentity", "Incognito") is { ValueKind: JsonValueKind.True },
            HideLevel: p.Get("PlayerIdentity", "HideAccountLevel") is { ValueKind: JsonValueKind.True },
            Level: p.Int("PlayerIdentity", "AccountLevel"))).Where(p => !string.IsNullOrEmpty(p.Puuid)).ToList();

        // Names only for players who didn't hide theirs (and always your own).
        var names = await Names(s, raw.Where(p => !p.Incognito || p.Puuid == s.Puuid).Select(p => p.Puuid).ToList());

        var list = new List<LobbyPlayer>();
        foreach (var p in raw)
        {
            var (tier, rr, peak, peakAct, win, games) = await Mmr(s, p.Puuid);
            var gains = await RecentGains(s, p.Puuid);
            var smurf = p.Puuid == s.Puuid ? Smurf.None : SmurfSigns(p.HideLevel ? 0 : p.Level, tier, peak, win, games, gains);
            bool hidden = p.Incognito && p.Puuid != s.Puuid;
            list.Add(new LobbyPlayer(p.Puuid, p.Team == myTeam, p.Puuid == s.Puuid,
                _agents != null && _agents.TryGetValue(p.Agent, out var a) ? a : ValorantMatches.AgentName(p.Agent),
                hidden ? null : names.GetValueOrDefault(p.Puuid), hidden, p.HideLevel ? 0 : p.Level, tier, rr, peak, peakAct, win, games, smurf));
            await Task.Delay(120);   // gentle on Riot's servers
        }
        var info = match.RootElement;
        Last = new Lobby(matchId, ValorantMatches.MapName(info.Str("MapID")), info.Get("MatchmakingData", "QueueID") is { } q ? q.GetString() ?? "" : "", list);
        return Last;
    }

    internal static async Task<Dictionary<string, string>> Names(ValorantMatches.Session s, List<string> puuids)
    {
        var map = new Dictionary<string, string>();
        if (puuids.Count == 0) return map;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Put, $"https://pd.{s.Shard}.a.pvp.net/name-service/v2/players")
            {
                Content = new StringContent(JsonSerializer.Serialize(puuids), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Access);
            req.Headers.Add("X-Riot-Entitlements-JWT", s.Entitlement);
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return map;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                var name = p.Str("GameName");
                if (name.Length > 0) map[p.Str("Subject")] = name + "#" + p.Str("TagLine");
            }
        }
        catch { }
        return map;
    }

    /// <summary>Current rank and RR this act, peak rank ever (with its act), and win rate this act.</summary>
    internal static async Task<(int Tier, int Rr, int Peak, string PeakAct, int Win, int Games)> Mmr(ValorantMatches.Session s, string puuid)
    {
        try
        {
            using var doc = await ValorantMatches.Get(s, $"https://pd.{s.Shard}.a.pvp.net/mmr/v1/players/{puuid}");
            if (doc == null) return default;
            return ReadMmr(doc.RootElement, _seasons ?? new());
        }
        catch { return default; }
    }

    internal static (int Tier, int Rr, int Peak, string PeakAct, int Win, int Games) ReadMmr(JsonElement root, List<(string Id, DateTime Start, bool Act, bool Active)> seasons)
    {
        int tier = 0, rr = 0, peak = 0, win = -1, games = 0;
        string peakAct = "";
        var current = seasons.FirstOrDefault(x => x.Act && x.Active).Id;
        if (root.Get("QueueSkills", "competitive", "SeasonalInfoBySeasonID") is { ValueKind: JsonValueKind.Object } bySeason)
            foreach (var season in bySeason.EnumerateObject())
            {
                var v = season.Value;
                var start = seasons.FirstOrDefault(x => x.Id == season.Name).Start;
                int Fix(int t) => start != default && start < AscendantAdded && t >= 21 ? t + 3 : t;   // old Immortal/Radiant numbers
                if (season.Name == current)
                {
                    tier = v.Int("CompetitiveTier");
                    rr = v.Int("RankedRating");
                    games = v.Int("NumberOfGames");
                    win = games > 0 ? (int)Math.Round(100.0 * v.Int("NumberOfWins") / games) : -1;
                }
                // Peak: the highest tier this player won a game at, in any act.
                if (v.Get("WinsByTier") is { ValueKind: JsonValueKind.Object } wins)
                    foreach (var w in wins.EnumerateObject())
                        if (int.TryParse(w.Name, out var t) && w.Value.ValueKind == JsonValueKind.Number && w.Value.GetInt32() > 0 && Fix(t) > peak)
                        {
                            peak = Fix(t);
                            peakAct = season.Name;
                        }
            }
        return (tier, rr, peak, peakAct, win, games);
    }

    /// <summary>One competitive act: the rank it ended on (or is on now), games, wins, and the best rank won at in it.</summary>
    public sealed record ActLine(string Id, string Name, DateTime StartUtc, DateTime EndUtc, int Tier, int Games, int Wins, int Peak, bool Current)
    {
        public double WinPct => Games > 0 ? 100.0 * Wins / Games : double.NaN;
    }

    /// <summary>Every act from the content list, oldest first, each ending when the next begins (UTC).</summary>
    internal static List<(string Id, string Name, DateTime Start, DateTime End, bool Active)> Acts()
    {
        if (_seasons == null || _seasons.Count == 0) LoadActCache();
        var acts = (_seasons ?? new()).Where(x => x.Act && x.Start != default).OrderBy(x => x.Start).ToList();
        var list = new List<(string, string, DateTime, DateTime, bool)>();
        for (int i = 0; i < acts.Count; i++)
            list.Add((acts[i].Id, ActName(acts[i].Id), acts[i].Start.ToUniversalTime(), i + 1 < acts.Count ? acts[i + 1].Start.ToUniversalTime() : DateTime.MaxValue, acts[i].Active));
        return list;
    }

    /// <summary>A player's competitive history act by act (newest first), from Riot's rank record.</summary>
    internal static async Task<List<ActLine>> ActHistory(ValorantMatches.Session s, string puuid)
    {
        try
        {
            await LoadContent(s);
            using var doc = await ValorantMatches.Get(s, $"https://pd.{s.Shard}.a.pvp.net/mmr/v1/players/{puuid}");
            return doc == null ? new() : ReadActs(doc.RootElement, Acts());
        }
        catch { return new(); }
    }

    internal static List<ActLine> ReadActs(JsonElement root, List<(string Id, string Name, DateTime Start, DateTime End, bool Active)> acts)
    {
        var list = new List<ActLine>();
        if (root.Get("QueueSkills", "competitive", "SeasonalInfoBySeasonID") is not { ValueKind: JsonValueKind.Object } bySeason) return list;
        foreach (var season in bySeason.EnumerateObject())
        {
            var v = season.Value;
            var a = acts.FirstOrDefault(x => x.Id == season.Name);
            int Fix(int t) => a.Start != default && a.Start < AscendantAdded && t >= 21 ? t + 3 : t;
            int games = v.Int("NumberOfGames");
            if (games == 0 && v.Int("CompetitiveTier") == 0) continue;
            int peak = 0;
            if (v.Get("WinsByTier") is { ValueKind: JsonValueKind.Object } wins)
                foreach (var w in wins.EnumerateObject())
                    if (int.TryParse(w.Name, out var t) && w.Value.ValueKind == JsonValueKind.Number && w.Value.GetInt32() > 0) peak = Math.Max(peak, Fix(t));
            list.Add(new ActLine(season.Name, a.Name is { Length: > 0 } n ? n : ActName(season.Name), a.Start, a.End == default ? DateTime.MaxValue : a.End,
                Fix(v.Int("CompetitiveTier")), games, v.Int("NumberOfWins"), peak, a.Active));
        }
        return list.OrderByDescending(x => x.StartUtc).ToList();
    }

    /// <summary>RR won or lost in the player's last competitive games (newest first), from Riot's competitive updates.</summary>
    /// <summary>
    /// The lobby of a match that just ended, from Riot's own match record (the same names and stats its match
    /// history shows everyone who played), plus each player's rank, peak and smurf signs read after the match.
    /// Nothing here happens while the match is being played.
    /// </summary>
    public static async Task<Lobby> AfterMatch(Pulse.Core.Games.Valorant.VMatch vm)
    {
        var s = await ValorantMatches.Login();
        if (s == null || vm == null) return null;
        await LoadContent(s);
        // Riot IDs: from the match record when it had them, else from Riot's name service (the match is over).
        var names = vm.Names != null ? new Dictionary<string, string>(vm.Names) : new Dictionary<string, string>();
        foreach (var p in vm.Players) if (!string.IsNullOrEmpty(p.Name)) names.TryAdd(p.Id, p.Name);
        if (vm.Players.Any(p => !names.ContainsKey(p.Id)))
            try { foreach (var kv in await Names(s, vm.Players.Select(p => p.Id).ToList())) if (!string.IsNullOrEmpty(kv.Value)) names.TryAdd(kv.Key, kv.Value); } catch { }
        var list = new List<LobbyPlayer>();
        foreach (var p in vm.Players)
        {
            var (tier, rr, peak, peakAct, win, games) = await Mmr(s, p.Id);
            var gains = await RecentGains(s, p.Id);
            bool me = p.Id == vm.Me;
            var t = Pulse.Core.Games.Valorant.Analysis.Totals(vm, p.Id);
            int matchTier = p.Tier >= 3 ? p.Tier : tier;
            string name = names.TryGetValue(p.Id, out var n) ? n : null;
            string agent = p.Agent.Length > 0 ? p.Agent : ValorantMatches.AgentName(p.AgentId);
            list.Add(new LobbyPlayer(p.Id, p.Team == vm.MyTeam, me, agent, name, false, 0, tier >= 3 ? tier : p.Tier, rr, Math.Max(peak, tier), ActName(peakAct),
                win, games, me ? Smurf.None : SmurfSigns(0, tier, peak, win, games, gains))
            {
                Extra = $"{t.Kills}/{t.Deaths}/{p.Assists} · {t.Acs:0} ACS",
                Unusual = me ? Smurf.None : Pulse.Core.Games.Valorant.Tracker.UnusualInMatch(vm, p.Id, matchTier),
            });
            await Task.Delay(120); // gentle on Riot's servers
        }
        return new Lobby(vm.Id, vm.Map, vm.Queue, list);
    }

    /// <summary>The rank after a player's last competitive match (for players who haven't played ranked this act).</summary>
    internal static async Task<int> LastTier(ValorantMatches.Session s, string puuid)
    {
        try
        {
            using var doc = await ValorantMatches.Get(s, $"https://pd.{s.Shard}.a.pvp.net/mmr/v1/players/{puuid}/competitiveupdates?startIndex=0&endIndex=1&queue=competitive");
            if (doc?.RootElement.Get("Matches") is { ValueKind: JsonValueKind.Array } m && m.GetArrayLength() > 0) return m[0].Int("TierAfterUpdate");
        }
        catch { }
        return 0;
    }

    /// <summary>Your own account level (Riot only shares it for your account).</summary>
    internal static async Task<int> MyLevel(ValorantMatches.Session s)
    {
        try
        {
            using var doc = await ValorantMatches.Get(s, $"https://pd.{s.Shard}.a.pvp.net/account-xp/v1/players/{s.Puuid}");
            return doc?.RootElement.Int("Progress", "Level") ?? 0;
        }
        catch { return 0; }
    }

    internal static async Task<List<int>> RecentGains(ValorantMatches.Session s, string puuid)
    {
        var list = new List<int>();
        try
        {
            using var doc = await ValorantMatches.Get(s, $"https://pd.{s.Shard}.a.pvp.net/mmr/v1/players/{puuid}/competitiveupdates?startIndex=0&endIndex=10&queue=competitive");
            if (doc?.RootElement.Get("Matches") is { ValueKind: JsonValueKind.Array } m)
                foreach (var x in m.EnumerateArray())
                    if (x.Get("RankedRatingEarned") is { ValueKind: JsonValueKind.Number } e) list.Add(e.GetInt32());
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Smurf signs, each from data Riot gives for every player, weighted by how strongly it points to a player whose
    /// skill is above their rank:
    /// <list type="bullet">
    /// <item>RR per win: Riot gives more RR per win when your hidden skill rating (MMR) is above your rank. Most players
    /// gain about 15–22 RR a win; a steady 25 or more means the game itself thinks they belong higher. The strongest sign.</item>
    /// <item>Win rate this act over at least 10 games: 65% and up is rare for someone at their right rank.</item>
    /// <item>A low account level in ranked (a new account).</item>
    /// <item>A peak rank two ranks or more above where they are now (a strong player in a lower lobby).</item>
    /// </list>
    /// Signs, not proof: a good player on a hot streak can show them too, so the page always says why.
    /// </summary>
    internal static Smurf SmurfSigns(int level, int tier, int peak, int winPercent, int games, IReadOnlyList<int> gains)
    {
        int score = 0;
        var signs = new List<string>();
        var wins = gains.Where(g => g > 0).ToList();
        if (wins.Count >= 3)
        {
            double avg = wins.Average();
            if (avg >= 28) { score += 40; signs.Add(Pulse.Ui.Loc.F("Gains {0} RR a win (most players get 15 to 22)", Math.Round(avg))); }
            else if (avg >= 24) { score += 25; signs.Add(Pulse.Ui.Loc.F("Gains {0} RR a win (most players get 15 to 22)", Math.Round(avg))); }
        }
        if (games >= 10 && winPercent >= 0)
        {
            if (winPercent >= 75) { score += 40; signs.Add(Pulse.Ui.Loc.F("Wins {0}% of {1} games this act", winPercent, games)); }
            else if (winPercent >= 65) { score += 25; signs.Add(Pulse.Ui.Loc.F("Wins {0}% of {1} games this act", winPercent, games)); }
        }
        if (level > 0 && tier >= 3)
        {
            if (level <= 30) { score += 25; signs.Add(Pulse.Ui.Loc.F("New account (level {0}) in ranked", level)); }
            else if (level <= 60) { score += 15; signs.Add(Pulse.Ui.Loc.F("Fairly new account (level {0})", level)); }
        }
        if (peak >= 3 && tier >= 3 && peak - tier >= 6) { score += 15; signs.Add(Pulse.Ui.Loc.F("Peaked {0}, now {1}", ValorantMatches.RankName(peak), ValorantMatches.RankName(tier))); }
        // A single sign on its own is weak: cap it so one alone never says "likely".
        if (signs.Count == 1) score = Math.Min(score, 40);
        return new Smurf(Math.Min(100, score), signs);
    }

    /// <summary>Agent names and the list of acts (with start dates) from Riot's content service.</summary>
    /// <summary>When the current act started (UTC), once the content list has been read; null before.</summary>
    internal static DateTime? CurrentActStart() => _seasons?.FirstOrDefault(x => x.Act && x.Active) is { Id: { Length: > 0 } } a && a.Start != default ? a.Start.ToUniversalTime() : null;

    /// <summary>An agent's name from the Riot Client's content list, when it has been read this session.</summary>
    internal static string ContentAgent(string id) => _agents != null && _agents.TryGetValue(id, out var a) && !string.IsNullOrEmpty(a) ? a : null;

    internal static async Task LoadContent(ValorantMatches.Session s)
    {
        if (_agents != null && _seasons != null) return;
        _agents = new(StringComparer.OrdinalIgnoreCase);
        _seasons = new();
        try
        {
            using var doc = await ValorantMatches.Get(s, $"https://shared.{s.Shard}.a.pvp.net/content-service/v3/content");
            if (doc == null) return;
            if (doc.RootElement.Get("Characters") is { ValueKind: JsonValueKind.Array } chars)
                foreach (var c in chars.EnumerateArray()) _agents[c.Str("ID")] = c.Str("Name");
            if (doc.RootElement.Get("Seasons") is { ValueKind: JsonValueKind.Array } seasons)
            {
                var named = new List<(string Id, string Name, DateTime Start, bool Act)>();
                foreach (var x in seasons.EnumerateArray())
                {
                    var start = DateTime.TryParse(x.Str("StartTime"), out var st) ? st : default;
                    bool act = x.Str("Type").Equals("act", StringComparison.OrdinalIgnoreCase);
                    _seasons.Add((x.Str("ID"), start, act, x.Get("IsActive") is { ValueKind: JsonValueKind.True }));
                    named.Add((x.Str("ID"), x.Str("Name"), start, act));
                }
                // An act's episode is the newest episode that started on or before it.
                foreach (var a in named.Where(n => n.Act))
                {
                    var ep = named.Where(n => !n.Act && n.Start <= a.Start && n.Name.Length > 0).OrderByDescending(n => n.Start).FirstOrDefault();
                    var actName = Friendly(a.Name);
                    _actNames[a.Id] = ep.Name is { Length: > 0 } && !actName.StartsWith(Friendly(ep.Name), StringComparison.OrdinalIgnoreCase) ? $"{Friendly(ep.Name)} · {actName}" : actName;
                }
                SaveActCache();
            }
        }
        catch { }
    }

    // Riot's act list, kept on disk so act history and act filters work when the Riot Client isn't open.
    sealed record CachedAct(string Id, string Name, DateTime Start, bool Active);
    static string ActCachePath => Path.Combine(TweakEngine.DataDir, "valorant", "acts.json");

    static void SaveActCache()
    {
        try
        {
            var list = _seasons.Where(x => x.Act && x.Start != default).Select(x => new CachedAct(x.Id, ActName(x.Id), x.Start.ToUniversalTime(), x.Active)).ToList();
            if (list.Count == 0) return;
            Directory.CreateDirectory(Path.GetDirectoryName(ActCachePath)!);
            File.WriteAllText(ActCachePath, System.Text.Json.JsonSerializer.Serialize(list));
        }
        catch { }
    }

    static void LoadActCache()
    {
        try
        {
            if (!File.Exists(ActCachePath)) return;
            var list = System.Text.Json.JsonSerializer.Deserialize<List<CachedAct>>(File.ReadAllText(ActCachePath));
            if (list == null || list.Count == 0) return;
            // The cached "active" flag can be stale: the newest act that has started is the current one.
            var started = list.Where(a => a.Start <= DateTime.UtcNow).OrderBy(a => a.Start).ToList();
            var current = started.LastOrDefault()?.Id;
            _seasons = list.Select(a => (a.Id, a.Start, true, a.Id == current)).ToList();
            foreach (var a in list) if (a.Name.Length > 0) _actNames.TryAdd(a.Id, a.Name);
        }
        catch { }
    }

    /// <summary>
    /// Act by act from Pulse's own saved competitive matches, for when Riot's rank record can't be read: games, wins,
    /// the rank in the act's last match and the best rank seen at. Only acts with saved matches are listed.
    /// </summary>
    public static List<ActLine> ActsFromMatches(IEnumerable<Pulse.Core.Games.Valorant.VMatch> matches, string puuid)
    {
        var acts = Acts();
        var list = new List<ActLine>();
        var comp = matches.Where(m => m.Queue == "competitive").ToList();
        foreach (var a in acts)
        {
            var inAct = comp.Where(m => m.StartUtc >= a.Start && m.StartUtc < a.End).OrderBy(m => m.StartUtc).ToList();
            var mine = inAct.Select(m => (m, p: m.Players.FirstOrDefault(x => x.Id == puuid))).Where(x => x.p != null).ToList();
            if (mine.Count == 0) continue;
            int wins = mine.Count(x => x.m.Rounds.Count(r => r.Winner == x.p.Team) > x.m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != x.p.Team));
            int last = mine[^1].p.Tier, peak = mine.Max(x => x.p.Tier);
            list.Add(new ActLine(a.Id, a.Name, a.Start, a.End, last, mine.Count, wins, peak, a.Active));
        }
        return list.OrderByDescending(x => x.StartUtc).ToList();
    }
}
