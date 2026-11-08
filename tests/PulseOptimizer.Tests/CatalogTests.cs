using System.Text.RegularExpressions;
using Pulse.Core;
using Xunit;

namespace PulseOptimizer.Tests;

/// <summary>
/// Keeps the tweak list honest: every tweak is explained, documented ones cite a source,
/// and tweaks cut by the research never come back.
/// </summary>
public class CatalogTests
{
    static List<Tweak> All => TweakCatalog.All;

    [Fact]
    public void Ids_are_unique() =>
        Assert.Equal(All.Count, All.Select(t => t.Id).Distinct().Count());

    [Fact]
    public void Every_tweak_is_explained_in_full()
    {
        foreach (var t in All)
        {
            foreach (var (field, value) in new[] { ("Title", t.Title), ("Summary", t.Summary), ("Explain", t.Explain), ("Changes", t.Changes), ("Gain", t.Gain), ("Tradeoff", t.Tradeoff) })
                Assert.False(string.IsNullOrWhiteSpace(value), $"{t.Id} has no {field}");
            Assert.NotEmpty(t.Actions);
        }
    }

    [Fact]
    public void Documented_tweaks_cite_an_https_source()
    {
        foreach (var t in All.Where(t => t.Evidence == Evidence.Documented))
            Assert.StartsWith("https://", t.SourceUrl);
    }

    [Fact]
    public void Comfort_tweaks_make_no_performance_claim()
    {
        foreach (var t in All.Where(t => t.Evidence == Evidence.NoPerformanceClaim))
            Assert.False(t.MakesPerformanceClaim, t.Id);
    }

    [Fact]
    public void Advanced_tweaks_are_never_recommended()
    {
        foreach (var t in All.Where(t => t.Risk == Risk.Advanced))
            Assert.False(t.Recommended, t.Id);
    }

    /// <summary>
    /// Cut on Microsoft's own documentation (see the research doc). These must never be re-added.
    /// NetworkThrottlingIndex and TCPNoDelay came back in 0.4.12 at the owner's request, as gated tweaks whose text
    /// says plainly that game ping usually doesn't change.
    /// </summary>
    [Theory]
    [InlineData("SFIO Priority")]
    [InlineData("GPU Priority")]
    [InlineData("GlobalTimerResolutionRequests")]
    public void Cut_tweaks_stay_cut(string valueName)
    {
        foreach (var t in All)
            foreach (var a in t.Actions.OfType<RegAction>())
                Assert.NotEqual(valueName, a.Name, StringComparer.OrdinalIgnoreCase);

        // Also catch it being written some other way in the catalog source.
        var src = File.ReadAllText(Path.Combine(Repo.Root, "src", "PulseOptimizer", "Core", "TweakCatalog.cs"));
        var code = string.Join("\n", src.Split('\n').Where(l => !l.TrimStart().StartsWith("//") && !l.TrimStart().StartsWith("///")));
        Assert.DoesNotMatch(new Regex("\"" + Regex.Escape(valueName) + "\""), code);
    }
}

public class SnippingToolSafetyTests
{
    [Fact]
    public void No_tweak_uses_the_all_apps_background_switch()
    {
        foreach (var t in Pulse.Core.TweakCatalog.All)
            foreach (var a in t.Actions.OfType<Pulse.Core.RegAction>())
                Assert.False(a.Name == "GlobalUserDisabled", $"{t.Id} switches off every Store app, which stops the Snipping Tool");
    }

    [Fact]
    public void Snipping_tool_keeps_running_in_the_background() =>
        Assert.DoesNotContain(Pulse.Core.TweakCatalog.BackgroundExtras, a => a.StartsWith("Microsoft.ScreenSketch", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Updates_are_swapped_in_by_the_new_version_not_a_script()
    {
        var src = File.ReadAllText(Path.Combine(Repo.Root, "src", "PulseOptimizer", "Core", "Updater.cs"));
        Assert.DoesNotContain("pulse-update.cmd", src);
        Assert.Contains("--finish-update", src);
        Assert.Contains("WaitForExit", src);
    }
}

public class UpdateResumeTests
{
    [Theory]
    [InlineData("Pulse.update.exe", true)]
    [InlineData("Pulse.update-3.exe", true)]
    [InlineData("PulseOptimizer.update.exe", true)]
    [InlineData("Pulse.exe", false)]
    [InlineData("PulseOptimizer.exe", false)]
    public void Download_names_are_recognised(string name, bool staged) => Assert.Equal(staged, Pulse.Core.Updater.IsStagedName(name));

    [Fact]
    public void A_held_download_name_has_spares() =>
        Assert.True(Pulse.Core.Updater.StagedNames().Distinct().Count() >= 4);

    [Fact]
    public void A_run_from_a_leftover_download_updates_Pulse_exe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pulse-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(Path.Combine(dir, "Pulse.exe"), Pulse.Core.Updater.InstallTarget(Path.Combine(dir, "Pulse.update.exe")));
            Assert.Equal(Path.Combine(dir, "Pulse.exe"), Pulse.Core.Updater.InstallTarget(Path.Combine(dir, "Pulse.exe")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Nothing_waiting_means_a_normal_start() => Assert.False(Pulse.Core.Updater.ResumeSwap());
}

public class DxGlobalTests
{
    [Fact]
    public void One_entry_changes_and_the_others_stay()
    {
        Assert.Equal("AutoHDREnable=1;SwapEffectUpgradeEnable=1;", DxGlobalAction.With("AutoHDREnable=1;", "SwapEffectUpgradeEnable", "1"));
        Assert.Equal("SwapEffectUpgradeEnable=1;VRROptimizeEnable=0;", DxGlobalAction.With("SwapEffectUpgradeEnable=0;VRROptimizeEnable=0;", "SwapEffectUpgradeEnable", "1"));
        Assert.Equal("VRROptimizeEnable=0;", DxGlobalAction.With("SwapEffectUpgradeEnable=1;VRROptimizeEnable=0;", "SwapEffectUpgradeEnable", null));
        Assert.Equal("SwapEffectUpgradeEnable=1;", DxGlobalAction.With(null, "SwapEffectUpgradeEnable", "1"));
    }
}
