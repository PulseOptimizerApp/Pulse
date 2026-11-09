using System.Text.Json;
using Pulse.Core.Mouse;
using Xunit;
using Xunit.Abstractions;

namespace PulseOptimizer.Tests;

/// <summary>
/// The bundled OpenMouse protocol library runs in Pulse's script engine and recognises mice from what Windows reports.
/// Skipped when the bundle isn't built (it's made by tools/get-mouse-protocol.sh in CI).
/// </summary>
public class MouseHostTests
{
    readonly ITestOutputHelper _out;
    public MouseHostTests(ITestOutputHelper o) => _out = o;

    static Hid.DeviceInfo WlMouse()
    {
        // A WLmouse interface as Windows lists it: vendor 0x36A7, a vendor-defined collection with feature report 0.
        var d = new Hid.DeviceInfo { Key = @"\\?\hid#vid_36a7&pid_1234&mi_01", VendorId = 0x36a7, ProductId = 0x1234, ProductName = "WLmouse" };
        var c = new Hid.Collection { Path = d.Key + "&col01", UsagePage = 0xff00, Usage = 1, FeatureLength = 65 };
        c.FeatureIds.Add(0);
        d.Collections.Add(c);
        return d;
    }

    [Fact]
    public async Task Recognises_a_supported_mouse_and_ignores_other_devices()
    {
        if (MouseHost.Script() == null) { _out.WriteLine("No bundled protocol library in this build: skipped."); return; }
        var host = MouseHost.Instance!;
        var keyboard = new Hid.DeviceInfo { Key = "kb", VendorId = 0x046d, ProductId = 0xc31c, ProductName = "Keyboard" };
        keyboard.Collections.Add(new Hid.Collection { Path = "kb", UsagePage = 0x01, Usage = 0x06, InputLength = 9 });

        var json = await host.Run("scanList", MouseHost.DevicesJson(new[] { WlMouse(), keyboard })).WaitAsync(TimeSpan.FromSeconds(60));
        _out.WriteLine(json);
        var found = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.Equal(1, found.GetArrayLength());
        Assert.Equal(0x36a7, found[0].GetProperty("vendorId").GetInt32());
        Assert.True(found[0].GetProperty("score").GetDouble() > 0);
    }

    [Fact]
    public async Task Scans_this_PCs_devices_without_failing()
    {
        if (MouseHost.Script() == null || !OperatingSystem.IsWindows()) return;
        var json = await MouseHost.Instance!.Run("scan").WaitAsync(TimeSpan.FromSeconds(60));
        _out.WriteLine(json);
        Assert.Equal(JsonValueKind.Array, JsonSerializer.Deserialize<JsonElement>(json).ValueKind);
    }

    [Fact]
    public void Collections_of_one_interface_group_together()
    {
        Assert.Equal(Hid.InterfaceKey(@"\\?\HID#VID_36A7&PID_A887&MI_01&Col02#8&1f&0&0001#{4d1e55b2}"),
                     Hid.InterfaceKey(@"\\?\HID#VID_36A7&PID_A887&MI_01&Col03#8&1f&0&0002#{4d1e55b2}".Replace("0002", "0001")));
        var json = MouseHost.DevicesJson(new[] { WlMouse() });
        Assert.Contains("\"featureReports\":[{\"reportId\":0", json);
        Assert.Contains("\"usagePage\":65280", json);
    }
}
