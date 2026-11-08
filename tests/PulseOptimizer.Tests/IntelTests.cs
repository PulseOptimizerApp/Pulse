using Pulse.Core.Games.Valorant;
using Pulse.Core.Games.Valorant.Intel;
using Xunit;

namespace PulseOptimizer.Tests;

public class IntelTests
{
    /// <summary>A match: "me" plus 4 teammates and 5 enemies at the given ranks; each player scores the given combat score.</summary>
    static VMatch M(int i, int myTier, int mates, int enemies, bool won, int myScore = 200, int othersScore = 200, double hoursApart = 1)
    {
        var m = new VMatch { Id = "m" + i, Queue = "competitive", Me = "me", Map = i % 2 == 0 ? "Ascent" : "Bind", StartUtc = new DateTime(2026, 10, 1).AddHours(i * hoursApart), LengthMs = 30 * 60000 };
        m.Players.Add(new VPlayer { Id = "me", Team = "Red", Tier = myTier, Agent = "Jett", Score = myScore * 13 });
        for (int k = 0; k < 4; k++) m.Players.Add(new VPlayer { Id = "t" + k, Team = "Red", Tier = mates, Agent = "Sova", Score = othersScore * 13 });
        for (int k = 0; k < 5; k++) m.Players.Add(new VPlayer { Id = "e" + k, Team = "Blue", Tier = enemies, Agent = "Reyna", Score = othersScore * 13 });
        for (int r = 0; r < 13; r++) m.Rounds.Add(new VRound { N = r, Winner = won ? "Red" : "Blue" });
        return m;
    }

    [Fact]
    public void Mmr_expectation_and_scale()
    {
        Assert.Equal(0.5, MmrModel.Expect(1250, 1250), 6);
        Assert.Equal(1 / 1.1, MmrModel.Expect(1650, 1250), 6);      // 4 tiers up: 10 to 1 odds
        Assert.Equal(1250, MmrModel.TierRating(12));
        Assert.Equal(12, MmrModel.TierOf(1299));
        Assert.Equal(100, MmrModel.ConfidenceOf(50));
        Assert.Equal(0, MmrModel.ConfidenceOf(350));
    }

    [Fact]
    public void Mmr_rises_when_winning_and_beating_the_lobby()
    {
        var ms = Enumerable.Range(0, 10).Select(i => M(i, 12, 12, 12, true, 300, 200)).ToList();
        var e = MmrModel.Run(ms, "me", 12);
        Assert.NotNull(e);
        Assert.Equal(10, e.Used);
        Assert.True(e.Rating > MmrModel.TierRating(12) + 150);   // well above Gold 3's start
        Assert.True(e.Gap > 1);
        Assert.True(e.Rd < 200);                                  // more certain after 10 matches
        Assert.All(e.History.Zip(e.History.Skip(1)), p => Assert.True(p.Second.Before == p.First.Rating));
    }

    [Fact]
    public void Mmr_skips_lobbies_without_ranked_enemies()
    {
        var ms = new List<VMatch> { M(0, 12, 12, 12, true), M(1, 12, 12, 0, true) };
        var e = MmrModel.Run(ms, "me", 12);
        Assert.Equal(1, e.Used);
        Assert.Equal(1, e.Skipped);
    }

    [Fact]
    public void Forecast_is_repeatable_and_follows_its_inputs()
    {
        var sure = new Forecast.Inputs(12, 0, 1.0, 20, 15, false, 20, 20);
        var o = Forecast.Run(sure, 10);
        Assert.Equal(14, o.Expected);                             // 10 wins × 20 RR = 200 RR = 2 tiers
        Assert.Equal(100, o.NextRankPct);
        var coin = new Forecast.Inputs(12, 50, 0.5, 20, 20, false, 20, 10);
        Assert.Equal(Forecast.Run(coin, 25), Forecast.Run(coin, 25));
        Assert.InRange(Forecast.Run(coin, 25).Expected, 11, 13);
    }

    [Fact]
    public void Forecast_inputs_shrink_win_rate_and_say_when_rr_is_assumed()
    {
        var ms = Enumerable.Range(0, 10).Select(i => M(i, 12, 12, 12, true)).ToList();
        var i = Forecast.From(ms, "me", 12, 40, null);
        Assert.Equal(15.0 / 20, i.WinChance, 6);                  // (10 wins + 5) / (10 + 10)
        Assert.True(i.RrAssumed);
        Assert.False(Forecast.From(ms, "me", 12, 40, new[] { 22, -15, 20, -16 }).RrAssumed);
    }

    [Fact]
    public void Matchmaking_windows_average_the_lobbies()
    {
        var ms = new List<VMatch> { M(2, 12, 11, 14, false), M(1, 12, 13, 13, true), M(0, 12, 12, 12, true) }; // newest first
        var w = Matchmaking.Of(ms, "me", 5);
        Assert.Equal(3, w.Matches);
        Assert.Equal(13, w.EnemyAvg, 6);
        Assert.Equal(14, w.HighestEnemy);
        Assert.Equal(12, w.LowestEnemy);
        Assert.Equal(200.0 / 3, w.HigherPct, 3);                  // enemies a tier or more above in 2 of 3
        Assert.Equal(200.0 / 3, w.ActualWin, 3);
    }

    [Fact]
    public void Head_to_head_counts_kills_both_ways_and_needs_three_meetings()
    {
        var ms = Enumerable.Range(0, 3).Select(i => M(i, 12, 12, 12, i != 1)).ToList();
        ms[0].Kills.Add(new VKill { Round = 0, Killer = "me", Victim = "e0" });
        ms[1].Kills.Add(new VKill { Round = 0, Killer = "e0", Victim = "me" });
        ms[2].Kills.Add(new VKill { Round = 0, Killer = "me", Victim = "e0" });
        var e0 = HeadToHead.Of(ms, "me").First(l => l.Id == "e0");
        Assert.Equal(3, e0.Encounters);
        Assert.Equal(2, e0.KillsOnThem);
        Assert.Equal(1, e0.DeathsToThem);
        Assert.Equal(2, e0.Wins);
        Assert.True(e0.IsReliable);
        Assert.False(HeadToHead.Of(ms.Take(2).ToList(), "me").First(l => l.Id == "e0").IsReliable);
    }

    [Fact]
    public void Expected_performance_only_uses_earlier_matches()
    {
        var ms = Enumerable.Range(0, 6).Select(i => M(i, 12, 12, 12, true, 200, 200)).ToList();
        ms[5].Players[0].Score = 400 * 13;                        // a huge last game can't change its own expectation
        var a = Expected.Of(ms, "me", MmrModel.Run(ms, "me", 12));
        Assert.Equal(3, a.Lines.Count);                           // the first 3 matches only build the baseline
        var last = a.Lines[^1];
        var b = Expected.Of(ms.Take(5).Append(M(5, 12, 12, 12, true, 200, 200)).ToList(), "me", MmrModel.Run(ms, "me", 12));
        Assert.Equal(b.Lines[^1].ExpAcs, last.ExpAcs, 6);
        Assert.Equal(400, last.Acs, 6);
    }

    [Fact]
    public void Opponent_adjusted_acs_counts_tougher_lobbies_for_more()
    {
        var easy = new List<VMatch> { M(0, 12, 12, 10, true, 250, 200) };
        var hard = new List<VMatch> { M(0, 12, 12, 14, true, 250, 200) };
        Assert.Equal(250 * 0.9, OpponentAdjusted.Of(easy, "me").Adjusted, 6);
        Assert.Equal(250 * 1.1, OpponentAdjusted.Of(hard, "me").Adjusted, 6);
        Assert.Equal(1, OpponentAdjusted.Of(hard, "me").HigherMatches);
    }

    [Fact]
    public void Sessions_split_on_long_gaps()
    {
        // Two sessions: three matches 35 minutes apart (start to start, 30 min long), then a day later two more.
        var ms = new List<VMatch>();
        for (int i = 0; i < 3; i++) { var m = M(i, 12, 12, 12, true); m.StartUtc = new DateTime(2026, 10, 1, 18, 0, 0).AddMinutes(35 * i); ms.Add(m); }
        for (int i = 3; i < 5; i++) { var m = M(i, 12, 12, 12, i == 3); m.StartUtc = new DateTime(2026, 10, 2, 18, 0, 0).AddMinutes(35 * (i - 3)); ms.Add(m); }
        var r = Sessions.Of(ms, "me");
        Assert.Equal(2, r.Sessions);
        Assert.Equal(2.5, r.AvgLength, 6);
        Assert.Equal(2, r.Slots.First(s => s.Position == 1).Matches);
    }

    [Fact]
    public void Career_finds_streaks()
    {
        var results = new[] { true, true, true, false, false, true };
        var ms = results.Select((w, i) => M(i, 12, 12, 12, w)).ToList();
        var c = Career.Of(ms, "me", null);
        Assert.Equal(3, c.LongestWin);
        Assert.Equal(2, c.LongestLoss);
        Assert.Equal(1, c.CurrentStreak);
    }

    [Fact]
    public void Data_checks_flag_conflicts_instead_of_hiding_them()
    {
        var m = M(0, 12, 12, 12, true);
        m.Version = VMatch.CurrentVersion;
        m.Players[0].Kills = 10;                                   // scoreboard says 10, the kill feed has none
        var (n, issues) = DataQuality.Check(new[] { m });
        Assert.Equal(1, n);
        Assert.Contains(issues, i => i.Problem.Contains("10"));
        var clean = M(1, 12, 12, 12, true);
        clean.Version = VMatch.CurrentVersion;
        Assert.Empty(DataQuality.Check(new[] { clean }).Issues);
    }

    [Fact]
    public void Synergy_compares_with_what_the_lobbies_predicted()
    {
        var ms = Enumerable.Range(0, 4).Select(i => M(i, 12, 12, 12, true)).ToList();
        var t0 = Synergy.Of(ms, "me").First(x => x.Id == "t0");
        Assert.Equal(4, t0.Matches);
        Assert.Equal(100, t0.WinPct);
        Assert.Equal(50, t0.ExpectedWinPct, 6);                    // even lobbies
        Assert.Equal(50, t0.VsExpected, 6);
    }
}
