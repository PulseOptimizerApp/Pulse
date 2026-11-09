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
/// <para>
/// To have the recap ready the moment a match ends, Pulse watches your own presence on the Riot Client's local API
/// (the same "in game / in menus" state your friends list shows). When it goes from in game to anything else, Pulse
/// asks for the match every few seconds until Riot has it, usually within half a minute.
/// </para>
/// </summary>
public sealed class ValorantMatches : IDisposable
{
    internal static readonly HttpClient Http = new(new HttpClientHandler
    {
        // The Riot Client's local API (127.0.0.1) uses its own certificate; Riot's servers use normal ones.
        ServerCertificateCustomValidationCallback = (req, _, _, errors) => req.RequestUri?.Host == "127.0.0.1" || errors == System.Net.Security.SslPolicyErrors.None,
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    // Riot's documented client platform header value (Windows PC), base64 JSON.
    internal const string ClientPlatform = "ew0KCSJwbGF0Zm9ybVR5cGUiOiAiUEMiLA0KCSJwbGF0Zm9ybU9TIjogIldpbmRvd3MiLA0KCSJwbGF0Zm9ybU9TVmVyc2lvbiI6ICIxMC4wLjE5MDQyLjEuMjU2LjY0Yml0IiwNCgkicGxhdGZvcm1DaGlwc2V0IjogIlVua25vd24iDQp9";

    CancellationTokenSource _cts;
    string _lastSeen;

    /// <summary>Raised (on a worker thread) when you load into a match.</summary>
    public static event Action InGame;
    /// <summary>How often the presence is read, and how long to keep asking for a match after one ends.</summary>
    static readonly TimeSpan PresenceEvery = TimeSpan.FromSeconds(3), CatchUpFor = TimeSpan.FromMinutes(3);
    Dictionary<string, string> _agents, _maps;

    public static bool Running()
    {
        return Pulse.Core.Measurement.GameDetector.IsRunning("VALORANT-Win64-Shipping");
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try { _lastSeen ??= await LatestMatchId(); } catch { }
            string before = null;
            var slow = DateTime.UtcNow.AddSeconds(90);
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(PresenceEvery, token); } catch { break; }
                string now = null;
                try { now = await LoopState(); } catch { }
                // Left the match (to the menus, the end screen or the lobby): get the result right away.
                if (before == "INGAME" && now != null && now != "INGAME") await CatchUp(token);
                // Just loaded into a match: the live lobby (if it's on) can show both teams.
                if (now == "INGAME" && before != null && before != "INGAME") try { InGame?.Invoke(); } catch { }
                if (now != null) before = now;
                // A slow check too, in case the presence can't be read on this PC.
                if (DateTime.UtcNow >= slow)
                {
                    slow = DateTime.UtcNow.AddSeconds(90);
                    try { await Check(); } catch { }
                }
            }
        });
    }

    /// <summary>A match just ended: ask for it every few seconds until Riot has it (it takes a few seconds to a minute).</summary>
    async Task CatchUp(CancellationToken token)
    {
        var until = DateTime.UtcNow + CatchUpFor;
        var wait = TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < until)
        {
            try { await Task.Delay(wait, token); } catch { return; }
            try { if (await Check()) return; } catch { }
            wait = TimeSpan.FromSeconds(Math.Min(10, wait.TotalSeconds + 2));
        }
    }

    /// <summary>VALORANT closed: keep looking for the match that just ended for a few minutes.</summary>
    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        _ = Task.Run(() => CatchUp(CancellationToken.None));
    }

    public void Dispose() { _cts?.Cancel(); _cts = null; }

    readonly SemaphoreSlim _checking = new(1, 1);

    /// <summary>Looks for a new match. True when one was found (or there's nothing left to wait for).</summary>
    async Task<bool> Check()
    {
        if (!await _checking.WaitAsync(0)) return false;
        try
        {
            var id = await LatestMatchId();
            if (id == null || id == _lastSeen) return false;
            if (MatchStore.All.Any(m => m.Id == "val-" + id)) { _lastSeen = id; return true; }
            var rec = await Details(id);
            if (rec == null) return false;   // listed but details not ready yet: try again shortly
            _lastSeen = id;
            MatchEvents.RaiseFinished(rec);
            return true;
        }
        finally { _checking.Release(); }
    }

    // ───────── Riot Client login and region ─────────

    internal record Session(string Puuid, string Access, string Entitlement, string Shard, string Version, string Port, string Password, string Region = "");

    // The login is kept for a few minutes, so checking every few seconds after a match doesn't re-read the game's log.
    static Session _session;
    static DateTime _sessionAt;

    internal static async Task<Session> Login()
    {
        if (_session != null && DateTime.UtcNow - _sessionAt < TimeSpan.FromMinutes(10)) return _session;
        var s = await FreshLogin();
        _session = s;
        _sessionAt = DateTime.UtcNow;
        return s;
    }

    static void Forget() => _session = null;

    internal static HttpRequestMessage Local(Session s, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{s.Port}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes("riot:" + s.Password)));
        return req;
    }

    /// <summary>The Riot account the Riot Client was last signed in to (null until Pulse has connected once this session).</summary>
    public static string SignedIn { get; private set; }

    /// <summary>Why the last login failed, in words for the user (null when it worked).</summary>
    public static string LastLoginProblem { get; private set; }

    /// <summary>
    /// Every Riot Client lockfile on this PC, newest first: the current Windows profile first, then the others. Pulse
    /// runs as administrator, and when that's a different Windows account its own profile has no Riot Client in it.
    /// </summary>
    static List<string> Lockfiles()
    {
        var rel = Path.Combine("Riot Games", "Riot Client", "Config", "lockfile");
        var list = new List<string>();
        var mine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), rel);
        if (File.Exists(mine)) list.Add(mine);
        try
        {
            var users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (users != null)
                foreach (var dir in Directory.EnumerateDirectories(users))
                {
                    var f = Path.Combine(dir, "AppData", "Local", rel);
                    try { if (File.Exists(f) && !list.Contains(f, StringComparer.OrdinalIgnoreCase)) list.Add(f); } catch { }
                }
        }
        catch { }
        return list.Take(1).Concat(list.Skip(1).OrderByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } })).ToList();
    }

    /// <summary>Riot's platform shard for a region (the pd.*.a.pvp.net host).</summary>
    internal static string ShardOf(string region) => region?.ToLowerInvariant() switch
    {
        "na" or "latam" or "br" or "pbe" => "na",
        "eu" => "eu",
        "ap" => "ap",
        "kr" => "kr",
        _ => "",
    };

    static async Task<Session> FreshLogin()
    {
        var files = Lockfiles();
        if (files.Count == 0) { LastLoginProblem = "The Riot Client isn't running. Open VALORANT or the Riot Client, then try again."; return null; }
        LastLoginProblem = "The Riot Client didn't answer. If it's open, make sure you're signed in, then try again.";
        foreach (var lockfile in files)
        {
            try
            {
                string text;
                using (var fs = new FileStream(lockfile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs)) text = await r.ReadToEndAsync();
                var parts = text.Split(':');
                if (parts.Length < 5) continue;
                var local = new Session(null, null, null, null, null, parts[2], parts[3]);
                using var req = Local(local, "/entitlements/v1/token");
                using var res = await Http.SendAsync(req);
                if (!res.IsSuccessStatusCode) continue;   // an old lockfile from a client that has closed, or signed out
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                if (string.IsNullOrEmpty(root.Str("accessToken"))) continue;

                // Region and version: VALORANT's log in the same Windows profile when it has them, else the Riot Client.
                string shard = "", region = "", version = "";
                var profileLocal = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(lockfile)!, "..", "..", ".."));
                var log = Path.Combine(profileLocal, "VALORANT", "Saved", "Logs", "ShooterGame.log");
                if (File.Exists(log))
                {
                    string logText;
                    using (var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var r = new StreamReader(fs)) logText = await r.ReadToEndAsync();
                    var glz = Regex.Match(logText, @"https://glz-([a-z0-9]+)-1\.([a-z0-9]+)\.a\.pvp\.net");
                    if (glz.Success) { region = glz.Groups[1].Value; shard = glz.Groups[2].Value; }
                    version = Regex.Match(logText, @"CI server version: (\S+)").Groups[1].Value;
                }
                if (shard.Length == 0)
                {
                    try
                    {
                        using var rq = Local(local, "/riotclient/region-locale");
                        using var rs = await Http.SendAsync(rq);
                        if (rs.IsSuccessStatusCode)
                        {
                            using var rd = JsonDocument.Parse(await rs.Content.ReadAsStringAsync());
                            region = rd.RootElement.Str("region").ToLowerInvariant();
                            shard = ShardOf(region);
                        }
                    }
                    catch { }
                }
                if (version.Length == 0)
                {
                    try
                    {
                        using var rq = Local(local, "/product-session/v1/external-sessions");
                        using var rs = await Http.SendAsync(rq);
                        if (rs.IsSuccessStatusCode)
                        {
                            using var sd = JsonDocument.Parse(await rs.Content.ReadAsStringAsync());
                            foreach (var p in sd.RootElement.EnumerateObject())
                                if (p.Value.Str("productId") == "valorant" && p.Value.Str("version") is { Length: > 0 } v) { version = v; break; }
                        }
                    }
                    catch { }
                }
                if (shard.Length == 0) { LastLoginProblem = "Pulse couldn't tell which region your account is in. Start VALORANT once, then try again."; continue; }
                LastLoginProblem = null;
                SignedIn = root.Str("subject");
                return new Session(root.Str("subject"), root.Str("accessToken"), root.Str("token"), shard, version, parts[2], parts[3], region);
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Your own VALORANT state from the Riot Client's presence: "INGAME", "PREGAME" (agent select) or "MENUS".
    /// Null when it can't be read.
    /// </summary>
    static async Task<string> LoopState()
    {
        var s = await Login();
        if (s == null) return null;
        using var req = Local(s, "/chat/v4/presences");
        using var res = await Http.SendAsync(req);
        if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) { Forget(); return null; }
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        if (doc.RootElement.Get("presences") is not { ValueKind: JsonValueKind.Array } list) return null;
        foreach (var p in list.EnumerateArray())
            if (p.Str("puuid") == s.Puuid && p.Str("product") == "valorant")
                return StateFromPresence(p.Str("private"));
        return null;
    }

    /// <summary>Reads sessionLoopState from a presence's base64 "private" part (old and new layouts).</summary>
    internal static string StateFromPresence(string privateBase64)
    {
        if (string.IsNullOrEmpty(privateBase64)) return null;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(privateBase64));
            var m = Regex.Match(json, "\"sessionLoopState\"\\s*:\\s*\"([A-Z]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    internal static async Task<JsonDocument> Get(Session s, string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Access);
        req.Headers.Add("X-Riot-Entitlements-JWT", s.Entitlement);
        req.Headers.Add("X-Riot-ClientPlatform", ClientPlatform);
        if (s.Version.Length > 0) req.Headers.Add("X-Riot-ClientVersion", s.Version);
        using var res = await Http.SendAsync(req);
        if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) Forget();   // token expired: log in again next time
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
        var marks = await RoundStarts();
        MatchEvents.RaiseMoments(KillsAndDeaths(doc.RootElement, s.Puuid, marks));
        SaveForCoach(doc.RootElement, s.Puuid, marks);
        return Parse(doc.RootElement, s.Puuid, _agents, _maps, "val-" + id);
    }

    /// <summary>A match you just finished was saved (with everyone's names in memory): for the after-match lobby.</summary>
    public static event Action<Pulse.Core.Games.Valorant.VMatch> MatchSaved;

    /// <summary>Keeps the full match (every kill with where everyone stood) for the coach, heatmaps and death review.</summary>
    void SaveForCoach(JsonElement root, string puuid, RoundMarks marks)
    {
        try
        {
            var vm = Pulse.Core.Games.Valorant.VMatch.Parse(root, puuid, _agents, _maps);
            if (vm == null) return;
            vm.ZeroUtc = ZeroUtc(root, marks);
            Pulse.Core.Games.Valorant.VStore.Save(vm);
            try { MatchSaved?.Invoke(vm); } catch { }
        }
        catch { }
    }

    /// <summary>What a download of recent matches did: how many it added, or why it couldn't.</summary>
    /// <summary>Added: new matches saved. Found: new ones Riot listed. Scanned: games in your history looked at. Real: of those, 5v5 matches (not Deathmatch etc.).</summary>
    public sealed record BackfillResult(int Added, int Found, string Error, int Scanned = 0, int Real = 0);

    /// <summary>
    /// Downloads your recent matches the coach doesn't have yet (up to <paramref name="count"/>), one every second or
    /// so, so the stats have something to work with from the first day. Only your own match history is read. Runs off
    /// the UI thread; progress reports the step ("list", "match") and how far it is.
    /// </summary>
    public static Task<BackfillResult> Backfill(int count, IProgress<(string Step, int Done, int Total)> progress = null, CancellationToken ct = default) =>
        Task.Run(() => BackfillCore(count, progress, ct), ct);

    static async Task<BackfillResult> BackfillCore(int count, IProgress<(string Step, int Done, int Total)> progress, CancellationToken ct)
    {
        Session s;
        try { s = await Login(); }
        catch (Exception ex) { return Fail("Couldn't connect to the Riot Client", ex.Message); }
        if (s == null) return new BackfillResult(0, 0, "no-client");
        progress?.Report(("list", 0, 0));

        // Your history mixes every mode (Deathmatch, Team Deathmatch…); page back up to 100 games to find enough real matches.
        var ids = new List<string>();
        int seen = 0, scanned = 0;
        for (int start = 0; start < 100 && seen < count; start += 20)
        {
            var (history, code) = await GetStatus(s, $"https://pd.{s.Shard}.a.pvp.net/match-history/v1/history/{s.Puuid}?startIndex={start}&endIndex={start + 20}");
            if (history == null)
            {
                if (start > 0) break; // keep what the first pages found
                if (code is 401 or 403) { Forget(); return Fail("Riot asked to log in again", $"HTTP {code}"); }
                return Fail(code == 429 ? "Riot says too many requests" : code == 0 ? "Riot's servers didn't answer" : "Riot refused the request", code == 0 ? "timeout" : $"HTTP {code}");
            }
            int got;
            using (history)
            {
                if (history.RootElement.Get("History") is not { ValueKind: JsonValueKind.Array } h) break;
                got = h.GetArrayLength();
                scanned += got;
                foreach (var x in h.EnumerateArray().Where(x => Pulse.Core.Games.Valorant.VMatch.Counts(x.Str("QueueID"))))
                {
                    seen++;
                    var id = x.Str("MatchID");
                    if (id.Length > 0 && (!Pulse.Core.Games.Valorant.VStore.Has(id) || Pulse.Core.Games.Valorant.VStore.Incomplete(id)) && !ids.Contains(id)) ids.Add(id);
                }
            }
            if (got < 20) break; // that was the last page
            await Task.Delay(600, ct);
        }
        TweakEngine.AddLog("valorant", $"Match download: {scanned} games in your history, {seen} real matches, {ids.Count} new");
        if (ids.Count == 0) return new BackfillResult(0, 0, null, scanned, seen);

        // Names are a nicety (Pulse has its own list); never let them hold the download up.
        var self = new ValorantMatches();
        try { await self.LoadNames(s).WaitAsync(TimeSpan.FromSeconds(10), ct); } catch { }
        RoundMarks marks = null;
        try { marks = await RoundStarts(); } catch { }

        // Three at a time, with a short pause between each: about a second a match instead of two, still gentle.
        int added = 0, failed = 0, done = 0;
        string lastError = null;
        using var gate = new SemaphoreSlim(3);
        async Task One(string id)
        {
            await gate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                var (d, c) = await GetStatus(s, $"https://pd.{s.Shard}.a.pvp.net/match-details/v1/matches/{id}");
                using (d)
                {
                    if (d != null && Pulse.Core.Games.Valorant.VMatch.Parse(d.RootElement, s.Puuid, self._agents, self._maps) is { } vm)
                    {
                        vm.ZeroUtc = ZeroUtc(d.RootElement, marks); // the log still has the last session's matches
                        Pulse.Core.Games.Valorant.VStore.Save(vm);
                        Interlocked.Increment(ref added);
                    }
                    else { Interlocked.Increment(ref failed); lastError = d == null ? $"HTTP {c}" : "unreadable match"; }
                }
                await Task.Delay(c == 429 ? 5000 : 300, ct); // Riot asked to slow down, or a short breather
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Interlocked.Increment(ref failed); lastError = ex.Message; }
            finally
            {
                gate.Release();
                progress?.Report(("match", Interlocked.Increment(ref done), ids.Count));
            }
        }
        await Task.WhenAll(ids.Select(One));
        progress?.Report(("match", ids.Count, ids.Count));
        if (failed > 0) TweakEngine.AddLog("valorant", $"Coach download: {added} added, {failed} failed ({lastError})");
        return new BackfillResult(added, ids.Count, added == 0 && failed > 0 ? "Riot didn't send the matches (" + lastError + ")" : null, scanned, seen);

        static BackfillResult Fail(string why, string detail)
        {
            TweakEngine.AddLog("valorant", $"Coach download failed: {why} ({detail})");
            return new BackfillResult(0, 0, why);
        }
    }

    /// <summary>Like <see cref="Get"/>, with the HTTP status (0 when Riot didn't answer at all).</summary>
    static async Task<(JsonDocument Doc, int Status)> GetStatus(Session s, string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Access);
            req.Headers.Add("X-Riot-Entitlements-JWT", s.Entitlement);
            req.Headers.Add("X-Riot-ClientPlatform", ClientPlatform);
            if (s.Version.Length > 0) req.Headers.Add("X-Riot-ClientVersion", s.Version);
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return (null, (int)res.StatusCode);
            return (JsonDocument.Parse(await res.Content.ReadAsStringAsync()), (int)res.StatusCode);
        }
        catch { return (null, 0); }
    }

    /// <summary>Your Riot account id, when the Riot Client is running (null otherwise).</summary>
    public static async Task<string> MyId() => (await Login())?.Puuid;

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

    // ───────── when each kill happened, on this PC's clock ─────────

    /// <summary>
    /// When rounds started on this PC, from VALORANT's own log: every round writes "Gameplay started at local time 0.x"
    /// when the buy phase opens and "... 30.x" (45.x after side swaps) when the barriers drop. Times are UTC.
    /// </summary>
    public sealed record RoundMarks(List<DateTime> BuyStarts, List<DateTime> BarrierDrops);

    static readonly Regex LogLine = new(@"^\[(\d{4})\.(\d\d)\.(\d\d)-(\d\d)\.(\d\d)\.(\d\d):(\d{3})\]\[\s*\d+\](.*)$", RegexOptions.Multiline);
    static readonly Regex GameplayStarted = new(@"Gameplay started at local time (\d+(?:\.\d+)?)");

    static async Task<RoundMarks> RoundStarts()
    {
        try
        {
            var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VALORANT", "Saved", "Logs", "ShooterGame.log");
            if (!File.Exists(log)) return null;
            string text;
            using (var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs)) text = await r.ReadToEndAsync();
            return ParseRoundMarks(text, File.GetLastWriteTimeUtc(log));
        }
        catch { return null; }
    }

    /// <summary>
    /// Reads the round markers. The log's clock is UTC or local depending on the PC, so it's lined up against the
    /// file's own last-write time (whole hours only; anything else is left as it is).
    /// </summary>
    internal static RoundMarks ParseRoundMarks(string text, DateTime lastWriteUtc)
    {
        var buy = new List<DateTime>();
        var drop = new List<DateTime>();
        DateTime last = default;
        foreach (Match m in LogLine.Matches(text))
        {
            DateTime t;
            try
            {
                t = new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                    int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value), int.Parse(m.Groups[7].Value), DateTimeKind.Utc);
            }
            catch { continue; }
            last = t;
            var g = GameplayStarted.Match(m.Groups[8].Value);
            if (!g.Success || !double.TryParse(g.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var at)) continue;
            if (at < 2) buy.Add(t);
            else if (at >= 25 && at <= 50) drop.Add(t);
        }
        if (last != default && lastWriteUtc != default)
        {
            var hours = Math.Round((lastWriteUtc - last).TotalHours);
            var rest = Math.Abs((lastWriteUtc - last).TotalHours - hours);
            if (hours != 0 && Math.Abs(hours) <= 14 && rest < 0.1)
            {
                buy = buy.Select(x => x.AddHours(hours)).ToList();
                drop = drop.Select(x => x.AddHours(hours)).ToList();
            }
        }
        return new RoundMarks(buy, drop);
    }

    /// <summary>
    /// Finds the real (UTC) time of game time 0. Each kill's game time minus its round time is when its round started
    /// in game time; that pattern of round starts is matched against the log's round starts (buy phase or barrier drop,
    /// whichever lines up), staying within a few minutes of Riot's own start time so another match in the same log is
    /// never picked. Null when it can't be lined up (two rounds must agree when there are two or more).
    /// </summary>
    internal static DateTime? Align(IReadOnlyList<(long GameMs, long RoundMs)> kills, RoundMarks marks, DateTime roughZeroUtc)
    {
        if (marks == null || kills.Count == 0) return null;
        var rounds = kills.Select(k => (double)(k.GameMs - k.RoundMs)).Where(g => g >= 0)
            .OrderBy(g => g).Aggregate(new List<double>(), (l, g) => { if (l.Count == 0 || g - l[^1] > 3000) l.Add(g); return l; });
        if (rounds.Count == 0) return null;
        (DateTime Zero, int Votes, double Error) best = default;
        foreach (var list in new[] { marks.BuyStarts, marks.BarrierDrops })
            foreach (var mark in list)
                foreach (var g in rounds)
                {
                    var zero = mark.AddMilliseconds(-g);
                    if (Math.Abs((zero - roughZeroUtc).TotalMinutes) > 3) continue;
                    int votes = 0; double err = 0;
                    foreach (var r in rounds)
                    {
                        var expect = zero.AddMilliseconds(r);
                        var near = list.Select(x => Math.Abs((x - expect).TotalMilliseconds)).DefaultIfEmpty(double.MaxValue).Min();
                        if (near <= 1500) { votes++; err += near; }
                    }
                    if (votes > best.Votes || votes == best.Votes && votes > 0 && err < best.Error) best = (zero, votes, err);
                }
        return best.Votes >= Math.Min(2, rounds.Count) ? best.Zero : null;
    }

    /// <summary>
    /// Your kills and deaths with when they happened, so clips from the match can show them on their timeline. Times
    /// come from lining the match up with VALORANT's own round log (exact to a fraction of a second); without the log,
    /// they fall back to the match's start time from Riot.
    /// </summary>
    public static List<Moment> KillsAndDeaths(JsonElement root, string puuid, RoundMarks marks = null)
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
        var all = kills.Where(k => k.Get("gameTime") is { ValueKind: JsonValueKind.Number }).ToList();
        if (ZeroUtc(root, marks) is DateTime z) start = z.ToLocalTime();
        foreach (var k in all)
        {
            var when = start.AddMilliseconds(k.Get("gameTime").Value.GetInt64());
            if (k.Str("killer") == puuid && k.Str("victim") != puuid) list.Add(new Moment(when, Moment.Kill, "VALORANT"));
            if (k.Str("victim") == puuid) list.Add(new Moment(when, Moment.Death, "VALORANT"));
        }
        return list;
    }

    /// <summary>When game time 0 was on this PC's clock (UTC), lined up with VALORANT's round log; null when it can't be.</summary>
    internal static DateTime? ZeroUtc(JsonElement root, RoundMarks marks)
    {
        var info = root.Get("matchInfo") ?? default;
        if (marks == null || info.Get("gameStartMillis") is not { ValueKind: JsonValueKind.Number } sm) return null;
        IEnumerable<JsonElement> kills = Enumerable.Empty<JsonElement>();
        if (root.Get("kills") is { ValueKind: JsonValueKind.Array } top && top.GetArrayLength() > 0) kills = top.EnumerateArray();
        else if (root.Get("roundResults") is { ValueKind: JsonValueKind.Array } rounds)
            kills = rounds.EnumerateArray()
                .SelectMany(r => r.Get("playerStats") is { ValueKind: JsonValueKind.Array } ps ? ps.EnumerateArray() : Enumerable.Empty<JsonElement>())
                .SelectMany(p => p.Get("kills") is { ValueKind: JsonValueKind.Array } k ? k.EnumerateArray() : Enumerable.Empty<JsonElement>());
        var pairs = kills.Where(k => k.Get("gameTime") is { ValueKind: JsonValueKind.Number } && k.Get("roundTime") is { ValueKind: JsonValueKind.Number })
            .Select(k => (k.Get("gameTime").Value.GetInt64(), k.Get("roundTime").Value.GetInt64())).ToList();
        return Align(pairs, marks, DateTimeOffset.FromUnixTimeMilliseconds(sm.GetInt64()).UtcDateTime);
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

    /// <summary>An agent's name by id: Pulse's own list, then the game content embedded at build time, then the Riot Client's.</summary>
    public static string AgentName(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        if (Agents.TryGetValue(id, out var n)) return n;
        try { if (Pulse.Core.Games.Valorant.MapGeo.AgentById(id) is { Length: > 0 } m) return m; } catch { }
        return ValorantLobby.ContentAgent(id) ?? "";
    }
}
