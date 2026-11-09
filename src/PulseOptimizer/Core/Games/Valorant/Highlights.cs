namespace Pulse.Core.Games.Valorant;

/// <summary>
/// The plays worth a clip in a finished match: rounds with 3 or more kills (3K, 4K, ace) and clutches won against two
/// or more. VALORANT doesn't tell other apps about kills while you play, so Pulse keeps the whole match in a temporary
/// file and, once Riot has the match, cuts these out using each kill's time.
/// </summary>
public static class Highlights
{
    public sealed record Play(int Round, string Title, DateTime From, DateTime To, int Rank);

    /// <summary>Seconds of build-up before the first kill, and after the last.</summary>
    public const double Before = 6, After = 3, MaxSeconds = 75;

    public static List<Play> Find(VMatch m, string me = null)
    {
        me ??= m.Me;
        var list = new List<Play>();
        if (m.ZeroUtc == null || string.IsNullOrEmpty(me)) return list;   // without the match's real start, times would be off
        var team = m.TeamOf(me);
        var clutches = Stats.Clutches(m, me).Where(c => c.Won && c.Vs >= 2).ToDictionary(c => c.Round);
        foreach (var r in m.Rounds.OrderBy(r => r.N))
        {
            var mine = m.Kills.Where(k => k.Round == r.N && k.Killer == me && k.Victim != me && m.TeamOf(k.Victim) != team).OrderBy(k => k.GameMs).ToList();
            bool clutch = clutches.TryGetValue(r.N, out var c);
            if (mine.Count < 3 && !clutch) continue;
            var kills = m.Kills.Where(k => k.Round == r.N).OrderBy(k => k.GameMs).ToList();
            long from = mine.Count > 0 ? mine[0].GameMs : kills.FirstOrDefault()?.GameMs ?? 0;
            if (clutch)
            {
                // From the moment you were left alone (the last teammate's death) to the end.
                var mates = m.Players.Where(p => p.Team == team && p.Id != me).Select(p => p.Id).ToHashSet();
                var lastMate = kills.LastOrDefault(k => mates.Contains(k.Victim));
                if (lastMate != null) from = Math.Min(from, lastMate.GameMs);
            }
            long to = Math.Max(mine.Count > 0 ? mine[^1].GameMs : from, kills.LastOrDefault()?.GameMs ?? from);
            if (!clutch) to = mine[^1].GameMs;
            var start = m.LocalTime(from).AddSeconds(-Before);
            var end = m.LocalTime(to).AddSeconds(After);
            if ((end - start).TotalSeconds > MaxSeconds) start = end.AddSeconds(-MaxSeconds);
            string title = mine.Count >= 5 ? "Ace" : clutch ? $"1v{c.Vs} clutch" : $"{mine.Count}K";
            if (clutch && mine.Count >= 3 && mine.Count < 5) title = $"1v{c.Vs} clutch ({mine.Count}K)";
            int rank = mine.Count >= 5 ? 100 : (clutch ? 40 + 10 * c.Vs : 0) + mine.Count * 10;
            list.Add(new Play(r.N, title, start, end, rank));
        }
        return list;
    }
}
