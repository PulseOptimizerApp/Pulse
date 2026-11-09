using Pulse.Core.Games.Valorant;
using Xunit;

namespace PulseOptimizer.Tests;

public class MetBeforeTests
{
    [Fact]
    public void Lists_players_met_before_and_skips_the_current_match()
    {
        VMatch M(string id, DateTime when, string mateId, string foeId, string winner)
        {
            var m = new VMatch { Id = id, Me = "me", Queue = "competitive", StartUtc = when };
            m.Players.Add(new VPlayer { Id = "me", Team = "Red", Agent = "Jett" });
            m.Players.Add(new VPlayer { Id = mateId, Team = "Red", Agent = "Sova" });
            m.Players.Add(new VPlayer { Id = foeId, Team = "Blue", Agent = "Sage" });
            m.Rounds.Add(new VRound { N = 0, Winner = winner, Result = "Elimination" });
            return m;
        }
        var t = new DateTime(2026, 11, 1);
        var all = new[] { M("a", t, "x", "y", "Red"), M("b", t.AddDays(1), "y", "x", "Blue"), M("now", t.AddDays(2), "x", "z", "Red") };
        var met = Stats.MetBefore(all, "me", skipMatchId: "now");
        Assert.Equal(1, met["x"].With);
        Assert.Equal(1, met["x"].WithWins);
        Assert.Equal(1, met["x"].Against);
        Assert.Equal(1, met["y"].Against);
        Assert.Equal(1, met["y"].AgainstWins);   // you won the game against y
        Assert.False(met.ContainsKey("z"));      // only in the current match
        Assert.False(met.ContainsKey("me"));
    }
}
