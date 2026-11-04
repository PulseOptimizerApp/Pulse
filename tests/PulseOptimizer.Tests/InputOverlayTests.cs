using Pulse.Core.Clips;
using Pulse.Core.Games;
using Xunit;

namespace PulseOptimizer.Tests;

public class InputOverlayTests
{
    [Fact]
    public void DualSense_usb_report_maps_to_the_shared_buttons()
    {
        var r = new byte[64];
        r[0] = 0x01; r[1] = 255; r[2] = 128; r[3] = 128; r[4] = 0; r[5] = 255; r[6] = 0;
        r[8] = 0x20 | 0x02;   // cross + d-pad right
        r[9] = 0x01 | 0x20;   // L1 + options
        r[10] = 0x02;         // touchpad
        Assert.True(InputCapture.ParsePlayStation(r, dualSense: true, out var pad, out var sticks, out var trig));
        var p = (PadButton)pad;
        Assert.True(p.HasFlag(PadButton.South));
        Assert.True(p.HasFlag(PadButton.Right));
        Assert.True(p.HasFlag(PadButton.LeftBumper));
        Assert.True(p.HasFlag(PadButton.Start));
        Assert.True(p.HasFlag(PadButton.Touchpad));
        Assert.False(p.HasFlag(PadButton.Up));
        Assert.Equal(100, sticks[0]);   // left stick full right
        Assert.Equal(100, sticks[3]);   // right stick full up
        Assert.Equal(100, trig[0]);
    }

    [Fact]
    public void DualShock4_report_uses_its_own_layout()
    {
        var r = new byte[64];
        r[0] = 0x01; r[1] = 128; r[2] = 128; r[3] = 128; r[4] = 128;
        r[5] = 0x08 | 0x80;   // no d-pad (8) + triangle
        r[8] = 128;           // L2 half
        Assert.True(InputCapture.ParsePlayStation(r, dualSense: false, out var pad, out _, out var trig));
        Assert.Equal(PadButton.North, (PadButton)pad);
        Assert.InRange(trig[0], 49, 51);
    }

    [Fact]
    public void Xbox_buttons_map_to_the_shared_buttons()
    {
        var p = (PadButton)InputCapture.MapXInput(0x1000 | 0x0100 | 0x0001);
        Assert.Equal(PadButton.South | PadButton.LeftBumper | PadButton.Up, p);
    }

    [Fact]
    public void Timeline_finds_what_was_held_and_slices_from_zero()
    {
        var t = new InputTimeline
        {
            Frames =
            {
                new InputFrame { T = 10, Keys = new[] { 0x57 } },
                new InputFrame { T = 11, Keys = new[] { 0x57, 0x20 } },
                new InputFrame { T = 12, Mouse = (int)MouseButton.Left },
            },
        };
        Assert.Null(t.At(9));
        Assert.Equal(new[] { 0x57, 0x20 }, t.At(11.5).Keys);
        var s = t.Slice(10.5, 12.5);
        Assert.Equal(0, s.Frames[0].T);                 // W was already held when the clip starts
        Assert.Equal(new[] { 0x57 }, s.Frames[0].Keys);
        Assert.Equal(new[] { 0.5, 1.5 }, s.Frames.Skip(1).Select(f => f.T));
        Assert.Equal("kbm", t.MostUsed());
    }

    [Fact]
    public void Only_overlay_keys_are_ever_kept_and_sides_are_merged()
    {
        Assert.Equal(0x10, InputTimeline.Normalize(0xA1));
        Assert.Contains(0x57, InputTimeline.OverlayKeys);
        Assert.DoesNotContain(0x4C, InputTimeline.OverlayKeys); // L: not on the overlay, never recorded
    }

    [Fact]
    public void Burned_overlay_goes_in_the_corner_before_scaling()
    {
        var burn = new InputBurn("dir", "list.txt");
        Assert.Contains("-f concat -safe 0 -i \"list.txt\"", ClipLibrary.OverlayInput(burn));
        var v = ClipLibrary.VideoArgs(burn, 720);
        Assert.Contains("[0:v:0]" + ClipLibrary.SquarePixels + "[base];[base][1:v]overlay=", v);
        Assert.Contains(",scale=-2:720,format=yuv420p[vout]", v);
        Assert.Equal("-map 0:v:0 -vf " + ClipLibrary.SquarePixels + ",scale=-2:720", ClipLibrary.VideoArgs(null, 720));
    }

    [Fact]
    public void Live_fps_uses_the_last_second_and_1_percent_low_the_worst_frames()
    {
        var frames = new List<(double, double)>();
        for (int i = 0; i < 600; i++) frames.Add((i / 60.0, i == 300 ? 50 : 1000 / 60.0));
        var (fps, low, _) = LiveFrames.Compute(frames, 600 / 60.0);
        Assert.InRange(fps, 59, 61);
        Assert.InRange(low, 20, 60);
        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", GamePowerPlan.Guid("Power Scheme GUID: 8C5E7FDA-E8BF-4A96-9A85-A6E23A8C635C  (High performance)"));
    }
}

public class StutterTests
{
    static List<double> Frames(int count, Func<int, double> ms) => Enumerable.Range(0, count).Select(ms).ToList();

    [Fact]
    public void Smooth_frames_have_no_hitches()
    {
        var f = Frames(3000, _ => 1000 / 144.0);
        Assert.Empty(Pulse.Core.Measurement.StutterCheck.FindHitches(f));
        Assert.StartsWith("Smooth", Pulse.Core.Measurement.StutterCheck.Explain(new(), 30, new()).Verdict);
    }

    [Fact]
    public void Evenly_spaced_hitches_point_at_something_on_a_timer()
    {
        // 144 FPS with a 60 ms skip every 5 seconds.
        var f = Frames(144 * 30, i => i % 720 == 700 ? 60 : 1000 / 144.0);
        var h = Pulse.Core.Measurement.StutterCheck.FindHitches(f);
        Assert.InRange(h.Count, 5, 7);
        Assert.Contains("every 5 seconds", Pulse.Core.Measurement.StutterCheck.Explain(h, 30, new()).Verdict);
    }

    [Fact]
    public void A_background_app_busy_at_the_hitches_is_named()
    {
        var hitches = new List<Pulse.Core.Measurement.Hitch> { new(3.2, 80), new(11.5, 70), new(19.1, 90), new(27.6, 75) };
        var cpu = new double[32];
        foreach (var s in new[] { 3, 11, 19, 27 }) cpu[s] = 90;
        var (verdict, _) = Pulse.Core.Measurement.StutterCheck.Explain(hitches, 30, new() { ["MsMpEng"] = cpu, ["explorer"] = new double[32] });
        Assert.Contains("MsMpEng", verdict);
    }
}

public class UpgradeAdvisorTests
{
    static Pulse.Core.Finding F(string id, Pulse.Core.Severity s = Pulse.Core.Severity.Problem) => new() { Id = id, Title = "t", Detail = "d", Severity = s };

    [Fact]
    public void Single_channel_ram_is_the_first_pick()
    {
        var f = Pulse.Core.PcDiagnostics.UpgradeAdvice(new[] { F("ram-channels"), F("games-hdd") }, 8, 60);
        Assert.Contains("RAM stick", f.Title);
    }

    [Fact]
    public void Nothing_wrong_means_no_upgrade_is_pushed()
    {
        var f = Pulse.Core.PcDiagnostics.UpgradeAdvice(new[] { F("mon-ok:DISPLAY1", Pulse.Core.Severity.Ok) }, 32, 165);
        Assert.Contains("No clear upgrade", f.Title);
        Assert.Contains("monitor", Pulse.Core.PcDiagnostics.UpgradeAdvice(new Pulse.Core.Finding[0], 32, 60).Title);
    }
}
