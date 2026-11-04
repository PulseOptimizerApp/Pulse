using Pulse.Core.Roblox;
using Xunit;

namespace PulseOptimizer.Tests;

public class RobloxTests
{
    static List<RobloxOption> With(Dictionary<string, string> choices)
    {
        var o = RobloxBoost.CreateOptions();
        foreach (var x in o) x.Value = choices.GetValueOrDefault(x.Key, "");
        return o;
    }

    [Fact]
    public void Every_option_writes_only_allowlisted_flags()
    {
        foreach (var opt in RobloxBoost.CreateOptions())
            foreach (var c in opt.Choices)
                foreach (var flag in opt.Flags(c.Value).Keys)
                    Assert.Contains(flag, RobloxBoost.Allowlist);
    }

    [Fact]
    public void Roblox_default_writes_no_flags()
    {
        Assert.Empty(RobloxBoost.BuildFlags(With(RobloxBoost.Presets["default"]), new Dictionary<string, string>()));
    }

    [Fact]
    public void Max_fps_preset()
    {
        var f = RobloxBoost.BuildFlags(With(RobloxBoost.Presets["maxfps"]), new Dictionary<string, string>());
        Assert.Equal("True", f["FFlagDebugGraphicsPreferD3D11"]);
        Assert.Equal("1", f["FIntDebugForceMSAASamples"]);
        Assert.Equal("0", f["FIntFRMMaxGrassDistance"]);
        Assert.Equal("True", f["DFFlagTextureQualityOverrideEnabled"]);
        Assert.All(f.Keys, k => Assert.Contains(k, RobloxBoost.Allowlist));
    }

    [Theory]
    [InlineData("FFlagDebugSkyGray", "True", true)]
    [InlineData("FFlagDebugSkyGray", "1", false)]
    [InlineData("DFIntTextureQualityOverride", "2", true)]
    [InlineData("DFIntTextureQualityOverride", "high", false)]
    [InlineData("DFIntTaskSchedulerTargetFps", "9999", false)] // the old FPS unlocker: not on the allowlist
    [InlineData("FFlagUnknownThing", "True", false)]
    public void Custom_flags_are_checked(string name, string value, bool ok) =>
        Assert.Equal(ok, RobloxBoost.IsValidCustom(name, value, out _));

    [Fact]
    public void Custom_flags_that_fail_the_check_are_dropped()
    {
        var f = RobloxBoost.BuildFlags(With(RobloxBoost.Presets["default"]),
            new Dictionary<string, string> { ["DFIntTaskSchedulerTargetFps"] = "9999", ["FFlagDebugSkyGray"] = "True" });
        Assert.Equal(new[] { "FFlagDebugSkyGray" }, f.Keys);
    }

    [Fact]
    public void Frame_cap_is_set_or_added_and_nothing_else_changes()
    {
        var xml = "<roblox><Item class=\"UserGameSettings\" referent=\"RBX1\"><Properties><int name=\"FramerateCap\">-1</int><float name=\"MouseSensitivity\">0.4</float></Properties></Item></roblox>";
        var after = RobloxBoost.SetFramerateCap(xml, RobloxBoost.UncappedValue);
        Assert.Contains("<int name=\"FramerateCap\">9999</int>", after);
        Assert.Contains("<float name=\"MouseSensitivity\">0.4</float>", after);

        var without = "<roblox><Item class=\"UserGameSettings\" referent=\"RBX1\"><Properties><float name=\"MouseSensitivity\">0.4</float></Properties></Item></roblox>";
        Assert.Contains("<int name=\"FramerateCap\">240</int>", RobloxBoost.SetFramerateCap(without, "240"));
    }

    [Fact]
    public void Max_fps_preset_uncaps()
    {
        Assert.Equal(RobloxBoost.UncappedValue, RobloxBoost.Presets["maxfps"]["fps"]);
    }

    // ───────── Finding Roblox / the Play button ─────────

    [Theory]
    [InlineData("\"C:\\Users\\a\\AppData\\Local\\Roblox\\Versions\\version-1\\RobloxPlayerBeta.exe\" %1", "C:\\Users\\a\\AppData\\Local\\Roblox\\Versions\\version-1\\RobloxPlayerBeta.exe")]
    [InlineData("C:\\Program Files (x86)\\Roblox\\Versions\\version-2\\RobloxPlayerBeta.exe %1", "C:\\Program Files (x86)\\Roblox\\Versions\\version-2\\RobloxPlayerBeta.exe")]
    [InlineData("\"C:\\Users\\a\\AppData\\Local\\Bloxstrap\\Bloxstrap.exe\" -player \"%1\"", "C:\\Users\\a\\AppData\\Local\\Bloxstrap\\Bloxstrap.exe")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Finds_the_program_in_a_play_button_command(string cmd, string exe) =>
        Assert.Equal(exe, RobloxBoost.ExeOf(cmd));

    [Fact]
    public void Looks_in_every_place_Roblox_can_be_installed()
    {
        var roots = RobloxBoost.VersionRoots();
        Assert.Contains(roots, r => r.EndsWith(Path.Combine("Roblox", "Versions")) && r.Contains("AppData", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(roots, r => r.Contains("Program Files (x86)", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(roots, r => r.EndsWith(Path.Combine("Bloxstrap", "Versions")));
        Assert.Equal(roots.Count, roots.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ───────── NVIDIA (Roblox profile) ─────────

    [Fact]
    public void Nvidia_rows_have_unique_keys_and_their_choices_write_their_own_setting()
    {
        var rows = NvidiaCatalog.CreateOptions(165);
        Assert.Equal(rows.Count, rows.Select(r => r.Key).Distinct().Count());
        foreach (var r in rows)
        {
            Assert.Equal("", r.Choices[0].Value); // "Don't change" always comes first and writes nothing
            Assert.Empty(r.Writes(""));
            foreach (var c in r.Choices.Skip(1))
                Assert.Contains(r.Writes(c.Value), w => w.Id == r.Id);
        }
    }

    [Fact]
    public void Nvidia_presets_only_use_real_choices()
    {
        var rows = NvidiaCatalog.CreateOptions(144);
        foreach (var preset in NvidiaCatalog.Presets.Values)
            foreach (var (key, value) in preset)
                Assert.Contains(rows.Single(r => r.Key == key).Choices, c => c.Value == value);
    }

    [Fact]
    public void Nvidia_frame_cap_sits_just_under_the_refresh_rate()
    {
        var frl = NvidiaCatalog.CreateOptions(165).Single(r => r.Key == "nv-frl");
        Assert.Contains(frl.Choices, c => c.Value == "162");
        var slow = NvidiaCatalog.CreateOptions(60).Single(r => r.Key == "nv-frl");
        Assert.DoesNotContain(slow.Choices, c => c.Value == "57");
    }

    [Fact]
    public void Ultra_low_latency_writes_all_three_driver_settings()
    {
        var rows = NvidiaCatalog.CreateOptions(144);
        rows.Single(r => r.Key == "nv-latency").Value = "ultra";
        var s = NvidiaCatalog.Build(rows, new Dictionary<string, string>());
        Assert.Equal(1u, s.Single(x => x.Id == NvidiaCatalog.ULL_ENABLED).Value);
        Assert.Equal(2u, s.Single(x => x.Id == NvidiaCatalog.ULL_CPL_STATE).Value);
        Assert.Equal(1u, s.Single(x => x.Id == NvidiaCatalog.PRERENDERLIMIT).Value);
    }

    [Theory]
    [InlineData("0x10835002", "0x00000090", true, 0x10835002u, 0x90u)]
    [InlineData("10835002", "144", true, 0x10835002u, 144u)]
    [InlineData("0x0", "1", false, 0u, 0u)]
    [InlineData("hello", "1", false, 0u, 0u)]
    [InlineData("0x123456789", "1", false, 0u, 0u)]
    [InlineData("0x10835002", "-1", false, 0u, 0u)]
    [InlineData("0x10835002", "0xZZ", false, 0u, 0u)]
    public void Advanced_nvidia_settings_are_checked(string id, string value, bool ok, uint wantId, uint wantValue)
    {
        Assert.Equal(ok, NvidiaCatalog.TryParseCustom(id, value, out var gotId, out var gotValue, out _));
        if (ok) { Assert.Equal(wantId, gotId); Assert.Equal(wantValue, gotValue); }
    }

    [Fact]
    public void An_advanced_setting_overrides_a_row_for_the_same_id()
    {
        var rows = NvidiaCatalog.CreateOptions(144);
        rows.Single(r => r.Key == "nv-power").Value = "1";
        var s = NvidiaCatalog.Build(rows, new Dictionary<string, string> { [$"0x{NvidiaCatalog.PREFERRED_PSTATE:X8}"] = "0x00000000" });
        Assert.Equal(0u, s.Single(x => x.Id == NvidiaCatalog.PREFERRED_PSTATE).Value);
    }

    [Fact]
    public void Now_text_names_the_choice_or_shows_the_raw_value()
    {
        var rows = NvidiaCatalog.CreateOptions(144);
        NvidiaCatalog.ShowCurrent(rows, new Dictionary<uint, NvCurrent>
        {
            [NvidiaCatalog.VSYNCMODE] = new(true, NvidiaCatalog.VSYNC_OFF),
            [NvidiaCatalog.THREAD_CONTROL] = new(true, 7),
        });
        Assert.Equal("Now: Off", rows.Single(r => r.Key == "nv-vsync").Current);
        Assert.Equal("Now: 0x00000007", rows.Single(r => r.Key == "nv-threads").Current);
        Assert.Equal("Now: driver default", rows.Single(r => r.Key == "nv-power").Current);
    }

    // ───────── NVIDIA Profile Inspector files (.nip) ─────────

    const string Nip = """
        <?xml version="1.0" encoding="utf-16"?>
        <ArrayOfProfile>
          <Profile>
            <ProfileName>Base Profile</ProfileName>
            <Executeables />
            <Settings>
              <ProfileSetting><SettingNameInfo> </SettingNameInfo><SettingID>390467</SettingID><SettingValue>2</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
              <ProfileSetting><SettingNameInfo>Vertical Sync</SettingNameInfo><SettingID>11041231</SettingID><SettingValue>138504007</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
              <ProfileSetting><SettingNameInfo>Texture filtering - Negative LOD bias</SettingNameInfo><SettingID>1686376</SettingID><SettingValue>1</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
              <ProfileSetting><SettingNameInfo>SLI rendering mode</SettingNameInfo><SettingID>271830737</SettingID><SettingValue>0</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
              <ProfileSetting><SettingNameInfo>Enable application for Optimus</SettingNameInfo><SettingID>284810369</SettingID><SettingValue>1</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
              <ProfileSetting><SettingNameInfo>Enable G-SYNC globally</SettingNameInfo><SettingID>294973784</SettingID><SettingValue>0</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
              <ProfileSetting><SettingNameInfo>List of Universal GPU ids</SettingNameInfo><SettingID>271929336</SettingID><SettingValue>none</SettingValue><ValueType>String</ValueType></ProfileSetting>
              <ProfileSetting><SettingNameInfo /><SettingID>541081465</SettingID><SettingValue>119</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
            </Settings>
          </Profile>
        </ArrayOfProfile>
        """;

    [Fact]
    public void Reads_a_profile_inspector_file_and_keeps_only_what_fits_one_game()
    {
        var r = NipFile.Parse(Nip);
        Assert.Equal("Base Profile", r.ProfileName);
        Assert.False(r.ForRoblox);
        Assert.Equal(new uint[] { NvidiaCatalog.ULL_CPL_STATE, NvidiaCatalog.VSYNCMODE, 1686376 }, r.Use.Select(x => x.Id));
        Assert.Equal("Low Latency Mode", r.Use[0].Name);          // blank in the file, named by Pulse
        Assert.Equal(NvidiaCatalog.VSYNC_OFF, r.Use[1].Value);
        Assert.Equal(2, r.SkippedHardware);                       // SLI, Optimus
        Assert.Equal(1, r.SkippedWholePc);                        // G-SYNC globally
        Assert.Equal(1, r.SkippedText);                           // a text value
        Assert.Equal(1, r.SkippedUnknown);                        // no name
    }

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("<Something />")]
    [InlineData("<ArrayOfProfile />")]
    public void A_file_that_isnt_a_profile_is_refused(string text) =>
        Assert.Throws<FormatException>(() => NipFile.Parse(text));

    [Fact]
    public void A_row_you_pick_wins_over_the_imported_file()
    {
        var rows = NvidiaCatalog.CreateOptions(144);
        rows.Single(r => r.Key == "nv-vsync").Value = NvidiaCatalog.VSYNC_ON.ToString();
        var import = new NvImport { Settings = NipFile.Parse(Nip).Use };
        var s = NvidiaCatalog.Build(rows, new Dictionary<string, string>(), import);
        Assert.Equal(NvidiaCatalog.VSYNC_ON, s.Single(x => x.Id == NvidiaCatalog.VSYNCMODE).Value);
        Assert.Contains(s, x => x.Id == 1686376);
    }
}
