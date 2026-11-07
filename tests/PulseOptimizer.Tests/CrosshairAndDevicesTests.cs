using Pulse.Core.Crosshair;
using Pulse.Core.Devices;
using Xunit;

namespace PulseOptimizer.Tests;

public class CrosshairCodeTests
{
    [Fact]
    public void Reads_a_classic_valorant_code()
    {
        var s = ValorantCode.Parse("0;s;1;P;c;5;h;0;m;1;0l;4;0o;2;0a;1;0f;0;1b;0;S;c;4;o;1");
        Assert.NotNull(s);
        Assert.Equal("#00FFFF", s.Color);   // 5 = cyan
        Assert.False(s.Outlines);           // h;0
        Assert.Equal(4, s.Inner.Length);
        Assert.Equal(2, s.Inner.Offset);
        Assert.Equal(1, s.Inner.Opacity);
        Assert.Equal(2, s.Inner.Thickness); // default
        Assert.False(s.Outer.Show);         // 1b;0
        Assert.Equal(0.5, s.OutlineOpacity); // the sniper section's "o;1" doesn't leak into the primary crosshair
    }

    [Fact]
    public void Missing_values_use_valorants_defaults()
    {
        var s = ValorantCode.Parse("0;P;o;1;d;1;0b;0;1b;0"); // a plain dot
        Assert.True(s.Dot);
        Assert.Equal(2, s.DotSize);
        Assert.Equal("#FFFFFF", s.Color);
        Assert.False(s.Inner.Show);
        Assert.Equal(6, s.Inner.Length);
        Assert.Equal(3, s.Inner.Offset);
        Assert.Equal(0.8, s.Inner.Opacity);
        Assert.Equal(10, s.Outer.Offset);
        Assert.Equal(0.35, s.Outer.Opacity);
    }

    [Fact]
    public void Custom_colors_and_separate_vertical_length()
    {
        var s = ValorantCode.Parse("0;P;c;8;u;FFFFCCFF;h;0;b;1;f;0;0l;4;0v;8;0g;1;0o;0;0a;1;0f;0;1b;0");
        Assert.Equal("#FFFFCC", s.Color);
        Assert.Equal(8, s.Inner.VLength);
        var plain = ValorantCode.Parse("0;P;0l;4;0v;8;0o;0");
        Assert.Equal(4, plain.Inner.VLength); // "0v" only counts with "0g;1"
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("1;P;c;5")]
    public void Not_a_code(string code) => Assert.Null(ValorantCode.Parse(code));

    [Fact]
    public void Writing_then_reading_gives_the_same_crosshair()
    {
        foreach (var (name, style) in CrosshairSettings.Presets)
        {
            var back = ValorantCode.Parse(ValorantCode.Write(style));
            Assert.True(back != null, name);
            Assert.Equal(style.Color, back.Color, ignoreCase: true);
            Assert.Equal(style.Outlines, back.Outlines);
            Assert.Equal(style.Dot, back.Dot);
            Assert.Equal(style.DotSize, back.DotSize);
            foreach (var (a, b) in new[] { (style.Inner, back.Inner), (style.Outer, back.Outer) })
            {
                Assert.Equal(a.Show, b.Show);
                if (!a.Show) continue;
                Assert.Equal(a.Length, b.Length); Assert.Equal(a.Thickness, b.Thickness);
                Assert.Equal(a.Offset, b.Offset); Assert.Equal(a.Opacity, b.Opacity);
            }
        }
        var custom = new CrosshairStyle { Color = "#123456" };
        Assert.Equal("#123456", ValorantCode.Parse(ValorantCode.Write(custom)).Color);
    }
}

public class KeyRemapTests
{
    [Fact]
    public void Scancode_map_has_windows_layout()
    {
        // Caps Lock (0x3A) acts as Left Ctrl (0x1D); left Windows key (E0 5B) turned off.
        var b = KeyRemap.Encode(new[] { new KeyRemap.Map(0x003A, 0x001D), new KeyRemap.Map(0xE05B, 0) });
        Assert.Equal(new byte[]
        {
            0, 0, 0, 0, 0, 0, 0, 0,   // version, flags
            3, 0, 0, 0,               // two remaps + the terminator
            0x1D, 0x00, 0x3A, 0x00,   // to, from
            0x00, 0x00, 0x5B, 0xE0,
            0, 0, 0, 0,               // terminator
        }, b);
    }

    [Fact]
    public void Polling_rates_round_to_the_usual_steps()
    {
        Assert.Equal(1000, MouseMeter.Nearest(987));
        Assert.Equal(125, MouseMeter.Nearest(131));
        Assert.Equal(4000, MouseMeter.Nearest(3700));
    }

    [Fact]
    public void Makers_are_known_by_usb_id()
    {
        Assert.Equal("Logitech", Makers.Name("046d"));
        Assert.Equal("Razer Synapse", Makers.App("1532"));
        Assert.Null(Makers.Name("FFFF"));
    }
}
