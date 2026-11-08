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

