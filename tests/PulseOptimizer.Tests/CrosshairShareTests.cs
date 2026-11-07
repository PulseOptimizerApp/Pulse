using Pulse.Core.Crosshair;
using Xunit;

namespace PulseOptimizer.Tests;

public class CrosshairShareTests
{
    [Fact]
    public void Pulse_codes_carry_every_setting_including_drawn_pixels()
    {
        var s = ValorantCode.Parse("0;P;c;5;h;0;0l;4;0o;2;0a;1;0f;0;1b;0");
        s.Rotation = 45; s.Circle = true; s.CircleRadius = 11;
        var code = PulseCode.Write(s);
        Assert.StartsWith("PULSE1-", code);
        Assert.True(PulseCode.Looks(code));
        var back = PulseCode.Parse(code);
        Assert.Equal(45, back.Rotation);
        Assert.Equal(11, back.CircleRadius);
        Assert.Equal(ValorantCode.Write(s), ValorantCode.Write(back));

        var d = new CrosshairStyle { Mode = "draw", GridSize = 21, Pixels = string.Join(";", Enumerable.Range(0, 21 * 21).Select(i => i % 7 == 0 ? "#00FFFF" : "")) };
        var dback = PulseCode.Parse(PulseCode.Write(d));
        Assert.Equal("draw", dback.Mode);
        Assert.Equal(d.PixelCells(), dback.PixelCells());
    }

    [Fact]
    public void Picture_crosshairs_and_broken_codes_are_refused()
    {
        Assert.Null(PulseCode.Write(new CrosshairStyle { Mode = "image", ImageFile = @"C:\x.png" }));
        Assert.Null(PulseCode.Parse("PULSE1-notreallyacode"));
        Assert.False(PulseCode.Looks("0;P;c;5"));
    }

    [Fact]
    public void A_turned_crosshair_gets_a_bigger_window()
    {
        var s = ValorantCode.Parse("0;P;c;5;h;0;0l;4;0o;2;0a;1;0f;0;1b;0");
        double straight = s.Reach();
        s.Rotation = 45;
        Assert.True(s.Reach() > straight * 1.3);
    }
}
