using System.Text;
using Pulse.Core.Games.Live;
using Xunit;

namespace PulseOptimizer.Tests;

/// <summary>The VALORANT recap shows up as soon as the presence says you left the match, in either presence layout.</summary>
public class ValorantPresenceTests
{
    static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Reads_the_match_state_from_both_presence_layouts()
    {
        Assert.Equal("INGAME", ValorantMatches.StateFromPresence(B64("{\"isValid\":true,\"sessionLoopState\":\"INGAME\",\"matchMap\":\"/Game/Maps/Ascent/Ascent\"}")));
        Assert.Equal("MENUS", ValorantMatches.StateFromPresence(B64("{\"isValid\":true,\"matchPresenceData\":{\"sessionLoopState\":\"MENUS\",\"provisioningFlow\":\"Invalid\"},\"partyPresenceData\":{}}")));
        Assert.Null(ValorantMatches.StateFromPresence(""));
        Assert.Null(ValorantMatches.StateFromPresence("not base64!"));
    }
}

/// <summary>Kills and deaths land where they happened in the clip: the match is lined up with VALORANT's round log.</summary>
public class ValorantTimingTests
{
    static readonly DateTime T0 = new(2026, 11, 8, 5, 0, 0, DateTimeKind.Utc);

    /// <summary>A log with rounds starting every 100 s: buy phase at 0.0x, barriers at 30 (45 on round 1).</summary>
    static string Log(DateTime first, int rounds, int hourShift = 0)
    {
        var sb = new System.Text.StringBuilder();
        for (int r = 0; r < rounds; r++)
        {
            var buy = first.AddSeconds(100 * r).AddHours(hourShift);
            var drop = buy.AddSeconds(r == 0 ? 45 : 30);
            sb.AppendLine($"[{buy:yyyy.MM.dd-HH.mm.ss}:{buy:fff}][123]LogShooterGameState: Display: Gameplay started at local time 0.03");
            sb.AppendLine($"[{drop:yyyy.MM.dd-HH.mm.ss}:{drop:fff}][124]LogShooterGameState: Display: Gameplay started at local time {(r == 0 ? "45.00" : "30.00")}");
        }
        return sb.ToString();
    }

    [Fact]
    public void Lines_the_match_up_with_the_round_log()
    {
        // Riot's game time starts 20 s before the first buy phase; its start time is 25 s off from the truth.
        var firstBuy = T0.AddSeconds(20);
        var marks = ValorantMatches.ParseRoundMarks(Log(firstBuy, 5), firstBuy.AddSeconds(600));
        var kills = new List<(long, long)>();
        for (int r = 0; r < 5; r++) kills.Add((20_000 + 100_000L * r + 52_000, 52_000));   // a kill 52 s into each round
        var zero = ValorantMatches.Align(kills, marks, T0.AddSeconds(-25));
        Assert.NotNull(zero);
        Assert.InRange(Math.Abs((zero.Value - T0).TotalMilliseconds), 0, 50);
    }

    [Fact]
    public void Works_when_round_time_counts_from_the_barriers_and_when_the_log_is_in_local_time()
    {
        var firstBuy = T0.AddSeconds(20);
        // Log written in UTC−5: the file's last-write time (UTC) puts it right.
        var marks = ValorantMatches.ParseRoundMarks(Log(firstBuy, 6, hourShift: -5), firstBuy.AddSeconds(700));
        Assert.Equal(firstBuy, marks.BuyStarts[0]);
        // Round time from the barrier drop: round 1's barriers drop 45 s in, the rest 30 s in.
        var kills = new List<(long, long)>();
        for (int r = 0; r < 6; r++)
        {
            long drop = 20_000 + 100_000L * r + (r == 0 ? 45_000 : 30_000);
            kills.Add((drop + 10_000, 10_000));
        }
        var zero = ValorantMatches.Align(kills, marks, T0.AddSeconds(40));
        Assert.NotNull(zero);
        Assert.InRange(Math.Abs((zero.Value - T0).TotalMilliseconds), 0, 50);
    }

    [Fact]
    public void Never_uses_a_log_that_does_not_line_up()
    {
        var marks = ValorantMatches.ParseRoundMarks(Log(T0.AddHours(-2), 5), T0.AddHours(-2).AddSeconds(600));
        var kills = new List<(long, long)> { (72_000, 52_000), (172_000, 52_000), (272_000, 52_000) };
        Assert.Null(ValorantMatches.Align(kills, marks, T0));
    }
}
