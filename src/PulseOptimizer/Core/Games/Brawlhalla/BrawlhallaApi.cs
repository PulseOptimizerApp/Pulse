using System.Net.Http;
using System.Text.Json;

namespace Pulse.Core.Games.Brawlhalla;

/// <summary>What the Brawlhalla page remembers: your Brawlhalla ID and recent lookups.</summary>
public class BrawlhallaSettings
{
    public long MyId { get; set; }
    public string MyName { get; set; }
    /// <summary>Recent lookups, newest first: (id, name).</summary>
    public List<BhRecent> Recent { get; set; } = new();
}

public class BhRecent { public long Id { get; set; } public string Name { get; set; } = ""; }

public sealed record BhLegend(int Id, string Name, string WeaponOne, string WeaponTwo);

public sealed class BhLegendStats
{
    public int LegendId, Games, Wins, Kos, Falls, Suicides, TeamKos;
    public long DamageDealt, DamageTaken, MatchTime, DamageUnarmed, DamageThrown, DamageWeaponOne, DamageWeaponTwo, DamageGadgets;
    public int KoUnarmed, KoWeaponOne, KoWeaponTwo, KoGadgets;
    public long TimeWeaponOne, TimeWeaponTwo;
    // Ranked (1v1) per legend, when known.
    public int Rating, PeakRating;
    public string Tier = "";
}

public sealed class BhRanked
{
    public string Mode = "";
    public int Rating, PeakRating, Games, Wins, GlobalRank, RegionRank;
    public string Region = "";
    /// <summary>The leaderboard's own tier (Valhallan is a top spot, not a rating), when it was looked up.</summary>
    public string BoardTier;
    public string Tier => BoardTier is { Length: > 0 } t ? t : Brawlhalla.TierOf(Rating);
}

public sealed class BhTeam
{
    public long PartnerId;
    public string Partner = "";
    public int Rating, PeakRating, Games, Wins, GlobalRank;
    public string Region = "", Tier = "";
}

public sealed class BhProfile
{
    public long Id;
    public string Name = "";
    public int Level, Games, Wins;
    public double XpPercent;
    public long DamageBomb, DamageMine, DamageSpikeball, DamageSidekick;
    public int KoBomb, KoMine, KoSpikeball, KoSidekick, KoSnowball, HitSnowball;
    public List<BhLegendStats> Legends = new();
    public BhRanked OneVsOne, ThreeVsThree;
    public List<BhTeam> Teams = new();
    public string Guild = "", GuildRank = "";
}

public sealed record BhRanking(int Rank, long Id, string Name, int Rating, int BestRating, int Wins, int Losses, string Region, string Tier);

/// <summary>
/// Brawlhalla's official public API (api.brawlhalla.com, version 1: no API key, 2000 requests every 5 minutes).
/// Only reads public stats: profiles, ranked, legends, 2v2 teams, clans and the leaderboards. The legend list is kept
/// on this PC (it changes only when a legend is added).
/// </summary>
public static class Brawlhalla
{
    const string Api = "https://api.brawlhalla.com/v1/";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static Dictionary<int, BhLegend> _legends;

    public static readonly string[] Regions = { "ALL", "US-E", "US-W", "EU", "BRZ", "SEA", "AUS", "JPS", "SA", "ME" };

    static string Dir => Path.Combine(TweakEngine.DataDir, "brawlhalla");

    static async Task<JsonDocument> Get(string path, CancellationToken ct)
    {
        // Their servers sometimes answer 502/503 for a moment: try twice more, a little later each time.
        HttpResponseMessage res = null;
        for (int attempt = 0; ; attempt++)
        {
            res?.Dispose();
            res = await Http.GetAsync(Api + path, ct);
            if ((int)res.StatusCode is < 500 or > 504 || attempt == 2) break;
            await Task.Delay(800 * (attempt + 1), ct);
        }
        using var _ = res;
        if ((int)res.StatusCode == 404) return null;
        if ((int)res.StatusCode == 429) throw new HttpRequestException("Brawlhalla's servers say too many requests. Try again in a few minutes.");
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Brawlhalla's servers answered {(int)res.StatusCode}.");
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    static int I(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? (int)Math.Clamp(n, int.MinValue, int.MaxValue) : 0;
    static long L(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
    static double D(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    static string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>The ranked tier for a rating (Diamond from 2000; Valhallan is a leaderboard spot, shown when the leaderboard says so).</summary>
    public static string TierOf(int rating)
    {
        if (rating <= 0) return "";
        if (rating >= 2000) return "Diamond";
        (int Min, string Name, int Step)[] bands =
        {
            (1680, "Platinum", 64), (1390, "Gold", 58), (1130, "Silver", 52), (910, "Bronze", 44), (720, "Tin", 38),
        };
        foreach (var (min, name, step) in bands)
            if (rating >= min) return $"{name} {Math.Min(5, 1 + (rating - min) / step)}";
        return "Tin 0";
    }

    /// <summary>Every legend (id → name and weapons), from this PC when it has them.</summary>
    public static async Task<Dictionary<int, BhLegend>> Legends(CancellationToken ct = default)
    {
        if (_legends != null) return _legends;
        var file = Path.Combine(Dir, "legends.json");
        try
        {
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(7))
                return _legends = JsonSerializer.Deserialize<List<BhLegend>>(await File.ReadAllTextAsync(file, ct))!.ToDictionary(l => l.Id);
        }
        catch { }
        var all = new List<BhLegend>();
        for (int page = 1; page <= 5; page++)
        {
            JsonDocument doc;
            try { doc = await Get($"static/legends?max_results=100&page={page}", ct); }
            catch (Exception) when (all.Count > 0) { break; }   // keep the legends already read
            using var __ = doc;
            if (doc == null || !doc.RootElement.TryGetProperty("legends", out var arr) || arr.ValueKind != JsonValueKind.Array) break;
            foreach (var l in arr.EnumerateArray())
                all.Add(new BhLegend(I(l, "legend_id"), S(l, "bio_name") is { Length: > 0 } n ? n : S(l, "legend_name"), S(l, "weapon_one"), S(l, "weapon_two")));
            if (page >= I(doc.RootElement, "total_pages")) break;
        }
        if (all.Count > 0)
        {
            try { Directory.CreateDirectory(Dir); await File.WriteAllTextAsync(file, JsonSerializer.Serialize(all), ct); } catch { }
        }
        return _legends = all.GroupBy(l => l.Id).ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>Ranked players whose name matches (the API only searches the ranked leaderboards).</summary>
    public static async Task<List<BhRanking>> Find(string name, CancellationToken ct = default)
    {
        var found = new List<BhRanking>();
        foreach (var mode in new[] { "1v1", "2v2" })
        {
            var page = await Rankings(mode, "ALL", 1, name, 20, ct);
            foreach (var r in page.Rows)
                if (found.All(f => f.Id != r.Id)) found.Add(r);
            if (found.Count >= 10) break;
        }
        // Exact names first, then the closest.
        return found.OrderByDescending(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(r => r.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ThenByDescending(r => r.Rating).Take(12).ToList();
    }

    /// <summary>One page of a ranked leaderboard.</summary>
    public static async Task<(List<BhRanking> Rows, int Pages)> Rankings(string mode, string region, int page = 1, string search = null, int max = 25, CancellationToken ct = default)
    {
        var q = $"leaderboard/ranked?game_mode={Uri.EscapeDataString(mode)}&region={Uri.EscapeDataString(region)}&page={page}&max_results={max}";
        if (!string.IsNullOrWhiteSpace(search)) q += "&search=" + Uri.EscapeDataString(search.Trim());
        using var doc = await Get(q, ct);
        var rows = new List<BhRanking>();
        if (doc == null || !doc.RootElement.TryGetProperty("rankings", out var arr) || arr.ValueKind != JsonValueKind.Array) return (rows, 0);
        foreach (var r in arr.EnumerateArray())
        {
            long id = 0; string name = "";
            if (r.TryGetProperty("players", out var ps) && ps.ValueKind == JsonValueKind.Array)
            {
                var names = ps.EnumerateArray().Select(p => S(p, "username")).Where(n => n.Length > 0).ToList();
                name = string.Join(" & ", names);
                id = ps.EnumerateArray().Select(p => L(p, "id")).FirstOrDefault();
            }
            rows.Add(new BhRanking(I(r, "rank"), id, name, I(r, "rating"), I(r, "best_rating"), I(r, "wins"), I(r, "losses"), S(r, "region"), S(r, "tier")));
        }
        return (rows, I(doc.RootElement, "total_pages"));
    }

    /// <summary>A player's whole profile: overall stats, ranked 1v1 and 3v3, 2v2 teams and clan, asked for together.</summary>
    public static async Task<BhProfile> Profile(long id, CancellationToken ct = default)
    {
        var allTask = Get($"player/stats?brawlhalla_id={id}&mode=all", ct);
        var oneTask = Optional($"player/stats?brawlhalla_id={id}&mode=ranked_1v1", ct);
        var threeTask = Optional($"player/stats?brawlhalla_id={id}&mode=ranked_3v3", ct);
        var teamsTask = Optional($"player/teams?brawlhalla_id={id}", ct);
        var guildTask = Optional($"player/guild?brawlhalla_id={id}", ct);
        using var all = await allTask;
        if (all == null) return null;
        var r = all.RootElement;
        var p = new BhProfile
        {
            Id = L(r, "brawlhalla_id") is > 0 and var bid ? bid : id, Name = S(r, "name"), Level = I(r, "level"), XpPercent = D(r, "xp_percentage"),
            Games = I(r, "games"), Wins = I(r, "wins"),
            DamageBomb = L(r, "damage_bomb"), DamageMine = L(r, "damage_mine"), DamageSpikeball = L(r, "damage_spikeball"), DamageSidekick = L(r, "damage_sidekick"),
            KoBomb = I(r, "ko_bomb"), KoMine = I(r, "ko_mine"), KoSpikeball = I(r, "ko_spikeball"), KoSidekick = I(r, "ko_sidekick"),
            KoSnowball = I(r, "ko_snowball"), HitSnowball = I(r, "hit_snowball"),
        };
        if (r.TryGetProperty("legends", out var ls) && ls.ValueKind == JsonValueKind.Array)
            foreach (var l in ls.EnumerateArray())
                p.Legends.Add(new BhLegendStats
                {
                    LegendId = I(l, "legend_id"), Games = I(l, "games"), Wins = I(l, "wins"), Kos = I(l, "kos"), Falls = I(l, "falls"), Suicides = I(l, "suicides"), TeamKos = I(l, "team_kos"),
                    DamageDealt = L(l, "damage_dealt"), DamageTaken = L(l, "damage_taken"), MatchTime = L(l, "match_time"),
                    DamageUnarmed = L(l, "damage_unarmed"), DamageThrown = L(l, "damage_thrown_item"), DamageWeaponOne = L(l, "damage_weapon_one"),
                    DamageWeaponTwo = L(l, "damage_weapon_two"), DamageGadgets = L(l, "damage_gadgets"),
                    KoUnarmed = I(l, "ko_unarmed"), KoWeaponOne = I(l, "ko_weapon_one"), KoWeaponTwo = I(l, "ko_weapon_two"), KoGadgets = I(l, "ko_gadgets"),
                    TimeWeaponOne = L(l, "time_held_weapon_one"), TimeWeaponTwo = L(l, "time_held_weapon_two"),
                });

        using (var one = await oneTask)
            if (one != null)
            {
                p.OneVsOne = Ranked(one.RootElement, "1v1");
                if (one.RootElement.TryGetProperty("legends", out var rl) && rl.ValueKind == JsonValueKind.Array)
                    foreach (var l in rl.EnumerateArray())
                        if (p.Legends.FirstOrDefault(x => x.LegendId == I(l, "legend_id")) is { } mine)
                        { mine.Rating = I(l, "rating"); mine.PeakRating = I(l, "peak_rating"); mine.Tier = S(l, "tier"); }
            }
        using (var three = await threeTask) if (three != null) p.ThreeVsThree = Ranked(three.RootElement, "3v3");
        // Diamond-rated players may be Valhallan, which only the leaderboard knows: find them on it.
        foreach (var rk in new[] { p.OneVsOne, p.ThreeVsThree })
            if (rk is { Rating: >= 2000 } && p.Name.Length > 0)
                try
                {
                    var (rows, _) = await Rankings(rk.Mode, rk.Region.Length > 0 ? rk.Region : "ALL", 1, p.Name, 50, ct);
                    if (rows.FirstOrDefault(x => x.Id == p.Id) is { } row) rk.BoardTier = row.Tier;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
        using (var teams = await teamsTask)
            if (teams?.RootElement.TryGetProperty("teams", out var t) == true && t.TryGetProperty("ranked_2v2", out var two) && two.ValueKind == JsonValueKind.Array)
                foreach (var x in two.EnumerateArray())
                {
                    bool firstIsMe = L(x, "brawlhalla_id_one") == p.Id;
                    p.Teams.Add(new BhTeam
                    {
                        PartnerId = firstIsMe ? L(x, "brawlhalla_id_two") : L(x, "brawlhalla_id_one"),
                        Partner = firstIsMe ? S(x, "username_two") : S(x, "username_one"),
                        Rating = I(x, "rating"), PeakRating = I(x, "peak_rating"), Games = I(x, "games"), Wins = I(x, "wins"),
                        GlobalRank = I(x, "global_rank"), Region = S(x, "region"), Tier = S(x, "tier"),
                    });
                }
        p.Teams = p.Teams.OrderByDescending(x => x.Rating).ToList();
        using (var guild = await guildTask)
            if (guild?.RootElement.TryGetProperty("guild", out var g) == true && g.ValueKind == JsonValueKind.Object)
            { p.Guild = S(g, "guild_name"); p.GuildRank = S(g, "rank"); }
        return p;
    }

    static BhRanked Ranked(JsonElement e, string mode)
    {
        var r = new BhRanked { Mode = mode, Rating = I(e, "rating"), PeakRating = I(e, "peak_rating"), Games = I(e, "games"), Wins = I(e, "wins"), GlobalRank = I(e, "global_rank"), Region = S(e, "region") };
        if (e.TryGetProperty("region_ranks", out var rr) && rr.ValueKind == JsonValueKind.Array)
            r.RegionRank = rr.EnumerateArray().Where(x => S(x, "region") == r.Region).Select(x => I(x, "rank")).FirstOrDefault();
        return r.Games > 0 || r.Rating > 0 ? r : null;
    }

    /// <summary>A part of a profile that may not exist (no ranked games, no clan): null instead of an error.</summary>
    static async Task<JsonDocument> Optional(string path, CancellationToken ct)
    {
        try { return await Get(path, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
