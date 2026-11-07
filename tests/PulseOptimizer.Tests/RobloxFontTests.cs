using Pulse.Core.Roblox;
using Xunit;

namespace PulseOptimizer.Tests;

public class RobloxFontTests
{
    [Fact]
    public void Every_face_points_at_the_custom_font()
    {
        const string family = """
        {"name":"Builder Sans","faces":[
          {"name":"Regular","weight":400,"style":"normal","assetId":"rbxasset://fonts/BuilderSans-Regular.otf"},
          {"name":"Bold","weight":700,"style":"normal","assetId":"rbxasset://fonts/BuilderSans-Bold.otf"}]}
        """;
        var text = RobloxFont.Rewrite(family, "rbxasset://fonts/PulseCustomFont.ttf");
        Assert.DoesNotContain("BuilderSans", text);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(text, "PulseCustomFont").Count);
        Assert.Contains("\"weight\": 700", text);
    }

    [Fact]
    public void Emoji_fonts_are_left_alone() =>
        Assert.Null(RobloxFont.Rewrite("""{"name":"Twemoji Mozilla","faces":[{"name":"Regular","assetId":"rbxasset://fonts/TwemojiMozilla.ttf"}]}""", "x"));

    [Fact]
    public void Apply_and_reset_one_version()
    {
        var root = Path.Combine(Path.GetTempPath(), "pulse-font-" + Guid.NewGuid().ToString("N"));
        var fam = Path.Combine(root, "content", "fonts", "families");
        Directory.CreateDirectory(fam);
        const string original = """{"name":"Gotham","faces":[{"name":"Regular","weight":400,"style":"normal","assetId":"rbxasset://fonts/GothamSSm-Book.otf"}]}""";
        File.WriteAllText(Path.Combine(fam, "Gotham.json"), original);
        var font = Path.Combine(root, "my.ttf");
        File.WriteAllBytes(font, new byte[] { 0, 1, 0, 0 }.Concat(new byte[2000]).ToArray());

        Assert.True(RobloxFont.IsFontFile(font, out _));
        Assert.Equal(1, RobloxFont.Apply(root, font));
        Assert.Contains("rbxasset://fonts/PulseCustomFont.ttf", File.ReadAllText(Path.Combine(fam, "Gotham.json")));
        Assert.True(File.Exists(Path.Combine(root, "content", "fonts", "PulseCustomFont.ttf")));

        RobloxFont.Reset(root);
        Assert.Equal(original, File.ReadAllText(Path.Combine(fam, "Gotham.json")));
    }

    [Fact]
    public void Rejects_files_that_arent_fonts()
    {
        var f = Path.Combine(Path.GetTempPath(), "notafont-" + Guid.NewGuid().ToString("N") + ".ttf");
        File.WriteAllText(f, new string('x', 4000));
        Assert.False(RobloxFont.IsFontFile(f, out _));
        Assert.False(RobloxFont.IsFontFile("font.ttc", out _));
    }
}

public class NetAdapterTests
{
    [Theory]
    [InlineData("0", "1", new[] { "0", "1" }, "", "", "0")]
    [InlineData("0", "1", new[] { "1", "2" }, "", "", null)]        // driver doesn't offer "off": leave it
    [InlineData("min", "9014", new[] { "1514", "4088", "9014" }, "", "", "1514")]
    [InlineData("cap:1024", "256", new string[0], "80", "2048", "1024")]
    [InlineData("cap:1024", "256", new string[0], "80", "512", "512")]
    [InlineData("cap:512", "128", new[] { "128", "256", "2048" }, "", "", "256")]
    public void Picks_only_values_the_driver_allows(string target, string current, string[] valid, string min, string max, string expected) =>
        Assert.Equal(expected, Pulse.Core.NetAdapterAction.Want(target, current, valid, min, max));
}
