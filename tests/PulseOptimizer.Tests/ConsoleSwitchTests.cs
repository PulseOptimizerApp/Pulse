using Pulse.Core;
using Xunit;

namespace PulseOptimizer.Tests;

public class ConsoleSwitchTests
{
    [Theory]
    [InlineData(0x0F, "DisplayPort 1")]
    [InlineData(0x11, "HDMI 1")]
    [InlineData(0x0112, "HDMI 2")] // some monitors put junk in the high byte
    [InlineData(0x1B, "USB-C")]
    [InlineData(0x30, "Input 48")]
    public void Inputs_have_names_people_know(int code, string name) => Assert.Equal(name, ConsoleSwitch.InputName(code));

    [Fact]
    public void Swapping_before_setup_says_what_to_do_and_changes_nothing()
    {
        var s = new ConsoleSwitchSettings();
        var msg = ConsoleSwitch.Swap(s, true);
        Assert.Contains("Set up the switch first", msg);
        Assert.False(s.OnConsole);
    }
}
