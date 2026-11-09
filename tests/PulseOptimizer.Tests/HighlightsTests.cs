using Pulse.Core.Games.Valorant;
using Xunit;

namespace PulseOptimizer.Tests;

public class HighlightsTests
{
    static VMatch Game()
    {
        var m = new VMatch { Id = "h", Me = "me", Queue = "competitive", Map = "Bind", FirstAttack = "Red", ZeroUtc = new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc) };
        foreach (var (id, team) in new[] { ("me", "Red"), ("m2", "Red"), ("m3", "Red"), ("e1", "Blue"), ("e2", "Blue"), ("e3", "Blue"), ("e4", "Blue"), ("e5", "Blue") })
            m.Players.Add(new VPlayer { Id = id, Team = team, Agent = "Jett" });
        for (int r = 0; r < 6; r++) m.Rounds.Add(new VRound { N = r, Winner = "Red", Result = "Elimination" });
        return m;
    }

    static void Kill(VMatch m, int round, int sec, string killer, string victim) =>
        m.Kills.Add(new VKill { Round = round, GameMs = (round * 120 + sec) * 1000L, RoundMs = sec * 1000, Killer = killer, Victim = victim });

    [Fact]
    public void Finds_aces_multi_kills_and_clutches_with_their_times()
    {
        var m = Game();
        // Round 1: an ace.
        foreach (var (v, i) in new[] { "e1", "e2", "e3", "e4", "e5" }.Select((v, i) => (v, i))) Kill(m, 1, 30 + i * 4, "me", v);
        // Round 2: a 3K.
        Kill(m, 2, 20, "me", "e1"); Kill(m, 2, 25, "me", "e2"); Kill(m, 2, 40, "me", "e3");
        // Round 3: two kills only: not a highlight.
        Kill(m, 3, 20, "me", "e1"); Kill(m, 3, 25, "me", "e2");
        // Round 4: a 1v2 clutch, won.
        Kill(m, 4, 10, "e1", "m2"); Kill(m, 4, 15, "e2", "m3"); Kill(m, 4, 30, "me", "e1"); Kill(m, 4, 38, "me", "e2");
        Kill(m, 4, 39, "m2", "e3"); // filler so the round isn't decided by kills alone

        var plays = Highlights.Find(m);
        Assert.Equal(new[] { 1, 2, 4 }, plays.Select(p => p.Round));
        Assert.Equal("Ace", plays[0].Title);
        Assert.Equal("3K", plays[1].Title);
        Assert.StartsWith("1v", plays[2].Title);

        // The ace: from 6 s before the first kill to 3 s after the last.
        Assert.Equal(m.LocalTime((1 * 120 + 30) * 1000L).AddSeconds(-Highlights.Before), plays[0].From);
        Assert.Equal(m.LocalTime((1 * 120 + 46) * 1000L).AddSeconds(Highlights.After), plays[0].To);
        // The clutch starts when you were left alone (the last teammate died at 15 s).
        Assert.Equal(m.LocalTime((4 * 120 + 15) * 1000L).AddSeconds(-Highlights.Before), plays[2].From);
        Assert.True(plays[0].Rank > plays[1].Rank);
    }

    [Fact]
    public void Nothing_without_the_matchs_real_start_time()
    {
        var m = Game();
        m.ZeroUtc = null;
        foreach (var (v, i) in new[] { "e1", "e2", "e3" }.Select((v, i) => (v, i))) Kill(m, 1, 30 + i * 4, "me", v);
        Assert.Empty(Highlights.Find(m));
    }
}
