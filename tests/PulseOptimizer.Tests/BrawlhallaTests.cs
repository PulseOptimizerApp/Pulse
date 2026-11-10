using Pulse.Core.Games.Brawlhalla;
using Xunit;

namespace PulseOptimizer.Tests;

public class BrawlhallaTests
{
    [Theory]
    [InlineData(0, "")]
    [InlineData(500, "Tin 0")]
    [InlineData(720, "Tin 1")]
    [InlineData(909, "Tin 5")]
    [InlineData(910, "Bronze 1")]
    [InlineData(1129, "Bronze 5")]
    [InlineData(1130, "Silver 1")]
    [InlineData(1390, "Gold 1")]
    [InlineData(1679, "Gold 5")]
    [InlineData(1680, "Platinum 1")]
    [InlineData(1999, "Platinum 5")]
    [InlineData(2000, "Diamond")]
    [InlineData(2800, "Diamond")]
    public void Ratings_map_to_the_games_tiers(int rating, string tier) => Assert.Equal(tier, Brawlhalla.TierOf(rating));

    static BrawlhallaMatches.Snap Snap(int games, int wins, params BrawlhallaMatches.SnapLegend[] legends) =>
        new() { Time = DateTime.Now, Games = games, Wins = wins, Legends = legends.ToList() };

    [Fact]
    public void A_ranked_win_becomes_one_match_with_its_rating_change()
    {
        var before = Snap(100, 50, new() { Id = 3, Games = 60, Wins = 30, Kos = 100, Damage = 50000, Time = 9000, RankedGames = 10, Rating = 1200 }, new() { Id = 4, Games = 40, Wins = 20 });
        before.One = new() { Games = 10, Wins = 5, Rating = 1200 };
        var after = Snap(101, 51, new() { Id = 3, Games = 61, Wins = 31, Kos = 104, Falls = 2, Damage = 51200, Time = 9180, RankedGames = 11, Rating = 1225 }, new() { Id = 4, Games = 40, Wins = 20 });
        after.One = new() { Games = 11, Wins = 6, Rating = 1225 };
        var m = Assert.Single(BrawlhallaMatches.Diff(before, after));
        Assert.Equal(3, m.LegendId);
        Assert.Equal((1, 1, 4, 2, 1200L, 180L), (m.Games, m.Wins, m.Kos, m.Falls, m.Damage, m.Seconds));
        Assert.Equal("1v1", m.Mode);
        Assert.Equal(25, m.RatingChange);
    }

    [Fact]
    public void Two_legends_played_are_two_entries_and_a_2v2_team_goes_to_one()
    {
        var before = Snap(10, 5, new() { Id = 1, Games = 5, Wins = 2 }, new() { Id = 2, Games = 5, Wins = 3 });
        before.Teams = new() { new() { Partner = 9, Name = "Mate", Games = 4, Wins = 2, Rating = 1000 } };
        var after = Snap(13, 6, new() { Id = 1, Games = 7, Wins = 2 }, new() { Id = 2, Games = 6, Wins = 4 });
        after.Teams = new() { new() { Partner = 9, Name = "Mate", Games = 5, Wins = 3, Rating = 1020 } };
        var list = BrawlhallaMatches.Diff(before, after);
        Assert.Equal(2, list.Count);
        Assert.Equal(3, list.Sum(m => m.Games));
        var team = Assert.Single(list, m => m.Mode == "2v2");
        Assert.Equal("Mate", team.Partner);
        Assert.Equal(20, team.RatingChange);
    }

    [Fact]
    public void Nothing_new_or_totals_that_went_down_log_nothing()
    {
        var a = Snap(10, 5, new BrawlhallaMatches.SnapLegend { Id = 1, Games = 10, Wins = 5 });
        Assert.Empty(BrawlhallaMatches.Diff(a, a));
        Assert.Empty(BrawlhallaMatches.Diff(null, a));
        Assert.Empty(BrawlhallaMatches.Diff(a, Snap(12, 4, new BrawlhallaMatches.SnapLegend { Id = 1, Games = 12, Wins = 4 })));
    }

    [Fact]
    public void Sessions_split_after_an_hour()
    {
        var t = new DateTime(2026, 11, 9, 20, 0, 0);
        var ms = new[] { 0, 10, 20, 150, 160 }.Select(min => new BhMatch { Time = t.AddMinutes(min), Games = 1, Wins = min % 20 == 0 ? 1 : 0 }).ToList();
        var s = BrawlhallaMatches.Sessions(ms);
        Assert.Equal(2, s.Count);
        Assert.Equal(2, s[0].Games);   // newest first
        Assert.Equal(3, s[1].Games);
        Assert.Equal(t, s[1].Start);
    }

    static BhProfile Player(int level, int games, int wins, int rating, int rankedGames, int rankedWins, long hours) => new()
    {
        Level = level, Games = games, Wins = wins,
        OneVsOne = rating > 0 ? new BhRanked { Mode = "1v1", Rating = rating, PeakRating = rating, Games = rankedGames, Wins = rankedWins } : null,
        Legends = { new BhLegendStats { LegendId = 1, Games = games, Wins = wins, MatchTime = hours * 3600 } },
    };

    [Fact]
    public void A_fresh_account_at_diamond_looks_like_a_smurf()
    {
        var s = BrawlhallaMatches.Smurf(Player(level: 22, games: 300, wins: 230, rating: 2050, rankedGames: 45, rankedWins: 38, hours: 30));
        Assert.True(s.Score >= 60, $"score {s.Score}");
        Assert.Equal("Likely smurf", s.Label);
        Assert.True(s.Signs.Count >= 3);
    }

    [Fact]
    public void A_long_standing_account_is_not_a_smurf_however_good()
    {
        var s = BrawlhallaMatches.Smurf(Player(level: 100, games: 9000, wins: 7000, rating: 2700, rankedGames: 600, rankedWins: 500, hours: 2000));
        Assert.True(s.Score <= 10);
    }

    [Fact]
    public void An_ordinary_account_scores_low_and_one_sign_alone_is_capped()
    {
        Assert.Equal(0, BrawlhallaMatches.Smurf(Player(level: 60, games: 2000, wins: 1000, rating: 1300, rankedGames: 200, rankedWins: 100, hours: 400)).Score);
        // Only a high ranked win rate.
        Assert.True(BrawlhallaMatches.Smurf(Player(level: 60, games: 2000, wins: 1000, rating: 1300, rankedGames: 100, rankedWins: 80, hours: 400)).Score <= 40);
    }

    [Fact]
    public void Win_chance_follows_the_ratings()
    {
        Assert.Equal(0.5, BrawlhallaMatchup.Elo(1500, 1500), 3);
        Assert.Equal(0.909, BrawlhallaMatchup.Elo(1900, 1500), 3);   // 400 apart: 10 to 1
        Assert.Equal(0.091, BrawlhallaMatchup.Elo(1500, 1900), 3);
        Assert.Equal(0.5, BrawlhallaMatchup.Log5(0.6, 0.6), 3);
        Assert.True(BrawlhallaMatchup.Log5(0.6, 0.4) > 0.65);
    }

    [Fact]
    public void Odds_use_ranked_when_both_have_it_otherwise_win_rates()
    {
        var a = Player(level: 60, games: 1000, wins: 600, rating: 1800, rankedGames: 100, rankedWins: 60, hours: 300);
        var b = Player(level: 60, games: 1000, wins: 500, rating: 1600, rankedGames: 100, rankedWins: 50, hours: 300);
        var o = BrawlhallaMatchup.Odds(a, b);
        Assert.Equal("ranked", o.Basis);
        Assert.True(o.Chance > 0.7);
        b.OneVsOne = null;
        Assert.Equal("winrate", BrawlhallaMatchup.Odds(a, b).Basis);
        Assert.Null(BrawlhallaMatchup.Odds(a, Player(1, 5, 2, 0, 0, 0, 1)));
    }

    [Theory]
    [InlineData("Sir Roland", "sirroland")]
    [InlineData("Bödvar", "bodvar")]
    [InlineData("Qinghua & Baobao", "qinghuabaobao")]
    public void Legend_names_map_to_their_pictures(string name, string key) => Assert.Equal(key, BrawlhallaArt.Key(name));

    [Fact]
    public void Every_picture_is_built_in()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PulseOptimizer", "Assets", "Brawlhalla");
        if (!Directory.Exists(dir)) return;
        foreach (var w in new[] { "sword", "spear", "grapplehammer", "battleboots", "gauntlets", "blasters", "rocketlance", "katars" })
            Assert.True(File.Exists(Path.Combine(dir, $"weapon-{w}.png")), w);
        Assert.True(Directory.GetFiles(dir, "legend-*.png").Length >= 65);
    }
}

/// <summary>The Brawlhalla page with a real top player, for a person to look at. Only with PULSE_SHOTS (needs internet).</summary>
public class BrawlhallaPageShots
{
    [Fact]
    public void Screenshot()
    {
        var dir = Environment.GetEnvironmentVariable("PULSE_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var legends = Brawlhalla.Legends().GetAwaiter().GetResult();
        var board = Brawlhalla.Rankings("1v1", "ALL", 1, null, 10).GetAwaiter().GetResult();
        var profile = Brawlhalla.Profile(board.Rows[0].Id).GetAwaiter().GetResult();
        var me = Brawlhalla.Profile(board.Rows[1].Id).GetAwaiter().GetResult() ?? Brawlhalla.Profile(board.Rows[2].Id).GetAwaiter().GetResult();
        Exception error = null;
        var th = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current == null)
                {
                    var app = new System.Windows.Application();
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/Pulse;component/Themes/Theme.xaml") });
                }
                const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                void Shot(string file, Action<Pulse.Pages.BrawlhallaPage> set)
                {
                    var page = new Pulse.Pages.BrawlhallaPage();
                    var t = page.GetType();
                    t.GetField("_legends", F)!.SetValue(page, legends);
                    set(page);
                    t.GetMethod("Build", F)!.Invoke(page, null);
                    var host = new System.Windows.Controls.Border { Width = 1200, Height = 2600, Padding = new System.Windows.Thickness(28), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x0C, 0x0F)), Child = page };
                    host.Measure(new System.Windows.Size(1200, 2600)); host.Arrange(new System.Windows.Rect(0, 0, 1200, 2600)); host.UpdateLayout();
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(1200, 2600, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(host);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using var fs = File.Create(Path.Combine(dir, file));
                    enc.Save(fs);
                }
                void Tab(Pulse.Pages.BrawlhallaPage p, string tab)
                {
                    p.GetType().GetField("_profile", F)!.SetValue(p, profile);
                    p.GetType().GetField("_tab", F)!.SetValue(p, tab);
                }
                Shot("brawlhalla-profile.png", p => Tab(p, "overview"));
                Shot("brawlhalla-legends.png", p =>
                {
                    Tab(p, "legends");
                    var top = profile.Legends.Where(l => l.Games > 0).OrderByDescending(l => l.Games).First();
                    p.GetType().GetField("_openLegend", F)!.SetValue(p, top.LegendId);
                });
                Shot("brawlhalla-weapons.png", p => Tab(p, "weapons"));
                Shot("brawlhalla-matches.png", p =>
                {
                    Tab(p, "matches");
                    var played = profile.Legends.Where(l => l.Games > 0).OrderByDescending(l => l.Games).Take(4).ToList();
                    var now = DateTime.Now;
                    var ms = new List<BhMatch>();
                    for (int i = 0; i < 9; i++)
                    {
                        var l = played[i % played.Count];
                        bool win = i % 3 != 1;
                        ms.Add(new BhMatch
                        {
                            Time = now.AddMinutes(-6 * i - (i >= 6 ? 180 : 0)), LegendId = l.LegendId, Games = i == 4 ? 3 : 1, Wins = i == 4 ? 2 : win ? 1 : 0,
                            Kos = 3 + i % 4, Falls = 2 + i % 3, Damage = 900 + 137 * i, Seconds = 170 + 11 * i, Xp = 120 + 9 * i,
                            Mode = i % 2 == 0 ? "1v1" : i == 3 ? "2v2" : "", Partner = i == 3 ? "Lopes" : "",
                            RatingBefore = i % 2 == 0 ? 2700 + i : 0, RatingAfter = i % 2 == 0 ? 2700 + i + (win ? 14 : -11) : 0,
                        });
                    }
                    p.GetType().GetField("_matches", F)!.SetValue(p, ms);
                });
                Shot("brawlhalla-ranked.png", p => Tab(p, "ranked"));
                Shot("brawlhalla-matchup.png", p =>
                {
                    var t = p.GetType();
                    t.GetField("_matchup", F)!.SetValue(p, true);
                    t.GetField("_me", F)!.SetValue(p, me);
                    t.GetField("_foe", F)!.SetValue(p, profile);
                    t.GetField("_board", F)!.SetValue(p, board);
                });
                // Someone else, for the smurf factor.
                Shot("brawlhalla-other.png", p =>
                {
                    Tab(p, "overview");
                    profile.Level = 23; profile.Games = 310; profile.Wins = 240;
                });
                Shot("brawlhalla-board.png", p => p.GetType().GetField("_board", F)!.SetValue(p, board));
            }
            catch (Exception ex) { error = ex; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start(); th.Join();
        if (error != null) throw new Exception("Screenshot failed", error);
    }
}
