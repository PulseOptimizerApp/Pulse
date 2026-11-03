namespace Pulse.Core.Games;

public record Suggestion(string Name, string Why, string[] Genres, bool Free, string Url = null)
{
    public string Link => Url ?? "https://store.steampowered.com/search/?term=" + Uri.EscapeDataString(Name);
    public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).Take(2).Select(w => char.ToUpperInvariant(w[0])));
    public string PriceText => Free ? "Free to play" : "Paid";
    public System.Windows.Media.ImageSource Cover => GameArt.Bundled(Name);
    public bool HasCover => Cover != null;
    public string GenreText => string.Join(" · ", Genres.Take(2).Select(g => char.ToUpperInvariant(g[0]) + g[1..]));
}

/// <summary>
/// A hand-picked list of well-reviewed PC games, by genre. Works offline: Pulse doesn't track what you play;
/// "For you" looks only at the games installed on this PC.
/// </summary>
public static class Suggestions
{
    public static readonly string[] Genres =
        { "shooter", "tactical", "battle royale", "hero shooter", "co-op", "survival", "sandbox", "rpg", "strategy", "racing", "sports", "fighting", "horror", "casual" };

    public static readonly List<Suggestion> All = new()
    {
        // Shooters
        new("Apex Legends", "Fast squad battle royale with hero abilities and great movement.", new[] { "battle royale", "hero shooter", "shooter" }, true),
        new("THE FINALS", "Team shooter where you can blow up almost every building.", new[] { "shooter", "competitive" }, true),
        new("ARC Raiders", "Extraction shooter: scavenge the surface, fight robots and other players, get out alive.", new[] { "shooter", "survival", "co-op" }, false),
        new("Battlefield 6", "Huge all-out battles with vehicles and destruction.", new[] { "shooter" }, false),
        new("Call of Duty: Warzone", "Call of Duty's free battle royale.", new[] { "battle royale", "shooter" }, true, "https://www.callofduty.com/warzone"),
        new("PUBG: Battlegrounds", "The slower, more tactical battle royale that started the genre.", new[] { "battle royale", "shooter", "tactical" }, true),
        new("Hunt: Showdown 1896", "Tense player-versus-player bounty hunting where sound matters.", new[] { "shooter", "tactical", "horror" }, false),
        new("Ready or Not", "Slow, methodical police tactics; close to Rainbow Six without the gadgets.", new[] { "tactical", "shooter", "co-op" }, false),
        new("Delta Force", "Free military shooter with large battles and a tactical extraction mode.", new[] { "shooter", "tactical" }, true),
        new("Halo Infinite", "Classic arena shooter multiplayer, free.", new[] { "shooter", "competitive" }, true),
        new("Team Fortress 2", "The original class-based hero shooter, still free and still fun.", new[] { "hero shooter", "shooter" }, true),
        new("Paladins", "Free hero shooter with loadout cards to build each champion your way.", new[] { "hero shooter", "shooter" }, true),
        new("Splitgate 2", "Arena shooter with portals.", new[] { "shooter", "competitive" }, true),

        // Co-op and survival
        new("Helldivers 2", "Chaotic four-player co-op against bugs and robots, with friendly fire on.", new[] { "co-op", "shooter" }, false),
        new("Deep Rock Galactic", "Dwarves mining and shooting bugs in co-op caves. Very good with friends.", new[] { "co-op", "shooter" }, false),
        new("Lethal Company", "Co-op horror scavenging that's as funny as it is scary.", new[] { "co-op", "horror" }, false),
        new("R.E.P.O.", "Physics-based co-op horror: carry valuables out without breaking them (or yourselves).", new[] { "co-op", "horror", "casual" }, false),
        new("PEAK", "Climb a mountain with friends; simple, silly and great in a group.", new[] { "co-op", "casual" }, false),
        new("Phasmophobia", "Co-op ghost hunting where the ghost can hear your voice.", new[] { "co-op", "horror" }, false),
        new("Split Fiction", "Two-player co-op adventure from the makers of It Takes Two.", new[] { "co-op" }, false),
        new("Rust", "Hardcore survival: build a base, gather, raid and get raided.", new[] { "survival", "shooter" }, false),
        new("Valheim", "Viking survival and building, at its best with a few friends.", new[] { "survival", "co-op", "sandbox" }, false),

        // Sandbox and casual
        new("Minecraft", "Build anything, alone or with friends.", new[] { "sandbox", "survival", "casual" }, false, "https://www.minecraft.net"),
        new("Terraria", "2D digging, building and boss fights with a huge amount to find.", new[] { "sandbox", "co-op", "survival" }, false),
        new("Garry's Mod", "Physics sandbox with endless community game modes.", new[] { "sandbox", "casual" }, false),
        new("Fall Guys", "Silly obstacle-course party battle royale.", new[] { "casual", "battle royale" }, true),
        new("Stardew Valley", "Relaxing farm life with co-op for up to four.", new[] { "casual", "co-op", "rpg" }, false),
        new("Rocket League", "Football with rocket cars. Easy to start, very deep to master.", new[] { "sports", "competitive", "casual" }, true),

        // RPGs and adventure
        new("Elden Ring", "Huge, hard and rewarding open-world action RPG.", new[] { "rpg" }, false),
        new("Baldur's Gate 3", "Story-rich RPG where your choices really change things; co-op too.", new[] { "rpg", "co-op", "strategy" }, false),
        new("Cyberpunk 2077", "Open-world RPG in a neon city, much improved since launch.", new[] { "rpg", "shooter" }, false),
        new("Clair Obscur: Expedition 33", "Turn-based RPG with real-time dodges and a striking world.", new[] { "rpg" }, false),
        new("Hades II", "Fast action roguelike where every run is different.", new[] { "rpg", "casual" }, false),
        new("Genshin Impact", "Free open-world action RPG with a big cast of characters.", new[] { "rpg" }, true, "https://genshin.hoyoverse.com"),

        // Strategy and MOBA
        new("League of Legends", "The most-played MOBA: five-versus-five lane battles.", new[] { "strategy", "competitive" }, true, "https://www.leagueoflegends.com"),
        new("Dota 2", "Deep, demanding MOBA; supports AMD Anti-Lag 2 and NVIDIA Reflex.", new[] { "strategy", "competitive" }, true),
        new("Age of Empires II: Definitive Edition", "Classic real-time strategy, still thriving online.", new[] { "strategy" }, false),

        // Racing, sports, fighting
        new("Forza Horizon 5", "Open-world racing across Mexico; great with a controller.", new[] { "racing" }, false),
        new("Trackmania", "Free arcade racing about perfect lines and shaving off milliseconds.", new[] { "racing", "competitive" }, true),
        new("Rematch", "Five-a-side football where you control one player, from the makers of Sifu.", new[] { "sports", "competitive" }, false),
        new("Golf With Your Friends", "Chaotic minigolf for up to 12 players.", new[] { "sports", "casual", "co-op" }, false),
        new("F1 25", "The official Formula 1 game with a full career mode.", new[] { "racing", "sports" }, false),
        new("Street Fighter 6", "The easiest modern fighting game to learn, with deep ranked play.", new[] { "fighting", "competitive" }, false),
        new("Tekken 8", "3D fighting with aggressive, flashy combat.", new[] { "fighting", "competitive" }, false),
        new("Brawlhalla", "Free platform fighter in the style of Smash Bros.", new[] { "fighting", "casual" }, true),
    };

    /// <summary>Games for one genre (or "free"), best matches first.</summary>
    public static List<Suggestion> ForGenre(string genre, IEnumerable<string> installedNames = null)
    {
        var owned = Owned(installedNames);
        var list = genre == "free"
            ? All.Where(s => s.Free)
            : All.Where(s => s.Genres.Contains(genre)).OrderBy(s => Array.IndexOf(s.Genres, genre));
        return list.Where(s => !owned.Contains(s.Name)).ToList();
    }

    /// <summary>
    /// "For you": genres weighted by the games installed on this PC (Pulse's seven plus anything else it recognizes),
    /// then the best-matching games you don't already have.
    /// </summary>
    public static (List<Suggestion> picks, string because) ForYou(IEnumerable<KnownGame> installed, IEnumerable<string> installedNames)
    {
        var names = installedNames?.ToList() ?? new();
        var weights = new Dictionary<string, double>();
        void Add(IEnumerable<string> genres, double w)
        {
            int i = 0;
            foreach (var g in genres) { weights[g] = weights.GetValueOrDefault(g) + w / (1 + 0.5 * i++); }
        }
        var basis = new List<string>();
        foreach (var g in installed) { Add(g.Genres, 1); basis.Add(g.Name); }
        foreach (var s in All.Where(s => names.Any(n => Matches(n, s.Name)))) { Add(s.Genres, 1); basis.Add(s.Name); }

        var owned = Owned(names);
        if (weights.Count == 0)
            return (All.Where(s => s.Free && !owned.Contains(s.Name)).Take(8).ToList(), "");

        var picks = All.Where(s => !owned.Contains(s.Name))
            .Select(s => (s, score: s.Genres.Select((g, i) => weights.GetValueOrDefault(g) / (1 + 0.5 * i)).Sum()))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenByDescending(x => x.s.Free)
            .Select(x => x.s).Take(8).ToList();
        var because = basis.Count switch
        {
            1 => $"Because you have {basis[0]}",
            2 => $"Because you have {basis[0]} and {basis[1]}",
            _ => $"Because you have {basis[0]}, {basis[1]} and {basis.Count - 2} more",
        };
        return (picks, because);
    }

    static HashSet<string> Owned(IEnumerable<string> installedNames)
    {
        var names = installedNames?.ToList() ?? new();
        return All.Where(s => names.Any(n => Matches(n, s.Name))).Select(s => s.Name).ToHashSet();
    }

    static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    internal static bool Matches(string installedName, string suggestion)
    {
        var a = Norm(installedName); var b = Norm(suggestion);
        return b.Length > 2 && (a == b || a.StartsWith(b) || (b.Length > 6 && a.Contains(b)));
    }
}
