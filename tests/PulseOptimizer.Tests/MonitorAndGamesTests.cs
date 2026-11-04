using Pulse.Core;
using Pulse.Core.Games;
using Xunit;

namespace PulseOptimizer.Tests;

public class MonitorAndGamesTests
{
    [Fact]
    public void Monitor_interface_name_maps_to_wmi_instance()
    {
        var key = Displays.WmiKeyFromInterface(@"\\?\DISPLAY#GSM5B7F#5&1a2b3c&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}");
        Assert.Equal(@"DISPLAY\GSM5B7F\5&1A2B3C&0&UID4352", key);
        Assert.StartsWith(key, @"DISPLAY\GSM5B7F\5&1a2b3c&0&UID4352_0".ToUpperInvariant());
        Assert.Equal("", Displays.WmiKeyFromInterface(""));
    }

    [Theory]
    [InlineData(5L, "HDMI", false)]
    [InlineData(10L, "DisplayPort", false)]
    [InlineData(11L, "Built-in screen", true)]
    [InlineData(2147483648L, "Built-in screen", true)]
    [InlineData(0L, "VGA", false)]
    public void Connection_types_use_windows_values(long tech, string name, bool builtIn)
    {
        Assert.Equal(name, Displays.ConnectionName(tech, out var b));
        Assert.Equal(builtIn, b);
    }

    static MonitorInfo Monitor(int curHz, params (int w, int h, int hz)[] modes) => new()
    {
        DeviceName = @"\\.\DISPLAY1", AdapterName = "NVIDIA GeForce RTX 4070", IsPrimary = true,
        Current = new ModeInfo(2560, 1440, curHz),
        Available = modes.Select(m => new ModeInfo(m.w, m.h, m.hz)).ToList(),
    };

    [Fact]
    public void Max_refresh_is_read_at_the_current_resolution()
    {
        var m = Monitor(60, (2560, 1440, 60), (2560, 1440, 165), (1920, 1080, 240));
        Assert.Equal(165, m.MaxHzAtCurrent);
        Assert.Equal(2560, m.Native.Width);
    }

    static byte[] Edid(int w, int h)
    {
        var e = new byte[128];
        new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 }.CopyTo(e, 0);
        e[54] = 0x02; e[55] = 0x3A;                       // pixel clock (non-zero = timing descriptor)
        e[56] = (byte)(w & 0xFF); e[58] = (byte)((w >> 8) << 4);
        e[59] = (byte)(h & 0xFF); e[61] = (byte)((h >> 8) << 4);
        return e;
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    public void Edid_preferred_timing_gives_the_panel_resolution(int w, int h) =>
        Assert.Equal((w, h), Displays.PreferredResolution(Edid(w, h)));

    [Fact]
    public void Bad_edid_gives_no_native_resolution()
    {
        Assert.Null(Displays.PreferredResolution(null));
        Assert.Null(Displays.PreferredResolution(new byte[128]));
    }

    [Fact]
    public void A_1080p_monitor_with_a_4K_DSR_mode_is_still_1080p()
    {
        // The reported case: an Acer XV240 F6 (1080p) whose mode list also has 3840×2160 at 60 Hz (DSR/VSR or downscaling).
        var m = new MonitorInfo
        {
            DeviceName = @"\\.\DISPLAY1", Current = new ModeInfo(1920, 1080, 180),
            Available = { new(1920, 1080, 180), new(1920, 1080, 60), new(3840, 2160, 60) },
            EdidNative = Displays.PreferredResolution(Edid(1920, 1080)),
        };
        Assert.True(m.NativeKnown);
        Assert.Equal(new ModeInfo(1920, 1080, 180), m.Native);
    }

    [Fact]
    public void Without_edid_native_is_not_trusted()
    {
        var m = new MonitorInfo { DeviceName = @"\\.\DISPLAY1", Current = new ModeInfo(1920, 1080, 60), Available = { new(3840, 2160, 60) } };
        Assert.False(m.NativeKnown);
    }

    static readonly HardwareFacts Nvidia = new() { Gpu = GpuVendor.Nvidia, Board = BoardMaker.Msi, Windows11 = true };
    static readonly HardwareFacts Amd = new() { Gpu = GpuVendor.Amd, Board = BoardMaker.Asus, AmdCpu = true, Windows11 = false };

    [Theory]
    [InlineData("mon-refresh:DISPLAY1")]
    [InlineData("mon-cable:DISPLAY1")]
    [InlineData("mon-res:DISPLAY1")]
    [InlineData("mon-vrr:DISPLAY1")]
    [InlineData("monitor-port")]
    [InlineData("ram")]
    [InlineData("ram-channels")]
    [InlineData("cpu-throttle")]
    [InlineData("dpc")]
    [InlineData("gpu-driver-NVIDIA GeForce RTX 4070")]
    [InlineData("space")]
    [InlineData("startup")]
    [InlineData("defender")]
    [InlineData("power")]
    [InlineData("games-hdd")]
    [InlineData("network")]
    [InlineData("laptop-power")]
    [InlineData("laptop-gpu")]
    [InlineData("rebar")]
    [InlineData("windowed-opt")]
    public void Every_problem_gets_numbered_steps(string id)
    {
        foreach (var hw in new[] { Nvidia, Amd })
        {
            var f = new Finding { Id = id, Title = "t", Detail = "d", Severity = Severity.Problem };
            f.Facts["hz"] = "165";
            FixGuides.Attach(f, hw);
            Assert.True(f.Steps.Count >= 2, $"{id} has {f.Steps.Count} steps");
            Assert.All(f.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s)));
        }
    }

    [Fact]
    public void Healthy_findings_get_no_steps()
    {
        var f = new Finding { Id = "ram", Title = "t", Detail = "d", Severity = Severity.Ok };
        FixGuides.Attach(f, Nvidia);
        Assert.Empty(f.Steps);
    }

    [Fact]
    public void Vrr_steps_work_out_the_frame_cap()
    {
        var f = new Finding { Id = "mon-vrr:DISPLAY1", Title = "t", Detail = "d", Severity = Severity.Check };
        f.Facts["hz"] = "144";
        FixGuides.Attach(f, Amd);
        Assert.Contains(f.Steps, s => s.Contains("141 FPS"));
    }

    [Fact]
    public void Reflex_games_get_reflex_on_nvidia_and_driver_option_otherwise()
    {
        var mon = Monitor(165, (2560, 1440, 165));
        var valorant = GameCatalog.All.First(g => g.Id == "valorant");
        var roblox = GameCatalog.All.First(g => g.Id == "roblox");
        Assert.Contains(LatencyPlan.Build(valorant, Nvidia, mon), s => s.Id == "reflex");
        Assert.Contains(LatencyPlan.Build(roblox, Nvidia, mon), s => s.Id == "ullm");
        Assert.Contains(LatencyPlan.Build(roblox, Amd, mon), s => s.Id == "antilag");
        var cs2 = GameCatalog.All.First(g => g.Id == "cs2");
        Assert.Contains(LatencyPlan.Build(cs2, Amd, mon), s => s.Id == "antilag2");
    }

    [Fact]
    public void Refresh_step_is_automatic_when_already_at_max()
    {
        var atMax = LatencyPlan.Build(GameCatalog.Generic, Nvidia, Monitor(165, (2560, 1440, 165)));
        Assert.True(atMax.First(s => s.Id == "refresh").Automatic);
        var low = LatencyPlan.Build(GameCatalog.Generic, Nvidia, Monitor(60, (2560, 1440, 60), (2560, 1440, 165)));
        var step = low.First(s => s.Id == "refresh");
        Assert.False(step.Done);
        Assert.Equal(@"refresh:\\.\DISPLAY1|2560|1440|165", step.ActionKey);
    }

    [Fact]
    public void Game_catalog_ids_are_unique_and_complete()
    {
        Assert.Equal(GameCatalog.All.Count, GameCatalog.All.Select(g => g.Id).Distinct().Count());
        Assert.All(GameCatalog.All, g =>
        {
            Assert.NotEmpty(g.Exes);
            Assert.NotEmpty(g.Matches);
            if (g.Reflex || g.AntiLag2) Assert.False(string.IsNullOrWhiteSpace(g.LatencySetting), g.Id);
            Assert.NotEmpty(g.FpsSettings);
            Assert.NotEmpty(g.Genres);
        });
    }

    [Fact]
    public void Catalog_is_exactly_the_supported_games()
    {
        var ids = GameCatalog.All.Select(g => g.Id).ToList();
        foreach (var id in new[] { "valorant", "cs2", "fortnite", "roblox", "overwatch", "rivals", "r6", "apex", "cod", "arcraiders", "rust", "rocketleague",
                                   "thefinals", "pubg", "bf6", "deltaforce", "halo", "hunt", "dota2", "lol", "sf6", "tekken8", "trackmania", "f125" })
            Assert.Contains(id, ids);
        Assert.DoesNotContain(GameCatalog.All, g => g.Name == "Minecraft");
        Assert.All(GameCatalog.All.Where(g => g.Id != "roblox"), g => Assert.False(string.IsNullOrWhiteSpace(g.ProSource), g.Id)); // Roblox has no pro scene; its FPS step covers it
    }

    [Fact]
    public void Every_plan_includes_fps_advice()
    {
        var mon = Monitor(144, (1920, 1080, 144));
        foreach (var g in GameCatalog.All.Append(GameCatalog.Generic))
            Assert.Contains(LatencyPlan.Build(g, Nvidia, mon), s => s.Id == "fps");
    }

    [Theory]
    [InlineData("rust", "Rust", true)]
    [InlineData("rust", "RustDesk", false)]
    [InlineData("rust", "Trusted Platform Module", false)]
    [InlineData("cod", "Call of Duty®", true)]
    [InlineData("r6", "Tom Clancy's Rainbow Six® Siege X", true)]
    [InlineData("apex", "Apex Legends", true)]
    public void Installed_game_names_match_whole_words(string id, string installed, bool expected) =>
        Assert.Equal(expected, GameCatalog.All.First(g => g.Id == id).IsNamed(installed));

    [Fact]
    public void Added_games_get_the_general_checklist_under_their_own_name()
    {
        var g = GameCatalog.Custom(new CustomGame { Name = "Deadlock", Exe = @"C:\Games\Deadlock\game\bin\win64\project8.exe" });
        Assert.True(g.IsCustom && g.IsGeneric && !g.IsAddTile);
        Assert.Equal("custom:deadlock", g.Id);
        Assert.Equal(new[] { "project8.exe" }, g.Exes);
        Assert.Equal("D", g.Short);
        var steps = LatencyPlan.Build(g, Nvidia, Monitor(144, (1920, 1080, 144)));
        Assert.Contains(steps, s => s.Id == "ullm");
        Assert.Contains(steps, s => s.Id == "fps" && s.Title == "Lower the settings that cost the most FPS");
    }
}
