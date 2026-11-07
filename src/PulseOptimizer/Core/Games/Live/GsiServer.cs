using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Pulse.Core.Games.Live;

/// <summary>
/// Valve's official Game State Integration for Counter-Strike 2 and Dota 2: the game itself sends its state (your
/// kills, the round, the map) to a small listener Pulse runs on this PC only (127.0.0.1). Valve made this for tools
/// like this one; it's VAC-safe and reads nothing from the game's memory. Setting up means writing one small .cfg
/// file into the game's folder, which the game reads at launch.
/// </summary>
public sealed class GsiServer : IDisposable
{
    public const int Port = 3396;
    readonly HttpListener _http = new();
    readonly string _token;
    readonly Cs2Tracker _cs2 = new();
    readonly DotaTracker _dota = new();
    volatile bool _running;

    public GsiServer(string token) { _token = token; }

    public bool Start()
    {
        try
        {
            _http.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _http.Start();
            _running = true;
            _ = Task.Run(Loop);
            return true;
        }
        catch { return false; }
    }

    async Task Loop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); } catch { break; }
            try
            {
                string body;
                using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync();
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (!root.TryGetProperty("auth", out var auth) || auth.GetProperty("token").GetString() != _token) continue;
                if (ctx.Request.Url?.AbsolutePath.StartsWith("/dota") == true) _dota.Update(root);
                else _cs2.Update(root);
            }
            catch { try { ctx.Response.Abort(); } catch { } }
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _http.Stop(); _http.Close(); } catch { }
    }

    // ───────── setup ─────────

    /// <summary>Install folders of Steam games by app id (730 = Counter-Strike 2, 570 = Dota 2).</summary>
    public static string SteamGameDir(int appId)
    {
        try
        {
            if (Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") is not string steam) return null;
            var vdf = Path.Combine(steam.Replace('/', '\\'), "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) return null;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
            {
                var apps = Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps");
                var acf = Path.Combine(apps, $"appmanifest_{appId}.acf");
                if (!File.Exists(acf)) continue;
                var dir = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"").Groups[1].Value;
                if (dir.Length > 0) return Path.Combine(apps, "common", dir);
            }
        }
        catch { }
        return null;
    }

    static string Cfg(string path, string token, string data) =>
        "\"Pulse Optimizer\"\n{\n" +
        $"  \"uri\" \"http://127.0.0.1:{Port}/{path}\"\n  \"timeout\" \"1.0\"\n  \"buffer\" \"0.0\"\n  \"throttle\" \"0.1\"\n  \"heartbeat\" \"5.0\"\n" +
        $"  \"auth\"\n  {{\n    \"token\" \"{token}\"\n  }}\n  \"data\"\n  {{\n{data}  }}\n}}\n";

    /// <summary>Writes the .cfg files for the installed games. Returns the games set up.</summary>
    public static List<string> Install(string token)
    {
        var done = new List<string>();
        var cs2 = SteamGameDir(730);
        if (cs2 != null && Directory.Exists(Path.Combine(cs2, "game", "csgo", "cfg")))
        {
            try
            {
                File.WriteAllText(Path.Combine(cs2, "game", "csgo", "cfg", "gamestate_integration_pulse.cfg"),
                    Cfg("cs2", token, "    \"provider\" \"1\"\n    \"map\" \"1\"\n    \"round\" \"1\"\n    \"player_id\" \"1\"\n    \"player_state\" \"1\"\n    \"player_match_stats\" \"1\"\n"));
                done.Add("Counter-Strike 2");
            }
            catch { }
        }
        var dota = SteamGameDir(570);
        if (dota != null && Directory.Exists(Path.Combine(dota, "game", "dota", "cfg")))
        {
            try
            {
                var dir = Path.Combine(dota, "game", "dota", "cfg", "gamestate_integration");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "gamestate_integration_pulse.cfg"),
                    Cfg("dota", token, "    \"provider\" \"1\"\n    \"map\" \"1\"\n    \"player\" \"1\"\n    \"hero\" \"1\"\n"));
                done.Add("Dota 2");
            }
            catch { }
        }
        return done;
    }
}

static class Json
{
    public static JsonElement? Get(this JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        }
        return e;
    }
    public static int Int(this JsonElement e, params string[] path) =>
        e.Get(path) is { } v && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
    public static string Str(this JsonElement e, params string[] path) =>
        e.Get(path) is { } v ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : "";
}

/// <summary>Counter-Strike 2: clips 3K / 4K / ACE rounds and makes a recap at the end of the match.</summary>
public sealed class Cs2Tracker
{
    string _map;
    DateTime _start;
    int _round = -1, _roundKills, _roundHs, _totalHs, _best3k;
    DateTime _firstKillAt, _lastKillAt;
    bool _pending, _finished, _savedThisRound;
    int _lastHealth = -1;
    readonly List<string> _highlights = new();
    JsonElement _lastStats;
    string _team = "";
    System.Threading.Timer _timer;

    public void Update(JsonElement root)
    {
        var me = root.Str("provider", "steamid");
        var player = root.Str("player", "steamid");
        var map = root.Str("map", "name");
        var phase = root.Str("map", "phase");
        if (string.IsNullOrEmpty(map)) return;

        if (map != _map || (phase is "warmup" && _finished))
        {
            // A new match.
            _map = map; _start = DateTime.Now; _round = -1; _totalHs = 0; _highlights.Clear(); _finished = false; _pending = false;
        }
        int round = root.Int("map", "round");
        if (round != _round)
        {
            Flush();
            _totalHs += _roundHs;
            _round = round; _roundKills = 0; _roundHs = 0; _savedThisRound = false;
        }

        if (player == me && phase != "warmup")
        {
            _team = root.Str("player", "team");
            if (root.Get("player", "match_stats") is { } ms) _lastStats = ms.Clone();
            int k = root.Int("player", "state", "round_kills");
            int hs = root.Int("player", "state", "round_killhs");
            int health = root.Int("player", "state", "health");
            if (_lastHealth > 0 && health == 0) MatchEvents.RaiseMoment("Counter-Strike 2", Moment.Death);
            _lastHealth = health;
            if (k > _roundKills)
            {
                for (int i = _roundKills; i < k; i++) MatchEvents.RaiseMoment("Counter-Strike 2", Moment.Kill);
                if (_roundKills == 0) _firstKillAt = DateTime.Now;
                _lastKillAt = DateTime.Now;
                _roundKills = k;
                if (k >= 3) { _pending = true; ArmTimer(); }
            }
            _roundHs = Math.Max(_roundHs, hs);
        }
        if (root.Str("round", "phase") == "over") Flush();

        if (phase == "gameover" && !_finished)
        {
            _finished = true;
            Flush();
            _totalHs += _roundHs; _roundHs = 0;
            int ct = root.Int("map", "team_ct", "score"), t = root.Int("map", "team_t", "score");
            int mine = _team == "CT" ? ct : t, theirs = _team == "CT" ? t : ct;
            var m = new MatchRecord
            {
                Game = "Counter-Strike 2", Map = MatchEvents.PrettyCsMap(map), Mode = Cap(root.Str("map", "mode")),
                Character = _team == "CT" ? "Counter-Terrorists" : _team == "T" ? "Terrorists" : "",
                Result = mine > theirs ? "Win" : mine < theirs ? "Loss" : "Draw", Score = $"{mine} : {theirs}",
                Start = _start, End = DateTime.Now, Highlights = _highlights.ToList(),
            };
            if (_lastStats.ValueKind == JsonValueKind.Object)
            {
                m.Kills = _lastStats.Int("kills"); m.Deaths = _lastStats.Int("deaths"); m.Assists = _lastStats.Int("assists");
                m.Extras.Add($"MVPs {_lastStats.Int("mvps")}");
                m.Extras.Add($"Score {_lastStats.Int("score")}");
                if (m.Kills > 0) m.HeadshotPercent = (int)Math.Round(100.0 * _totalHs / m.Kills);
            }
            MatchEvents.RaiseFinished(m);
        }
    }

    static string Cap(string s) => string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s[1..];

    void ArmTimer()
    {
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => { if ((DateTime.Now - _lastKillAt).TotalSeconds >= 6) Flush(); }, null, 6500, Timeout.Infinite);
    }

    readonly object _gate = new();

    /// <summary>Saves the round's big play once (at round end, or 6 seconds after the last kill).</summary>
    void Flush()
    {
        lock (_gate)
        {
            if (!_pending) return;
            _pending = false;
            var label = MatchEvents.RoundLabel(_roundKills);
            // A 3K that turns into a 4K later in the same round is one play, not two.
            if (_savedThisRound && _highlights.Count > 0) _highlights[^1] = label;
            else _highlights.Add(label);
            _savedThisRound = true;
            int secs = (int)Math.Clamp((DateTime.Now - _firstKillAt).TotalSeconds + 6, 15, 120);
            MatchEvents.RaiseHighlight("Counter-Strike 2", label, secs);
        }
    }
}

/// <summary>Dota 2: clips triple kills and better, and makes a recap when the game ends.</summary>
public sealed class DotaTracker
{
    readonly List<DateTime> _kills = new();
    int _lastKills;
    DateTime _start = DateTime.Now;
    bool _finished;
    string _matchId = "";
    readonly List<string> _highlights = new();
    int _pendingStreak, _lastDeaths = -1;
    System.Threading.Timer _timer;

    void FlushStreak()
    {
        int streak = Interlocked.Exchange(ref _pendingStreak, 0);
        if (streak < 3) return;
        var label = streak switch { 3 => "Triple kill", 4 => "Ultra kill", _ => "Rampage" };
        lock (_highlights) _highlights.Add(label);
        MatchEvents.RaiseHighlight("Dota 2", label, Math.Clamp(25 + 18 * (streak - 1), 30, 120));
    }

    public void Update(JsonElement root)
    {
        var id = root.Str("map", "matchid");
        if (id != _matchId) { _matchId = id; _start = DateTime.Now; _lastKills = 0; _lastDeaths = -1; _finished = false; _kills.Clear(); _highlights.Clear(); }
        int kills = root.Int("player", "kills"), deaths = root.Int("player", "deaths");
        if (_lastDeaths >= 0 && deaths > _lastDeaths) MatchEvents.RaiseMoment("Dota 2", Moment.Death);
        _lastDeaths = deaths;
        if (kills > _lastKills)
        {
            for (int i = _lastKills; i < kills; i++) { _kills.Add(DateTime.Now); MatchEvents.RaiseMoment("Dota 2", Moment.Kill); }
            _lastKills = kills;
            // Dota's multi-kill window is 18 seconds between kills.
            int streak = 1;
            for (int i = _kills.Count - 1; i > 0 && (_kills[i] - _kills[i - 1]).TotalSeconds <= 18; i--) streak++;
            if (streak >= 3)
            {
                // Wait until the streak is over (18 s without a kill), so a Rampage is one clip, not three.
                _pendingStreak = streak;
                _timer?.Dispose();
                _timer = new System.Threading.Timer(_ => FlushStreak(), null, 19_000, Timeout.Infinite);
            }
        }
        var state = root.Str("map", "game_state");
        if (state == "DOTA_GAMERULES_STATE_POST_GAME" && !_finished)
        {
            _finished = true;
            FlushStreak();
            var team = root.Str("player", "team_name");
            var win = root.Str("map", "win_team");
            int rad = root.Int("map", "radiant_score"), dire = root.Int("map", "dire_score");
            MatchEvents.RaiseFinished(new MatchRecord
            {
                Game = "Dota 2", Map = "", Character = MatchEvents.PrettyDotaHero(root.Str("hero", "name")),
                Result = win == "none" || string.IsNullOrEmpty(win) ? "" : win == team ? "Win" : "Loss",
                Score = team == "dire" ? $"{dire} : {rad}" : $"{rad} : {dire}",
                Kills = root.Int("player", "kills"), Deaths = root.Int("player", "deaths"), Assists = root.Int("player", "assists"),
                Extras = { $"Last hits {root.Int("player", "last_hits")}", $"GPM {root.Int("player", "gpm")}" },
                Start = _start, End = DateTime.Now, Highlights = _highlights.ToList(),
            });
        }
    }
}
