using System.Text.Json;

namespace Pulse.Core.Games.Valorant;

/// <summary>One lineup you've saved: where to stand, where to aim, and what it does.</summary>
public sealed class Lineup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Agent { get; set; } = "";
    public string Map { get; set; } = "";
    /// <summary>A, B, C or Mid.</summary>
    public string Site { get; set; } = "A";
    /// <summary>"Attack" or "Defense".</summary>
    public string Side { get; set; } = "Attack";
    public string Ability { get; set; } = "";
    /// <summary>1 easy, 2 medium, 3 hard.</summary>
    public int Difficulty { get; set; } = 1;
    public List<string> Steps { get; set; } = new();
    /// <summary>A picture copied into Pulse's folder (file name only), or empty.</summary>
    public string Image { get; set; } = "";
    /// <summary>A link to a video of it (http or https), or empty.</summary>
    public string Video { get; set; } = "";
    public bool Favorite { get; set; }
    /// <summary>Your rating, 0 (none) to 5.</summary>
    public int Rating { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Where a lineup from Pulse's library was written up (credited on its card), or empty for your own.</summary>
    public string Source { get; set; } = "";
    public string SourceName { get; set; } = "";
    /// <summary>True for lineups from Pulse's built-in library (they can be copied to yours, not edited).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool BuiltIn { get; set; }
}

/// <summary>
/// Your lineup library, kept on this PC (valorant/lineups.json, pictures in valorant/lineups/). Nothing is uploaded;
/// a shared community library would need a Pulse server.
/// </summary>
public static class Lineups
{
    static string Dir => Path.Combine(TweakEngine.DataDir, "valorant");
    static string FilePath => Path.Combine(Dir, "lineups.json");
    public static string ImageDir => Path.Combine(Dir, "lineups");
    static List<Lineup> _all;
    static readonly object Gate = new();

    public static readonly string[] Maps = { "Abyss", "Ascent", "Bind", "Breeze", "Corrode", "Fracture", "Haven", "Icebox", "Lotus", "Pearl", "Plummet", "Split", "Sunset" };

    /// <summary>Agents whose kit has real lineups (thrown or shot abilities); the others play setups, not lineups.</summary>
    public static readonly string[] LineupAgents = { "Sova", "Viper", "Brimstone", "KAY/O", "Killjoy", "Fade", "Gekko", "Tejo", "Raze", "Cypher", "Astra", "Harbor", "Deadlock", "Vyse", "Breach", "Omen", "Clove", "Skye", "Phoenix" };

    static List<Lineup> _library;

    /// <summary>
    /// Pulse's built-in library: lineups written up by guide sites (each card credits its source), built into the
    /// app. Video opens a YouTube search for that exact lineup, since the exact aim spot is easiest to see.
    /// </summary>
    public static IReadOnlyList<Lineup> Library
    {
        get
        {
            if (_library != null) return _library;
            var list = new List<Lineup>();
            try
            {
                using var s = typeof(Lineups).Assembly.GetManifestResourceStream("lineups.json");
                if (s != null)
                {
                    var items = JsonSerializer.Deserialize<List<Lineup>>(s) ?? new();
                    for (int i = 0; i < items.Count; i++)
                    {
                        var l = items[i];
                        l.Id = "lib-" + i;
                        l.BuiltIn = true;
                        l.AddedUtc = DateTime.MinValue;
                        l.Video = VideoSearch(l.Agent, l.Map, l.Title);
                        list.Add(l);
                    }
                }
            }
            catch { }
            return _library = list;
        }
    }

    /// <summary>A YouTube search for a lineup (opened in the browser; Pulse itself doesn't go online for it).</summary>
    public static string VideoSearch(string agent, string map, string what = "") =>
        "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(string.Join(" ", new[] { "valorant", agent, map, what, "lineup" }.Where(x => !string.IsNullOrWhiteSpace(x))));
    public static readonly string[] Agents = { "Brimstone", "Viper", "Sova", "KAY/O", "Killjoy", "Cypher", "Fade", "Gekko", "Breach", "Skye", "Astra", "Harbor", "Deadlock", "Omen", "Clove", "Vyse", "Tejo", "Raze", "Chamber", "Waylay", "Sage", "Phoenix", "Jett", "Reyna", "Yoru", "Neon", "Iso" };
    public static readonly string[] Sites = { "A", "B", "C", "Mid" };

    public static IReadOnlyList<Lineup> All
    {
        get
        {
            lock (Gate)
            {
                if (_all == null)
                {
                    try { _all = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Lineup>>(File.ReadAllText(FilePath)) : null; } catch { }
                    _all ??= new();
                }
                return _all.ToList();
            }
        }
    }

    public static void Save(Lineup l)
    {
        lock (Gate)
        {
            _ = All;
            var at = _all.FindIndex(x => x.Id == l.Id);
            if (at >= 0) _all[at] = l; else _all.Add(l);
            Write();
        }
    }

    public static void Delete(string id)
    {
        lock (Gate)
        {
            _ = All;
            var l = _all.FirstOrDefault(x => x.Id == id);
            if (l == null) return;
            _all.Remove(l);
            // Only Pulse's own copy, by bare file name inside its own folder.
            if (l.Image.Length > 0 && l.Image == Path.GetFileName(l.Image)) try { File.Delete(Path.Combine(ImageDir, l.Image)); } catch { }
            Write();
        }
    }

    static void Write()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_all, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }

    /// <summary>Copies a picture into Pulse's folder and returns its file name (empty if it couldn't).</summary>
    public static string AddImage(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp")) return "";
            if (new FileInfo(path).Length > 15_000_000) return "";
            Directory.CreateDirectory(ImageDir);
            var name = Guid.NewGuid().ToString("N") + ext;
            File.Copy(path, Path.Combine(ImageDir, name));
            return name;
        }
        catch { return ""; }
    }

    /// <summary>True for a link Pulse will open (http or https only).</summary>
    public static bool SafeLink(string url) => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    /// <summary>Lineups matching the filters (empty filter = any) and search text, favorites first, then by rating.</summary>
    public static List<Lineup> Find(string map, string agent, string site, string side, int difficulty, bool favoritesOnly, string text, bool library = false)
    {
        text = (text ?? "").Trim();
        return (library ? Library : All).Where(l => (map.Length == 0 || l.Map == map) && (agent.Length == 0 || l.Agent == agent) && (site.Length == 0 || l.Site == site)
                && (side.Length == 0 || l.Side == side) && (difficulty == 0 || l.Difficulty == difficulty) && (!favoritesOnly || l.Favorite)
                && (text.Length == 0 || l.Title.Contains(text, StringComparison.OrdinalIgnoreCase) || l.Ability.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || l.Steps.Any(s => s.Contains(text, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(l => l.Favorite).ThenByDescending(l => l.Rating).ThenByDescending(l => l.AddedUtc).ToList();
    }
}
