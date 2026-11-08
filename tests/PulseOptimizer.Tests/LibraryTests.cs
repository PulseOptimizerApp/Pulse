using Pulse.Core.Crosshair;
using Xunit;

namespace PulseOptimizer.Tests;

public class LibraryTests
{
    [Fact]
    public void Every_library_crosshair_is_a_valid_valorant_code() =>
        Assert.All(CrosshairLibrary.All, e => Assert.NotNull(e.Style));

    [Fact]
    public void Every_library_use_is_a_known_use_case() =>
        Assert.All(CrosshairLibrary.All, e => Assert.All(e.Uses, u => Assert.Contains(u, CrosshairLibrary.UseCases)));

    [Fact]
    public void Library_names_are_unique() =>
        Assert.Equal(CrosshairLibrary.All.Count, CrosshairLibrary.All.Select(e => e.Name).Distinct().Count());

    [Fact]
    public void Search_finds_by_use_and_by_word()
    {
        Assert.All(CrosshairLibrary.Find("", "Snipers"), e => Assert.Contains("Snipers", e.Uses));
        Assert.Contains(CrosshairLibrary.Find("magenta", ""), e => e.Name == "Magenta dot");
        Assert.Empty(CrosshairLibrary.Find("zzzz", ""));
    }

    [Fact]
    public void Compare_lists_only_settings_that_differ()
    {
        var a = CrosshairLibrary.All.First(e => e.Name == "Classic cross").Style;
        Assert.Empty(CrosshairLibrary.Differences(a, a.Copy()));
        var b = CrosshairLibrary.All.First(e => e.Name == "Dot").Style;
        var d = CrosshairLibrary.Differences(a, b);
        Assert.Contains(d, x => x.Setting == "Center dot");
        Assert.Contains(d, x => x.Setting == "Inner lines");
    }

    [Fact]
    public void Only_web_links_open() 
    {
        Assert.True(Pulse.Core.Games.Valorant.Lineups.SafeLink("https://youtu.be/abc"));
        Assert.False(Pulse.Core.Games.Valorant.Lineups.SafeLink("file:///C:/Windows/system32/calc.exe"));
        Assert.False(Pulse.Core.Games.Valorant.Lineups.SafeLink("javascript:alert(1)"));
        Assert.False(Pulse.Core.Games.Valorant.Lineups.SafeLink(""));
    }
}
