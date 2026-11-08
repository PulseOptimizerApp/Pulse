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

    [Fact]
    public void Step_by_step_guides_are_complete()
    {
        var g = GuideData.BuiltIn;
        Assert.True(g.Guides.Length >= 8);
        Assert.Contains(g.Guides, d => d.Id == "afterburner" && d.Tool == "afterburner" && d.Featured);
        Assert.Equal(g.Guides.Length, g.Guides.Select(d => d.Id).Distinct().Count());
        var pages = new[] { "home", "diagnose", "fresh", "roblox", "clips", "tweaks", "measure", "reaction", "macros", "guide", "games", "cleanup", "startup", "security", "safety" };
        foreach (var d in g.Guides)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Title) || string.IsNullOrWhiteSpace(d.Summary), d.Id);
            Assert.All(d.Chapters, c => Assert.NotEmpty(c.Steps));
            Assert.All(d.Chapters.Where(c => c.Only != null), c => Assert.Contains(c.Only, new[] { "nvidia", "amd", "intel" }));
            foreach (var s in d.Chapters.SelectMany(c => c.Steps))
            {
                if (s.Url != null) Assert.StartsWith("https://", s.Url);
                if (s.Page != null) Assert.Contains(s.Page, pages);
                if (s.Url != null || s.Page != null) Assert.False(string.IsNullOrEmpty(s.Button), s.Text);
            }
            Assert.All(d.Sources, x => Assert.StartsWith("https://", x.Url));
            // Ticked-off steps are saved by chapter and step text, so those must be unique within a guide.
            var keys = d.Chapters.SelectMany(c => c.Steps.Select(s => GuideDoc.Key(c, s))).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
        }
    }

    [Fact]
    public void Every_guide_step_is_translated()
    {
        var g = GuideData.BuiltIn;
        foreach (var lang in new[] { "es", "pt-BR" })
        {
            var tr = g.Tr[lang];
            foreach (var d in g.Guides)
            {
                var texts = new[] { d.Title, d.Summary }.Concat(d.Chapters.SelectMany(c => new[] { c.Title, c.Intro }
                    .Concat(c.Steps.SelectMany(s => new[] { s.Text, s.Detail, s.Tip, s.Warn, s.Button })))).Where(x => !string.IsNullOrEmpty(x));
                Assert.All(texts, x => Assert.True(tr.ContainsKey(x), $"{lang}: {x}"));
            }
        }
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070", "nvidia", "950 mV")]
    [InlineData("NVIDIA GeForce RTX 5080", "nvidia", "900 to 950 mV")]
    [InlineData("NVIDIA GeForce RTX 3060 Ti", "nvidia", "850 to 875 mV")]
    [InlineData("AMD Radeon RX 9070 XT", "amd", "Voltage Offset")]
    [InlineData("AMD Radeon RX 7800 XT", "amd", "Max Voltage")]
    public void Afterburner_guide_knows_the_card(string gpu, string vendor, string start)
    {
        Assert.Equal(vendor, Afterburner.Vendor(gpu));
        Assert.StartsWith(start, Afterburner.StartingPoint(gpu));
    }

    [Fact]
    public void Older_guide_files_without_guides_still_load()
    {
        var json = File.ReadAllText(Path.Combine(Repo.Root, "src", "PulseOptimizer", "Core", "Guide", "guide.json"));
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node.Remove("Guides");
        var g = GuideContent.Parse(node.ToJsonString());
        Assert.NotNull(g);
        Assert.Empty(g.Guides);
    }
}
