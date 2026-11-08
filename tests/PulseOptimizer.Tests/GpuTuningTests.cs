using Pulse.Core.Gpu;
using Xunit;

namespace PulseOptimizer.Tests;

public class GpuTuningTests
{
    [Fact]
    public void Says_what_holds_the_card_back()
    {
        Assert.Equal("Temperature", GpuTuning.HeldBack(0x20, 99));
        Assert.Equal("Power limit", GpuTuning.HeldBack(0x4, 99));
        Assert.Equal("Power supply", GpuTuning.HeldBack(0x80, 99));
        Assert.Equal("Nothing (idle)", GpuTuning.HeldBack(0x1, 5));
        Assert.Equal("Nothing", GpuTuning.HeldBack(0, 98));
    }

    [Fact]
    public void Stock_means_nothing_changed()
    {
        Assert.True(new GpuTuneSettings().IsStock);
        Assert.False(new GpuTuneSettings { PowerPercent = 80 }.IsStock);
        Assert.False(new GpuTuneSettings { MaxClock = 2700 }.IsStock);
        var t = new GpuTuneSettings { CoreOffset = 150 };
        var c = t.Copy();
        c.CoreOffset = 0;
        Assert.Equal(150, t.CoreOffset);
    }
}
