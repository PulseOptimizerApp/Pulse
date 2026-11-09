using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pulse.Core.Games.Valorant;
using Xunit;

namespace PulseOptimizer.Tests;

/// <summary>Made-up but complete VALORANT matches for the tracker's tests and screenshots.</summary>
static class FakeMatches
{
    static readonly string[] AgentsA = { "Chamber", "Jett", "Sova", "Omen", "Killjoy" };
    static readonly string[] AgentsB = { "Reyna", "Raze", "Brimstone", "Cypher", "Clove" };
    static readonly string[] Maps = { "Bind", "Ascent", "Haven", "Abyss", "Lotus", "Sunset", "Pearl", "Split" };
    static readonly string[] Weapons = { "9c82e19d-4575-0200-1a81-3eacf00cf872", "ee8e8d15-496b-07ac-e5f6-8fae5d4c7b1a", "e336c6b8-418d-9340-d77f-7a9e4cfe0702" };

    public static List<VMatch> Make(int count, int seed = 7)
    {
        var rnd = new Random(seed);
        var list = new List<VMatch>();
        for (int i = 0; i < count; i++) list.Add(One(rnd, i, i % 9 == 4 ? "deathmatch" : i % 5 == 3 ? "unrated" : "competitive"));
        return list;
    }

    static VMatch One(Random rnd, int i, string queue)
    {
        var m = new VMatch
        {
            Id = "m" + i, Map = Maps[i % Maps.Length], Queue = queue, Me = "me", FirstAttack = "Red", Version = VMatch.CurrentVersion,
            StartUtc = DateTime.UtcNow.AddHours(-6 - i * 17), LengthMs = 34 * 60_000 + rnd.Next(0, 600_000),
        };
        for (int p = 0; p < 10; p++)
        {
            var id = p == 0 ? "me" : "p" + p;
            m.Players.Add(new VPlayer
            {
                Id = id, Team = p < 5 ? "Blue" : "Red", Agent = p < 5 ? AgentsA[p] : AgentsB[p - 5], Name = (p == 0 ? "dilan" : "player" + p) + "#" + (p == 0 ? "fps" : "NA" + p),
                Level = 30 + rnd.Next(0, 500), Tier = 14 + rnd.Next(0, 6), Party = p is 0 or 1 ? "party1" : p is 6 or 7 ? "party2" : "",
            });
        }
        int rounds = 20 + rnd.Next(0, 5);
        for (int r = 0; r < rounds; r++)
        {
            var round = new VRound { N = r };
            var alive = m.Players.ToDictionary(p => p.Id, _ => true);
            long ms = 5000;
            string winner = null;
            while (winner == null)
            {
                var team = rnd.Next(0, 2) == 0 ? "Blue" : "Red";
                var killers = m.Players.Where(p => p.Team == team && alive[p.Id]).ToList();
                var victims = m.Players.Where(p => p.Team != team && alive[p.Id]).ToList();
                if (killers.Count == 0 || victims.Count == 0) { winner = killers.Count == 0 ? (team == "Blue" ? "Red" : "Blue") : team; break; }
                var k = killers[rnd.Next(killers.Count)];
                var v = victims[rnd.Next(victims.Count)];
                if (rnd.Next(0, 3) == 0) k = killers[0];
                ms += rnd.Next(2000, 9000);
                var kill = new VKill
                {
                    Round = r, RoundMs = ms, GameMs = r * 100_000 + ms, Killer = k.Id, Victim = v.Id, DamageType = "Weapon", Weapon = Weapons[rnd.Next(Weapons.Length)],
                    VX = rnd.Next(-5000, 5000), VY = rnd.Next(-5000, 5000),
                };
                if (rnd.Next(0, 2) == 0 && killers.Count > 1) kill.Assists.Add(killers.First(x => x.Id != k.Id).Id);
                foreach (var p in m.Players.Where(p => alive[p.Id])) kill.Pos.Add(new VPos { Id = p.Id, X = rnd.Next(-5000, 5000), Y = rnd.Next(-5000, 5000) });
                m.Kills.Add(kill);
                alive[v.Id] = false;
                round.Damage.Add(new VDamage { From = k.Id, To = v.Id, Amount = 150, Head = rnd.Next(0, 2), Body = 1 + rnd.Next(0, 2), Legs = rnd.Next(0, 2) });
                if (rnd.Next(0, 4) == 0 && round.PlantMs < 0)
                {
                    var planter = m.Players.FirstOrDefault(p => p.Team == m.Attackers(r) && alive[p.Id]);
                    if (planter != null) { round.Planter = planter.Id; round.PlantMs = ms + 1000; round.Site = "A"; }
                }
            }
            round.Winner = winner;
            round.Result = round.PlantMs >= 0 ? (winner == m.Attackers(r) ? "Bomb detonated" : "Bomb defused") : "Elimination";
            if (round.Result == "Bomb defused") round.Defuser = m.Players.FirstOrDefault(p => p.Team == winner)?.Id;
            foreach (var p in m.Players) round.Eco[p.Id] = new VEco { Loadout = r % 12 == 0 ? 800 : 1500 + rnd.Next(0, 3500), Remaining = rnd.Next(0, 6000), Spent = 800 + rnd.Next(0, 3000) };
            m.Rounds.Add(round);
            if (m.Rounds.Count(x => x.Winner == "Blue") == 13 || m.Rounds.Count(x => x.Winner == "Red") == 13) break;
        }
        foreach (var p in m.Players)
        {
            p.Kills = m.Kills.Count(k => k.Killer == p.Id);
            p.Deaths = m.Kills.Count(k => k.Victim == p.Id);
            p.Assists = m.Kills.Count(k => k.Assists.Contains(p.Id));
            p.Rounds = m.Rounds.Count;
            p.Score = p.Kills * 200 + p.Assists * 50 + m.Rounds.Count * 20;
        }
        return m;
    }
}

public class TrackerGgTests
{
    [Fact]
    public void Clutches_are_found_when_the_player_is_the_last_one_alive()
    {
        var m = new VMatch { Id = "c", Queue = "competitive", Me = "me" };
        foreach (var (id, team) in new[] { ("me", "Blue"), ("a", "Blue"), ("x", "Red"), ("y", "Red") }) m.Players.Add(new VPlayer { Id = id, Team = team });
        m.Rounds.Add(new VRound { N = 0, Winner = "Blue" });
        m.Kills.Add(new VKill { Round = 0, RoundMs = 1, Killer = "x", Victim = "a" });   // me alone vs 2
        m.Kills.Add(new VKill { Round = 0, RoundMs = 2, Killer = "me", Victim = "x" });
        m.Kills.Add(new VKill { Round = 0, RoundMs = 3, Killer = "me", Victim = "y" });
        var c = Stats.Clutches(m, "me");
        Assert.Single(c);
        Assert.Equal(2, c[0].Vs);
        Assert.True(c[0].Won);
        Assert.Empty(Stats.Clutches(m, "a"));
        Assert.Equal(1, Stats.MultiKills(m, "me")[2]);
    }

    [Fact]
    public void Encounters_count_games_with_and_against()
    {
        var ms = FakeMatches.Make(6).Where(m => m.Queue != "deathmatch").ToList();
        var e = Stats.Encounters(ms, "me");
        var mate = e.First(x => x.Id == "p1");
        var foe = e.First(x => x.Id == "p5");
        Assert.Equal(ms.Count, mate.With);
        Assert.Equal(0, mate.Against);
        Assert.Equal(ms.Count, foe.Against);
        Assert.Equal(9, e.Count);
    }

    [Fact]
    public void Versus_and_rivalries_add_up_to_the_kill_feed()
    {
        var m = FakeMatches.Make(1)[0];
        var v = Stats.Versus(m, "me");
        Assert.Equal(5, v.Count);
        Assert.Equal(m.Kills.Count(k => k.Killer == "me" && m.TeamOf(k.Victim) == "Red"), v.Sum(x => x.Kills));
        var (top, a, b) = Stats.Rivalries(m, "Blue");
        Assert.NotNull(top);
        Assert.True(a.AKills - a.BKills >= b.AKills - b.BKills);
        var sides = Stats.Sides(m, "me");
        Assert.Equal(m.Kills.Count(k => k.Victim == "me"), sides.Attack.Deaths + sides.Defense.Deaths);
    }

    [Fact]
    public void Deathmatch_only_shows_when_picked()
    {
        var ms = FakeMatches.Make(10);
        Assert.DoesNotContain(new TrackerFilter().Apply(ms), m => m.Queue == "deathmatch");
        Assert.All(new TrackerFilter("deathmatch").Apply(ms), m => Assert.Equal("deathmatch", m.Queue));
        Assert.NotEmpty(new TrackerFilter("deathmatch").Apply(ms));
    }

    [Fact]
    public void Map_details_split_attack_and_defense()
    {
        var ms = FakeMatches.Make(8).Where(m => m.Queue == "competitive").ToList();
        var d = Stats.MapDetails(ms, "me");
        Assert.Equal(ms.Sum(m => m.Rounds.Count), d.Sum(x => x.AtkPlayed + x.DefPlayed));
        Assert.True(d.Sum(x => x.Matches) <= ms.Count);
        Assert.All(d, x => Assert.True(x.AtkWon <= x.AtkPlayed && x.DefWon <= x.DefPlayed));
    }

    /// <summary>
    /// Pictures of the tracker for a person to look at (not a check): set PULSE_SHOTS to a folder and run this test
    /// on Windows (with the VALORANT art from tools/get-match-art.py in place).
    /// </summary>
    [Fact]
    public void Screenshots()
    {
        var dir = Environment.GetEnvironmentVariable("PULSE_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        Exception error = null;
        var t = new Thread(() =>
        {
            try { Shoot(dir); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Exception("Screenshots failed", error);
    }

    static void Shoot(string dir)
    {
        if (Application.Current == null)
        {
            var app = new Application();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Pulse;component/Themes/Theme.xaml") });
        }
        var matches = FakeMatches.Make(24);
        var real = matches.Where(m => VMatch.Counts(m.Queue)).ToList();
        var profile = Tracker.FromYourMatches(real, "me");
        profile.Name = "dilan#fps";
        profile.Level = 120;
        profile.Tier = 15; profile.Rr = 49; profile.Peak = 16;

        var page = new Pulse.Pages.ValorantPage();
        var type = page.GetType();
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        void Set(string field, object value) => type.GetField(field, F)!.SetValue(null, value);
        object Call(string method, params object[] args) => type.GetMethod(method, F)!.Invoke(page, args);

        // Deathmatch games come from the saved matches; for pictures, add them to the profile's list.
        profile.Matches = matches.Where(m => m.Players.Any(p => p.Id == "me")).OrderByDescending(m => m.StartUtc).ToList();

        foreach (var view in new[] { "overview", "matches", "performance", "agents", "maps", "weapons", "encounters" })
        {
            Set("_trView", view);
            Set("_trQueue", "competitive");
            var panel = new StackPanel();
            foreach (var el in (IEnumerable<FrameworkElement>)Call("ProfileView", profile)) panel.Children.Add(el);
            Save(panel, Path.Combine(dir, $"profile-{view}.png"), 1440);
        }
        Set("_trQueue", "deathmatch");
        Set("_trView", "matches");
        var dm = new StackPanel();
        foreach (var el in (IEnumerable<FrameworkElement>)Call("ProfileView", profile)) dm.Children.Add(el);
        Save(dm, Path.Combine(dir, "profile-deathmatch.png"), 1440);

        var search = (FrameworkElement)Call("SearchCard");
        Save(new Border { Background = new SolidColorBrush(Color.FromRgb(0x17, 0x18, 0x1C)), Padding = new Thickness(20), Child = search }, Path.Combine(dir, "search.png"), 900);

        var match = real[0];
        foreach (var tab in new[] { "scoreboard", "performance", "economy", "rounds", "duels", "autopsy" })
        {
            Set("_trMatchTab", tab);
            var panel = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0x19, 0x1E, 0x25)) };
            panel.Children.Add((FrameworkElement)Call("MatchHeader", match));
            panel.Children.Add((FrameworkElement)Call("MatchTabBar", match));
            panel.Children.Add((FrameworkElement)Call("MatchTabBody", match, "me"));
            Save(panel, Path.Combine(dir, $"match-{tab}.png"), 1180);
        }
    }

    static void Save(FrameworkElement content, string path, double width)
    {
        var host = new Border { Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x10)), Padding = new Thickness(16), Width = width, Child = content };
        for (int i = 0; i < 3; i++)
        {
            host.Measure(new Size(width, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
        }
        var h = Math.Min(9000, Math.Max(10, host.ActualHeight));
        var rtb = new RenderTargetBitmap((int)width, (int)h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var f = File.Create(path);
        enc.Save(f);
    }
}

public class ReworkTests
{
    [Fact]
    public void Speed_is_the_trimmed_mean_of_one_second_samples()
    {
        // Ramp-up (slow) and a burst (fast) are left out, like Speedtest.
        var samples = new List<double> { 50, 300, 900, 920, 930, 925, 935, 915, 940, 2000 };
        double v = Pulse.Core.SpeedTest.Trimmed(samples);
        Assert.InRange(v, 900, 945);
        Assert.Equal(0, Pulse.Core.SpeedTest.Trimmed(new List<double>()));
    }

    [Fact]
    public void A_sudden_jump_in_combat_score_is_a_smurf_sign()
    {
        var ms = FakeMatches.Make(20).Where(m => VMatch.Counts(m.Queue)).OrderByDescending(m => m.StartUtc).ToList();
        // Make "me" far better in the newest four matches.
        foreach (var m in ms.Take(4)) { var me = m.Players.First(p => p.Id == "me"); me.Score *= 6; }
        foreach (var m in ms.Skip(4)) { var me = m.Players.First(p => p.Id == "me"); me.Score = Math.Max(1, me.Score / 2); }
        var p = new Profile { Puuid = "me", Matches = ms, Tier = 12 };
        var jump = Tracker.Jump(p);
        Assert.NotNull(jump);
        Assert.Contains("Combat score jumped", jump.Value.Sign);
    }

    [Fact]
    public void A_new_account_with_big_match_stats_adds_a_sign()
    {
        var t = new PlayerTotals { Rounds = 20, Kills = 30, Deaths = 12, Score = 6000 };
        var s = Pulse.Core.Games.Live.ValorantLobby.WithMatch(Pulse.Core.Games.Live.Smurf.None, 25, t, 12);
        Assert.Equal(25, s.Score);
        Assert.Single(s.Signs);
        Assert.Same(Pulse.Core.Games.Live.Smurf.None, Pulse.Core.Games.Live.ValorantLobby.WithMatch(Pulse.Core.Games.Live.Smurf.None, 200, t, 12));
    }

    [Fact]
    public void The_lineup_library_is_built_in_and_complete()
    {
        var lib = Pulse.Core.Games.Valorant.Lineups.Library;
        Assert.True(lib.Count >= 50, $"only {lib.Count}");
        Assert.All(lib, l =>
        {
            Assert.True(l.BuiltIn);
            Assert.Contains(l.Map, Pulse.Core.Games.Valorant.Lineups.Maps);
            Assert.Contains(l.Agent, Pulse.Core.Games.Valorant.Lineups.Agents);
            Assert.NotEmpty(l.Steps);
            Assert.StartsWith("https://", l.Source);
            Assert.StartsWith("https://www.youtube.com/results?search_query=", l.Video);
        });
    }
}
