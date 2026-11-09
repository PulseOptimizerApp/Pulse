using System.Text.Json;
using Pulse.Core.Mouse;
using Xunit;
using Xunit.Abstractions;

namespace PulseOptimizer.Tests;

/// <summary>
/// The bundled OpenMouse protocol library runs in Pulse's script engine and recognises mice from what Windows reports.
/// Skipped when the bundle isn't built (it's made by tools/get-mouse-protocol.sh in CI).
/// </summary>
[Collection("Mouse engine")]
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
                var foundType = t.GetNestedType("Found", System.Reflection.BindingFlags.NonPublic)!;
                const string Beast = "https://img.openmouse.app/wlmouse-beast-max.png";
                object Found(string name, int pid, string image = null) => Activator.CreateInstance(foundType, "k" + pid, 0x36A7, pid, name, 1.0, image)!;
                var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(foundType))!;
                list.Add(Found("WLMOUSE BEAST MAX 8K RECEIVER", 0xA887, Beast)); list.Add(Found("WLMOUSE 1K RECEIVER", 0xA888)); list.Add(Found("Wooting 60HE+", 0x1312));
                t.GetField("_found", F)!.SetValue(page, list);
                // The Beast Max is connected; its picture is downloaded the way the page does it.
                Task.Run(() => Pulse.Core.Mouse.DeviceArt.Get(Beast)).Wait(TimeSpan.FromSeconds(30));
                Task.Run(() => Pulse.Core.Mouse.DeviceArt.Get("https://img.openmouse.app/logitech-g502-x-plus.png")).Wait(TimeSpan.FromSeconds(30));
                var status = (JsonElement)t.GetField("_status", F)!.GetValue(page)!;
                t.GetField("_openKey", F)!.SetValue(page, "k" + 0xA887);
                ((System.Collections.IDictionary)t.GetField("_known", F)!.GetValue(page)!)["k" + 0xA887] = (status, "WLMouse", Beast);
                // The list, then a device's overview and performance tabs.
                Render(page, dir, "mouse-list.png", () => t.GetMethod("ShowList", F)!.Invoke(page, new object[] { null }));
                t.GetField("_current", F)!.SetValue(page, list[0]);
                Render(page, dir, "mouse.png", () => t.GetMethod("ShowDevice", F)!.Invoke(page, new object[] { null }));
                t.GetField("_tab", F)!.SetValue(page, "performance");
                Render(page, dir, "mouse-performance.png", () => t.GetMethod("ShowDevice", F)!.Invoke(page, new object[] { null }));
            }
            catch (Exception ex) { error = ex; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start(); th.Join();
        if (error != null) throw new Exception("Screenshot failed", error);
    }

    static void Render(Pulse.Pages.MousePage page, string dir, string file, Action show)
    {
        show();
        {
                var host = new System.Windows.Controls.Border { Width = 1100, Height = 1400, Padding = new System.Windows.Thickness(28), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x0C, 0x0F)) };
                if (page.Parent is System.Windows.Controls.Border old) old.Child = null;
                host.Child = page;
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
                using var fs = File.Create(Path.Combine(dir, file));
                enc.Save(fs);
        }
    }
}

/// <summary>
/// Every setting the Mouse page changes, end to end through Pulse's own host: the engine, OpenMouse's WLmouse driver,
/// and the feature reports it sends, answered by a simulated mouse that keeps what it's told (the same rules as
/// OpenMouse's own WLmouse test). Each change is read back from the "mouse", as the page does.
/// </summary>
[Collection("Mouse engine")]
public class MouseSettingsEndToEndTests
{
    readonly ITestOutputHelper _out;
    public MouseSettingsEndToEndTests(ITestOutputHelper o) => _out = o;

    /// <summary>A WLmouse (compx protocol on feature report 0) that stores what's written and answers reads from it.</summary>
    sealed class FakeWlMouse : Hid.ILink
    {
        readonly Dictionary<(int Page, int Cmd), byte[]> _regs = new()
        {
            [(1, 0x01)] = new byte[] { 1, 0x06, 0x40, 0x06, 0x40 },   // one DPI stage: 1600
            [(1, 0x02)] = new byte[] { 1 },                           // active stage 1
            [(1, 0x00)] = new byte[] { 0x01 },                        // 1000 Hz
            [(1, 0x08)] = new byte[] { 0x01 },                        // lift-off: medium
            [(0, 0x08)] = new byte[] { 0 },                           // debounce 0 ms
            [(0, 0x07)] = new byte[] { 0, 60 },                       // sleep 60 s
            [(1, 0x09)] = new byte[] { 0 },                           // motion sync off
        };
        byte[] _last = new byte[64];
        public int Writes;
#pragma warning disable CS0067
        public event Action<byte, byte[]> Input;
#pragma warning restore CS0067
        public Task SendReport(byte reportId, byte[] data) => Task.CompletedTask;
        public Task SendFeature(byte reportId, byte[] data) { Assert.Equal(0, reportId); _last = data; return Task.CompletedTask; }
        public Task<byte[]> GetFeature(byte reportId)
        {
            var r = _last;
            int target = r[2], length = r[3], page = r[4], cmd = r[5];
            byte[] payload;
            if (target == 1 && cmd == 0x8b) payload = new byte[] { 0, 0, 0, 0, 0xa8, 0x63 };
            else if (page == 0 && cmd == 0x85) payload = new byte[] { 1, 0 };
            else if (page == 0 && cmd == 0x83) payload = new byte[] { 0, 76 };
            else if ((cmd & 0x80) == 0) { _regs[(page, cmd)] = r[7..(6 + length)]; Writes++; payload = new byte[] { 1, 1 }; }
            else payload = _regs.TryGetValue((page, cmd & 0x7f), out var v) ? new[] { r[6] }.Concat(v).ToArray() : new byte[] { 1, 1 };
            var reply = new byte[64];
            reply[0] = 0xa1; reply[3] = (byte)payload.Length; reply[4] = (byte)page; reply[5] = (byte)cmd;
            payload.CopyTo(reply, 6);
            return Task.FromResult(reply);
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task Dpi_stages_polling_rate_lift_off_debounce_sleep_and_motion_sync_all_change_on_the_mouse()
    {
        if (MouseHost.Script() == null) { _out.WriteLine("No bundled protocol library in this build: skipped."); return; }
        var info = new Hid.DeviceInfo { Key = "fake-wlmouse", VendorId = 0x36a7, ProductId = 0xa863, ProductName = "WLMOUSE 8K RECEIVER" };
        var c = new Hid.Collection { Path = "fake", UsagePage = 0xff00, Usage = 1, FeatureLength = 65 };
        c.FeatureIds.Add(0);
        info.Collections.Add(c);
        var mouse = new FakeWlMouse();
        var (list, connect) = (MouseHost.ListDevices, MouseHost.Connect);
        MouseHost.ListDevices = () => new() { info };
        MouseHost.Connect = _ => mouse;
        MouseHost.Hold();
        MouseHost.ClearLog();
        try
        {
            var host = MouseHost.Instance!;
            host.Log += line => _out.WriteLine("engine: " + line);
            async Task<JsonElement> Run(string method, params object[] args) =>
                JsonSerializer.Deserialize<JsonElement>(await host.Run(method, args).WaitAsync(TimeSpan.FromSeconds(60)));
            var found = await Run("scan");
            Assert.Equal("fake-wlmouse", found[0].GetProperty("key").GetString());
            var opened = await Run("open", "fake-wlmouse");
            int id = opened.GetProperty("id").GetInt32();
            var options = opened.GetProperty("options");
            Assert.Equal(new[] { 30, 60, 120, 300, 600, 1800 }, options.GetProperty("sleep").EnumerateArray().Select(x => x.GetInt32()));
            Assert.Equal(15, options.GetProperty("debounceMax").GetInt32());

            var s = await Run("status", id);
            Assert.Equal(1600, s.GetProperty("dpi").GetInt32());
            Assert.Equal(1000, s.GetProperty("pollingRateHz").GetInt32());

            async Task Call(string method, params object[] args)
            {
                var r = await host.Run("call", id, method, JsonSerializer.Serialize(args)).WaitAsync(TimeSpan.FromSeconds(60));
                _out.WriteLine($"{method}({string.Join(", ", args)}) -> {r}");
            }
            await Call("setDpi", 800);
            await Call("setDpiStageCount", 3);
            await Call("setDpiStageValue", 2, 3200);
            await Call("setActiveDpiStage", 2);
            await Call("setPollingRate", 500);
            await Call("setPollingRate", 8000);
            await Call("setLiftOffDistance", "Low");
            await Call("setDebounceTime", 4);
            await Call("setSleepTimeout", 120);
            await Call("setMotionSync", true);

            s = await Run("status", id);
            _out.WriteLine(s.ToString());
            Assert.Equal(new[] { 800, 800, 3200 }, s.GetProperty("dpiStages").EnumerateArray().Select(x => x.GetInt32()));
            Assert.Equal(2, s.GetProperty("activeDpiStage").GetInt32());
            Assert.Equal(3200, s.GetProperty("dpi").GetInt32());
            Assert.Equal(8000, s.GetProperty("pollingRateHz").GetInt32());
            Assert.Equal("Low", s.GetProperty("liftOffDistance").GetString());
            Assert.Equal(4, s.GetProperty("debounceMs").GetInt32());
            Assert.Equal(120, s.GetProperty("sleepTimeout").GetInt32());
            Assert.True(s.GetProperty("motionSync").GetBoolean());
            Assert.True(mouse.Writes >= 10);

            // A value the mouse refuses comes back as an error, not a hang.
            await Assert.ThrowsAnyAsync<Exception>(() => host.Run("call", id, "setDebounceTime", "[99]").WaitAsync(TimeSpan.FromSeconds(30)));
            await Run("close", id);
        }
        finally
        {
            _out.WriteLine("── what was said to the device ──");
            foreach (var line in MouseHost.RecentLog()) _out.WriteLine(line);
            MouseHost.ListDevices = list;
            MouseHost.Connect = connect;
            MouseHost.Release();
        }
    }
}
