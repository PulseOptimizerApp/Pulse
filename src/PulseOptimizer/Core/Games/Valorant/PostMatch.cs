namespace Pulse.Core.Games.Valorant;

/// <summary>
/// The short summary shown a few seconds after a match: the result, your numbers, and the one thing that mattered most,
/// either what cost you the most rounds or (after a good game) what won them. One point, not a list: one thing to
/// work on is something you'll actually remember next game.
/// </summary>
public static class PostMatch
{
    public sealed record Point(
        // English text with {0}/{1} placeholders, for translation.
        string Format,
        object[] Args,
        // true = something to fix, false = something that went well.
        bool Problem,
        // Which death type to show in the heatmaps' "Your rounds" for this, if any.
        DeathTag? Tag);

    public sealed record Summary(
        VMatch Match, string Map, string Agent, int Result, int Us, int Them,
        int Kills, int Deaths, int Assists, double Acs, double Adr, double HsPct, double Score, int Place, Point Main);

    public static Summary For(VMatch m, string me = null)
    {
        me ??= m.Me;
        var p = m.Players.FirstOrDefault(x => x.Id == me);
        if (p == null) return null;
        var team = p.Team;
        var t = Analysis.Totals(m, me);
        int us = m.Rounds.Count(r => r.Winner == team), them = m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != team);
        int place = 1 + m.Players.Count(x => x.Id != me && x.Score > p.Score);
        return new Summary(m, m.Map, p.Agent, m.Outcome(team), us, them, p.Kills, p.Deaths, p.Assists,
            t.Acs, t.Adr, t.HeadshotPct, Tracker.MatchScore(m, me), place, MainPoint(m, me));
    }

    /// <summary>
    /// The one thing that mattered most. Problems are counted in rounds your team lost (a first death in a round you
    /// still won didn't cost anything); the biggest one is picked if it cost at least 3 rounds. Otherwise, the best thing
    /// you did.
    /// </summary>
    public static Point MainPoint(VMatch m, string me)
    {
        var team = m.TeamOf(me);
        var lost = new HashSet<int>(m.Rounds.Where(r => r.Winner.Length > 0 && r.Winner != team).Select(r => r.N));
        var deaths = Analysis.Deaths(m, me).Where(d => lost.Contains(d.Kill.Round)).ToList();
        int firstDeaths = deaths.Count(d => d.Tags.Contains(DeathTag.FirstDeath));
        int untraded = deaths.Count(d => d.Tags.Contains(DeathTag.NotTraded) && !d.Tags.Contains(DeathTag.FirstDeath));
        int alone = deaths.Count(d => d.Tags.Contains(DeathTag.Alone) && !d.Tags.Contains(DeathTag.FirstDeath) && !d.Tags.Contains(DeathTag.NotTraded));
        int outgunned = deaths.Count(d => d.Tags.Contains(DeathTag.Outgunned));
        var clutches = Stats.Clutches(m, me);
        int clutchLost = clutches.Count(c => !c.Won && c.Vs == 1);

        var problems = new List<(int Rounds, Point P)>
        {
            (firstDeaths, new Point("You died first in {0} rounds your team lost. Let a teammate take first contact, or play off them.", new object[] { firstDeaths }, true, DeathTag.FirstDeath)),
            (untraded, new Point("You died without a trade in {0} rounds your team lost. Play closer to a teammate so they can refrag.", new object[] { untraded }, true, DeathTag.NotTraded)),
            (alone, new Point("You died far from your team in {0} lost rounds. Fewer solo plays, more crossfires.", new object[] { alone }, true, DeathTag.Alone)),
            (outgunned, new Point("You were outgunned in {0} lost rounds: a cheaper gun against a better one. Save together, buy together.", new object[] { outgunned }, true, DeathTag.Outgunned)),
            (clutchLost * 2, new Point("You lost {0} 1v1s. Slow down when it's one on one: use utility and sound, not a dry peek.", new object[] { clutchLost }, true, DeathTag.LastAlive)),
        };
        var worst = problems.OrderByDescending(x => x.Rounds).First();
        if (worst.Rounds >= 3) return worst.P;

        // No big problem: the best thing you did.
        var multi = Stats.MultiKills(m, me);
        int clutchWon = clutches.Count(c => c.Won);
        var t = Analysis.Totals(m, me);
        if (multi[5] > 0) return new Point("You got an ace. Clip it.", Array.Empty<object>(), false, null);
        if (clutchWon > 0) return new Point("You won {0} clutch(es), the rounds that swing games.", new object[] { clutchWon }, false, null);
        if (multi[4] + multi[3] >= 2) return new Point("{0} rounds with 3 or more kills: you carried fights.", new object[] { multi[4] + multi[3] }, false, null);
        if (t.FirstKills >= 4 && t.FirstKills > t.FirstDeaths) return new Point("You opened {0} rounds with the first kill, more than you died first.", new object[] { t.FirstKills }, false, null);
        if (t.Rounds > 0 && t.KastPct >= 75) return new Point("You did something useful in {0}% of rounds (KAST). Steady.", new object[] { Math.Round(t.KastPct) }, false, null);
        if (worst.Rounds > 0) return worst.P;
        return new Point("A quiet game. Look at your rounds to see where it slipped.", Array.Empty<object>(), false, null);
    }
}
