using Pulse.Core.Games;
using Xunit;

namespace PulseOptimizer.Tests;

public class GameConfigTests
{
    [Fact]
    public void Never_touches_personal_settings()
    {
        foreach (var p in GameConfigs.All)
            foreach (var e in p.Edits)
                Assert.DoesNotContain(GameConfigs.Forbidden, f => e.Key.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_profile_is_for_a_known_game()
    {
        foreach (var p in GameConfigs.All) Assert.Contains(GameCatalog.All, g => g.Id == p.GameId);
    }

    const string Cs2 = "\"video.cfg\"\n{\n\t\"Version\"\t\t\"14\"\n\t\"setting.fullscreen\"\t\t\"0\"\n\t\"setting.mat_vsync\"\t\t\"1\"\n\t\"setting.shaderquality\"\t\t\"0\"\n\t\"setting.sensitivity_like\"\t\t\"2.5\"\n}\n";

    [Fact]
    public void Valve_files_change_only_listed_keys_that_exist()
    {
        var p = GameConfigs.For("cs2");
        var plan = p.Plan(Cs2);
        Assert.Equal(new[] { "setting.fullscreen", "setting.mat_vsync" }, plan.Select(c => c.Key)); // shaderquality already 0; others absent
        var after = p.Apply(Cs2, plan);
        Assert.Contains("\"setting.fullscreen\"\t\t\"1\"", after);
        Assert.Contains("\"setting.mat_vsync\"\t\t\"0\"", after);
        Assert.Contains("\"setting.sensitivity_like\"\t\t\"2.5\"", after);
        Assert.Equal(Cs2.Length, after.Length); // layout kept
        Assert.Empty(p.Plan(after));            // idempotent
    }

    const string Ue = "[ScalabilityGroups]\r\nsg.ResolutionQuality=75\r\nsg.ViewDistanceQuality=3\r\nsg.TextureQuality=2\r\n\r\n[/Script/FortniteGame.FortGameUserSettings]\r\nbUseVSync=True\r\nMouseX=0.08\r\n\r\n[D3DRHIPreference]\r\nPreferredRHI=dx12\r\nPreferredFeatureLevel=sm6\r\n";

    [Fact]
    public void Unreal_ini_edits_keep_everything_else()
    {
        var p = GameConfigs.For("fortnite");
        var plan = p.Plan(Ue);
        Assert.Equal(6, plan.Count);
        Assert.Contains(plan, c => c.Label == "Rendering mode" && c.To == "Performance");
        var after = p.Apply(Ue, plan);
        Assert.Contains("sg.ResolutionQuality=100\r\n", after);
        Assert.Contains("PreferredFeatureLevel=es31\r\n", after);
        Assert.Contains("bUseVSync=False\r\n", after);
        Assert.Contains("MouseX=0.08\r\n", after);
        Assert.Empty(p.Plan(after));
    }

}
