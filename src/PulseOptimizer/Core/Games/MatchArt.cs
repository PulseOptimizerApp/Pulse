using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pulse.Core.Games;

/// <summary>
/// VALORANT art for match recap cards: agent portraits, rank badges and map backgrounds, embedded at build time by
/// tools/get-match-art.py. Nothing is fetched while Pulse runs; when a picture isn't there the card shows text only.
/// </summary>
public static class MatchArt
{
    static readonly Dictionary<string, ImageSource> Cache = new();
    static Dictionary<string, string> _agentColors;

    public static string Key(string name) => new string((name ?? "").ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

    /// <summary>The agent's portrait for VALORANT; for other games, the game's own cover.</summary>
    public static ImageSource Agent(MatchRecord m) => m.Game == "VALORANT" ? Load($"agent-{Key(m.Character)}.png", 256) ?? GameArt.Bundled(m.Game) : GameArt.Bundled(m.Game);
    /// <summary>The map for VALORANT; for other games, the game's cover, faded behind the card.</summary>
    public static ImageSource Map(MatchRecord m) => m.Game == "VALORANT" ? Load($"map-{Key(m.Map)}.jpg", 640) ?? GameArt.Bundled(m.Game) : GameArt.Bundled(m.Game);
    public static ImageSource Rank(MatchRecord m) => m.Game == "VALORANT" && m.RankTier >= 3 ? Load($"rank-{m.RankTier}.png", 64) : null;
    /// <summary>A VALORANT rank badge by tier number (3 = Iron 1 … 27 = Radiant); null when unranked.</summary>
    public static ImageSource RankIcon(int tier) => tier >= 3 ? Load($"rank-{tier}.png", 64) : null;
    /// <summary>A VALORANT map's minimap (top-down layout) by name; null when it wasn't embedded.</summary>
    public static ImageSource Minimap(string map) => string.IsNullOrEmpty(map) ? null : Load($"mini-{Key(map)}.png", 512);
    /// <summary>A VALORANT map's picture by name.</summary>
    public static ImageSource MapPicture(string map) => string.IsNullOrEmpty(map) ? null : Load($"map-{Key(map)}.jpg", 640);
    /// <summary>A VALORANT agent's portrait by name.</summary>
    public static ImageSource AgentIcon(string agent) => string.IsNullOrEmpty(agent) ? null : Load($"agent-{Key(agent)}.png", 256);
    /// <summary>A weapon's killfeed silhouette (white on clear) by Riot's weapon id; null when it wasn't embedded.</summary>
    /// <summary>VALORANT's own role icon (white on transparent), or null when the build couldn't get it.</summary>
    public static ImageSource RoleIcon(string role) => string.IsNullOrEmpty(role) ? null : Load($"role-{Key(role)}.png", 96);

    /// <summary>The agent's bust portrait (the tracker's player card); null when the build couldn't get it.</summary>
    public static ImageSource AgentBust(string agent) => string.IsNullOrEmpty(agent) ? null : Load($"bust-{Key(agent)}.png", 300);

    public static ImageSource WeaponIcon(string id) => string.IsNullOrEmpty(id) ? null : Load($"weapon-{id.ToLowerInvariant()}.png", 192);

    /// <summary>The agent's own color (from the game's art), for the tint behind the portrait; null if unknown.</summary>
    public static Color? AgentColor(MatchRecord m)
    {
        if (m.Game != "VALORANT") return null;
        if (_agentColors == null)
        {
            _agentColors = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var s = Open("meta.json");
                if (s != null && JsonDocument.Parse(s).RootElement.TryGetProperty("agents", out var a))
                    foreach (var p in a.EnumerateObject()) _agentColors[p.Name] = p.Value.GetString();
            }
            catch { }
        }
        if (!_agentColors.TryGetValue(Key(m.Character), out var hex) || hex is not { Length: 6 }) return null;
        try { return (Color)ColorConverter.ConvertFromString("#" + hex); } catch { return null; }
    }

    static Stream Open(string file)
    {
        try { return Application.GetResourceStream(new Uri($"pack://application:,,,/Pulse;component/Assets/Matches/{file}", UriKind.Absolute))?.Stream; }
        catch { return null; }
    }

    static ImageSource Load(string file, int width)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(file, out var img)) return img;
            try
            {
                using var stream = Open(file);
                if (stream != null)
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = stream;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = width;
                    bmp.EndInit();
                    bmp.Freeze();
                    img = bmp;
                }
            }
            catch { img = null; }
            Cache[file] = img;
            return img;
        }
    }

    /// <summary>A clip's picture from disk (made by Clips), small and unlocked; null if there isn't one yet.</summary>
    public static ImageSource Thumb(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = 480;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
