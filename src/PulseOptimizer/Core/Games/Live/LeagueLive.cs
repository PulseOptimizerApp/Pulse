using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace Pulse.Core.Games.Live;

/// <summary>
/// League of Legends: Riot's official Live Client Data API, which the game serves on this PC (127.0.0.1:2999) while
/// you're in a match. Pulse reads it once a second: triple, quadra and penta kills and aces get clipped, and the end
/// of the game makes a recap. Nothing leaves this PC.
/// </summary>
public sealed class LeagueLive : IDisposable
{
    static readonly HttpClient Http = new(new HttpClientHandler
    {
        // The game uses its own certificate for 127.0.0.1; accept it only for this address.
        ServerCertificateCustomValidationCallback = (req, _, _, _) => req.RequestUri?.Host == "127.0.0.1",
    })
    { Timeout = TimeSpan.FromSeconds(2) };

    const string Url = "https://127.0.0.1:2999/liveclientdata/allgamedata";

    CancellationTokenSource _cts;
    int _lastEventId = -1;
    DateTime _start;
    readonly List<string> _highlights = new();
    JsonElement _lastData;
    bool _ended, _primed;

    public static bool InGame()
    {
        return Pulse.Core.Measurement.GameDetector.IsRunning("League of Legends");
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _start = DateTime.Now;
        _lastEventId = -1;
        _ended = false;
        _primed = false;
        _lastData = default;
        _highlights.Clear();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await Poll(); } catch { }
                try { await Task.Delay(1000, token); } catch { break; }
            }
        });
    }

    public void Stop()
    {
        // The game closed: if it ended without a GameEnd event we saw, make the recap from the last data.
        if (!_ended && _lastData.ValueKind == JsonValueKind.Object) Finish("");
        _cts?.Cancel();
        _cts = null;
    }

    public void Dispose() => Stop();

    static string Name(JsonElement active) =>
        active.Str("riotIdGameName") is { Length: > 0 } g ? g : active.Str("summonerName").Split('#')[0];

    static bool IsMe(string who, string me) =>
        !string.IsNullOrEmpty(who) && (who.Equals(me, StringComparison.OrdinalIgnoreCase) || who.Split('#')[0].Equals(me, StringComparison.OrdinalIgnoreCase));

    async Task Poll()
    {
        var json = await Http.GetStringAsync(Url);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        _lastData = root.Clone();
        var me = Name(root.Get("activePlayer") ?? default);
        if (root.Get("events", "Events") is not { ValueKind: JsonValueKind.Array } events) return;
        if (!_primed)
        {
            _primed = true;
            // Pulse started (or Clips came on) mid-game: skip what already happened instead of clipping it now.
            if (root.Get("gameData", "gameTime") is { ValueKind: JsonValueKind.Number } gt && gt.GetDouble() > 60)
            {
                foreach (var e in events.EnumerateArray()) _lastEventId = Math.Max(_lastEventId, e.Int("EventID"));
                return;
            }
        }
        foreach (var e in events.EnumerateArray())
        {
            int id = e.Int("EventID");
            if (id <= _lastEventId) continue;
            _lastEventId = id;
            switch (e.Str("EventName"))
            {
                case "ChampionKill":
                {
                    // Event times are game seconds: place them on this PC's clock from the current game time.
                    double now = root.Get("gameData", "gameTime") is { ValueKind: JsonValueKind.Number } g ? g.GetDouble() : 0;
                    double at = e.Get("EventTime") is { ValueKind: JsonValueKind.Number } et ? et.GetDouble() : now;
                    var when = DateTime.Now.AddSeconds(-Math.Max(0, now - at));
                    if (IsMe(e.Str("KillerName"), me)) MatchEvents.RaiseMoment("League of Legends", Moment.Kill, when);
                    if (IsMe(e.Str("VictimName"), me)) MatchEvents.RaiseMoment("League of Legends", Moment.Death, when);
                    break;
                }
                case "Multikill" when IsMe(e.Str("KillerName"), me):
                {
                    int streak = e.Int("KillStreak");
                    if (streak < 3) break;
                    var label = streak switch { 3 => "Triple kill", 4 => "Quadra kill", _ => "Penta kill" };
                    _highlights.Add(label);
                    MatchEvents.RaiseHighlight("League of Legends", label, 30);
                    break;
                }
                case "Ace" when IsMe(e.Str("Acer"), me):
                    _highlights.Add("Ace");
                    MatchEvents.RaiseHighlight("League of Legends", "Ace", 30);
                    break;
                case "GameEnd":
                    Finish(e.Str("Result"));
                    break;
            }
        }
    }

    void Finish(string result)
    {
        if (_ended) return;
        _ended = true;
        var root = _lastData;
        var me = Name(root.Get("activePlayer") ?? default);
        var m = new MatchRecord
        {
            Game = "League of Legends", Start = _start, End = DateTime.Now, Highlights = _highlights.ToList(),
            Result = result == "Win" ? "Win" : result == "Lose" ? "Loss" : "",
            Mode = root.Str("gameData", "gameMode") switch { "CLASSIC" => "Summoner's Rift", "ARAM" => "ARAM", "CHERRY" => "Arena", var x => x },
            Map = root.Str("gameData", "mapName") switch { "Map11" => "Summoner's Rift", "Map12" => "Howling Abyss", "Map30" => "Rings of Wrath", var x => x },
        };
        if (root.Get("allPlayers") is { ValueKind: JsonValueKind.Array } players)
        {
            string myTeam = "";
            int mine = 0, theirs = 0;
            foreach (var p in players.EnumerateArray())
                if (IsMe(p.Str("riotIdGameName") is { Length: > 0 } g ? g : p.Str("summonerName"), me))
                {
                    myTeam = p.Str("team");
                    m.Character = p.Str("championName");
                    m.Kills = p.Int("scores", "kills"); m.Deaths = p.Int("scores", "deaths"); m.Assists = p.Int("scores", "assists");
                    m.Extras.Add($"CS {p.Int("scores", "creepScore")}");
                    m.Extras.Add($"Level {p.Int("level")}");
                }
            foreach (var p in players.EnumerateArray())
            {
                if (p.Str("team") == myTeam) mine += p.Int("scores", "kills"); else theirs += p.Int("scores", "kills");
            }
            m.Score = $"{mine} : {theirs}";
        }
        MatchEvents.RaiseFinished(m);
    }
}
