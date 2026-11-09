using System.Text.Json;
using Pulse.Core.Games.Valorant;
using Xunit;

namespace PulseOptimizer.Tests;

/// <summary>
/// Cases where Pulse's numbers differed from what trackers show: surrenders, players who left or joined late, and
/// Riot's own match result.
/// </summary>
public class StatsAccuracyTests
{
    static VMatch Match(int redWins, int blueWins, string lastResult = "Elimination")
    {
        var m = new VMatch { Id = "x", Me = "me", Queue = "competitive", Map = "Ascent" };
        m.Players.Add(new VPlayer { Id = "me", Team = "Red", Agent = "Jett", Score = 4000, Kills = 20, Deaths = 10 });
        m.Players.Add(new VPlayer { Id = "foe", Team = "Blue", Agent = "Sova", Score = 3000, Kills = 10, Deaths = 20 });
        int n = 0;
        for (int i = 0; i < redWins; i++) m.Rounds.Add(new VRound { N = n++, Winner = "Red", Result = "Elimination" });
        for (int i = 0; i < blueWins; i++) m.Rounds.Add(new VRound { N = n++, Winner = "Blue", Result = "Elimination" });
        if (lastResult != "Elimination") m.Rounds[^1].Result = lastResult;
        return m;
    }

    [Fact]
    public void A_surrender_is_won_by_the_team_that_did_not_surrender_even_when_behind_on_rounds()
    {
        // Red led 9–6, then surrendered: Riot gives the surrender "round" to Blue, and Blue wins the match.
        var m = Match(9, 6);
        m.Rounds.Add(new VRound { N = 15, Winner = "Blue", Result = "Surrendered" });
        Assert.Equal(-1, m.Outcome("Red"));
        Assert.True(m.WonBy("Blue"));
        Assert.False(m.Won);
        Assert.Equal(0, Analysis.Totals(m, "me").Wins);
        Assert.Equal(15, m.PlayableRounds);
    }

    [Fact]
    public void Riots_own_result_wins_over_counting_rounds()
    {
        var m = Match(13, 11);
        m.WinningTeam = "Blue";                  // e.g. a result Riot changed afterwards
        Assert.Equal(-1, m.Outcome("Red"));
        m.WinningTeam = "";                      // Riot says neither team won: a draw
        Assert.Equal(0, m.Outcome("Red"));
        m.WinningTeam = null;                    // matches saved before Pulse kept it: rounds decide
        Assert.Equal(1, m.Outcome("Red"));
    }

    [Fact]
    public void Reads_the_winning_team_from_match_details()
    {
        var json = """
        { "matchInfo": { "matchId": "m1", "mapId": "/Game/Maps/Ascent/Ascent", "queueID": "competitive", "gameStartMillis": 1700000000000 },
          "players": [ { "subject": "me", "teamId": "Red", "characterId": "x", "stats": { "score": 100, "roundsPlayed": 1, "kills": 1, "deaths": 0, "assists": 0 } } ],
          "teams": [ { "teamId": "Red", "won": false, "roundsWon": 12 }, { "teamId": "Blue", "won": true, "roundsWon": 13 } ],
          "roundResults": [], "kills": [] }
        """;
        using var doc = JsonDocument.Parse(json);
        var m = VMatch.Parse(doc.RootElement, "me", null, null);
        Assert.Equal("Blue", m.WinningTeam);

        using var draw = JsonDocument.Parse(json.Replace("\"won\": true", "\"won\": false"));
        Assert.Equal("", VMatch.Parse(draw.RootElement, "me", null, null).WinningTeam);
    }

    [Fact]
    public void Acs_adr_and_kast_are_per_round_played_and_skip_rounds_the_player_missed()
    {
        // 20 rounds; "me" left after 10 (Riot: roundsPlayed 10, and no player stats for "me" in rounds 10–19).
        var m = Match(10, 10);
        var me = m.Players[0];
        me.Rounds = 10;
        for (int r = 0; r < 20; r++)
        {
            m.Rounds[r].Eco["foe"] = new VEco { Loadout = 3900 };
            if (r < 10)
            {
                m.Rounds[r].Eco["me"] = new VEco { Loadout = 3900 };
                m.Rounds[r].Damage.Add(new VDamage { From = "me", To = "foe", Amount = 150, Head = 1, Body = 2 });
            }
        }
        var t = Analysis.Totals(m, "me");
        Assert.Equal(10, t.Rounds);
        Assert.Equal(400, t.Acs, 3);           // 4000 score / 10 rounds played, not / 20
        Assert.Equal(150, t.Adr, 3);
        Assert.Equal(100, t.KastPct, 3);       // survived the 10 rounds played; the 10 missed don't count as "survived"
        Assert.Equal(10, m.PlayedRounds("me"));
        Assert.Equal(20, m.PlayedRounds("foe"));
    }

    [Fact]
    public void The_surrender_round_gives_no_free_kast()
    {
        var m = Match(5, 4);
        m.Players[0].Rounds = 9;
        m.Kills.AddRange(Enumerable.Range(0, 9).Select(r => new VKill { Round = r, GameMs = r * 100000 + 5000, Killer = "foe", Victim = "me" }));
        m.Rounds.Add(new VRound { N = 9, Winner = "Red", Result = "Surrendered" });
        var t = Analysis.Totals(m, "me");
        Assert.Equal(9, t.Rounds);
        Assert.Equal(0, t.KastPct, 3);         // died untraded every round played; the surrender round isn't "survived"
        Assert.Equal(1, t.Wins);
    }
}
