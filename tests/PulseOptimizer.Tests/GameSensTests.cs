using Pulse.Core.Reaction;
using Xunit;

namespace PulseOptimizer.Tests;

public class GameSensTests
{
    [Fact]
    public void Cm_per_360_matches_valorant()
    {
        // 0.4 at 800 DPI is about 40.8 cm per 360 in VALORANT (0.07° per count × sensitivity).
        Assert.Equal(40.82, new GameSens(0.4, 800).Cm360, 2);
        Assert.Equal(0.028, new GameSens(0.4, 800).DegreesPerCount, 6);
        Assert.Equal(20.41, new GameSens(0.4, 1600).Cm360, 2);
        Assert.False(new GameSens(0, 800).Valid);
        Assert.False(new GameSens(0.4, 50).Valid);
    }

    [Fact]
    public void The_screen_edge_is_half_the_field_of_view()
    {
        double f = GameSens.Focal(1920, 1080);
        Assert.Equal(960, GameSens.Project(51.5, 0, f).X, 6);         // 103° across the width
        Assert.Equal(0, GameSens.Project(0, 0, f).X, 6);
        var (yaw, pitch) = GameSens.Unproject(300, -120, f);
        var (x, y) = GameSens.Project(yaw, pitch, f);
        Assert.Equal(300, x, 6);
        Assert.Equal(-120, y, 6);
        // Ultrawide is letterboxed to 16:9, like the game.
        Assert.Equal(f, GameSens.Focal(2560 * 1080 / 1080.0 * 1, 1080), 6);
    }

    [Fact]
    public void A_flick_takes_the_same_counts_as_in_game()
    {
        // A target 400 px right of centre on a 1920×1080 screen is atan(400 / f) degrees away; at 0.4 sensitivity that's
        // that many degrees / 0.028 counts, the same number of counts the game needs.
        var s = new GameSens(0.4, 800);
        double f = GameSens.Focal(1920, 1080);
        double counts = GameSens.Unproject(400, 0, f).Yaw / s.DegreesPerCount;
        Assert.Equal(400, GameSens.Project(counts * s.DegreesPerCount, 0, f).X, 6);
        Assert.InRange(counts, 950, 1020);     // about 987 counts: 3.1 cm of mousepad at 800 DPI
    }
}
