using System.Text.Json;

namespace Pulse.Core.Games.Valorant;

/// <summary>
/// Riot IDs of the players in your matches: from the matches themselves (Riot includes them), plus any Pulse looked
/// up for matches saved before it kept them. Only on this PC.
/// </summary>
public static class NameBook
{
    static readonly object Gate = new();
    static Dictionary<string, string> _fromMatches, _looked;
    static int _builtFor = -1;
    static string FilePath => Path.Combine(TweakEngine.DataDir, "valorant", "names.json");

    static void Ensure()
    {
        var all = VStore.All;
        if (_fromMatches != null && _builtFor == all.Count) return;
        var d = new Dictionary<string, string>();
        foreach (var m in all.OrderBy(m => m.StartUtc)) // newest last, so a changed name wins
        {
            foreach (var p in m.Players) if (!string.IsNullOrEmpty(p.Name)) d[p.Id] = p.Name;
            if (m.Names != null) foreach (var kv in m.Names) if (!string.IsNullOrEmpty(kv.Value)) d[kv.Key] = kv.Value;
        }
        _fromMatches = d;
        _builtFor = all.Count;
        if (_looked == null)
        {
            try { _looked = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) : null; } catch { }
            _looked ??= new();
        }
    }

    /// <summary>The Riot ID ("Name#TAG") for an account, or null when Pulse doesn't know it.</summary>
    public static string Get(string puuid)
    {
        if (string.IsNullOrEmpty(puuid)) return null;
        lock (Gate)
        {
            Ensure();
            return _fromMatches.TryGetValue(puuid, out var n) ? n : _looked.TryGetValue(puuid, out var l) ? l : null;
        }
    }

    /// <summary>Accounts with no known name yet (to look up).</summary>
    public static List<string> Missing(IEnumerable<string> puuids)
    {
        lock (Gate)
        {
            Ensure();
            return puuids.Where(id => !string.IsNullOrEmpty(id) && !_fromMatches.ContainsKey(id) && !_looked.ContainsKey(id)).Distinct().ToList();
        }
    }

    /// <summary>Names found by a lookup (Riot's name service).</summary>
    public static void Add(IReadOnlyDictionary<string, string> found)
    {
        if (found == null || found.Count == 0) return;
        lock (Gate)
        {
            Ensure();
            foreach (var kv in found) if (!string.IsNullOrEmpty(kv.Value)) _looked[kv.Key] = kv.Value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_looked));
            }
            catch { }
        }
    }

    /// <summary>Known Riot IDs that contain the text (for search suggestions).</summary>
    public static List<(string Puuid, string Name)> Find(string text, int take = 8)
    {
        text = (text ?? "").Trim();
        lock (Gate)
        {
            Ensure();
            return _fromMatches.Concat(_looked).GroupBy(kv => kv.Key).Select(g => (g.Key, g.First().Value))
                .Where(x => text.Length == 0 || x.Value.Contains(text, StringComparison.OrdinalIgnoreCase))
                .Take(take).ToList();
        }
    }
}
