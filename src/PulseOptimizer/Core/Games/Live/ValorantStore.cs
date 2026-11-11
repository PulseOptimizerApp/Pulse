using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Pulse.Core.Games.Live;

public sealed record StoreItem(string Id, string Name, int Price, int FullPrice, int DiscountPercent, string Icon, string Tier);

/// <summary>A skin on your wishlist: its id, name and picture, and the ids the store can show it under (each level).</summary>
public sealed class WishSkin
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public List<string> Ids { get; set; } = new();
    public DateTime Added { get; set; } = DateTime.UtcNow;
}

public sealed record Storefront(
    List<StoreItem> Daily, TimeSpan DailyLeft,
    List<StoreItem> Night, TimeSpan NightLeft,
    StoreItem Bundle, TimeSpan BundleLeft,
    int Vp, int Radianite, int KingdomCredits);

/// <summary>
/// Your VALORANT store, read the way the game's own store screen reads it: today's four skins, the Night Market
/// when it's on, the featured bundle and your wallet, through the Riot Client's login on this PC. Nothing is bought
/// or changed. Skin names and pictures come from valorant-api.com, the community mirror of the game's own content
/// (only the skin's id is sent), and are kept on this PC so each one is fetched once.
/// </summary>
public static class ValorantStore
{
    const string Vp = "85ad13f7-3d1b-5128-9eb2-7cd8ee0b5741", Rp = "e59aa87c-4cbf-517a-5983-6e81511be9b7", Kc = "85ca954a-41f2-ce94-9b45-8ca3dd39a00d";
    static readonly HttpClient Content = new() { Timeout = TimeSpan.FromSeconds(15) };
    static string Dir => Path.Combine(TweakEngine.DataDir, "valorant", "store");

    /// <summary>The store now, or null when the Riot Client isn't running or Riot didn't answer.</summary>
    public static async Task<Storefront> Load()
    {
        var s = await ValorantMatches.Login();
        if (s == null) return null;
        using var front = await Send(s, HttpMethod.Post, $"https://pd.{s.Shard}.a.pvp.net/store/v3/storefront/{s.Puuid}", "{}");
        if (front == null) return null;
        using var wallet = await Send(s, HttpMethod.Get, $"https://pd.{s.Shard}.a.pvp.net/store/v1/wallet/{s.Puuid}", null);
        return await Name(Parse(front.RootElement, wallet?.RootElement));
    }

    static async Task<JsonDocument> Send(ValorantMatches.Session s, HttpMethod method, string url, string body)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Access);
        req.Headers.Add("X-Riot-Entitlements-JWT", s.Entitlement);
        req.Headers.Add("X-Riot-ClientPlatform", ValorantMatches.ClientPlatform);
        if (s.Version.Length > 0) req.Headers.Add("X-Riot-ClientVersion", s.Version);
        if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var res = await ValorantMatches.Http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return null;
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync());
    }

    static int Cost(JsonElement e, string key) => e.Get(key) is { ValueKind: JsonValueKind.Object } c ? c.Int(Vp) : 0;
    static TimeSpan Secs(JsonElement e, params string[] path) => TimeSpan.FromSeconds(Math.Max(0, e.Int(path)));

    /// <summary>The usual price of each skin tier, which is how the store's own colors are picked.</summary>
    public static string TierFor(int price) => price switch
    {
        <= 0 => "",
        <= 875 => "Select",
        <= 1275 => "Deluxe",
        <= 1775 => "Premium",
        <= 2175 => "Ultra",
        _ => "Exclusive",
    };

    internal static Storefront Parse(JsonElement root, JsonElement? wallet)
    {
        var daily = new List<StoreItem>();
        if (root.Get("SkinsPanelLayout", "SingleItemStoreOffers") is { ValueKind: JsonValueKind.Array } offers)
            foreach (var o in offers.EnumerateArray())
            {
                var id = o.Get("Rewards") is { ValueKind: JsonValueKind.Array } r && r.GetArrayLength() > 0 ? r[0].Str("ItemID") : o.Str("OfferID");
                int price = Cost(o, "Cost");
                daily.Add(new StoreItem(id, "", price, price, 0, null, TierFor(price)));
            }
        else if (root.Get("SkinsPanelLayout", "SingleItemOffers") is { ValueKind: JsonValueKind.Array } ids)
            foreach (var i in ids.EnumerateArray()) daily.Add(new StoreItem(i.GetString(), "", 0, 0, 0, null, ""));

        var night = new List<StoreItem>();
        if (root.Get("BonusStore", "BonusStoreOffers") is { ValueKind: JsonValueKind.Array } bonus)
            foreach (var b in bonus.EnumerateArray())
            {
                var offer = b.Get("Offer") ?? default;
                var id = offer.Get("Rewards") is { ValueKind: JsonValueKind.Array } r && r.GetArrayLength() > 0 ? r[0].Str("ItemID") : offer.Str("OfferID");
                int full = Cost(offer, "Cost"), now = Cost(b, "DiscountCosts");
                night.Add(new StoreItem(id, "", now > 0 ? now : full, full, b.Int("DiscountPercent"), null, TierFor(full)));
            }

        StoreItem bundle = null;
        var bundleLeft = TimeSpan.Zero;
        var fb = root.Get("FeaturedBundle") ?? default;
        JsonElement? first = fb.Get("Bundles") is { ValueKind: JsonValueKind.Array } bl && bl.GetArrayLength() > 0 ? bl[0] : fb.Get("Bundle");
        if (first is { ValueKind: JsonValueKind.Object } bd)
        {
            int price = Cost(bd, "TotalDiscountedCost");
            if (price == 0) price = Cost(bd, "TotalBaseCost");
            bundle = new StoreItem(bd.Str("DataAssetID"), "", price, Cost(bd, "TotalBaseCost"), 0, null, "Bundle");
            bundleLeft = Secs(bd, "DurationRemainingInSeconds");
            if (bundleLeft == TimeSpan.Zero) bundleLeft = Secs(fb, "BundleRemainingDurationInSeconds");
        }

        int vp = 0, rp = 0, kc = 0;
        if (wallet?.Get("Balances") is { ValueKind: JsonValueKind.Object } bal) { vp = bal.Int(Vp); rp = bal.Int(Rp); kc = bal.Int(Kc); }
        return new Storefront(daily, Secs(root, "SkinsPanelLayout", "SingleItemOffersRemainingDurationInSeconds"),
            night, Secs(root, "BonusStore", "BonusStoreRemainingDurationInSeconds"), bundle, bundleLeft, vp, rp, kc);
    }

    // ───────── every skin, for the wishlist (valorant-api.com, kept on this PC for a week) ─────────

    static List<WishSkin> _skins;
    static string SkinsFile => Path.Combine(Dir, "skins.json");

    /// <summary>
    /// Every weapon skin in the game (not the free Standard ones), to pick wishlist skins from. Downloaded from the
    /// community mirror of the game's content once a week; empty when it can't be reached.
    /// </summary>
    public static async Task<List<WishSkin>> Skins()
    {
        if (_skins != null) return _skins;
        try
        {
            if (File.Exists(SkinsFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(SkinsFile) < TimeSpan.FromDays(7))
                return _skins = JsonSerializer.Deserialize<List<WishSkin>>(await File.ReadAllTextAsync(SkinsFile)) ?? new();
        }
        catch { }
        try
        {
            using var doc = JsonDocument.Parse(await Content.GetStringAsync("https://valorant-api.com/v1/weapons/skins"));
            var list = ParseSkins(doc.RootElement);
            if (list.Count > 0)
            {
                Directory.CreateDirectory(Dir);
                await File.WriteAllTextAsync(SkinsFile, JsonSerializer.Serialize(list));
                return _skins = list;
            }
        }
        catch { }
        // Offline: last week's list is better than none.
        try { if (File.Exists(SkinsFile)) return _skins = JsonSerializer.Deserialize<List<WishSkin>>(await File.ReadAllTextAsync(SkinsFile)) ?? new(); } catch { }
        return new();
    }

    internal static List<WishSkin> ParseSkins(JsonElement root)
    {
        var list = new List<WishSkin>();
        if (root.Get("data") is not { ValueKind: JsonValueKind.Array } data) return list;
        foreach (var s in data.EnumerateArray())
        {
            var name = s.Str("displayName");
            if (name.Length == 0 || name.StartsWith("Standard", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Random", StringComparison.OrdinalIgnoreCase) || s.Str("contentTierUuid").Length == 0) continue;
            var w = new WishSkin { Id = s.Str("uuid"), Name = name, Icon = s.Str("displayIcon") };
            w.Ids.Add(w.Id);
            if (s.Get("levels") is { ValueKind: JsonValueKind.Array } levels)
                foreach (var l in levels.EnumerateArray())
                {
                    if (l.Str("uuid") is { Length: > 0 } id) w.Ids.Add(id);
                    if (w.Icon.Length == 0) w.Icon = l.Str("displayIcon");
                }
            if (w.Id.Length > 0) list.Add(w);
        }
        return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A wishlist skin's picture, downloaded once and kept on this PC.</summary>
    public static Task<string> SkinPicture(WishSkin w) => Picture(w.Id, w.Icon);

    /// <summary>Wishlist skins in the store now: each match and where it is ("daily" or "night").</summary>
    public static List<(WishSkin Skin, StoreItem Item, string Where)> WishMatches(Storefront sf, IEnumerable<WishSkin> wishlist)
    {
        var found = new List<(WishSkin, StoreItem, string)>();
        foreach (var w in wishlist)
        {
            var ids = new HashSet<string>(w.Ids, StringComparer.OrdinalIgnoreCase);
            foreach (var i in sf.Daily) if (ids.Contains(i.Id)) found.Add((w, i, "daily"));
            foreach (var i in sf.Night) if (ids.Contains(i.Id)) found.Add((w, i, "night"));
        }
        return found;
    }

    // ───────── names and pictures (valorant-api.com, cached on this PC) ─────────

    static Dictionary<string, (string Name, string Icon)> _names;

    static string NamesFile => Path.Combine(Dir, "names.json");

    static async Task<Storefront> Name(Storefront sf)
    {
        _names ??= LoadNames();
        async Task<StoreItem> One(StoreItem i, bool isBundle)
        {
            if (string.IsNullOrEmpty(i.Id)) return i;
            if (!_names.TryGetValue(i.Id, out var n))
            {
                try
                {
                    var url = isBundle ? $"https://valorant-api.com/v1/bundles/{i.Id}" : $"https://valorant-api.com/v1/weapons/skinlevels/{i.Id}";
                    using var doc = JsonDocument.Parse(await Content.GetStringAsync(url));
                    var d = doc.RootElement.Get("data") ?? default;
                    var icon = isBundle ? d.Str("displayIcon2") is { Length: > 0 } i2 ? i2 : d.Str("displayIcon") : d.Str("displayIcon");
                    n = (d.Str("displayName"), icon);
                    if (n.Name.Length > 0) { _names[i.Id] = n; SaveNames(); }
                }
                catch { n = ("", null); }
            }
            return i with { Name = n.Name, Icon = await Picture(i.Id, n.Icon) };
        }
        for (int k = 0; k < sf.Daily.Count; k++) sf.Daily[k] = await One(sf.Daily[k], false);
        for (int k = 0; k < sf.Night.Count; k++) sf.Night[k] = await One(sf.Night[k], false);
        return sf.Bundle != null ? sf with { Bundle = await One(sf.Bundle, true) } : sf;
    }

    /// <summary>
    /// A player card's picture, downloaded once from the community mirror's picture host and kept on this PC: "small"
    /// (the square profile picture) or "wide" (the banner). Only the card's id is sent.
    /// </summary>
    public static Task<string> CardArt(string cardId, bool wide) =>
        Guid.TryParse(cardId, out var g)
            ? Picture($"card-{g:D}-{(wide ? "wide" : "small")}", $"https://media.valorant-api.com/playercards/{g:D}/{(wide ? "wideart" : "smallart")}.png")
            : Task.FromResult<string>(null);

    static async Task<string> Picture(string id, string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var file = Path.Combine(Dir, "img", id + ".png");
        if (File.Exists(file)) return file;
        // Only the community mirror's picture host is ever asked.
        if (!url.StartsWith("https://media.valorant-api.com/", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var bytes = await Content.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(file, bytes);
            return file;
        }
        catch { return null; }
    }

    static Dictionary<string, (string, string)> LoadNames()
    {
        try
        {
            if (File.Exists(NamesFile))
                return JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(NamesFile))!
                    .Where(kv => kv.Value.Length == 2).ToDictionary(kv => kv.Key, kv => (kv.Value[0], kv.Value[1]), StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    static void SaveNames()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(NamesFile, JsonSerializer.Serialize(_names.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.Name, kv.Value.Icon })));
        }
        catch { }
    }
}
