using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Pulse.Core.Games.Live;

/// <summary>One player in the live lobby.</summary>
public sealed record LobbyPlayer(string Puuid, bool Ally, bool Me, string Agent, string Name, bool Hidden, int Level,
    int Tier, int Rr, int PeakTier, string PeakAct, int WinPercent, int Games);

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
            bool hidden = p.Incognito && p.Puuid != s.Puuid;
            list.Add(new LobbyPlayer(p.Puuid, p.Team == myTeam, p.Puuid == s.Puuid,
                _agents != null && _agents.TryGetValue(p.Agent, out var a) ? a : ValorantMatches.AgentName(p.Agent),
                hidden ? null : names.GetValueOrDefault(p.Puuid), hidden, p.HideLevel ? 0 : p.Level, tier, rr, peak, peakAct, win, games));
            await Task.Delay(120);   // gentle on Riot's servers
        }
        var info = match.RootElement;
        Last = new Lobby(matchId, ValorantMatches.MapName(info.Str("MapID")), info.Get("MatchmakingData", "QueueID") is { } q ? q.GetString() ?? "" : "", list);
        return Last;
    }

    static async Task<Dictionary<string, string>> Names(ValorantMatches.Session s, List<string> puuids)
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
    static async Task<(int Tier, int Rr, int Peak, string PeakAct, int Win, int Games)> Mmr(ValorantMatches.Session s, string puuid)
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

    /// <summary>Agent names and the list of acts (with start dates) from Riot's content service.</summary>
    static async Task LoadContent(ValorantMatches.Session s)
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
                foreach (var x in seasons.EnumerateArray())
                    _seasons.Add((x.Str("ID"), DateTime.TryParse(x.Str("StartTime"), out var st) ? st : default,
                        x.Str("Type").Equals("act", StringComparison.OrdinalIgnoreCase), x.Get("IsActive") is { ValueKind: JsonValueKind.True }));
        }
        catch { }
    }
}
