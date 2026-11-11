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
}
