using Pulse.Core.Mouse;
using Xunit;

namespace PulseOptimizer.Tests;

public class SensConverterTests
{
    static SensConverter.Game G(string id) => SensConverter.Find(id)!;

    [Fact]
    public void Matches_well_known_conversions()
    {
        Assert.Equal(1.2727, SensConverter.Convert(G("valorant"), 0.4, 800, G("cs2"), 800), 4);      // VALORANT × 3.18 = CS2
        Assert.Equal(6.6667, SensConverter.Convert(G("cs2"), 2, 800, G("overwatch"), 800), 4);       // CS2 × 3.33 = Overwatch 2
        Assert.Equal(1.6, SensConverter.Convert(G("valorant"), 0.4, 800, G("rivals"), 800), 6);      // VALORANT × 4 = Marvel Rivals
        Assert.Equal(2, SensConverter.Convert(G("cs2"), 2, 800, G("apex"), 800), 6);                 // same engine scale
        Assert.Equal(22, SensConverter.Convert(G("cs2"), 1, 800, G("finals"), 800), 6);              // The Finals = CS2 × 22
        Assert.Equal(6.6667, SensConverter.Convert(G("overwatch"), 6.6667, 800, G("cod"), 800), 4);   // same scale
    }

    [Fact]
    public void A_dpi_change_keeps_the_same_turn()
    {
        Assert.Equal(1, SensConverter.Convert(G("cs2"), 2, 400, G("cs2"), 800), 6);
        var v = G("valorant");
        Assert.Equal(SensConverter.Cm360(v, 0.4, 800), SensConverter.Cm360(G("cs2"), SensConverter.Convert(v, 0.4, 800, G("cs2"), 1600), 1600), 6);
    }

    [Fact]
    public void Cm_per_360()
    {
        Assert.Equal(40.82, SensConverter.Cm360(G("valorant"), 0.4, 800), 2);
        Assert.Equal(51.95, SensConverter.Cm360(G("cs2"), 1, 800), 2);
        Assert.True(double.IsNaN(SensConverter.Cm360(G("cs2"), 0, 800)));
        Assert.True(double.IsNaN(SensConverter.Convert(G("cs2"), 1, 0, G("valorant"), 800)));
    }

    [Fact]
    public void Rounds_like_each_game_takes_it()
    {
        Assert.Equal(1.27, SensConverter.Round(G("cs2"), 1.2727));
        Assert.Equal(0.364, SensConverter.Round(G("valorant"), 0.36363));
        Assert.Equal(13, SensConverter.Round(G("r6"), 12.6));
        Assert.All(SensConverter.Games, g => Assert.True(g.Yaw > 0));
        Assert.Equal(SensConverter.Games.Count, SensConverter.Games.Select(g => g.Id).Distinct().Count());
    }
}

public class MouseProfileTests
{
    [Fact]
    public void Picks_the_games_profile_or_the_desktop_one()
    {
        var s = new MouseSettings();
        Assert.False(MouseSettings.WantsGames(s));                      // no mouse picked
        s.Device = MouseSettings.DeviceId(0x36a7, 0xa887);
        Assert.Equal("36A7:A887", s.Device);
        Assert.False(MouseSettings.WantsGames(s));                      // nothing to switch to
        s.Desktop = new MouseProfile { Dpi = 1600, PollingHz = 1000 };
        Assert.True(MouseSettings.WantsGames(s));
        s.Games.Add(new MouseProfile { Game = "VALORANT", Dpi = 800, PollingHz = 4000 });
        Assert.Equal(800, s.For("valorant").Dpi);                      // ignores case
        Assert.Equal(1600, s.For("Counter-Strike 2").Dpi);             // no profile: the desktop one
        Assert.Equal(1600, s.For(null).Dpi);
        Assert.True(MouseSettings.WantsGames(new MouseSettings { Device = "x", Games = { new MouseProfile { Game = "A" } } }));
    }
}
