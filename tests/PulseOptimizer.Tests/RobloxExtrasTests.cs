using System.Net;
using Pulse.Core;
using Pulse.Core.Roblox;
using Xunit;

namespace PulseOptimizer.Tests;

public class RobloxExtrasTests
{
    [Fact]
    public void Place_id_is_read_from_the_launch_link()
    {
        const string uri = "roblox-player:1+launchmode:play+gameinfo:abc+launchtime:1730700000000+placelauncherurl:https%3A%2F%2Fassetgame.roblox.com%2Fgame%2FPlaceLauncher.ashx%3Frequest%3DRequestGame%26browserTrackerId%3D123%26placeId%3D606849621%26isPlayTogetherGame%3Dfalse+browsertrackerid:123+robloxLocale:en_us";
        Assert.Equal("606849621", RobloxBoost.PlaceId(uri));
        Assert.Null(RobloxBoost.PlaceId("roblox-player:1+launchmode:app"));
    }

    [Fact]
    public void Power_plan_is_read_from_powercfg()
    {
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e",
            RobloxBoost.ActivePlan("Power Scheme GUID: 381B4222-F694-41F0-9685-FF5BB260DF2E  (Balanced)"));
        Assert.Null(RobloxBoost.ActivePlan(""));
    }

    [Fact]
    public void Server_address_comes_from_the_newest_join_in_the_log()
    {
        var log = "2026-11-04T01:00:00Z,1.0,abc,6 [FLog::Network] UDMUX Address = 128.116.1.10, Port = 51000\n" +
                  "2026-11-04T01:20:00Z,1.0,abc,6 [FLog::Network] UDMUX Address = 128.116.44.7, Port = 50433\n";
        Assert.Equal(IPAddress.Parse("128.116.44.7"), NetworkCheck.ServerFromLog(log));
        Assert.Null(NetworkCheck.ServerFromLog("nothing here"));
    }

    [Fact]
    public void Lag_verdict_points_at_the_right_part()
    {
        var goodRouter = NetworkCheck.Summarize("192.168.1.1", 20, Enumerable.Repeat(2.0, 20).ToList());
        var badRouter = NetworkCheck.Summarize("192.168.1.1", 20, new double[] { 2, 60, 3, 80, 2, 70 });
        var goodServer = NetworkCheck.Summarize("128.116.1.1", 20, Enumerable.Repeat(30.0, 20).ToList());
        Assert.Contains("Wi-Fi", NetworkCheck.Verdict(badRouter, goodServer, wifi: true));
        Assert.StartsWith("All good", NetworkCheck.Verdict(goodRouter, goodServer, false));
        Assert.Contains("doesn't answer", NetworkCheck.Verdict(goodRouter, NetworkCheck.Summarize("x", 20, new double[0]), false));
    }
}
