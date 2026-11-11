using Pulse.Core.Games.Valorant;
using Xunit;

namespace PulseOptimizer.Tests;

public class ConsoleScaleTests
{
    [Fact]
    public void Console_players_are_only_compared_with_console_players()
    {
        // Console matches only.
        foreach (var m in FakeMatches.Make(5))
        {
            m.Id = "console-scale-" + m.Id;
            m.Console = true;
            VStore.Save(m);
        }
        double before = Tracker.TopPercentOf("kd", 1.0, console: true);
        Assert.False(double.IsNaN(before));

        // PC matches arriving (your PC games, players you look up) must not move a console player's standing.
        foreach (var m in FakeMatches.Make(20))
        {
            m.Id = "pc-scale-" + m.Id;
            foreach (var p in m.Players) { p.Kills *= 3; }
            VStore.Save(m);
        }
        Assert.Equal(before, Tracker.TopPercentOf("kd", 1.0, console: true));
    }
}
