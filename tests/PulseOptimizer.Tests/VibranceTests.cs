using Pulse.Core.Display;
using Xunit;

namespace PulseOptimizer.Tests;

public class VibranceTests
{
    [Theory]
    // NVIDIA: 0 is normal, 63 the most.
    [InlineData(50, 0, 0, 63, 0)]
    [InlineData(100, 0, 0, 63, 63)]
    [InlineData(75, 0, 0, 63, 32)]
    // AMD: 100 is normal, 0–200.
    [InlineData(50, 0, 100, 200, 100)]
    [InlineData(75, 0, 100, 200, 150)]
    [InlineData(0, 0, 100, 200, 0)]
    public void Levels_map_onto_the_drivers_range(int level, int min, int normal, int max, int driver)
    {
        Assert.Equal(driver, Vibrance.ToDriver(level, min, normal, max));
        Assert.InRange(Vibrance.FromDriver(driver, min, normal, max), level - 1, level + 1);
    }

    [Fact]
    public void Looks_keep_black_and_white_and_lift_the_shadows()
    {
        var dark = PulseColor.Look("dark", "red", null);
        Assert.Equal(0, PulseColor.Curve(0, dark, 1), 3);
        Assert.True(PulseColor.Curve(0.15, dark, 1) > 0.17);           // shadows brighter
        Assert.InRange(PulseColor.Curve(0.95, dark, 1), 0.93, 0.97);    // highlights about the same
        var ramp = PulseColor.Ramp(PulseColor.Look("pop", "yellow", null));
        Assert.Equal(768, ramp.Length);
        for (int i = 1; i < 256; i++) Assert.True(ramp[i] >= ramp[i - 1]); // never goes backwards
    }

    [Fact]
    public void The_preview_makes_dull_colours_richer_and_leaves_grey_grey()
    {
        var px = new byte[] { 100, 120, 140, 255, 128, 128, 128, 255 };
        PulseColor.Apply(px, new ColorLook(90, 0, 1, 1, 1, 1));
        Assert.True(px[2] - px[0] > 40);          // the blue-ish pixel spread out
        Assert.Equal(px[4], px[5]); Assert.Equal(px[5], px[6]); // grey stays grey
    }
}
