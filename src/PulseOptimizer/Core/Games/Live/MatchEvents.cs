namespace Pulse.Core.Games.Live;

/// <summary>What the game integrations report: big plays to clip, and finished matches.</summary>
public static class MatchEvents
{
    /// <summary>(game, label like "3K" or "Triple kill", seconds back to cover the whole play).</summary>
    public static event Action<string, string, int> Highlight;
    public static event Action<MatchRecord> Finished;

    internal static void RaiseHighlight(string game, string label, int seconds)
    {
        try { Highlight?.Invoke(game, label, seconds); } catch { }
    }

    internal static void RaiseFinished(MatchRecord m)
    {
        try { Finished?.Invoke(m); } catch { }
    }

    /// <summary>"de_mirage" → "Mirage", "cs_office" → "Office".</summary>
    public static string PrettyCsMap(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var n = name.Contains('_') ? name[(name.IndexOf('_') + 1)..] : name;
        return string.Join(" ", n.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    /// <summary>"npc_dota_hero_shadow_shaman" → "Shadow Shaman".</summary>
    public static string PrettyDotaHero(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var n = name.Replace("npc_dota_hero_", "");
        return string.Join(" ", n.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    /// <summary>Kills in one round → the label players use.</summary>
    public static string RoundLabel(int kills) => kills >= 5 ? "ACE" : kills + "K";
}
