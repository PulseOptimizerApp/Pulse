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

    /// <summary>Cut on Microsoft's own documentation (see the research doc). These must never be re-added.</summary>
    [Theory]
    [InlineData("NetworkThrottlingIndex")]
    [InlineData("TCPNoDelay")]
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
    public void Update_swap_retries_until_pulse_has_closed()
    {
        var s = Pulse.Core.Updater.UpdateScript(@"C:\P\PulseOptimizer.update.exe", @"C:\P\PulseOptimizer.exe");
        Assert.Contains("for /l %%i in (1,1,60)", s);
        Assert.Contains("goto started", s);
        Assert.Contains("--restarted", s);
    }
}
