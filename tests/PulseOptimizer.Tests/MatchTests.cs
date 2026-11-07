using System.Text.Json;
using Pulse.Core.Games;
using Pulse.Core.Games.Live;
using Xunit;

namespace PulseOptimizer.Tests;

public class MatchTests
{
    const string Me = "aaaa-me";

    [Fact]
    public void Valorant_match_becomes_a_recap()
    {
        var json = $$"""
        {
          "matchInfo": { "mapId": "/Game/Maps/Infinity/Infinity", "queueID": "competitive", "gameStartMillis": 1760000000000, "gameLengthMillis": 1800000 },
          "players": [
            { "subject": "{{Me}}", "teamId": "Blue", "characterId": "569fdd95-4d10-43ab-ca70-79becc718b46", "competitiveTier": 14,
              "stats": { "kills": 9, "deaths": 10, "assists": 6, "score": 4100 } },
            { "subject": "someone", "teamId": "Red", "characterId": "add6443a-41bd-e414-f6ad-e58d267f4e95", "stats": { "kills": 20 } }
          ],
          "teams": [ { "teamId": "Blue", "won": true, "roundsWon": 13 }, { "teamId": "Red", "won": false, "roundsWon": 3 } ],
          "roundResults": [
            { "playerStats": [ { "subject": "{{Me}}", "kills": [ {}, {}, {} ], "damage": [ { "headshots": 3, "bodyshots": 14, "legshots": 3 } ] } ] },
            { "playerStats": [ { "subject": "{{Me}}", "kills": [ {} ], "damage": [] } ] },
            { "playerStats": [ { "subject": "{{Me}}", "kills": [ {}, {}, {}, {}, {} ], "damage": [] } ] }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var m = ValorantMatches.Parse(doc.RootElement, Me, null, null, "val-1");

        Assert.NotNull(m);
        Assert.Equal("VALORANT", m.Game);
        Assert.Equal("Sage", m.Character);
        Assert.Equal("Abyss", m.Map);
        Assert.Equal("Win", m.Result);
        Assert.Equal("13 : 3", m.Score);
        Assert.Equal((9, 10, 6), (m.Kills, m.Deaths, m.Assists));
        Assert.Equal(15, m.HeadshotPercent);
        Assert.Equal("Gold 3", m.Rank);
        Assert.Equal("Competitive", m.Mode);
        Assert.Equal(new[] { "3K", "ACE" }, m.Highlights);
        Assert.Equal(TimeSpan.FromMinutes(30), m.End - m.Start);
    }

    [Fact]
    public void Valorant_unrated_has_no_rank_and_riot_names_win()
    {
        var json = $$"""
        { "matchInfo": { "mapId": "/Game/Maps/Ascent/Ascent", "queueID": "unrated" },
          "players": [ { "subject": "{{Me}}", "teamId": "Red", "characterId": "x", "competitiveTier": 20, "stats": {} } ],
          "teams": [ { "teamId": "Red", "won": false, "roundsWon": 12 }, { "teamId": "Blue", "won": false, "roundsWon": 12 } ] }
        """;
        using var doc = JsonDocument.Parse(json);
        var maps = new Dictionary<string, string> { ["/Game/Maps/Ascent/Ascent"] = "Ascent (Riot)" };
        var agents = new Dictionary<string, string> { ["x"] = "NewAgent" };
        var m = ValorantMatches.Parse(doc.RootElement, Me, agents, maps, "val-2");

        Assert.Equal("", m.Rank);
        Assert.Equal("Draw", m.Result);
        Assert.Equal("Ascent (Riot)", m.Map);
        Assert.Equal("NewAgent", m.Character);
        Assert.Equal(-1, m.HeadshotPercent);
    }

    [Fact]
    public void Valorant_match_without_me_is_skipped()
    {
        using var doc = JsonDocument.Parse("""{ "players": [ { "subject": "other" } ] }""");
        Assert.Null(ValorantMatches.Parse(doc.RootElement, Me, null, null, "x"));
    }

    [Theory]
    [InlineData("/Game/Maps/Duality/Duality", "Bind")]
    [InlineData("/Game/Maps/Juliett/Juliett", "Sunset")]
    [InlineData("/Game/Maps/Brandnew/Brandnew", "Brandnew")]
    [InlineData("", "")]
    public void Valorant_map_names(string id, string name) => Assert.Equal(name, ValorantMatches.MapName(id));

    [Theory]
    [InlineData(0, "")]
    [InlineData(3, "Iron 1")]
    [InlineData(24, "Immortal 1")]
    [InlineData(27, "Radiant")]
    [InlineData(99, "")]
    public void Valorant_ranks(int tier, string name) => Assert.Equal(name, ValorantMatches.RankName(tier));

    [Theory]
    [InlineData("de_mirage", "Mirage")]
    [InlineData("cs_office", "Office")]
    [InlineData("de_ancient_night", "Ancient Night")]
    public void Cs_map_names(string raw, string name) => Assert.Equal(name, MatchEvents.PrettyCsMap(raw));

    [Fact]
    public void Dota_hero_names() => Assert.Equal("Shadow Shaman", MatchEvents.PrettyDotaHero("npc_dota_hero_shadow_shaman"));

    [Theory]
    [InlineData(3, "3K")]
    [InlineData(4, "4K")]
    [InlineData(5, "ACE")]
    public void Round_labels(int kills, string label) => Assert.Equal(label, MatchEvents.RoundLabel(kills));

    static JsonElement Cs(string phase, int round, int kills, int hs, string roundPhase = "live", int ct = 0, int t = 0, int matchKills = 0)
    {
        var json = $$"""
        { "provider": { "steamid": "1" },
          "map": { "name": "de_mirage", "mode": "competitive", "phase": "{{phase}}", "round": {{round}},
                   "team_ct": { "score": {{ct}} }, "team_t": { "score": {{t}} } },
          "round": { "phase": "{{roundPhase}}" },
          "player": { "steamid": "1", "team": "CT",
                      "state": { "round_kills": {{kills}}, "round_killhs": {{hs}} },
                      "match_stats": { "kills": {{matchKills}}, "deaths": 4, "assists": 2, "mvps": 1, "score": 30 } } }
        """;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void Cs2_clips_a_round_and_makes_a_recap()
    {
        var plays = new List<string>();
        MatchRecord done = null;
        void OnPlay(string g, string label, int s) { if (g == "Counter-Strike 2") plays.Add(label); }
        void OnDone(MatchRecord m) { if (m.Game == "Counter-Strike 2" && m.Map == "Mirage") done = m; }
        MatchEvents.Highlight += OnPlay;
        MatchEvents.Finished += OnDone;
        try
        {
            var cs = new Cs2Tracker();
            cs.Update(Cs("live", 0, 1, 1, matchKills: 1));
            cs.Update(Cs("live", 0, 3, 2, matchKills: 3));
            cs.Update(Cs("live", 0, 4, 2, matchKills: 4));
            cs.Update(Cs("live", 0, 4, 2, "over", matchKills: 4));
            cs.Update(Cs("live", 1, 1, 0, matchKills: 5));
            cs.Update(Cs("gameover", 1, 1, 0, "over", ct: 13, t: 7, matchKills: 5));

            Assert.Equal(new[] { "4K" }, plays);
            Assert.NotNull(done);
            Assert.Equal("Win", done.Result);
            Assert.Equal("13 : 7", done.Score);
            Assert.Equal((5, 4, 2), (done.Kills, done.Deaths, done.Assists));
            Assert.Equal(40, done.HeadshotPercent);
            Assert.Equal(new[] { "4K" }, done.Highlights);
        }
        finally
        {
            MatchEvents.Highlight -= OnPlay;
            MatchEvents.Finished -= OnDone;
        }
    }
}
