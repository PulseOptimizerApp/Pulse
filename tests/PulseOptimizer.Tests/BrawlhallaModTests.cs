using Pulse.Core.Games.Brawlhalla;
using Xunit;

namespace PulseOptimizer.Tests;

public class BrawlhallaModTests
{
    /// <summary>Bits, the way Flash files pack them (most significant first).</summary>
    sealed class Bits
    {
        readonly List<byte> _out = new();
        int _acc, _n;
        public Bits U(int v, int n) { for (int i = n - 1; i >= 0; i--) { _acc = (_acc << 1) | ((v >> i) & 1); if (++_n == 8) { _out.Add((byte)_acc); _acc = 0; _n = 0; } } return this; }
        public Bits S(int v, int n) => U(v & ((1 << n) - 1), n);
        public Bits Byte(int v) { Align(); _out.Add((byte)v); return this; }
        public Bits Align() { if (_n > 0) { _out.Add((byte)(_acc << (8 - _n))); _acc = 0; _n = 0; } return this; }
        public byte[] Done() { Align(); return _out.ToArray(); }
    }

    static byte[] Tag(int code, byte[] data)
    {
        var h = data.Length >= 63 ? new byte[] { (byte)(((code << 6) | 63) & 0xFF), (byte)((code << 6 | 63) >> 8) }.Concat(BitConverter.GetBytes(data.Length)) : BitConverter.GetBytes((ushort)((code << 6) | data.Length));
        return h.Concat(data).ToArray();
    }

    /// <summary>A rectangle shape (DefineShape, id) of 200×100 twips (10×5 px) in one solid color, with an edge.</summary>
    static byte[] Shape(int id, int rgb)
    {
        var b = new Bits();
        b.Byte(id & 0xFF).Byte(id >> 8);
        b.U(9, 5).S(0, 9).S(200, 9).S(0, 9).S(100, 9).Align();
        b.Byte(1).Byte(0x00).Byte(rgb >> 16).Byte((rgb >> 8) & 0xFF).Byte(rgb & 0xFF); // one solid fill
        b.Byte(0);                                                                      // no lines
        b.U(1, 4).U(0, 4);
        b.U(0, 1).U(0, 1).U(0, 1).U(0, 1).U(1, 1).U(1, 1).U(2, 5).S(0, 2).S(0, 2).U(1, 1); // move to 0,0 with fill 1
        b.U(1, 1).U(1, 1).U(9 - 2, 4).U(0, 1).U(0, 1).S(200, 9);                         // straight edge →
        b.U(0, 6);                                                                      // end
        return b.Done();
    }

    /// <summary>A sprite (id) that places <paramref name="shapeId"/> at depth 1, with two frames.</summary>
    static byte[] Sprite(int id, int shapeId)
    {
        var inner = Tag(26, new byte[] { 0x02, 1, 0, (byte)shapeId, 0 }).Concat(Tag(1, Array.Empty<byte>())).Concat(Tag(1, Array.Empty<byte>())).Concat(Tag(0, Array.Empty<byte>()));
        return BitConverter.GetBytes((ushort)id).Concat(BitConverter.GetBytes((ushort)2)).Concat(inner).ToArray();
    }

    static byte[] Symbols(params (int Id, string Name)[] list)
    {
        var data = new List<byte>(BitConverter.GetBytes((ushort)list.Length));
        foreach (var (id, name) in list) { data.AddRange(BitConverter.GetBytes((ushort)id)); data.AddRange(System.Text.Encoding.UTF8.GetBytes(name)); data.Add(0); }
        return data.ToArray();
    }

    /// <summary>A small skin file: hair (red) and a sword (blue), each a part with its own shape.</summary>
    internal static byte[] SkinFile()
    {
        var body = new List<byte>();
        body.AddRange(new Bits().U(5, 5).S(0, 5).S(10, 5).S(0, 5).S(10, 5).Done()); // stage rect
        body.AddRange(new byte[] { 0, 24, 1, 0 });                                   // 24 fps, 1 frame
        body.AddRange(Tag(2, Shape(1, 0xFF0000)));
        body.AddRange(Tag(39, Sprite(2, 1)));
        body.AddRange(Tag(2, Shape(3, 0x0000FF)));
        body.AddRange(Tag(39, Sprite(4, 3)));
        body.AddRange(Tag(76, Symbols((2, "a_Hair_Test"), (4, "a_WeaponSword_Test"))));
        body.AddRange(Tag(1, Array.Empty<byte>()));
        body.AddRange(Tag(0, Array.Empty<byte>()));
        var file = new List<byte> { (byte)'F', (byte)'W', (byte)'S', 10 };
        file.AddRange(BitConverter.GetBytes(body.Count + 8));
        file.AddRange(body);
        return file.ToArray();
    }

    [Fact]
    public void Reads_the_parts_a_skin_names_with_their_sizes()
    {
        var swf = SwfFile.Load(SkinFile());
        var parts = swf.Parts();
        Assert.Equal(new[] { "a_Hair_Test", "a_WeaponSword_Test" }, parts.Select(p => p.Name));
        Assert.Equal(10, parts[0].Width, 3);
        Assert.Equal(5, parts[0].Height, 3);
    }

    [Fact]
    public void Colors_change_only_in_the_parts_asked_for()
    {
        var swf = SwfFile.Load(SkinFile());
        Assert.Equal(new[] { 0xFF0000 }, swf.Palette(new[] { "a_Hair_Test" }).Select(p => p.Rgb));
        Assert.Equal(1, swf.Recolor(_ => 0x00FF00, new[] { "a_Hair_Test" }));
        var again = SwfFile.Load(swf.Save());
        Assert.Equal(new[] { 0x00FF00 }, again.Palette(new[] { "a_Hair_Test" }).Select(p => p.Rgb));
        Assert.Equal(new[] { 0x0000FF }, again.Palette(new[] { "a_WeaponSword_Test" }).Select(p => p.Rgb));
    }

    [Fact]
    public void A_picture_takes_a_parts_place_and_the_file_still_reads()
    {
        var swf = SwfFile.Load(SkinFile());
        var px = Enumerable.Repeat(new byte[] { 255, 128, 0, 255 }, 4 * 4).SelectMany(x => x).ToArray();
        Assert.Equal(1, swf.ReplaceParts(new Dictionary<string, SwfImage> { ["a_Hair_Test"] = new(4, 4, px) }));
        var again = SwfFile.Load(swf.Save());
        var parts = again.Parts();
        Assert.Equal(2, parts.Count);
        var hair = parts.Single(p => p.Name == "a_Hair_Test");
        // A square picture in a 10×5 space keeps its shape: 5×5, centred.
        Assert.Equal(5, hair.Width, 1);
        Assert.Equal(5, hair.Height, 1);
        // The picture has no vector colors; the sword is untouched.
        Assert.Empty(again.Palette(new[] { "a_Hair_Test" }));
        Assert.Equal(new[] { 0x0000FF }, again.Palette(new[] { "a_WeaponSword_Test" }).Select(p => p.Rgb));
    }

    [Fact]
    public void Stretched_pictures_fill_the_part()
    {
        var swf = SwfFile.Load(SkinFile());
        var px = new byte[2 * 2 * 4];
        swf.ReplaceParts(new Dictionary<string, SwfImage> { ["a_WeaponSword_Test"] = new(2, 2, px) }, stretch: true);
        var sword = SwfFile.Load(swf.Save()).Parts().Single(p => p.Name == "a_WeaponSword_Test");
        Assert.Equal(10, sword.Width, 1);
        Assert.Equal(5, sword.Height, 1);
    }

    [Fact]
    public void Unknown_parts_and_bad_files_are_refused_cleanly()
    {
        var swf = SwfFile.Load(SkinFile());
        Assert.Equal(0, swf.ReplaceParts(new Dictionary<string, SwfImage> { ["a_Nope"] = new(1, 1, new byte[4]) }));
        Assert.Throws<InvalidDataException>(() => SwfFile.Load(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
    }

    [Fact]
    public void Colors_turn_round_the_wheel_and_outlines_stay_black()
    {
        var map = BrawlhallaMods.ColorMap(new BhColors { Hue = 120 });
        Assert.Equal(0x000000, map(0x000000));
        Assert.Equal(0x00FF00, map(0xFF0000));
        var picks = BrawlhallaMods.ColorMap(new BhColors { Map = { ["FF0000"] = "123456" }, Hue = 120 });
        Assert.Equal(0x123456, picks(0xFF0000));
        Assert.Equal(0x0000FF, picks(0x00FF00));
        Assert.True(new BhColors().IsOriginal);
    }

    [Fact]
    public void Tinting_keeps_white_highlights()
    {
        Assert.Equal(0xFFFFFF, BrawlhallaMods.Tint(0xFFFFFF, 0xFF0000));
        var (h, s, _) = BrawlhallaMods.ToHsl(BrawlhallaMods.Tint(0x3366AA, 0xFF0000));
        Assert.Equal(0, h, 2);
        Assert.True(s > 0.9);
    }

    [Theory]
    [InlineData("a_WeaponSpearBend1Back_Mikasa", "Spear", "Weapons")]
    [InlineData("a_SpearShaftSegement_Mikasa", "Spear", "Weapons")]
    [InlineData("a_EyesAngry_Zuko", "Eyes", "Head")]
    [InlineData("a_HairBack_Zuko", "Hair", "Head")]
    [InlineData("a_Torso1_Bear", "Torso", "Body")]
    [InlineData("a_WeaponHammerShort_Sandy", "Hammer", "Weapons")]
    [InlineData("a_WeaponKatarBladeTop_Mikasa", "Katar", "Weapons")]
    public void Parts_group_the_way_people_think_of_them(string part, string group, string kind)
    {
        Assert.Equal(group, BrawlhallaMods.PartGroup(part));
        Assert.Equal(kind, BrawlhallaMods.GroupKind(BrawlhallaMods.PartGroup(part)));
    }

    [Theory]
    [InlineData("BG_MiamiDome_Anim.jpg", "Miami Dome (animated)")]
    [InlineData("BG_Grove.jpg", "Grove")]
    [InlineData("Refinery-map.jpg", "Refinery map")]
    public void Backgrounds_get_readable_names(string file, string label) =>
        Assert.Equal(label, BrawlhallaMods.BackgroundLabel(file));

    [Fact]
    public void A_part_draws_as_its_own_shapes_and_colors()
    {
        var swf = SwfFile.Load(SkinFile());
        var d = swf.Draw("a_Hair_Test");
        Assert.NotNull(d);
        var item = Assert.Single(d.Value.Items);
        Assert.Equal(0xFFFF0000u, item.Argb);
        Assert.Equal(0, item.LineWidth);
        Assert.Equal(200, d.Value.X1 - d.Value.X0);
        Assert.Null(swf.Draw("a_Nope"));
    }

    [Fact]
    public void A_part_renders_to_a_png_sized_four_times_its_game_size()
    {
        System.Windows.Media.Imaging.BitmapSource bmp = null;
        Exception error = null;
        var t = new Thread(() => { try { bmp = BrawlhallaMods.RenderPart(SwfFile.Load(SkinFile()), "a_WeaponSword_Test"); } catch (Exception e) { error = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        Assert.Null(error);
        Assert.Equal(40, bmp.PixelWidth);
        Assert.Equal(20, bmp.PixelHeight);
    }

    [Theory]
    [InlineData("dash", "Movement")]
    [InlineData("hit", "Hits and KOs")]
    [InlineData("legend", "Legends and crossovers")]
    [InlineData("attack-Sword", "Weapons")]
    public void Effects_sit_where_people_look_for_them(string key, string group) =>
        Assert.Equal(group, BrawlhallaMods.EffectGroup(key));

    [Fact]
    public void Part_labels_are_readable() =>
        Assert.Equal("Weapon Spear Bend 1 Back", BrawlhallaMods.PartLabel("a_WeaponSpearBend1Back_Mikasa"));
}
