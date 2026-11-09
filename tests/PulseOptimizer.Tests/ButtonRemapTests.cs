using Pulse.Core.Input;
using Xunit;

namespace PulseOptimizer.Tests;

public class ButtonRemapTests
{
    [Theory]
    [InlineData("off", true)]
    [InlineData("button:0", true)]
    [InlineData("button:4", true)]
    [InlineData("button:5", false)]
    [InlineData("wheel:up", true)]
    [InlineData("wheel:down", true)]
    [InlineData("key:81", true)]        // Q
    [InlineData("key:1", false)]        // a mouse button isn't a key
    [InlineData("key:300", false)]
    [InlineData("media:179", true)]     // play/pause
    [InlineData("media:65", false)]
    [InlineData("move:10", false)]      // never mouse movement
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_presses_clicks_scroll_and_media_keys_are_allowed(string action, bool valid) =>
        Assert.Equal(valid, ButtonRemap.IsValid(action));

    [Fact]
    public void Nothing_runs_without_remaps()
    {
        ButtonRemap.Apply(new Dictionary<string, string> { ["back"] = "" , ["forward"] = "nonsense" });
        Assert.False(ButtonRemap.Running);
    }
}
