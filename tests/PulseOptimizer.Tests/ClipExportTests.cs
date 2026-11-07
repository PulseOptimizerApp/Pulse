using Pulse.Core.Clips;
using Xunit;

namespace PulseOptimizer.Tests;

public class ClipExportTests
{
    [Fact]
    public void Slow_motion_sound_keeps_atempo_in_range()
    {
        Assert.Equal("atempo=0.5", ClipLibrary.Tempo(0.5));
        Assert.Equal("atempo=0.5,atempo=0.5", ClipLibrary.Tempo(0.25));
    }

    [Fact]
    public void Vertical_copies_are_9_by_16_full_hd() =>
        Assert.Contains("scale=1080:1920", ClipLibrary.VerticalFilter);
}

public class SensitivityTests
{
    [Fact]
    public void Valorant_and_cs2_convert_by_the_known_ratio()
    {
        // The widely used ratio: CS2 sensitivity = VALORANT sensitivity × 3.181818…
        Assert.Equal(1.1136, Pulse.Pages.MousePage.ConvertSens(0.35, 0.07, 0.022), 3);
        // 800 DPI, CS2 1.0 → about 52 cm per full turn.
        Assert.InRange(Pulse.Pages.MousePage.Cm360(0.022, 1.0, 800), 51.5, 52.5);
    }
}
