using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace Pulse.Core.Games.Valorant;

/// <summary>
/// VALORANT map geometry from the game's own data (embedded at build time by tools/get-match-art.py): how to place
/// a point from a match on the minimap picture, the callout names ("A Main", "Heaven"), and weapon names.
/// </summary>
public static class MapGeo
{
    public sealed record Callout(string Region, string Super, double X, double Y)
    {
        public string Name => string.IsNullOrEmpty(Super) || Region.StartsWith(Super, StringComparison.OrdinalIgnoreCase) ? Region : $"{Super} {Region}";
    }

    sealed record MapInfo(double XMul, double YMul, double XAdd, double YAdd, List<Callout> Callouts);

    static Dictionary<string, MapInfo> _maps;
    static Dictionary<string, string> _weapons, _weaponCats, _agentIds;

    static void Load()
    {
        if (_maps != null) return;
        _maps = new(StringComparer.OrdinalIgnoreCase);
        _weapons = new(StringComparer.OrdinalIgnoreCase);
        _weaponCats = new(StringComparer.OrdinalIgnoreCase);
        _agentIds = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Matches/meta.json", UriKind.Absolute))?.Stream;
            if (s == null) return;
            using (s) LoadFrom(JsonDocument.Parse(s).RootElement);
        }
        catch { }
    }

    /// <summary>Reads the map and weapon lists (separate so tests can feed their own).</summary>
    internal static void LoadFrom(JsonElement root)
    {
        _maps ??= new(StringComparer.OrdinalIgnoreCase);
        _weapons ??= new(StringComparer.OrdinalIgnoreCase);
        _weaponCats ??= new(StringComparer.OrdinalIgnoreCase);
        _agentIds ??= new(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("maps", out var maps) && maps.ValueKind == JsonValueKind.Object)
            foreach (var m in maps.EnumerateObject())
            {
                try
                {
                    var t = m.Value.GetProperty("m").EnumerateArray().Select(x => x.GetDouble()).ToArray();
                    var calls = new List<Callout>();
                    if (m.Value.TryGetProperty("c", out var c))
                        foreach (var x in c.EnumerateArray())
                        {
                            var a = x.EnumerateArray().ToArray();
                            calls.Add(new Callout(a[0].GetString(), a[1].GetString(), a[2].GetDouble(), a[3].GetDouble()));
                        }
                    if (t.Length == 4 && t[0] != 0) _maps[m.Name] = new MapInfo(t[0], t[1], t[2], t[3], calls);
                }
                catch { }
            }
        if (root.TryGetProperty("weapons", out var w) && w.ValueKind == JsonValueKind.Object)
            foreach (var p in w.EnumerateObject()) _weapons[p.Name] = p.Value.GetString();
        if (root.TryGetProperty("agentIds", out var ag) && ag.ValueKind == JsonValueKind.Object)
            foreach (var p in ag.EnumerateObject()) _agentIds[p.Name] = p.Value.GetString();
        if (root.TryGetProperty("weaponCats", out var wc) && wc.ValueKind == JsonValueKind.Object)
            foreach (var p in wc.EnumerateObject()) _weaponCats[p.Name] = p.Value.GetString();
    }

    /// <summary>An agent's name by Riot's id, from the game content embedded at build time; null when unknown.</summary>
    public static string AgentById(string id)
    {
        Load();
        return id != null && _agentIds.TryGetValue(id, out var n) ? n : null;
    }

    /// <summary>"Rifle", "Sniper", "Sidearm", "SMG", "Shotgun", "Heavy", "Melee"; "" when unknown.</summary>
    public static string WeaponCategory(string id)
    {
        Load();
        return id != null && _weaponCats.TryGetValue(id, out var c) ? c switch { "Sidearm" => "Sidearm", "SMG" => "SMG", "Shotgun" => "Shotgun", "Rifle" => "Rifle", "Sniper" => "Sniper", "Heavy" => "Machine gun", "Melee" => "Melee", var x => x } : "";
    }

    public static bool HasMap(string map) { Load(); return _maps.ContainsKey(MatchArt.Key(map)); }

    /// <summary>The minimap picture for a map; null when it wasn't embedded.</summary>
    public static ImageSource Minimap(string map) => MatchArt.Minimap(map);

    /// <summary>
    /// Where a point from a match sits on the minimap, from 0 to 1 across and down. The game's X and Y are swapped
    /// on the minimap, which is how Riot's own data describes it.
    /// </summary>
    public static Point? ToMinimap(string map, double x, double y)
    {
        Load();
        if (!_maps.TryGetValue(MatchArt.Key(map), out var i)) return null;
        return new Point(y * i.XMul + i.XAdd, x * i.YMul + i.YAdd);
    }

    /// <summary>The nearest callout to a point ("A Main"), or "" when the map has none.</summary>
    public static string CalloutAt(string map, double x, double y)
    {
        Load();
        if (!_maps.TryGetValue(MatchArt.Key(map), out var i) || i.Callouts.Count == 0) return "";
        return i.Callouts.MinBy(c => (c.X - x) * (c.X - x) + (c.Y - y) * (c.Y - y))!.Name;
    }

    public static string Weapon(string id)
    {
        Load();
        return id != null && _weapons.TryGetValue(id, out var n) ? n : null;
    }

    /// <summary>For tests.</summary>
    internal static void Reset() { _maps = null; _weapons = null; _weaponCats = null; _agentIds = null; }
}
