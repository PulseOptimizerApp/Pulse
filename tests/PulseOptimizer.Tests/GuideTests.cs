using Pulse.Core.Guide;
using Xunit;

namespace PulseOptimizer.Tests;

public class GuideTests
{
    [Fact]
    public void The_built_in_guide_loads_with_every_section()
    {
        var g = GuideData.BuiltIn;
        Assert.Equal(GuideContent.CurrentSchema, g.Schema);
        Assert.NotEmpty(g.Tips); Assert.NotEmpty(g.Gpus); Assert.NotEmpty(g.Ram); Assert.NotEmpty(g.News);
        Assert.NotEmpty(g.Gear); Assert.NotEmpty(g.Builders); Assert.True(g.RamHistory.Length >= 2);
        Assert.All(g.Gpus, x => Assert.True(x.Msrp > 0 && x.Now > 0, x.Name));
        Assert.All(g.Tips, t => Assert.Contains(t.Impact, new[] { "Huge", "High", "Medium", "Low" }));
    }

    [Fact]
    public void Every_guide_link_is_https()
    {
        var g = GuideData.BuiltIn;
        var urls = g.Tips.Select(t => t.SourceUrl).Concat(g.Gear.SelectMany(x => x.Picks).Select(p => p.SourceUrl))
            .Concat(g.Builders.Select(b => b.SourceUrl)).Concat(g.News.Select(n => n.SourceUrl)).Append(g.GpuSourceUrl).Append(g.RamSourceUrl)
            .Where(u => u != null);
        Assert.All(urls, u => Assert.StartsWith("https://", u));
    }

    [Fact]
    public void Tip_buttons_open_real_pages()
    {
        var pages = new[] { "home", "diagnose", "fresh", "roblox", "clips", "tweaks", "measure", "reaction", "macros", "guide", "games", "cleanup", "startup", "security", "safety" };
        Assert.All(GuideData.BuiltIn.Tips.Where(t => t.PulsePage != null), t => Assert.Contains(t.PulsePage, pages));
    }

    [Fact]
    public void A_broken_or_wrong_download_is_ignored()
    {
        Assert.Null(GuideContent.Parse("not json"));
        Assert.Null(GuideContent.Parse("{}"));
        Assert.Null(GuideContent.Parse("{\"Schema\":99,\"Updated\":\"2026-11-07\"}"));
        Assert.Null(GuideContent.Parse("{\"Schema\":1,\"Updated\":\"2026-11-07\",\"Tips\":[]}"));
    }

    [Fact]
    public void The_websites_copy_is_valid_when_present()
    {
        // The site repo sits next to this one in development; CI only has this repo.
        var site = Path.Combine(Repo.Root, "..", "pulseoptimizer-app.github.io", "guide", "guide.json");
        if (!File.Exists(site)) return;
        Assert.NotNull(GuideContent.Parse(File.ReadAllText(site)));
    }
}
