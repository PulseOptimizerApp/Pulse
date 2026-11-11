using System.Text.Json;
using Pulse.Core.Games.Live;
using Pulse.Core.Games.Valorant;
using Pulse.Core.Reports;
using Xunit;

namespace PulseOptimizer.Tests;

/// <summary>The VALORANT coach: reading a match, trades, first deaths, positions, sides and the coach's verdicts.</summary>
public class CoachTests
{
    const string Me = "me", Mate = "mate", Mate2 = "mate2", E1 = "e1", E2 = "e2";

    static string Pos(string id, double x, double y) => "{\"subject\":\"" + id + "\",\"viewRadians\":0,\"location\":{\"x\":" + x + ",\"y\":" + y + "}}";

    static string Kill(int round, long game, long roundMs, string killer, string victim, double vx, double vy, params string[] pos) =>
        "{\"round\":" + round + ",\"gameTime\":" + game + ",\"roundTime\":" + roundMs + ",\"killer\":\"" + killer + "\",\"victim\":\"" + victim + "\",\"victimLocation\":{\"x\":" + vx + ",\"y\":" + vy + "},\"assistants\":[],\"playerLocations\":[" + string.Join(",", pos) + "],\"finishingDamage\":{\"damageType\":\"Weapon\",\"damageItem\":\"9C82E19D-4575-0200-1A81-3EACF00CF872\"}}";

    /// <summary>
    /// Three rounds. Round 0: you die first, alone (mate 30 m away), to e1 with e2 close by; nobody trades.
    /// Round 1: you die, mate trades e1 2 s later. Round 2 (you attack second half? no: same half): you get the first kill.
    /// </summary>
    static JsonElement Match(string queue = "competitive")
    {
        var kills = new[]
        {
            Kill(0, 40000, 20000, E1, Me, 0, 0, Pos(Mate, 3000, 0), Pos(E1, 800, 0), Pos(E2, 0, 900), Pos(Mate2, 5000, 5000)),
            Kill(1, 140000, 18000, E1, Me, 100, 100, Pos(Mate, 300, 100), Pos(E1, 900, 100), Pos(Mate2, 5000, 5000), Pos(E2, 7000, 0)),
            Kill(1, 142000, 20000, Mate, E1, 900, 100, Pos(Mate, 300, 100), Pos(E1, 900, 100), Pos(Mate2, 5000, 5000), Pos(E2, 7000, 0)),
            Kill(2, 240000, 15000, Me, E2, 1000, 0, Pos(Me, 0, 0), Pos(Mate, 200, 0), Pos(E1, 2000, 0), Pos(E2, 1000, 0), Pos(Mate2, 5000, 5000)),
        };
        string P(string id, string team, string agent, int k = 1, int d = 1) =>
            "{\"subject\":\"" + id + "\",\"teamId\":\"" + team + "\",\"characterId\":\"\",\"competitiveTier\":12,\"stats\":{\"kills\":" + k + ",\"deaths\":" + d + ",\"assists\":0,\"score\":100,\"roundsPlayed\":3,\"abilityCasts\":{\"grenadeCasts\":3,\"ability1Casts\":2,\"ability2Casts\":1,\"ultimateCasts\":0}}}";
        string Round(int n, string winner, string planter = null) =>
            "{\"roundNum\":" + n + ",\"winningTeam\":\"" + winner + "\",\"roundResultCode\":\"Elimination\",\"bombPlanter\":\"" + (planter ?? "") + "\",\"plantRoundTime\":" + (planter != null ? 50000 : 0) + ",\"playerStats\":[{\"subject\":\"" + Me + "\",\"damage\":[{\"receiver\":\"" + E2 + "\",\"damage\":150,\"headshots\":1,\"bodyshots\":2,\"legshots\":0}],\"economy\":{\"loadoutValue\":3900,\"spent\":3900,\"remaining\":200}}]}";
        var json = $$"""
        {
          "matchInfo": {"matchId":"m1","mapId":"/Game/Maps/Ascent/Ascent","queueID":"{{queue}}","gameStartMillis":1762560000000,"gameLengthMillis":1800000},
          "players": [{{P(Me, "Red", "Jett", 1, 2)}},{{P(Mate, "Red", "Sova")}},{{P(Mate2, "Red", "Omen")}},{{P(E1, "Blue", "Reyna")}},{{P(E2, "Blue", "Sage")}}],
          "teams": [{"teamId":"Red","won":true,"roundsWon":2},{"teamId":"Blue","won":false,"roundsWon":1}],
          "roundResults": [{{Round(0, "Blue")}},{{Round(1, "Red", Mate)}},{{Round(2, "Red")}}],
          "kills": [{{string.Join(",", kills)}}]
        }
        """;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    static VMatch Parsed(string queue = "competitive") => VMatch.Parse(Match(queue), Me, null, null);

    [Fact]
    public void A_match_is_read_with_positions_rounds_and_casts()
    {
        var m = Parsed();
        Assert.NotNull(m);
        Assert.Equal("Ascent", m.Map);
        Assert.Equal(5, m.Players.Count);
        Assert.Equal(3, m.Rounds.Count);
        Assert.Equal(4, m.Kills.Count);
        Assert.Equal(4, m.Kills[0].Pos.Count);
        Assert.Equal(new[] { 3, 2, 1, 0 }, m.MeP.Casts);
        Assert.True(m.Won);
        Assert.Equal(50000, m.Rounds[1].PlantMs);
        Assert.Null(m.Rounds[0].UsedAbility); // Riot sent no per-round ability data
    }

    [Fact]
    public void Deaths_are_classified_from_where_everyone_stood()
    {
        var d = Analysis.Deaths(Parsed(), Me);
        Assert.Equal(2, d.Count);
        // Round 0: first death, alone (mate 30 m away), two enemies within 20 m, not traded.
        Assert.Contains(DeathTag.FirstDeath, d[0].Tags);
        Assert.Contains(DeathTag.Alone, d[0].Tags);
        Assert.Contains(DeathTag.Outnumbered, d[0].Tags);
        Assert.Contains(DeathTag.NotTraded, d[0].Tags);
        Assert.Equal(DeathTag.FirstDeath, d[0].Main);
        Assert.Equal(30, d[0].NearestMateM, 1);
        // Round 1: traded by the mate 2 s later, so it isn't filed as a first death.
        Assert.Contains(DeathTag.Traded, d[1].Tags);
        Assert.DoesNotContain(DeathTag.Alone, d[1].Tags);
        Assert.Equal(DeathTag.Traded, d[1].Main);
    }

    [Fact]
    public void Overview_counts_like_the_trackers()
    {
        var m = Parsed();
        var o = Stats.Of(new[] { m }, Me);
        Assert.Equal(1, o.Matches);
        Assert.Equal(1, o.Wins);                 // Red won 2–1
        Assert.Equal(3, o.Rounds);
        Assert.Equal(1, o.Kills);                // Riot's scoreboard
        Assert.Equal(2, o.Deaths);
        Assert.Equal(1, o.FirstBloods);
        Assert.Equal(1, o.Flawless);             // round 2: won, nobody on the team died
        Assert.Equal(0, o.Plants);
        Assert.Equal(450.0 / 3, o.DamagePerRound, 3);
        Assert.Equal(0.5, o.Kd, 3);
        Assert.Equal(100, o.HeadshotPct * 3, 3); // 3 head of 9 hits
        Assert.Equal(1, Stats.Of(new[] { m }, Mate).Plants);
    }

    [Fact]
    public void Match_pages_count_duels_economy_and_rounds()
    {
        var m = Parsed();
        var duels = Stats.Duels(m);
        Assert.Equal(2, duels[E1][Me]);
        Assert.Equal(1, duels[Me][E2]);
        Assert.Equal(1, duels[Mate][E1]);
        var eco = Stats.Economy(m, "Red");
        Assert.Equal(3, eco.Count);
        Assert.Equal(Stats.Buy.FullBuy, eco[0].TeamBuy);
        Assert.Equal(3900, eco[0].TeamLoadout);
        var feed = Stats.Feed(m);
        Assert.Equal(2, feed[1].Kills.Count);
        Assert.Equal(Me, feed[1].Kills[0].Victim.Id);
    }

    [Fact]
    public void Aces_multi_kills_and_placements()
    {
        var m = new VMatch { Id = "a", Queue = "competitive", Me = "me" };
        m.Players.Add(new VPlayer { Id = "me", Team = "Red", Score = 400 });
        for (int i = 0; i < 4; i++) m.Players.Add(new VPlayer { Id = "m" + i, Team = "Red", Score = 100 + i });
        for (int i = 0; i < 5; i++) m.Players.Add(new VPlayer { Id = "e" + i, Team = "Blue", Score = 200 + i });
        m.Rounds.Add(new VRound { N = 0, Winner = "Red" });
        m.Rounds.Add(new VRound { N = 1, Winner = "Blue" });
        for (int i = 0; i < 5; i++) m.Kills.Add(new VKill { Round = 0, GameMs = i, Killer = "me", Victim = "e" + i });
        m.Kills.Add(new VKill { Round = 1, GameMs = 100, Killer = "me", Victim = "e0" });
        m.Kills.Add(new VKill { Round = 1, GameMs = 101, Killer = "me", Victim = "e1" });
        m.Kills.Add(new VKill { Round = 1, GameMs = 102, Killer = "e2", Victim = "me" });
        var o = Stats.Of(new[] { m }, "me");
        Assert.Equal(1, o.Aces);
        Assert.Equal(1, o.TwoK);
        Assert.Equal(2, o.MultiKills);
        Assert.Equal(1, o.Flawless);             // round 0: an ace and nobody lost
        var place = Stats.PlaceOf(m, "me");
        Assert.True(place.MatchMvp);
        Assert.Equal(1, place.Place);
        var top = Stats.PlaceOf(m, "e4");        // best of the other team, second overall
        Assert.True(top.TeamMvp);
        Assert.Equal(2, top.Place);
    }

    [Fact]
    public void Filters_pick_a_playlist_and_an_act()
    {
        var a = new VMatch { Id = "1", Queue = "competitive", StartUtc = new DateTime(2026, 10, 1) };
        var b = new VMatch { Id = "2", Queue = "unrated", StartUtc = new DateTime(2026, 10, 20) };
        var c = new VMatch { Id = "3", Queue = "deathmatch", StartUtc = new DateTime(2026, 10, 21) };
        var all = new[] { a, b, c };
        Assert.Equal(2, new TrackerFilter().Apply(all).Count());
        Assert.Equal(new[] { "1" }, new TrackerFilter("competitive").Apply(all).Select(m => m.Id));
        Assert.Equal(new[] { "2" }, new TrackerFilter("all", new DateTime(2026, 10, 15)).Apply(all).Select(m => m.Id));
        Assert.Equal(new[] { "1" }, new TrackerFilter("all", null, new DateTime(2026, 10, 15)).Apply(all).Select(m => m.Id));
    }

    [Fact]
    public void Riot_ids_are_kept_with_the_match()
    {
        var json = Match().GetRawText().Replace("\"subject\":\"" + Me + "\",", "\"subject\":\"" + Me + "\",\"gameName\":\"Pro\",\"tagLine\":\"NA1\",\"accountLevel\":42,\"partyId\":\"p1\",");
        var m = VMatch.Parse(JsonDocument.Parse(json).RootElement, Me, null, null);
        Assert.Equal("Pro#NA1", m.Players.First(p => p.Id == Me).Name);
        Assert.Equal(42, m.Players.First(p => p.Id == Me).Level);
        Assert.Equal("Pro#NA1", Stats.NameOf(m, Me));
        Assert.Equal(VMatch.CurrentVersion, m.Version);
    }

    [Fact]
    public void Totals_count_kast_first_kills_trades_and_damage()
    {
        var m = Parsed();
        var me = Analysis.Totals(m, Me);
        Assert.Equal(3, me.Rounds);
        Assert.Equal(1, me.Kills);
        Assert.Equal(2, me.Deaths);
        Assert.Equal(1, me.FirstKills);
        Assert.Equal(2, me.FirstDeaths);
        Assert.Equal(2, me.KastRounds);      // round 1 (traded) and round 2 (kill + survived)
        Assert.Equal(450, me.Damage);
        Assert.Equal(1, me.Untraded);
        Assert.Equal(2, me.DeathsWithMates);
        var mate = Analysis.Totals(m, Mate);
        Assert.Equal(1, mate.TradeKills);    // killed e1 2 s after e1 killed you
    }

    [Fact]
    public void Sides_follow_the_halves_and_overtime()
    {
        var m = new VMatch { Queue = "competitive", FirstAttack = "Red" };
        Assert.Equal("Red", m.Attackers(0));
        Assert.Equal("Red", m.Attackers(11));
        Assert.Equal("Blue", m.Attackers(12));
        Assert.Equal("Blue", m.Attackers(23));
        Assert.Equal("Red", m.Attackers(24));
        Assert.Equal("Blue", m.Attackers(25));
        var sw = new VMatch { Queue = "swiftplay", FirstAttack = "Red" };
        Assert.Equal("Blue", sw.Attackers(4));
        // The plant in round 1 was by a Red player: Red attacks first, as guessed.
        Assert.Equal("Red", Parsed().FirstAttack);
    }

    [Fact]
    public void Round_clock_takes_off_the_buy_phase_only_when_it_is_in_the_data()
    {
        var m = Parsed();
        Assert.False(Analysis.IncludesBuyPhase(m)); // a kill 15 s in: counted from the barrier drop
        Assert.Equal(15000, Analysis.RoundClockMs(m, m.Kills[3]));
        var slow = Parsed();
        foreach (var k in slow.Kills) k.RoundMs += 40000;
        Assert.True(Analysis.IncludesBuyPhase(slow));
        Assert.Equal(55000 - 30000, Analysis.RoundClockMs(slow, slow.Kills[3]));
    }

    [Fact]
    public void The_coach_finds_weaknesses_against_the_lobby_and_judges_a_focus()
    {
        var matches = Enumerable.Range(0, 30).Select(i => { var m = Parsed(); m.Id = "m" + i; return m; }).ToList();
        var r = Coach.Analyze(matches);
        Assert.Equal(90, r.Rounds);
        Assert.NotEmpty(r.All);
        Assert.All(r.Weaknesses, w => Assert.True(w.Gap <= -0.10));
        Assert.True(r.DeathsCounted == 60);

        var f = new CoachFocus { Metric = "untraded", Baseline = 50 };
        Assert.Equal(Coach.Verdict.TooEarly, Coach.Judge(f, 20, 3));
        Assert.Equal(Coach.Verdict.Better, Coach.Judge(f, 40, 6));   // fewer untraded deaths is better
        Assert.Equal(Coach.Verdict.Same, Coach.Judge(f, 48, 6));
        Assert.Equal(Coach.Verdict.Worse, Coach.Judge(f, 60, 6));
        var kast = new CoachFocus { Metric = "kast", Baseline = 70 };
        Assert.Equal(Coach.Verdict.Better, Coach.Judge(kast, 78, 8));
    }

    [Fact]
    public void Every_coach_tip_is_complete()
    {
        foreach (var m in Coach.Metrics)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Name));
            Assert.False(string.IsNullOrWhiteSpace(m.Tip.Why));
            Assert.True(m.Tip.Steps.Length >= 2, m.Id);
            Assert.False(string.IsNullOrWhiteSpace(m.Tip.Drill));
        }
        Assert.Equal(Coach.Metrics.Length, Coach.Metrics.Select(m => m.Id).Distinct().Count());
    }

    [Fact]
    public void Minimap_places_points_with_the_games_own_numbers()
    {
        MapGeo.Reset();
        var meta = JsonDocument.Parse("""{"maps":{"ascent":{"m":[0.00007,-0.00007,0.813895,0.573242],"c":[["Market","A",4500,-6000],["Main","B",-3000,2000]]}},"weapons":{"9c82e19d-4575-0200-1a81-3eacf00cf872":"Vandal"}}""").RootElement;
        MapGeo.LoadFrom(meta);
        var p = MapGeo.ToMinimap("Ascent", 1000, 2000)!.Value;
        Assert.Equal(2000 * 0.00007 + 0.813895, p.X, 6);   // across uses the game's y
        Assert.Equal(1000 * -0.00007 + 0.573242, p.Y, 6);  // down uses the game's x
        Assert.Equal("A Market", MapGeo.CalloutAt("Ascent", 4400, -5900));
        Assert.Equal("Vandal", MapGeo.Weapon("9C82E19D-4575-0200-1A81-3EACF00CF872"));
        MapGeo.Reset();
    }

    [Fact]
    public void Lineups_sit_on_the_map_by_callout_or_by_the_spot_you_placed()
    {
        MapGeo.Reset();
        var meta = JsonDocument.Parse("""{"maps":{"ascent":{"m":[0.00007,-0.00007,0.813895,0.573242],"c":[["Main","B",-3000,2000],["Site","B",-5000,-1000],["Site","A",4500,-6000]]}}}""").RootElement;
        MapGeo.LoadFrom(meta);
        var byCallout = new Lineup { Map = "Ascent", Stand = "B Main", Land = "B Site" };
        var (s, d) = Lineups.Spots(byCallout);
        Assert.Equal(MapGeo.ToMinimap("Ascent", -3000, 2000), s);
        Assert.Equal(MapGeo.ToMinimap("Ascent", -5000, -1000), d);
        var placed = new Lineup { Map = "Ascent", Stand = "B Main", StandX = 0.2, StandY = 0.3, LandX = 0.6, LandY = 0.7 };
        Assert.Equal(new System.Windows.Point(0.2, 0.3), Lineups.Spots(placed).Stand);
        Assert.Equal(new System.Windows.Point(0.6, 0.7), Lineups.Spots(placed).Land);
        Assert.Null(Lineups.Spots(new Lineup { Map = "Ascent", Stand = "Nowhere" }).Stand);
        Assert.Equal(new[] { "B", "A" }, MapGeo.Sites("Ascent").Select(x => x.Letter));
        Assert.Equal("B Main", MapGeo.CalloutNear("Ascent", MapGeo.ToMinimap("Ascent", -2900, 2100)!.Value));
        MapGeo.Reset();
    }

    [Fact]
    public void Every_library_lineup_says_where_you_stand_and_where_it_lands()
    {
        Assert.NotEmpty(Lineups.Library);
        Assert.All(Lineups.Library, l =>
        {
            Assert.False(string.IsNullOrWhiteSpace(l.Stand), l.Title);
            Assert.False(string.IsNullOrWhiteSpace(l.Land), l.Title);
        });
    }

    [Fact]
    public void The_skin_list_skips_free_skins_and_knows_every_level()
    {
        var doc = JsonDocument.Parse("""
        {"data":[
          {"uuid":"s1","displayName":"Reaver Vandal","contentTierUuid":"t","displayIcon":null,"levels":[{"uuid":"l1","displayIcon":"https://media.valorant-api.com/x.png"},{"uuid":"l2"}]},
          {"uuid":"s2","displayName":"Standard Vandal","contentTierUuid":null,"levels":[{"uuid":"l3"}]},
          {"uuid":"s3","displayName":"Random Favorite Skin","contentTierUuid":"t","levels":[]},
          {"uuid":"s4","displayName":"Prime Phantom","contentTierUuid":"t","displayIcon":"https://media.valorant-api.com/p.png","levels":[{"uuid":"l4"}]}
        ]}
        """).RootElement;
        var skins = ValorantStore.ParseSkins(doc);
        Assert.Equal(new[] { "Prime Phantom", "Reaver Vandal" }, skins.Select(x => x.Name));
        var reaver = skins.Single(x => x.Name == "Reaver Vandal");
        Assert.Equal(new[] { "s1", "l1", "l2" }, reaver.Ids);
        Assert.Equal("https://media.valorant-api.com/x.png", reaver.Icon);
    }

    [Fact]
    public void Wishlist_skins_are_found_in_the_daily_store_and_night_market()
    {
        var sf = new Storefront(
            new List<StoreItem> { new("l1", "Reaver Vandal", 1775, 1775, 0, null, "Premium"), new("x", "Other", 875, 875, 0, null, "Select") }, TimeSpan.Zero,
            new List<StoreItem> { new("L4", "Prime Phantom", 900, 1775, 49, null, "Premium") }, TimeSpan.Zero,
            null, TimeSpan.Zero, 0, 0, 0);
        var wish = new List<WishSkin>
        {
            new() { Id = "s1", Name = "Reaver Vandal", Ids = { "s1", "l1", "l2" } },
            new() { Id = "s4", Name = "Prime Phantom", Ids = { "s4", "l4" } },
            new() { Id = "s9", Name = "Not There", Ids = { "s9", "l9" } },
        };
        var found = ValorantStore.WishMatches(sf, wish);
        Assert.Equal(2, found.Count);
        Assert.Contains(found, f => f.Skin.Name == "Reaver Vandal" && f.Where == "daily");
        Assert.Contains(found, f => f.Skin.Name == "Prime Phantom" && f.Where == "night" && f.Item.DiscountPercent == 49);
    }

    [Fact]
    public void The_store_day_starts_at_midnight_UTC()
    {
        Assert.Equal(new DateTime(2026, 11, 10), Pulse.Ui.StoreWatch.StoreDay(new DateTime(2026, 11, 10, 23, 59, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 11, 11), Pulse.Ui.StoreWatch.StoreDay(new DateTime(2026, 11, 11, 0, 1, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void The_store_is_read_with_prices_discounts_and_tiers()
    {
        var front = JsonDocument.Parse("""
        {"SkinsPanelLayout":{"SingleItemStoreOffers":[{"OfferID":"o1","Cost":{"85ad13f7-3d1b-5128-9eb2-7cd8ee0b5741":1775},"Rewards":[{"ItemID":"s1"}]},{"OfferID":"o2","Cost":{"85ad13f7-3d1b-5128-9eb2-7cd8ee0b5741":875},"Rewards":[{"ItemID":"s2"}]}],"SingleItemOffersRemainingDurationInSeconds":3600},
         "BonusStore":{"BonusStoreOffers":[{"Offer":{"OfferID":"n1","Cost":{"85ad13f7-3d1b-5128-9eb2-7cd8ee0b5741":2175},"Rewards":[{"ItemID":"s3"}]},"DiscountPercent":30,"DiscountCosts":{"85ad13f7-3d1b-5128-9eb2-7cd8ee0b5741":1523}}],"BonusStoreRemainingDurationInSeconds":86400},
         "FeaturedBundle":{"Bundles":[{"DataAssetID":"b1","TotalBaseCost":{"85ad13f7-3d1b-5128-9eb2-7cd8ee0b5741":9000},"TotalDiscountedCost":{"85ad13f7-3d1b-5128-9eb2-7cd8ee0b5741":7100},"DurationRemainingInSeconds":100000}]}}
        """).RootElement;
        var wallet = JsonDocument.Parse("""{"Balances":{"85ad13f7-3d1b-5128-9eb2-7cd8ee0b5741":1200,"e59aa87c-4cbf-517a-5983-6e81511be9b7":40}}""").RootElement;
        var s = ValorantStore.Parse(front, wallet);
        Assert.Equal(2, s.Daily.Count);
        Assert.Equal("s1", s.Daily[0].Id);
        Assert.Equal("Premium", s.Daily[0].Tier);
        Assert.Equal("Select", s.Daily[1].Tier);
        Assert.Equal(TimeSpan.FromHours(1), s.DailyLeft);
        Assert.Single(s.Night);
        Assert.Equal(1523, s.Night[0].Price);
        Assert.Equal(2175, s.Night[0].FullPrice);
        Assert.Equal(30, s.Night[0].DiscountPercent);
        Assert.Equal("Ultra", s.Night[0].Tier);
        Assert.Equal(7100, s.Bundle.Price);
        Assert.Equal(1200, s.Vp);
        Assert.Equal(40, s.Radianite);
    }
}

public class ReportTests
{
    [Theory]
    [InlineData("32.0.15.8157", "NVIDIA", "581.57")]
    [InlineData("31.0.15.5222", "NVIDIA", "552.22")]
    [InlineData("32.0.15.6094", "NVIDIA", "560.94")]
    [InlineData("31.0.24033.1003", "AMD", "31.0.24033.1003")]
    public void Driver_versions_read_like_the_maker_writes_them(string windows, string vendor, string friendly) =>
        Assert.Equal(friendly, PlayLog.FriendlyVersion(windows, vendor));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070", true)]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", true)]
    [InlineData("NVIDIA GeForce GTX 1080 Ti", false)]
    [InlineData("NVIDIA GeForce GTX 970", false)]
    public void Only_cards_still_on_Game_Ready_drivers_get_alerts(string card, bool current) =>
        Assert.Equal(current, DriverCheck.NvidiaCurrent(card));

    [Fact]
    public void NVIDIAs_lookup_answer_is_read()
    {
        var j = JsonDocument.Parse("""{"Success":"1","IDS":[{"downloadInfo":{"Version":"582.15","ReleaseDateTime":"Tue Nov 4, 2025","Name":"GeForce%20Game%20Ready%20Driver"}}]}""").RootElement;
        var l = DriverCheck.ParseNvidia(j);
        Assert.Equal("582.15", l.Version);
        Assert.Equal(new DateTime(2025, 11, 4), l.Date);
        Assert.Equal("GeForce Game Ready Driver", l.Name);
        var s = new DriverCheck.Status("NVIDIA", "RTX 4070", "581.57", default, l);
        Assert.True(s.UpdateAvailable);
        Assert.False((s with { Installed = "582.15" }).UpdateAvailable);
    }

    [Fact]
    public void A_sessions_frames_give_its_average_and_1_percent_low()
    {
        var t = new FrameTally();
        for (int i = 0; i < 9900; i++) t.Add(5);    // 200 FPS
        for (int i = 0; i < 100; i++) t.Add(20);    // the slowest 1%: 50 FPS
        var (frames, secs, avg, low) = t.Result();
        Assert.Equal(10000, frames);
        Assert.Equal(51.5, secs, 3);
        Assert.Equal(10000 / 51.5, avg, 1);
        Assert.Equal(1000.0 / 20.25, low, 1); // half-millisecond steps: 20 ms lands in the 20–20.5 step
    }

    [Fact]
    public void The_week_and_driver_changes_are_worked_out()
    {
        var monday = PlayLog.WeekStart(new DateTime(2026, 11, 4));
        Assert.Equal(new DateTime(2026, 11, 2), monday);
        PlaySession S(DateTime start, string driver, double fps, double low) => new()
        {
            Game = "VALORANT", Start = start, End = start.AddMinutes(40), Frames = (long)(fps * 2400), FrameSeconds = 2400, AvgFps = fps, Low1 = low, Driver = driver, Windows = "W",
        };
        var all = new List<PlaySession>
        {
            S(monday.AddDays(-6), "NVIDIA 581.57", 300, 200), S(monday.AddDays(-5), "NVIDIA 581.57", 300, 200),
            S(monday.AddDays(1), "NVIDIA 582.15", 250, 150), S(monday.AddDays(2), "NVIDIA 582.15", 250, 150),
        };
        var week = PlayLog.Week(all, monday);
        Assert.Single(week);
        Assert.Equal(250, week[0].AvgFps, 1);
        Assert.Equal(300, week[0].PrevAvgFps, 1);
        var ch = PlayLog.Changes(all);
        Assert.Single(ch);
        Assert.Equal("driver", ch[0].What);
        Assert.True(ch[0].Worse);
        Assert.Equal("NVIDIA 581.57", ch[0].From);
    }
}

public class TrackerTests
{
    [Theory]
    [InlineData("TenZ#0505", "TenZ", "0505")]
    [InlineData("  Some Name #NA1 ", "Some Name", "NA1")]
    [InlineData("ab#NA1", null, null)]
    [InlineData("NoTag", null, null)]
    [InlineData("Name#", null, null)]
    public void Riot_ids_are_read(string text, string name, string tag)
    {
        var r = Pulse.Core.Games.Live.ValorantSearch.ParseRiotId(text);
        if (name == null) Assert.Null(r);
        else { Assert.Equal(name, r!.Value.Name); Assert.Equal(tag, r.Value.Tag); }
    }

    [Fact]
    public void A_lookup_answer_must_be_the_player_asked_for()
    {
        Assert.Equal("p1", Pulse.Core.Games.Live.ValorantSearch.FirstPuuid("""[{"puuid":"p1","game_name":"TenZ","tag_line":"0505"}]""", "tenz", "0505"));
        Assert.Null(Pulse.Core.Games.Live.ValorantSearch.FirstPuuid("""[{"puuid":"p2","game_name":"Other","tag_line":"0505"}]""", "TenZ", "0505"));
        Assert.Equal("p3", Pulse.Core.Games.Live.ValorantSearch.FirstPuuid("""{"puuid":"p3"}""", "TenZ", "0505"));
        Assert.Null(Pulse.Core.Games.Live.ValorantSearch.FirstPuuid("not json", "TenZ", "0505"));
    }

    [Fact]
    public void Top_percent_counts_players_who_did_better()
    {
        var sorted = Enumerable.Range(1, 100).Select(i => (double)i).ToList();
        Assert.Equal(1, Tracker.TopPercent(sorted, 1000), 3);
        Assert.Equal(50, Tracker.TopPercent(sorted, 51), 3);
        Assert.Equal(100, Tracker.TopPercent(sorted, 0), 3);
        Assert.True(double.IsNaN(Tracker.TopPercent(sorted.Take(5).ToList(), 3)));
    }

    [Fact]
    public void Plays_like_follows_how_each_rank_actually_scores()
    {
        // Ranks 12 (Gold 1) to 21 (Ascendant 1), median rising 2 points per rank, with one noisy rank out of order.
        var byTier = new Dictionary<int, List<double>>();
        for (int t = 12; t <= 21; t++) byTier[t] = Enumerable.Repeat(10.0 + 2 * (t - 12), 10).ToList();
        byTier[16] = Enumerable.Repeat(12.0, 10).ToList(); // noisy
        Assert.Equal(12, Tracker.PlaysLike(byTier, 5));
        Assert.Equal(21, Tracker.PlaysLike(byTier, 99));
        Assert.Equal(20, Tracker.PlaysLike(byTier, 26));
        Assert.Equal(0, Tracker.PlaysLike(new Dictionary<int, List<double>> { [12] = new() { 1, 2 } }, 5));
    }

    [Fact]
    public void Smurf_chance_adds_up_signs_and_one_alone_is_never_likely()
    {
        var p = new Profile { Tier = 9 }; // Silver 1
        p.Totals.Matches = 6; p.Totals.Kills = 120; p.Totals.Deaths = 60; // K/D 2.0
        var one = Tracker.SmurfChance(p, Pulse.Core.Games.Live.Smurf.None);
        Assert.Single(one.Signs);
        Assert.Equal(25, one.Score);
        p.Totals.Rounds = 100; p.Totals.KastRounds = 82; // KAST 82%
        var two = Tracker.SmurfChance(p, Pulse.Core.Games.Live.Smurf.None);
        Assert.Equal(2, two.Signs.Count);
        Assert.Equal(45, two.Score);
        var clean = Tracker.SmurfChance(new Profile { Tier = 9 }, Pulse.Core.Games.Live.Smurf.None);
        Assert.Equal(0, clean.Score);
    }

    /// <summary>A Radiant player on a Diamond 2 account: 80% of 10 won, K/D 1.46, KAST 76%.</summary>
    static Profile RadiantOnDiamond()
    {
        var p = new Profile { Tier = 17, CompareTier = 17, ActWinPct = 80, ActGames = 10 };
        p.Totals.Matches = 10; p.Totals.Wins = 8; p.Totals.Kills = 204; p.Totals.Deaths = 140; p.Totals.Rounds = 222; p.Totals.KastRounds = 169;
        return p;
    }

    [Fact]
    public void A_radiant_smurfing_in_diamond_reads_as_a_likely_smurf()
    {
        var p = RadiantOnDiamond();
        var account = Pulse.Core.Games.Live.ValorantLobby.SmurfSigns(0, 17, 17, 80, 10, new[] { 25, 26, 24, -12, 25, 25 });
        var s = Tracker.SmurfChance(p, account);
        Assert.Equal("Likely smurf", s.Label);
        Assert.DoesNotContain(s.Signs, x => x.Contains("RR"));
    }

    [Fact]
    public void Winning_that_much_on_a_new_account_is_worth_a_look_but_not_proof()
    {
        var p = RadiantOnDiamond();
        var (cheat, _) = Tracker.CheatSignals(p);
        Assert.InRange(cheat.Score, 50, 60);         // win rate and K/D
        p.Level = 45;
        (cheat, _) = Tracker.CheatSignals(p);
        Assert.InRange(cheat.Score, 60, 70);         // plus a fairly new account
        var normal = new Profile { Tier = 17, CompareTier = 17, ActWinPct = 52, ActGames = 40 };
        normal.Totals.Matches = 10; normal.Totals.Wins = 5; normal.Totals.Kills = 160; normal.Totals.Deaths = 150; normal.Totals.Rounds = 220;
        Assert.Equal(0, Tracker.CheatSignals(normal).Signals.Score);
    }

    static VMatch Lobby(int myTier, int othersTier, bool won, int i)
    {
        var m = new VMatch { Id = "x" + i, Queue = "competitive", Me = "me", StartUtc = new DateTime(2026, 11, 1).AddHours(i) };
        m.Players.Add(new VPlayer { Id = "me", Team = "Red", Tier = myTier, Agent = "Jett" });
        for (int k = 0; k < 9; k++) m.Players.Add(new VPlayer { Id = "p" + k, Team = k < 4 ? "Red" : "Blue", Tier = othersTier, Agent = "Sova" });
        for (int r = 0; r < 13; r++) m.Rounds.Add(new VRound { N = r, Winner = (won ? r < 13 : r >= 13) ? "Red" : "Blue" });
        if (!won) for (int r = 0; r < 13; r++) m.Rounds[r].Winner = "Blue";
        return m;
    }

    [Fact]
    public void Mmr_estimate_needs_lobbies_and_rr_to_agree()
    {
        var high = Enumerable.Range(0, 6).Select(i => Lobby(9, 13, true, i)).ToList(); // Silver 1 in Gold 2 lobbies
        var e = Tracker.Mmr(high, new[] { 26, 27, -12, 25, -14 });
        Assert.Equal(Tracker.MmrSide.Above, e.Side);
        Assert.Equal(13, e.EstimateTier);
        Assert.Equal(Tracker.MmrSide.Even, Tracker.Mmr(high, new[] { 12, 13, -25, -26 }).Side);   // they disagree
        var even = Enumerable.Range(0, 6).Select(i => Lobby(12, 12, i % 2 == 0, i)).ToList();
        Assert.Equal(Tracker.MmrSide.Even, Tracker.Mmr(even, new[] { 18, -18, 19, -17 }).Side);
        Assert.Equal(Tracker.MmrSide.Below, Tracker.Mmr(Enumerable.Range(0, 6).Select(i => Lobby(15, 11, false, i)).ToList(), new[] { 12, -26, -25 }).Side);
        Assert.Equal(Tracker.MmrSide.Unknown, Tracker.Mmr(new List<VMatch>(), null).Side);
    }

    [Fact]
    public void Opposition_averages_enemy_and_teammate_ranks()
    {
        var ms = Enumerable.Range(0, 5).Select(i => Lobby(12, 15, true, i)).ToList();
        var o = Tracker.Against(ms);
        Assert.Equal(5, o.Matches);
        Assert.Equal(15, o.EnemyAvg, 3);
        Assert.Equal(12, o.MyAvg, 3);
        Assert.Equal(15, o.Toughest.Tier);
        Assert.NotEmpty(o.Repeats);
    }

    [Theory]
    [InlineData("EPISODE 9", "Episode 9")]
    [InlineData("ACT II", "Act 2")]
    [InlineData("V25 ACT III", "V25 Act 3")]
    public void Act_names_read_naturally(string riot, string shown) =>
        Assert.Equal(shown, Pulse.Core.Games.Live.ValorantLobby.Friendly(riot));
}

public class SpeedTestTests
{
    [Fact]
    public void Median_and_jitter()
    {
        Assert.Equal(20, Pulse.Core.SpeedTest.Median(new() { 30, 10, 20 }));
        Assert.Equal(15, Pulse.Core.SpeedTest.Median(new() { 10, 20, 30, 5 }));
        Assert.Equal(0, Pulse.Core.SpeedTest.Jitter(new() { 12 }));
        Assert.Equal(5, Pulse.Core.SpeedTest.Jitter(new() { 10, 15, 10, 15 }), 3);
    }

    [Fact]
    public void Bufferbloat_grade_uses_the_worse_direction()
    {
        var r = new Pulse.Core.SpeedResult { IdleMs = 20, LoadedDownMs = 24, LoadedUpMs = 25 };
        Assert.Equal("A", r.BloatGrade);
        r.LoadedUpMs = 21; r.LoadedDownMs = 22;
        Assert.Equal("A+", r.BloatGrade);
        r.LoadedUpMs = 150;
        Assert.Equal("C", r.BloatGrade);
        r.LoadedDownMs = 600;
        Assert.Equal("F", r.BloatGrade);
    }

    [Fact]
    public void Bad_results_come_with_fixes_and_wifi_gets_the_cable_tip_first()
    {
        var r = new Pulse.Core.SpeedResult { DownMbps = 8, UpMbps = 1, IdleMs = 80, JitterMs = 25, LossPercent = 4, LoadedDownMs = 500, LoadedUpMs = 300, Wifi = true };
        var d = Pulse.Core.SpeedTest.Diagnose(r);
        Assert.All(d, f => Assert.Equal(Pulse.Core.SpeedTest.Level.Bad, f.Level));
        Assert.All(d, f => Assert.NotEmpty(f.Fixes));
        Assert.StartsWith("Plug your PC", d.Single(f => f.Area == "Ping").Fixes[0]);
        Assert.Equal(Pulse.Core.SpeedTest.Level.Bad, Pulse.Core.SpeedTest.Verdict(r).Level);
        var good = new Pulse.Core.SpeedResult { DownMbps = 500, UpMbps = 50, IdleMs = 10, JitterMs = 1, LossPercent = 0, LoadedDownMs = 12, LoadedUpMs = 13 };
        Assert.Equal(Pulse.Core.SpeedTest.Level.Great, Pulse.Core.SpeedTest.Verdict(good).Level);
    }
}

public class LobbyWeightedSkillTests
{
    [Fact]
    public void Stronger_lobbies_count_for_more()
    {
        Assert.Equal(1, Tracker.LobbyFactor(double.NaN));
        Assert.True(Tracker.LobbyFactor(27) > Tracker.LobbyFactor(20));
        Assert.True(Tracker.LobbyFactor(20) > Tracker.LobbyFactor(13));
        Assert.True(Tracker.LobbyFactor(13) > Tracker.LobbyFactor(9));
        Assert.InRange(Tracker.LobbyFactor(27), 1.5, 1.6);
        Assert.InRange(Tracker.LobbyFactor(3), 0.6, 0.7);
    }

    /// <summary>
    /// The case from a real pro's profile: average for his own Radiant lobbies, so on raw numbers he looked like an
    /// average player. Weighted by lobby, he has to come out well above a player who's average in Silver lobbies.
    /// </summary>
    [Fact]
    public void An_average_radiant_ranks_far_above_an_average_silver()
    {
        // Players at every rank, each scoring around 1.0 against their own lobby (that's what raw stats look like).
        var rnd = new Random(3);
        var byTier = new Dictionary<int, List<double>>();
        var all = new List<double>();
        for (int tier = 3; tier <= 27; tier++)
        {
            var l = new List<double>();
            for (int i = 0; i < 40; i++) { double raw = 0.7 + 0.6 * rnd.NextDouble(); double adj = raw * Tracker.LobbyFactor(tier); l.Add(adj); all.Add(adj); }
            l.Sort(); byTier[tier] = l;
        }
        all.Sort();
        double pro = 1.02 * Tracker.LobbyFactor(27), silver = 1.02 * Tracker.LobbyFactor(10);
        Assert.True(Tracker.PlaysLike(byTier, pro) >= 25, "an average Radiant should play like Immortal 3 / Radiant");
        Assert.InRange(Tracker.PlaysLike(byTier, silver), 8, 12);
        Assert.True(Tracker.TopPercent(all, pro) < 10, "an average Radiant is in the top 10% of everyone");
        Assert.True(Tracker.TopPercent(all, silver) > 50);
    }
}

public class AimRatingTests
{
    [Fact]
    public void Operator_players_arent_marked_down_for_body_shots()
    {
        PlayerTotals T(int head, int body, int sniper)
        {
            var t = new PlayerTotals { Rounds = 100, Kills = 80, Damage = 15000, Head = head, Body = body, FeedKills = 80, SniperKills = sniper };
            return t;
        }
        var rifler = T(25, 75, 0);           // 25% headshots, rifles
        var oper = T(12, 88, 50);            // 12% headshots, most kills with the Operator, same damage and kills
        var weak = T(12, 88, 0);             // 12% headshots with rifles
        Assert.InRange(rifler.AimRating, 48, 60);
        Assert.True(oper.AimRating > weak.AimRating + 5, "sniper kills shouldn't count against aim like rifle body shots");
        Assert.True(oper.AimRating > rifler.AimRating - 8);
    }
}
