using System.Text.Json;
using Pulse.Core.Games.Valorant;

namespace Pulse.Core.Games.Live;

/// <summary>
/// Console VALORANT (PlayStation, Xbox) from Riot's servers. Console matches are played in their own queues
/// (console_competitive…), and Riot's servers may only hand them out to a request that says it comes from a console,
/// which the X-Riot-ClientPlatform header tells them. So the history is asked for as PC, then PlayStation, then Xbox,
/// and the first that has console matches is remembered (and used for their details and the console rank).
/// Everything tried, and what came back, is kept in <see cref="Probe"/> so it can be shown and copied.
/// </summary>
public static class ValorantConsole
{
    static readonly string[] Platforms = { null, "playstation", "xbox" };

    /// <summary>The platform header that returned console matches (null: the PC one does).</summary>
    public static string Platform { get; private set; }

    static readonly List<string> _probe = new();

    /// <summary>What the last console lookup tried and what Riot answered, one line each.</summary>
    public static List<string> Probe { get { lock (_probe) return _probe.ToList(); } }

    static void Note(string line)
    {
        lock (_probe) _probe.Add(line);
        TweakEngine.AddLog("valorant", "Console: " + line);
    }

    /// <summary>
    /// A player's latest console matches (id, start time), newest first: each console queue's latest page, plus the
    /// unfiltered history in case console matches are listed there.
    /// </summary>
    internal static async Task<List<(string Id, long At)>> History(ValorantMatches.Session s, string puuid)
    {
        lock (_probe) _probe.Clear();
        var order = Platform != null ? Platforms.Where(p => p == Platform).Concat(Platforms.Where(p => p != Platform)) : Platforms;
        foreach (var plat in order)
        {
            var found = new List<(string Id, long At)>();
            foreach (var q in new[] { "" }.Concat(VMatch.ConsoleQueues.Select(x => "&queue=" + x)))
            {
                var (doc, code) = await ValorantMatches.GetStatus(s, $"https://pd.{s.Shard}.a.pvp.net/match-history/v1/history/{puuid}?startIndex=0&endIndex=20{q}", plat);
                int n = 0;
                var queues = new SortedSet<string>();
                using (doc)
                {
                    if (doc?.RootElement.Get("History") is { ValueKind: JsonValueKind.Array } h)
                        foreach (var x in h.EnumerateArray())
                        {
                            n++;
                            var queue = x.Str("QueueID");
                            queues.Add(queue.Length > 0 ? queue : "(none)");
                            if (VMatch.IsConsole(queue) && VMatch.Counts(queue))
                                found.Add((x.Str("MatchID"), x.Get("GameStartTime") is { ValueKind: JsonValueKind.Number } st ? st.GetInt64() : 0L));
                        }
                }
                Note($"{plat ?? "pc"} {(q.Length == 0 ? "all queues" : q[7..])}: HTTP {code}, {n} games" + (queues.Count > 0 ? " (" + string.Join(", ", queues) + ")" : ""));
            }
            if (found.Count > 0)
            {
                Platform = plat;
                Note($"using the {plat ?? "pc"} platform: {found.Select(f => f.Id).Distinct().Count()} console matches");
                return found.Where(f => f.Id.Length > 0).GroupBy(f => f.Id).Select(g => g.First()).OrderByDescending(f => f.At).ToList();
            }
        }
        Note("no console matches on any platform");
        return new();
    }

    /// <summary>A console match's details: as PC first, then with the console platform header.</summary>
    internal static async Task<JsonDocument> Details(ValorantMatches.Session s, string id)
    {
        var url = $"https://pd.{s.Shard}.a.pvp.net/match-details/v1/matches/{id}";
        var (doc, code) = await ValorantMatches.GetStatus(s, url, Platform);
        if (doc != null) return doc;
        foreach (var plat in Platforms.Where(p => p != Platform))
        {
            var (d2, c2) = await ValorantMatches.GetStatus(s, url, plat);
            if (d2 != null) { Platform = plat; return d2; }
            code = c2;
        }
        Note($"details for {id[..Math.Min(8, id.Length)]}…: HTTP {code}");
        return null;
    }
}
