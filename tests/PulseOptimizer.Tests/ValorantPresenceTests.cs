using System.Text;
using Pulse.Core.Games.Live;
using Xunit;

namespace PulseOptimizer.Tests;

/// <summary>The VALORANT recap shows up as soon as the presence says you left the match, in either presence layout.</summary>
public class ValorantPresenceTests
{
    static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Reads_the_match_state_from_both_presence_layouts()
    {
        Assert.Equal("INGAME", ValorantMatches.StateFromPresence(B64("{\"isValid\":true,\"sessionLoopState\":\"INGAME\",\"matchMap\":\"/Game/Maps/Ascent/Ascent\"}")));
        Assert.Equal("MENUS", ValorantMatches.StateFromPresence(B64("{\"isValid\":true,\"matchPresenceData\":{\"sessionLoopState\":\"MENUS\",\"provisioningFlow\":\"Invalid\"},\"partyPresenceData\":{}}")));
        Assert.Null(ValorantMatches.StateFromPresence(""));
        Assert.Null(ValorantMatches.StateFromPresence("not base64!"));
    }
}
