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

public class MousePageShots
{
    /// <summary>The Mouse page with a sample mouse's settings, for a person to look at. Only with PULSE_SHOTS.</summary>
    [Fact]
    public void Screenshot()
    {
        var dir = Environment.GetEnvironmentVariable("PULSE_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        Exception error = null;
        var th = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current == null)
                {
                    var app = new System.Windows.Application();
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/Pulse;component/Themes/Theme.xaml") });
                }
                var page = new Pulse.Pages.MousePage();
                var t = page.GetType();
                const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                t.GetField("_status", F)!.SetValue(page, JsonSerializer.Deserialize<JsonElement>("""
                    { "name": "WLmouse Beast Max", "connectionType": "Wireless", "connectionDetail": "8K receiver", "batteryPercent": 76, "batteryState": "Discharging",
                      "firmware": ["Mouse 2.11", "Dongle 2.7"], "dpi": 1600, "dpiStages": [400, 800, 1600, 3200, 6400], "activeDpiStage": 2,
                      "pollingRateHz": 4000, "supportedPollingRates": [125, 250, 500, 1000, 2000, 4000, 8000], "liftOffDistance": "Low",
                      "motionSync": true, "angleSnapping": false, "rippleControl": false, "debounceMs": 2, "sleepTimeout": 60, "activeProfile": 1 }
                    """));
                t.GetField("_methods", F)!.SetValue(page, new HashSet<string> { "setDpiStageValue", "setActiveDpiStage", "setPollingRate", "setLiftOffDistance", "setMotionSync", "setAngleSnapping", "setRippleControl", "setDebounceTime", "setSleepTimeout" });
                t.GetMethod("ShowSettings", F)!.Invoke(page, null);
                var host = new System.Windows.Controls.Border { Width = 1100, Height = 1400, Padding = new System.Windows.Thickness(28), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x0C, 0x0F)), Child = page };
                host.Measure(new System.Windows.Size(1100, 1400)); host.Arrange(new System.Windows.Rect(0, 0, 1100, 1400)); host.UpdateLayout();
                void Show(System.Windows.DependencyObject d)
                {
                    if (d is System.Windows.FrameworkElement fe && Pulse.Ui.Anim.GetReveal(fe)) { fe.BeginAnimation(System.Windows.UIElement.OpacityProperty, null); fe.Opacity = 1; }
                    for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++) Show(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
                }
                Show(host); host.UpdateLayout();
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(1100, 1400, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(host);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using var fs = File.Create(Path.Combine(dir, "mouse.png"));
                enc.Save(fs);
            }
            catch (Exception ex) { error = ex; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start(); th.Join();
        if (error != null) throw new Exception("Screenshot failed", error);
    }
}
