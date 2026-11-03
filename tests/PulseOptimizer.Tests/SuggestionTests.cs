using Pulse.Core.Games;
using Xunit;

namespace PulseOptimizer.Tests;

public class SuggestionTests
{
    [Fact]
    public void Every_genre_has_at_least_three_games()
    {
        foreach (var g in Suggestions.Genres)
            Assert.True(Suggestions.ForGenre(g).Count >= 3, g);
    }

    [Fact]
    public void Names_are_unique_and_every_game_has_a_reason()
    {
        Assert.Equal(Suggestions.All.Count, Suggestions.All.Select(s => s.Name).Distinct().Count());
        Assert.All(Suggestions.All, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Why));
            Assert.NotEmpty(s.Genres);
            Assert.StartsWith("https://", s.Link);
        });
    }

    [Fact]
    public void For_you_follows_installed_games_and_skips_owned_ones()
    {
        var valorant = GameCatalog.All.First(g => g.Id == "valorant");
        var (picks, because) = Suggestions.ForYou(new[] { valorant }, new[] { "Ready or Not" });
        Assert.NotEmpty(picks);
        Assert.DoesNotContain(picks, p => p.Name == "Ready or Not");
        Assert.Contains("VALORANT", because);
        Assert.Contains(picks.Take(4), p => p.Genres.Contains("shooter") || p.Genres.Contains("tactical"));
    }

    [Fact]
    public void Nothing_installed_falls_back_to_free_games()
    {
        var (picks, because) = Suggestions.ForYou(Array.Empty<KnownGame>(), Array.Empty<string>());
        Assert.Equal("", because);
        Assert.All(picks, p => Assert.True(p.Free));
    }

    [Theory]
    [InlineData("Counter-Strike 2", "Counter-Strike 2", true)]
    [InlineData("Tom Clancy's Rainbow Six Siege", "Rust", false)]
    [InlineData("Rust", "Rust", true)]
    [InlineData("Terraria Soundtrack", "Terraria", true)]
    public void Name_matching(string installed, string suggestion, bool expected) =>
        Assert.Equal(expected, Suggestions.Matches(installed, suggestion));
}
