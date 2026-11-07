using System.Text.RegularExpressions;
using Pulse.Core.Devices;
using Xunit;

namespace PulseOptimizer.Tests;

public class MouseDriverTests
{
    static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Repo.Root }.Concat(parts).ToArray()));

    [Fact]
    public void The_driver_page_never_goes_online()
    {
        Assert.True(MouseDrivers.IsLocal("https://pulse-drivers.local/host.html"));
        Assert.False(MouseDrivers.IsLocal("https://example.com/"));
        Assert.False(MouseDrivers.IsLocal("https://pulse-drivers.local.evil.com/"));
        Assert.False(MouseDrivers.IsLocal(null));
        var src = Read("src", "PulseOptimizer", "Core", "Devices", "MouseDrivers.cs");
        Assert.Contains("e.Cancel = true", src);                       // other navigations refused
        Assert.Contains("CreateWebResourceResponse(null, 403", src);    // every other request refused
        Assert.Contains("connect-src 'none'", src);                     // and the page itself may not connect anywhere
    }

    [Fact]
    public void Drivers_can_only_change_settings_not_firmware()
    {
        var host = Read("drivers", "host.ts");
        var m = Regex.Match(host, @"const ALLOWED = /(.+)/;");
        Assert.True(m.Success);
        var allowed = new Regex(m.Groups[1].Value);
        foreach (var ok in new[] { "setDpi", "setPollingRate", "setButtonMapping", "readStatus", "writeActiveProfile" }) Assert.Matches(allowed, ok);
        foreach (var no in new[] { "startDfu", "flashFirmware", "resetAllOnboardProfiles", "initializeBlankOnboardProfiles", "pairDevice", "unpair", "factoryReset" })
            Assert.DoesNotMatch(allowed, no);
    }

    [Fact]
    public void Driver_version_is_pinned()
    {
        var lockFile = Read("drivers", "protocol.lock").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("https://github.com/OpenMouse-Project/mouse-protocol", lockFile[0]);
        Assert.Matches("^[0-9a-f]{40}$", lockFile[1]);
        Assert.Contains("OpenMouse", Read("NOTICE.md"));
        Assert.Contains("GNU AFFERO GENERAL PUBLIC LICENSE", Read("LICENSE"));
    }

    [Fact]
    public void Collections_of_one_interface_group_like_webhid()
    {
        var a = NativeHid.DeviceKey(@"\\?\hid#vid_046d&pid_c54d&mi_02&col01#7&2c9b4b2b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}");
        var b = NativeHid.DeviceKey(@"\\?\hid#vid_046d&pid_c54d&mi_02&col02#7&2c9b4b2b&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}");
        var other = NativeHid.DeviceKey(@"\\?\hid#vid_046d&pid_c54d&mi_01&col01#7&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}");
        Assert.Equal(a, b);
        Assert.NotEqual(a, other);
    }
}
