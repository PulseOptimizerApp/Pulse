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
    /// <summary>While Brawlhalla is open, check your stats every minute and log each match you play.</summary>
    public bool TrackMatches { get; set; } = true;
    /// <summary>When a match you played shows up: a small summary in the corner.</summary>
    public bool MatchPopup { get; set; } = true;
    /// <summary>Players you checked in Matchup, newest first.</summary>
    public List<BhRecent> Opponents { get; set; } = new();
    /// <summary>Ctrl+Shift+B while Brawlhalla is open: Pulse comes up on Matchup.</summary>
    public bool MatchupHotkey { get; set; } = true;
}

public class BhRecent { public long Id { get; set; } public string Name { get; set; } = ""; }

public sealed record BhLegend(int Id, string Name, string WeaponOne, string WeaponTwo);

public sealed class BhLegendStats
{
    public int LegendId, Games, Wins, Kos, Falls, Suicides, TeamKos;
    public long DamageDealt, DamageTaken, MatchTime, DamageUnarmed, DamageThrown, DamageWeaponOne, DamageWeaponTwo, DamageGadgets;
    public int KoUnarmed, KoWeaponOne, KoWeaponTwo, KoGadgets;
    public long TimeWeaponOne, TimeWeaponTwo;
    // This legend's level and XP, and its ranked 1v1 this season.
    public int Level, RankedGames, RankedWins;
    public long Xp;
    public double XpPercent;
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
    public long Xp;
    public double XpPercent;
    public long DamageBomb, DamageMine, DamageSpikeball, DamageSidekick;
    public int KoBomb, KoMine, KoSpikeball, KoSidekick, KoSnowball, HitSnowball;
    public List<BhLegendStats> Legends = new();
    public BhRanked OneVsOne, ThreeVsThree;
    public List<BhTeam> Teams = new();
    public string Guild = "", GuildRank = "";
    /// <summary>Set when Brawlhalla's servers didn't answer and this is the copy Pulse saved then.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public DateTime? SavedAt;
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
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static Dictionary<int, BhLegend> _legends;

    public static readonly string[] Regions = { "ALL", "US-E", "US-W", "EU", "BRZ", "SEA", "AUS", "JPS", "SA", "ME" };

    static string Dir => Path.Combine(TweakEngine.DataDir, "brawlhalla");

    static async Task<JsonDocument> Get(string path, CancellationToken ct)
    {
        // Their servers often answer 502/503 for a moment, or hang: give each try 6 seconds, and try twice more, a
        // little later each time.
        HttpResponseMessage res = null;
        for (int attempt = 0; ; attempt++)
        {
            res?.Dispose();
            res = null;
            using (var slow = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                slow.CancelAfter(TimeSpan.FromSeconds(6));
                try { res = await Http.GetAsync(Api + path, slow.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < 2) { }
            }
            if (res != null && ((int)res.StatusCode is < 500 or > 504 || attempt == 2)) break;
            await Task.Delay(400 * (attempt + 1), ct);
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
        // Both boards at once (a 2v2 board that fails doesn't lose the 1v1 results).
        var one = Rankings("1v1", "ALL", 1, name, 20, ct);
        var two = Rankings("2v2", "ALL", 1, name, 20, ct);
        var found = new List<BhRanking>((await one).Rows);
        try
        {
            foreach (var r in (await two).Rows)
                if (found.All(f => f.Id != r.Id)) found.Add(r);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
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

    /// <summary>
    /// A player's whole profile: overall stats, ranked 1v1 and 3v3, 2v2 teams and clan, asked for together.
    /// <paramref name="lookUpTier"/>: also find Diamond players on the leaderboard (for Valhallan), one more request.
    /// </summary>
    public static async Task<BhProfile> Profile(long id, CancellationToken ct = default, bool lookUpTier = true)
    {
        var allTask = Get($"player/stats?brawlhalla_id={id}&mode=all", ct);
        var oneTask = Optional($"player/stats?brawlhalla_id={id}&mode=ranked_1v1", ct);
        var threeTask = Optional($"player/stats?brawlhalla_id={id}&mode=ranked_3v3", ct);
        var teamsTask = Optional($"player/teams?brawlhalla_id={id}", ct);
        var guildTask = Optional($"player/guild?brawlhalla_id={id}", ct);
        var allDoc = await allTask;
        // Their servers often say "Player not found" for a player who exists, for a few seconds or longer: ask a few
        // more times, then fall back to the copy saved last time this player loaded.
        for (int attempt = 0; allDoc == null && attempt < 3; attempt++)
        {
            await Task.Delay(600 * (attempt + 1), ct);
            try { allDoc = await Get($"player/stats?brawlhalla_id={id}&mode=all", ct); }
            catch (HttpRequestException) when (attempt < 2) { }
        }
        using var all = allDoc;
        if (all == null) return Saved(id);
        var r = all.RootElement;
        var p = new BhProfile
        {
            Id = L(r, "brawlhalla_id") is > 0 and var bid ? bid : id, Name = S(r, "name"), Level = I(r, "level"), Xp = L(r, "xp"), XpPercent = D(r, "xp_percentage"),
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
                    Level = I(l, "level"), Xp = L(l, "xp"), XpPercent = D(l, "xp_percentage"),
                });

        // The rest gets 2.5 more seconds: a part that's still hanging is left out (ranked falls back to last time's).
        var more = Task.Delay(2500, ct);
        oneTask = Within(oneTask, more); threeTask = Within(threeTask, more); teamsTask = Within(teamsTask, more); guildTask = Within(guildTask, more);
        using (var one = await oneTask)
            if (one != null)
            {
                p.OneVsOne = Ranked(one.RootElement, "1v1");
                if (one.RootElement.TryGetProperty("legends", out var rl) && rl.ValueKind == JsonValueKind.Array)
                    foreach (var l in rl.EnumerateArray())
                        if (p.Legends.FirstOrDefault(x => x.LegendId == I(l, "legend_id")) is { } mine)
                        { mine.Rating = I(l, "rating"); mine.PeakRating = I(l, "peak_rating"); mine.Tier = S(l, "tier"); mine.RankedGames = I(l, "games"); mine.RankedWins = I(l, "wins"); }
            }
        using (var three = await threeTask) if (three != null) p.ThreeVsThree = Ranked(three.RootElement, "3v3");
        // Diamond-rated players may be Valhallan, which only the leaderboard knows: find them on it.
        foreach (var rk in new[] { p.OneVsOne, p.ThreeVsThree })
            if (lookUpTier && rk is { Rating: >= 2000 } && p.Name.Length > 0)
                try
                {
                    using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    quick.CancelAfter(TimeSpan.FromSeconds(3));
                    var (rows, _) = await Rankings(rk.Mode, rk.Region.Length > 0 ? rk.Region : "ALL", 1, p.Name, 50, quick.Token);
                    if (rows.FirstOrDefault(x => x.Id == p.Id) is { } row) rk.BoardTier = row.Tier;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
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
        // Ranked that didn't answer this time: last time's (it can't vanish mid-season).
        if (p.OneVsOne == null && Saved(id) is { OneVsOne: { } savedOne } saved && saved.SavedAt > DateTime.Now.AddDays(-7))
        {
            p.OneVsOne = savedOne;
            foreach (var l in p.Legends)
                if (l.Rating == 0 && saved.Legends.FirstOrDefault(x => x.LegendId == l.LegendId) is { Rating: > 0 } sl)
                { l.Rating = sl.Rating; l.PeakRating = sl.PeakRating; l.Tier = sl.Tier; l.RankedGames = sl.RankedGames; l.RankedWins = sl.RankedWins; }
        }
        using (var guild = await guildTask)
            if (guild?.RootElement.TryGetProperty("guild", out var g) == true && g.ValueKind == JsonValueKind.Object)
            { p.Guild = S(g, "guild_name"); p.GuildRank = S(g, "rank"); }
        Save(p);
        return p;
    }

    // ───────── portraits of legends newer than this version of Pulse ─────────

    /// <summary>Where a downloaded portrait is kept.</summary>
    public static string PortraitFile(string key) => Path.Combine(Dir, "art", $"legend-{key}.png");

    /// <summary>
    /// A legend Pulse has no picture for (added to the game after this version): its portrait from the Brawlhalla
    /// Wiki (brawlhalla.wiki.gg), saved on this PC. False when there isn't one.
    /// </summary>
    public static async Task<bool> DownloadPortrait(string name, string key)
    {
        try
        {
            var file = new string((name ?? "").Normalize(System.Text.NormalizationForm.FormD).Where(char.IsAsciiLetterOrDigit).ToArray());
            if (file.Length == 0) return false;
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://brawlhalla.wiki.gg/images/Portrait_{file}.png");
            req.Headers.UserAgent.ParseAdd("PulseOptimizer/1.0 (+https://github.com/PulseOptimizerApp/Pulse)");
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode || res.Content.Headers.ContentType?.MediaType != "image/png") return false;
            var bytes = await res.Content.ReadAsByteArrayAsync();
            if (bytes.Length is < 100 or > 2_000_000) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(PortraitFile(key))!);
            await File.WriteAllBytesAsync(PortraitFile(key), bytes);
            return true;
        }
        catch { return false; }
    }

    // ───────── Steam ─────────

    /// <summary>
    /// A Steam profile link, custom URL name or SteamID64 → the Steam account's id and current name, from Steam's
    /// public profile page (steamcommunity.com, ?xml=1). Null when it isn't Steam-shaped or the profile isn't found.
    /// Brawlhalla's API can't search by Steam id any more, so callers look the name up on the leaderboards.
    /// </summary>
    public static async Task<(string SteamId, string Name)?> Steam(string text, CancellationToken ct = default)
    {
        text = (text ?? "").Trim();
        string url;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"steamcommunity\.com/(profiles|id)/([^/?#\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) url = $"https://steamcommunity.com/{m.Groups[1].Value.ToLowerInvariant()}/{Uri.EscapeDataString(m.Groups[2].Value)}/?xml=1";
        else if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^7656119\d{10}$")) url = $"https://steamcommunity.com/profiles/{text}/?xml=1";
        else if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^[A-Za-z0-9_-]{2,32}$")) url = $"https://steamcommunity.com/id/{text}/?xml=1";
        else return null;
        try
        {
            using var slow = CancellationTokenSource.CreateLinkedTokenSource(ct);
            slow.CancelAfter(TimeSpan.FromSeconds(6));
            var xml = await Http.GetStringAsync(url, slow.Token);
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var id = doc.Root?.Element("steamID64")?.Value;
            var name = doc.Root?.Element("steamID")?.Value;
            return string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) ? null : (id, name.Trim());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    // ───────── the last copy of each profile, for when their servers don't answer ─────────

    static readonly JsonSerializerOptions Fields = new() { IncludeFields = true };
    static string SavedFile(long id) => Path.Combine(Dir, $"profile-{id}.json");

    static void Save(BhProfile p)
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(SavedFile(p.Id), JsonSerializer.Serialize(p, Fields)); } catch { }
    }

    /// <summary>The profile saved last time (with <see cref="BhProfile.SavedAt"/>), or null: shown at once while a fresh one loads.</summary>
    public static BhProfile Saved(long id)
    {
        try
        {
            var f = SavedFile(id);
            if (!File.Exists(f)) return null;
            var p = JsonSerializer.Deserialize<BhProfile>(File.ReadAllText(f), Fields);
            if (p != null) p.SavedAt = File.GetLastWriteTime(f);
            return p;
        }
        catch { return null; }
    }

    static BhRanked Ranked(JsonElement e, string mode)
    {
        var r = new BhRanked { Mode = mode, Rating = I(e, "rating"), PeakRating = I(e, "peak_rating"), Games = I(e, "games"), Wins = I(e, "wins"), GlobalRank = I(e, "global_rank"), Region = S(e, "region") };
        if (e.TryGetProperty("region_ranks", out var rr) && rr.ValueKind == JsonValueKind.Array)
            r.RegionRank = rr.EnumerateArray().Where(x => S(x, "region") == r.Region).Select(x => I(x, "rank")).FirstOrDefault();
        return r.Games > 0 || r.Rating > 0 ? r : null;
    }

    /// <summary>The answer if it comes before <paramref name="deadline"/>, otherwise null (a late one is disposed).</summary>
    static async Task<JsonDocument> Within(Task<JsonDocument> task, Task deadline)
    {
        if (await Task.WhenAny(task, deadline) == task) return await task;
        _ = task.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result?.Dispose(); }, TaskScheduler.Default);
        return null;
    }

    /// <summary>A part of a profile that may not exist (no ranked games, no clan): null instead of an error.</summary>
    static async Task<JsonDocument> Optional(string path, CancellationToken ct)
    {
        try { return await Get(path, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
