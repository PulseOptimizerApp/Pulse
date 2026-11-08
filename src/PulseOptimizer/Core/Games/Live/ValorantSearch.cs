using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Pulse.Core.Games.Valorant;

namespace Pulse.Core.Games.Live;

/// <summary>
/// Player search for the VALORANT tracker: finds a Riot ID through the Riot Client on this PC (the same lookup its
/// friend search uses), then reads that player's rank and recent matches the way the game's own career screen does.
/// Off unless the user turns it on and accepts the risk: it isn't an official Riot feature. Nothing about the player
/// is saved; the profile lives in memory until Pulse closes.
/// </summary>
public static class ValorantSearch
{
    public sealed record Result(Profile Profile, string Error);

    static readonly Dictionary<string, (Profile P, DateTime At)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Splits "Name#TAG" into its two parts; null when it isn't a Riot ID.</summary>
    public static (string Name, string Tag)? ParseRiotId(string text)
    {
        var t = (text ?? "").Trim();
        int i = t.LastIndexOf('#');
        if (i < 3 || i == t.Length - 1) return null;
        string name = t[..i].Trim(), tag = t[(i + 1)..].Trim();
        if (name.Length is < 3 or > 16 || tag.Length is < 3 or > 5) return null;
        return (name, tag);
    }

    public static async Task<Result> Search(string riotId, IProgress<string> progress = null, CancellationToken ct = default)
    {
        if (ParseRiotId(riotId) is not { } id) return new Result(null, "Type a full Riot ID, like Name#TAG.");
        if (Cache.TryGetValue(riotId.Trim(), out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(10)) return new Result(c.P, null);
        var s = await ValorantMatches.Login();
        if (s == null) return new Result(null, "Open VALORANT or the Riot Client first, then try again.");

        progress?.Report("Finding the player…");
        var puuid = await FindPuuid(s, id.Name, id.Tag);
        if (puuid == null) return new Result(null, "No player with that Riot ID was found. Check the spelling and the tag.");
        var r = await Load(s, puuid, $"{id.Name}#{id.Tag}", progress, ct);
        if (r.Profile != null) Cache[riotId.Trim()] = (r.Profile, DateTime.UtcNow);
        return r;
    }

    /// <summary>Riot ID → account id: the Riot Client's own lookup first, then Riot's account service with your login.</summary>
    static async Task<string> FindPuuid(ValorantMatches.Session s, string name, string tag)
    {
        var q = $"gameName={Uri.EscapeDataString(name)}&tagLine={Uri.EscapeDataString(tag)}";
        try
        {
            using var req = ValorantMatches.Local(s, $"/player-account/aliases/v1/lookup?{q}");
            using var res = await ValorantMatches.Http.SendAsync(req);
            if (res.IsSuccessStatusCode && FirstPuuid(await res.Content.ReadAsStringAsync(), name, tag) is { } p) return p;
        }
        catch { }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.account.riotgames.com/aliases/v1/aliases?{q}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Access);
            using var res = await ValorantMatches.Http.SendAsync(req);
            if (res.IsSuccessStatusCode && FirstPuuid(await res.Content.ReadAsStringAsync(), name, tag) is { } p) return p;
        }
        catch { }
        return null;
    }

    /// <summary>The account id in a lookup answer (an array or a single object; field names vary in case).</summary>
    internal static string FirstPuuid(string json, string name, string tag)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            IEnumerable<JsonElement> items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray() : new[] { doc.RootElement };
            foreach (var e in items)
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                string Field(params string[] names)
                {
                    foreach (var p in e.EnumerateObject())
                        if (names.Any(n => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)) && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
                    return null;
                }
                var puuid = Field("puuid", "subject");
                var gn = Field("game_name", "gameName", "alias");
                var tl = Field("tag_line", "tagLine", "tag");
                if (string.IsNullOrEmpty(puuid)) continue;
                // When the answer says whose account it is, it has to be the one asked for.
                if (gn != null && tl != null && !(string.Equals(gn, name, StringComparison.OrdinalIgnoreCase) && string.Equals(tl, tag, StringComparison.OrdinalIgnoreCase))) continue;
                return puuid;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Rank, peak, smurf signs and the last few competitive-style matches of one player.</summary>
    static async Task<Result> Load(ValorantMatches.Session s, string puuid, string name, IProgress<string> progress, CancellationToken ct)
    {
        await ValorantLobby.LoadContent(s);
        progress?.Report("Reading rank…");
        var (tier, rr, peak, peakAct, win, games) = await ValorantLobby.Mmr(s, puuid);
        var gains = await ValorantLobby.RecentGains(s, puuid);

        progress?.Report("Reading recent matches…");
        var matches = new List<VMatch>();
        using (var hist = await ValorantMatches.Get(s, $"https://pd.{s.Shard}.a.pvp.net/match-history/v1/history/{puuid}?startIndex=0&endIndex=15"))
        {
            if (hist?.RootElement.Get("History") is { ValueKind: JsonValueKind.Array } h)
            {
                var ids = h.EnumerateArray().Where(x => VMatch.Counts(x.Str("QueueID"))).Select(x => x.Str("MatchID")).Where(x => x.Length > 0).Take(10).ToList();
                for (int i = 0; i < ids.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"match:{i + 1}:{ids.Count}");
                    try
                    {
                        using var d = await ValorantMatches.Get(s, $"https://pd.{s.Shard}.a.pvp.net/match-details/v1/matches/{ids[i]}");
                        if (d != null && VMatch.Parse(d.RootElement, puuid, null, null) is { } vm) matches.Add(vm);
                    }
                    catch { }
                    await Task.Delay(1200, ct); // gentle on Riot's servers
                }
            }
        }
        var p = Tracker.FromTheirMatches(puuid, matches);
        p.Name = name;
        p.Tier = tier; p.Rr = rr; p.Peak = Math.Max(peak, tier); p.PeakAct = ValorantLobby.ActName(peakAct); p.ActWinPct = win; p.ActGames = games;
        p.IsYou = puuid == s.Puuid;
        if (tier < 3) p.LastTier = await ValorantLobby.LastTier(s, puuid);
        Tracker.Finish(p, ValorantLobby.SmurfSigns(0, tier, peak, win, games, gains));
        if (matches.Count == 0 && tier == 0) return new Result(p, "Found the player, but Riot didn't share any matches or rank for them.");
        return new Result(p, null);
    }

    public sealed record Mine(int Tier, int Rr, int Peak, string PeakAct, int Win, int Games, int LastTier, int Level, List<int> RrChanges);

    /// <summary>Your own live rank, level and RR changes, for your profile (null when the Riot Client isn't running).</summary>
    public static async Task<Mine> MyRank()
    {
        var s = await ValorantMatches.Login();
        if (s == null) return null;
        await ValorantLobby.LoadContent(s);
        var (tier, rr, peak, act, win, games) = await ValorantLobby.Mmr(s, s.Puuid);
        int last = tier < 3 ? await ValorantLobby.LastTier(s, s.Puuid) : 0;
        int level = await ValorantLobby.MyLevel(s);
        var gains = await ValorantLobby.RecentGains(s, s.Puuid);
        return new Mine(tier, rr, Math.Max(peak, tier), ValorantLobby.ActName(act), win, games, last, level, gains);
    }

    /// <summary>Your RR change in each of your last 10 competitive matches (wins positive, losses negative).</summary>
    public static async Task<List<int>> MyRrChanges()
    {
        var s = await ValorantMatches.Login();
        if (s == null) return null;
        return await ValorantLobby.RecentGains(s, s.Puuid);
    }

    /// <summary>Current and peak rank of each player (players you met in your own matches), a little apart each.</summary>
    public static async Task<Dictionary<string, (int Tier, int Peak)>> Peaks(List<string> puuids)
    {
        var map = new Dictionary<string, (int, int)>();
        var s = await ValorantMatches.Login();
        if (s == null) return map;
        await ValorantLobby.LoadContent(s);
        foreach (var id in puuids.Take(25))
        {
            var (tier, _, peak, _, _, _) = await ValorantLobby.Mmr(s, id);
            map[id] = (tier, Math.Max(tier, peak));
            await Task.Delay(150); // gentle on Riot's servers
        }
        return map;
    }

    /// <summary>Names for players from your own matches (Riot ID or null), for the "people you played with" list.</summary>
    public static async Task<Dictionary<string, string>> NamesFor(List<string> puuids)
    {
        var s = await ValorantMatches.Login();
        if (s == null || puuids.Count == 0) return new();
        return await ValorantLobby.Names(s, puuids.Take(100).ToList());
    }
}
