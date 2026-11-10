using System.Text.Json;
using Pulse.Core.Games.Valorant;
using Xunit;

namespace PulseOptimizer.Tests;

/// <summary>Console VALORANT plays its own queues (console_competitive…); the tracker keeps them apart from PC.</summary>
public class ConsoleTests
{
    [Theory]
    [InlineData("console_competitive", true, "competitive", true)]
    [InlineData("console_unrated", true, "unrated", true)]
    [InlineData("console_deathmatch", true, "deathmatch", false)]
    [InlineData("competitive", false, "competitive", true)]
    [InlineData("deathmatch", false, "deathmatch", false)]
    public void Console_queues_read_like_pc_ones(string queue, bool console, string baseQueue, bool counts)
    {
        Assert.Equal(console, VMatch.IsConsole(queue));
        Assert.Equal(baseQueue, VMatch.BaseQueue(queue));
        Assert.Equal(counts, VMatch.Counts(queue));
    }

    [Fact]
    public void A_console_match_is_marked_and_keeps_its_queue_name()
    {
        var json = """
        { "matchInfo": { "matchId": "c1", "mapId": "/Game/Maps/Ascent/Ascent", "queueID": "console_competitive", "gameStartMillis": 1700000000000 },
          "players": [ { "subject": "me", "teamId": "Red", "characterId": "x", "playerCard": "9fb348bc-41a0-91ad-8a3e-818035c4e561", "stats": { "score": 100, "roundsPlayed": 1, "kills": 1, "deaths": 0, "assists": 0 } } ],
          "teams": [ { "teamId": "Red", "won": true, "roundsWon": 13 }, { "teamId": "Blue", "won": false, "roundsWon": 5 } ],
          "roundResults": [], "kills": [] }
        """;
        using var doc = JsonDocument.Parse(json);
        var m = VMatch.Parse(doc.RootElement, "me", null, null);
        Assert.True(m.Console);
        Assert.Equal("competitive", m.Queue);
        Assert.Equal("9fb348bc-41a0-91ad-8a3e-818035c4e561", m.Players[0].Card);
    }
}
