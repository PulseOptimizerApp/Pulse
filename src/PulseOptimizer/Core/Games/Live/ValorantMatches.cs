using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pulse.Core.Games.Live;

/// <summary>
/// VALORANT recaps: after each match, Pulse asks Riot for your own last match, the same way the Riot Client and
/// trackers do, using the login the Riot Client already has on this PC (its local lockfile). Only your own match is
/// read; nothing is changed and nothing goes anywhere but Riot. VALORANT has no live kill feed for apps, so its big
/// plays are listed on the recap but can't be clipped automatically.
/// </summary>
public sealed class ValorantMatches : IDisposable
{
    static readonly HttpClient Http = new(new HttpClientHandler
    {
        // The Riot Client's local API (127.0.0.1) uses its own certificate; Riot's servers use normal ones.
        ServerCertificateCustomValidationCallback = (req, _, _, errors) => req.RequestUri?.Host == "127.0.0.1" || errors == System.Net.Security.SslPolicyErrors.None,
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    // Riot's documented client platform header value (Windows PC), base64 JSON.
    const string ClientPlatform = "ew0KCSJwbGF0Zm9ybVR5cGUiOiAiUEMiLA0KCSJwbGF0Zm9ybU9TIjogIldpbmRvd3MiLA0KCSJwbGF0Zm9ybU9TVmVyc2lvbiI6ICIxMC4wLjE5MDQyLjEuMjU2LjY0Yml0IiwNCgkicGxhdGZvcm1DaGlwc2V0IjogIlVua25vd24iDQp9";

    CancellationTokenSource _cts;
    string _lastSeen;
    Dictionary<string, string> _agents, _maps;

    public static bool Running()
    {
        var p = Process.GetProcessesByName("VALORANT-Win64-Shipping");
        foreach (var x in p) x.Dispose();
        return p.Length > 0;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try { _lastSeen ??= await LatestMatchId(); } catch { }
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(90_000, token); } catch { break; }
                try { await Check(); } catch { }
            }
        });
    }

    /// <summary>VALORANT closed: one last look for the match that just ended.</summary>
    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        _ = Task.Run(async () => { try { await Task.Delay(20_000); await Check(); } catch { } });
    }

    public void Dispose() { _cts?.Cancel(); _cts = null; }

    async Task Check()
    {
        var id = await LatestMatchId();
        if (id == null || id == _lastSeen) return;
        _lastSeen = id;
        if (MatchStore.All.Any(m => m.Id == "val-" + id)) return;
        var rec = await Details(id);
        if (rec != null) MatchEvents.RaiseFinished(rec);
    }

    // ───────── Riot Client login and region ─────────

    record Session(string Puuid, string Access, string Entitlement, string Shard, string Version);

    static async Task<Session> Login()
    {
        var lockfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Config", "lockfile");
        if (!File.Exists(lockfile)) return null;
        string text;
        using (var fs = new FileStream(lockfile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var r = new StreamReader(fs)) text = await r.ReadToEndAsync();
        var parts = text.Split(':');
        if (parts.Length < 5) return null;
        var req = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{parts[2]}/entitlements/v1/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes("riot:" + parts[3])));
        using var res = await Http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VALORANT", "Saved", "Logs", "ShooterGame.log");
        if (!File.Exists(log)) return null;
        string logText;
        using (var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var r = new StreamReader(fs)) logText = await r.ReadToEndAsync();
        var shard = Regex.Match(logText, @"https://glz-[a-z0-9]+-1\.([a-z0-9]+)\.a\.pvp\.net").Groups[1].Value;
        var version = Regex.Match(logText, @"CI server version: (\S+)").Groups[1].Value;
        if (shard.Length == 0) return null;
        return new Session(root.Str("subject"), root.Str("accessToken"), root.Str("token"), shard, version);
    }

    static async Task<JsonDocument> Get(Session s, string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Access);
        req.Headers.Add("X-Riot-Entitlements-JWT", s.Entitlement);
        req.Headers.Add("X-Riot-ClientPlatform", ClientPlatform);
        if (s.Version.Length > 0) req.Headers.Add("X-Riot-ClientVersion", s.Version);
        using var res = await Http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return null;
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync());
    }

    async Task<string> LatestMatchId()
    {
        var s = await Login();
        if (s == null) return null;
        using var doc = await Get(s, $"https://pd.{s.Shard}.a.pvp.net/match-history/v1/history/{s.Puuid}?startIndex=0&endIndex=1");
        if (doc?.RootElement.Get("History") is not { ValueKind: JsonValueKind.Array } h || h.GetArrayLength() == 0) return null;
        return h[0].Str("MatchID");
    }

    async Task<MatchRecord> Details(string id)
    {
        var s = await Login();
        if (s == null) return null;
        await LoadNames(s);
        using var doc = await Get(s, $"https://pd.{s.Shard}.a.pvp.net/match-details/v1/matches/{id}");
        if (doc == null) return null;
        MatchEvents.RaiseMoments(KillsAndDeaths(doc.RootElement, s.Puuid));
        return Parse(doc.RootElement, s.Puuid, _agents, _maps, "val-" + id);
    }

    /// <summary>Agent and map names from Riot's content service (falls back to Pulse's own list).</summary>
    async Task LoadNames(Session s)
    {
        if (_agents != null) return;
        _agents = new(StringComparer.OrdinalIgnoreCase);
        _maps = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = await Get(s, $"https://shared.{s.Shard}.a.pvp.net/content-service/v3/content");
            if (doc == null) return;
            if (doc.RootElement.Get("Characters") is { ValueKind: JsonValueKind.Array } chars)
                foreach (var c in chars.EnumerateArray()) _agents[c.Str("ID")] = c.Str("Name");
            if (doc.RootElement.Get("Maps") is { ValueKind: JsonValueKind.Array } maps)
                foreach (var m in maps.EnumerateArray())
                {
                    var path = m.Str("AssetPath");
                    if (path.Length > 0) _maps[path] = m.Str("Name");
                }
        }
        catch { }
    }

    // ───────── reading a match ─────────

    static readonly string[] Tiers =
    {
        "Unrated", "", "", "Iron 1", "Iron 2", "Iron 3", "Bronze 1", "Bronze 2", "Bronze 3", "Silver 1", "Silver 2", "Silver 3",
        "Gold 1", "Gold 2", "Gold 3", "Platinum 1", "Platinum 2", "Platinum 3", "Diamond 1", "Diamond 2", "Diamond 3",
        "Ascendant 1", "Ascendant 2", "Ascendant 3", "Immortal 1", "Immortal 2", "Immortal 3", "Radiant",
    };

    public static string RankName(int tier) => tier > 0 && tier < Tiers.Length ? Tiers[tier] : "";

    /// <summary>Riot's internal map folder names → the names players know.</summary>
    static readonly Dictionary<string, string> MapCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ascent"] = "Ascent", ["Duality"] = "Bind", ["Triad"] = "Haven", ["Bonsai"] = "Split", ["Port"] = "Icebox",
        ["Foxtrot"] = "Breeze", ["Canyon"] = "Fracture", ["Pitt"] = "Pearl", ["Jam"] = "Lotus", ["Juliett"] = "Sunset",
        ["Infinity"] = "Abyss", ["Rook"] = "Corrode", ["Range"] = "The Range", ["HURM_Alley"] = "District",
        ["HURM_Yard"] = "Piazza", ["HURM_Bowl"] = "Kasbah", ["HURM_Helix"] = "Drift", ["HURM_HighTide"] = "Glitch",
    };

    public static string MapName(string mapId, IReadOnlyDictionary<string, string> fromRiot = null)
    {
        if (string.IsNullOrEmpty(mapId)) return "";
        if (fromRiot != null && fromRiot.TryGetValue(mapId, out var n) && !string.IsNullOrEmpty(n)) return n;
        var code = mapId.TrimEnd('/').Split('/').Last();
        return MapCodes.TryGetValue(code, out var name) ? name : code;
    }

    static string ModeName(string queue) => queue switch
    {
        "competitive" => "Competitive", "unrated" => "Unrated", "swiftplay" => "Swiftplay", "spikerush" => "Spike Rush",
        "deathmatch" => "Deathmatch", "ggteam" => "Escalation", "premier" => "Premier", "hurm" => "Team Deathmatch",
        "" => "Custom", var q => char.ToUpperInvariant(q[0]) + q[1..],
    };

    public static MatchRecord Parse(JsonElement root, string puuid, IReadOnlyDictionary<string, string> agents, IReadOnlyDictionary<string, string> maps, string id)
    {
        var info = root.Get("matchInfo") ?? default;
        var me = root.Get("players") is { ValueKind: JsonValueKind.Array } players
            ? players.EnumerateArray().FirstOrDefault(p => p.Str("subject") == puuid) : default;
        if (me.ValueKind != JsonValueKind.Object) return null;
        var team = me.Str("teamId");
        int mine = 0, theirs = 0;
        string result = "";
        if (root.Get("teams") is { ValueKind: JsonValueKind.Array } teams)
            foreach (var t in teams.EnumerateArray())
            {
                if (t.Str("teamId") == team) { mine = t.Int("roundsWon"); result = t.Get("won") is { ValueKind: JsonValueKind.True } ? "Win" : "Loss"; }
                else theirs = Math.Max(theirs, t.Int("roundsWon"));
            }
        if (mine == theirs && result == "Loss") result = "Draw";

        int heads = 0, body = 0, legs = 0;
        var highlights = new List<string>();
        if (root.Get("roundResults") is { ValueKind: JsonValueKind.Array } rounds)
            foreach (var r in rounds.EnumerateArray())
            {
                if (r.Get("playerStats") is not { ValueKind: JsonValueKind.Array } ps) continue;
                foreach (var p in ps.EnumerateArray())
                {
                    if (p.Str("subject") != puuid) continue;
                    if (p.Get("kills") is { ValueKind: JsonValueKind.Array } k && k.GetArrayLength() >= 3) highlights.Add(MatchEvents.RoundLabel(k.GetArrayLength()));
                    if (p.Get("damage") is { ValueKind: JsonValueKind.Array } dmg)
                        foreach (var d in dmg.EnumerateArray()) { heads += d.Int("headshots"); body += d.Int("bodyshots"); legs += d.Int("legshots"); }
                }
            }

        var charId = me.Str("characterId");
        long startMs = info.Get("gameStartMillis") is { ValueKind: JsonValueKind.Number } sm ? sm.GetInt64() : 0;
        long lenMs = info.Get("gameLengthMillis") is { ValueKind: JsonValueKind.Number } lm ? lm.GetInt64() : 0;
        var start = startMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(startMs).LocalDateTime : DateTime.Now.AddMinutes(-35);
        var rec = new MatchRecord
        {
            Id = id, Game = "VALORANT", Map = MapName(info.Str("mapId"), maps), Mode = ModeName(info.Str("queueID")),
            Character = agents != null && agents.TryGetValue(charId, out var a) && !string.IsNullOrEmpty(a) ? a : AgentName(charId),
            Result = result, Score = $"{mine} : {theirs}",
            Kills = me.Int("stats", "kills"), Deaths = me.Int("stats", "deaths"), Assists = me.Int("stats", "assists"),
            HeadshotPercent = heads + body + legs > 0 ? (int)Math.Round(100.0 * heads / (heads + body + legs)) : -1,
            Rank = info.Str("queueID") is "competitive" or "premier" ? RankName(me.Int("competitiveTier")) : "",
            RankTier = info.Str("queueID") is "competitive" or "premier" ? me.Int("competitiveTier") : 0,
            Highlights = highlights, Start = start, End = lenMs > 0 ? start.AddMilliseconds(lenMs) : DateTime.Now,
        };
        rec.Extras.Add($"Score {me.Int("stats", "score")}");
        return rec;
    }

    /// <summary>
    /// Your kills and deaths with when they happened (the match's start plus each kill's game time), so clips from the
    /// match can show them on their timeline.
    /// </summary>
    public static List<Moment> KillsAndDeaths(JsonElement root, string puuid)
    {
        var list = new List<Moment>();
        var info = root.Get("matchInfo") ?? default;
        if (info.Get("gameStartMillis") is not { ValueKind: JsonValueKind.Number } sm) return list;
        var start = DateTimeOffset.FromUnixTimeMilliseconds(sm.GetInt64()).LocalDateTime;
        IEnumerable<JsonElement> kills = Enumerable.Empty<JsonElement>();
        if (root.Get("kills") is { ValueKind: JsonValueKind.Array } top && top.GetArrayLength() > 0) kills = top.EnumerateArray();
        else if (root.Get("roundResults") is { ValueKind: JsonValueKind.Array } rounds)
            kills = rounds.EnumerateArray()
                .SelectMany(r => r.Get("playerStats") is { ValueKind: JsonValueKind.Array } ps ? ps.EnumerateArray() : Enumerable.Empty<JsonElement>())
                .SelectMany(p => p.Get("kills") is { ValueKind: JsonValueKind.Array } k ? k.EnumerateArray() : Enumerable.Empty<JsonElement>());
        foreach (var k in kills)
        {
            if (k.Get("gameTime") is not { ValueKind: JsonValueKind.Number } gt) continue;
            var when = start.AddMilliseconds(gt.GetInt64());
            if (k.Str("killer") == puuid && k.Str("victim") != puuid) list.Add(new Moment(when, Moment.Kill, "VALORANT"));
            if (k.Str("victim") == puuid) list.Add(new Moment(when, Moment.Death, "VALORANT"));
        }
        return list;
    }

    /// <summary>Agent IDs → names, used when Riot's content service can't be reached.</summary>
    static readonly Dictionary<string, string> Agents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["569fdd95-4d10-43ab-ca70-79becc718b46"] = "Sage", ["add6443a-41bd-e414-f6ad-e58d267f4e95"] = "Jett",
        ["eb93336a-449b-9c1b-0a54-a891f7921d69"] = "Phoenix", ["a3bfb853-43b2-7238-a4f1-ad90e9e46bcc"] = "Reyna",
        ["f94c3b30-42be-e959-889c-5aa313dba261"] = "Raze", ["320b2a48-4d9b-a075-30f1-1f93a9b638fa"] = "Sova",
        ["8e253930-4c05-31dd-1b6c-968525494517"] = "Omen", ["9f0d8ba9-4140-b941-57d3-a7ad57c6b417"] = "Brimstone",
        ["117ed9e3-49f3-6512-3ccf-0cada7e3823b"] = "Cypher", ["1e58de9c-4950-5125-93e9-a0aee9f98746"] = "Killjoy",
        ["707eab51-4836-f488-046a-cda6bf494859"] = "Viper", ["5f8d3a7f-467b-97f3-062c-13acf203c006"] = "Breach",
        ["6f2a04ca-43e0-be17-7f36-b3908627744d"] = "Skye", ["7f94d92c-4234-0a36-9646-3a87eb8b5c89"] = "Yoru",
        ["41fb69c1-4189-7b37-f117-bcaf1e96f1bf"] = "Astra", ["601dbbe7-43ce-be57-2a40-4abd24953621"] = "KAY/O",
        ["22697a3d-45bf-8dd7-4fec-84a9e28c69d7"] = "Chamber", ["bb2a4828-46eb-8cd1-e765-15848195d751"] = "Neon",
        ["dade69b4-4f5a-8528-247b-219e5a1facd6"] = "Fade", ["95b78ed7-4637-86d9-7e41-71ba8c293152"] = "Harbor",
        ["e370fa57-4757-3604-3648-499e1f642d3f"] = "Gekko", ["cc8b64c8-4b25-4ff9-6e7f-37b4da43d235"] = "Deadlock",
        ["0e38b510-41a8-5780-5e8f-568b2a4f2d6c"] = "Iso", ["1dbf2edd-4729-0984-3115-daa5eed44993"] = "Clove",
        ["efba5359-4016-a1e5-7626-b1ae76895940"] = "Vyse", ["b444168c-4e35-8076-db47-ef9bf368f384"] = "Tejo",
        ["df1cb487-4902-002e-5c17-d28e83e78588"] = "Waylay",
    };

    public static string AgentName(string id) => Agents.TryGetValue(id ?? "", out var n) ? n : "";
}
